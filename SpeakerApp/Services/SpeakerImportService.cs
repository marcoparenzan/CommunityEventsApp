using System.Data;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

/// <summary>One speaker read from a Sessionize "flattened accepted sessions" export.</summary>
public sealed record SpeakerImportRow(int RowNumber, string SessionizeSpeakerId, string FirstName, string LastName, string Email, string? Error);

/// <summary>Result of comparing one file row with the speaker accounts.</summary>
public sealed class SpeakerImportItem
{
    public int RowNumber { get; init; }
    public string SessionizeSpeakerId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string FileEmail { get; init; } = string.Empty;
    public int? SpeakerId { get; set; }
    public string CurrentEmail { get; set; } = string.Empty;
    public string CurrentSessionizeSpeakerId { get; set; } = string.Empty;
    public bool SetSessionizeId { get; set; }
    public bool ChangeEmail { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public bool HasChanges => SpeakerId is not null && (SetSessionizeId || ChangeEmail);
}

public sealed record SpeakerImportResult(int Updated, int EmailsChanged, int IdsSet);

/// <summary>
/// Verifies and imports Sessionize speaker ids and emails from the xlsx export. When the email in the
/// file differs from the account's, the account is updated and the change is recorded in
/// community.SpeakerChangeLog.
/// </summary>
public sealed class SpeakerImportService(ILogger<SpeakerImportService> logger, SqlConnectionFactory connections)
{
    private const int MaximumLength = 64;
    private const string Source = "Import Sessionize xlsx";

    public static IReadOnlyList<SpeakerImportRow> Parse(Stream stream, string fileName)
    {
        var table = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? UserImportService.ReadCsv(stream)
            : UserImportService.ReadXlsx(stream);
        if (table.Count < 2)
        {
            throw new InvalidOperationException("Il file non contiene righe di dati.");
        }

        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var column = 0; column < table[0].Count; column++)
        {
            columns.TryAdd(table[0][column].Trim(), column);
        }

        int Column(string name) => columns.TryGetValue(name, out var number)
            ? number
            : throw new InvalidOperationException($"Colonna '{name}' non trovata: servono Speaker Id, FirstName, LastName ed Email (export Sessionize).");
        var idColumn = Column("Speaker Id");
        var firstNameColumn = Column("FirstName");
        var lastNameColumn = Column("LastName");
        var emailColumn = Column("Email");

        // L'export ha una riga per sessione e relatore: lo stesso relatore compare più volte.
        var rows = new List<SpeakerImportRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < table.Count; index++)
        {
            string Cell(int column) => column < table[index].Count ? table[index][column].Trim() : string.Empty;
            var id = Cell(idColumn);
            var firstName = Cell(firstNameColumn);
            var lastName = Cell(lastNameColumn);
            var email = Cell(emailColumn).ToLowerInvariant();
            if (id.Length == 0 && firstName.Length == 0 && lastName.Length == 0 && email.Length == 0)
            {
                continue;
            }

            if (id.Length > 0 && !seen.Add(id))
            {
                continue;
            }

            string? error = null;
            if (!Guid.TryParse(id, out _))
            {
                error = "Speaker Id non valido.";
            }
            else if (email.Length == 0 || email.Length > MaximumLength ||
                     !(System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
                       string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Email mancante o non valida (massimo 64 caratteri).";
            }

            rows.Add(new SpeakerImportRow(index + 1, id.ToLowerInvariant(), firstName, lastName, email, error));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("Il file non contiene speaker.");
        }

        return rows;
    }

    public async Task<IReadOnlyList<SpeakerImportItem>> AnalyzeAsync(
        IReadOnlyList<SpeakerImportRow> rows,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Analyzing a speakers import.");
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await AnalyzeAsync(connection, null, rows, cancellationToken);
    }

    public async Task<SpeakerImportResult> ImportAsync(
        IReadOnlyList<SpeakerImportRow> rows,
        string changedBy,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Importing speakers.");
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        // Rianalizza dentro la transazione: i dati potrebbero essere cambiati dopo la verifica.
        var items = await AnalyzeAsync(connection, transaction, rows, cancellationToken);
        var emailsChanged = 0;
        var idsSet = 0;
        var updated = 0;
        foreach (var item in items.Where(item => item.HasChanges))
        {
            await using (var update = new SqlCommand("""
                UPDATE community.Speakers
                SET EMail = CASE WHEN @changeEmail = 1 THEN @email ELSE EMail END,
                    SessionizeSpeakerID = CASE WHEN @setId = 1 THEN @sessionizeId ELSE SessionizeSpeakerID END
                WHERE ID = @id;
                """, connection, transaction))
            {
                update.Parameters.Add("@id", SqlDbType.Int).Value = item.SpeakerId!.Value;
                update.Parameters.Add("@changeEmail", SqlDbType.Bit).Value = item.ChangeEmail;
                update.Parameters.Add("@setId", SqlDbType.Bit).Value = item.SetSessionizeId;
                update.Parameters.Add("@email", SqlDbType.NVarChar, MaximumLength).Value = item.FileEmail;
                update.Parameters.Add("@sessionizeId", SqlDbType.NVarChar, 80).Value = item.SessionizeSpeakerId;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            if (item.ChangeEmail)
            {
                await LogAsync(connection, transaction, item.SpeakerId!.Value, "EMail", item.CurrentEmail, item.FileEmail, changedBy, cancellationToken);
                emailsChanged++;
            }

            if (item.SetSessionizeId)
            {
                await LogAsync(connection, transaction, item.SpeakerId!.Value, "SessionizeSpeakerID",
                    item.CurrentSessionizeSpeakerId, item.SessionizeSpeakerId, changedBy, cancellationToken);
                idsSet++;
            }

            updated++;
        }

        await transaction.CommitAsync(cancellationToken);
        return new SpeakerImportResult(updated, emailsChanged, idsSet);
    }

    private static async Task LogAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int speakerId,
        string field,
        string oldValue,
        string newValue,
        string changedBy,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO community.SpeakerChangeLog (SpeakerID, Field, OldValue, NewValue, Source, ChangedBy)
            VALUES (@speakerId, @field, NULLIF(@oldValue, N''), @newValue, @source, @changedBy);
            """, connection, transaction);
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        command.Parameters.Add("@field", SqlDbType.NVarChar, 64).Value = field;
        command.Parameters.Add("@oldValue", SqlDbType.NVarChar, 256).Value = oldValue;
        command.Parameters.Add("@newValue", SqlDbType.NVarChar, 256).Value = newValue;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 64).Value = Source;
        command.Parameters.Add("@changedBy", SqlDbType.NVarChar, 128).Value = changedBy.Length > 128 ? changedBy[..128] : changedBy;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record Account(int Id, string FirstName, string LastName, string Email, string SessionizeName, string SessionizeSpeakerId);

    private static async Task<IReadOnlyList<SpeakerImportItem>> AnalyzeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<SpeakerImportRow> rows,
        CancellationToken cancellationToken)
    {
        var accounts = new List<Account>();
        await using (var command = new SqlCommand("""
            SELECT ID, ISNULL(FirstName, N''), ISNULL(LastName, N''), ISNULL(EMail, N''),
                   ISNULL(SessionizeName, N''), ISNULL(SessionizeSpeakerID, N'')
            FROM community.Speakers;
            """, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                accounts.Add(new Account(
                    reader.GetInt32(0), reader.GetString(1).Trim(), reader.GetString(2).Trim(),
                    reader.GetString(3).Trim(), reader.GetString(4).Trim(), reader.GetString(5).Trim()));
            }
        }

        // Account già collegato allo speaker Sessionize tramite la sincronizzazione degli eventi.
        var accountByEventSpeaker = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand("""
            SELECT SessionizeSpeakerID, MIN(SpeakerID), COUNT(DISTINCT SpeakerID)
            FROM community.EventSpeakers
            WHERE SpeakerID IS NOT NULL
            GROUP BY SessionizeSpeakerID;
            """, connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(2) == 1)
                {
                    accountByEventSpeaker[reader.GetString(0)] = reader.GetInt32(1);
                }
            }
        }

        var items = new List<SpeakerImportItem>();
        var targeted = new HashSet<int>();
        foreach (var row in rows)
        {
            var name = $"{row.FirstName} {row.LastName}".Trim();
            var item = new SpeakerImportItem
            {
                RowNumber = row.RowNumber,
                SessionizeSpeakerId = row.SessionizeSpeakerId,
                Name = name,
                FileEmail = row.Email
            };
            items.Add(item);

            if (row.Error is not null)
            {
                item.Status = "Non valido";
                item.Note = row.Error;
                continue;
            }

            var (account, ambiguous) = Match(row, name, accounts, accountByEventSpeaker);
            if (ambiguous)
            {
                item.Status = "Ambiguo";
                item.Note = "Più account speaker corrispondono: correggi il nome Sessionize in Gestisci speaker.";
                continue;
            }

            if (account is null)
            {
                item.Status = "Non trovato";
                item.Note = "Nessun account speaker corrisponde (esegui prima la sincronizzazione Sessionize dell'evento).";
                continue;
            }

            item.SpeakerId = account.Id;
            item.CurrentEmail = account.Email;
            item.CurrentSessionizeSpeakerId = account.SessionizeSpeakerId;
            if (!targeted.Add(account.Id))
            {
                item.SpeakerId = null;
                item.Status = "Duplicato";
                item.Note = "Lo stesso account è già collegato a un'altra riga del file.";
                continue;
            }

            item.SetSessionizeId = !string.Equals(account.SessionizeSpeakerId, row.SessionizeSpeakerId, StringComparison.OrdinalIgnoreCase);
            item.ChangeEmail = !string.Equals(account.Email, row.Email, StringComparison.OrdinalIgnoreCase);

            if (item.ChangeEmail &&
                accounts.Any(other => other.Id != account.Id &&
                                      string.Equals(other.Email, row.Email, StringComparison.OrdinalIgnoreCase)))
            {
                item.ChangeEmail = false;
                item.Status = item.SetSessionizeId ? "Da aggiornare" : "Email già usata";
                item.Note = "L'email del file appartiene a un altro account speaker: l'email non viene cambiata.";
                continue;
            }

            if (item.SetSessionizeId || item.ChangeEmail)
            {
                item.Status = "Da aggiornare";
                var parts = new List<string>();
                if (item.ChangeEmail)
                {
                    parts.Add(account.Email.Length == 0 || account.Email == SpeakerManagementService.UnsetEmail
                        ? "email da impostare"
                        : "email diversa: verrà cambiata");
                }

                if (item.SetSessionizeId)
                {
                    parts.Add(account.SessionizeSpeakerId.Length == 0 ? "Speaker Id da impostare" : "Speaker Id diverso: verrà cambiato");
                }

                item.Note = string.Join("; ", parts);
            }
            else
            {
                item.Status = "Invariato";
            }
        }

        return items;
    }

    private static (Account? Account, bool Ambiguous) Match(
        SpeakerImportRow row,
        string name,
        IReadOnlyList<Account> accounts,
        IReadOnlyDictionary<string, int> accountByEventSpeaker)
    {
        // 1. Speaker Id Sessionize già memorizzato sull'account.
        var byId = accounts.Where(a => string.Equals(a.SessionizeSpeakerId, row.SessionizeSpeakerId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (byId.Length == 1) return (byId[0], false);

        // 2. Account collegato allo speaker dell'evento sincronizzato da Sessionize.
        if (accountByEventSpeaker.TryGetValue(row.SessionizeSpeakerId, out var linkedId) &&
            accounts.FirstOrDefault(a => a.Id == linkedId) is { } linked)
        {
            return (linked, false);
        }

        // 3. Nome Sessionize, 4. nome e cognome, 5. email già presente.
        foreach (var candidates in new[]
        {
            accounts.Where(a => a.SessionizeName.Length > 0 && string.Equals(a.SessionizeName, name, StringComparison.OrdinalIgnoreCase)).ToArray(),
            accounts.Where(a => string.Equals(a.FirstName, row.FirstName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(a.LastName, row.LastName, StringComparison.OrdinalIgnoreCase)).ToArray(),
            accounts.Where(a => string.Equals(a.Email, row.Email, StringComparison.OrdinalIgnoreCase)).ToArray()
        })
        {
            if (candidates.Length == 1) return (candidates[0], false);
            if (candidates.Length > 1) return (null, true);
        }

        return (null, false);
    }
}
