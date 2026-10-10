using System.Data;
using System.Text;
using ClosedXML.Excel;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

public sealed record UserImportRow(int RowNumber, string FirstName, string LastName, string Email, string? Error);

public sealed class UserImportItem
{
    public int RowNumber { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public int? UserId { get; set; }
    public bool AddUser { get; set; }
    public bool FillNames { get; set; }
    public bool Register { get; set; }
    /// <summary>Turns on "receives 1nn0va communications" (with a consent record).</summary>
    public bool SetNotifications { get; set; }
    /// <summary>Sets "eats pizza" on the event registration.</summary>
    public bool SetPizza { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;

    public bool HasChanges => AddUser || FillNames || Register || SetNotifications || SetPizza;
}

public sealed record UserImportResult(int UsersAdded, int NamesFilled, int Registered, int NotificationsSet = 0, int PizzaSet = 0);

/// <summary>Values applied to every imported person: communications flag (users) and pizza (event registrations).</summary>
public sealed record UserImportOptions(bool SetNotifications = false, bool SetPizza = false);

/// <summary>
/// Imports last name, first name and email from an xlsx/csv file into community.Users (one time load)
/// or, for a single event, also registers those people to the event (adding the missing users).
/// </summary>
public sealed class UserImportService(SqlConnectionFactory connections)
{
    private const int MaximumLength = 64;

    private static readonly string[] LastNameHeaders = ["lastname", "cognome", "surname", "familyname"];
    private static readonly string[] FirstNameHeaders = ["firstname", "nome", "givenname"];
    private static readonly string[] EmailHeaders = ["email", "emailaddress", "mail", "indirizzoemail", "e-mail"];

    /// <summary>
    /// Turns an uploaded file into CSV text: an Excel file is converted (first sheet, ';' separated, one line per row);
    /// a CSV file is just decoded. <paramref name="columns"/> limits an Excel file to the listed headers.
    /// </summary>
    public static string ToCsvText(byte[] data, string fileName, IReadOnlyList<string>? columns = null)
    {
        using var stream = new MemoryStream(data);
        if (fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd().TrimStart('\uFEFF');
        }

        var table = ReadXlsx(stream);
        if (table.Count == 0)
        {
            throw new InvalidOperationException("Il foglio è vuoto.");
        }

        var keep = Enumerable.Range(0, table.Max(row => row.Count)).ToList();
        if (columns is { Count: > 0 })
        {
            var selected = columns
                .Select(name => table[0].FindIndex(header => string.Equals(header.Trim(), name, StringComparison.OrdinalIgnoreCase)))
                .Where(index => index >= 0)
                .ToList();
            if (selected.Count > 0)
            {
                keep = selected;
            }
        }

        static string Field(string value)
        {
            value = value.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            return value.Contains(';') || value.Contains('"') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        var text = new StringBuilder();
        foreach (var row in table)
        {
            var cells = keep.Select(column => column < row.Count ? Field(row[column]) : string.Empty).ToList();
            if (cells.All(cell => cell.Length == 0))
            {
                continue;
            }

            text.Append(string.Join(';', cells)).Append('\n');
        }

        return text.ToString();
    }

    public static IReadOnlyList<UserImportRow> Parse(Stream stream, string fileName)
    {
        var table = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? ReadCsv(stream)
            : ReadXlsx(stream);
        if (table.Count == 0)
        {
            throw new InvalidOperationException("Il file è vuoto.");
        }

        // Le colonne si riconoscono dalle intestazioni; quelle non riconosciute seguono l'ordine
        // Cognome, Nome, Email (l'email è la colonna che contiene la chiocciola).
        var header = table[0].Select(Normalize).ToList();
        int Find(string[] names) => header.FindIndex(h => names.Contains(h));
        var lastColumn = Find(LastNameHeaders);
        var firstColumn = Find(FirstNameHeaders);
        var emailColumn = Find(EmailHeaders);
        var hasHeader = lastColumn >= 0 || firstColumn >= 0 || emailColumn >= 0;

        if (emailColumn < 0)
        {
            var width = table.Max(row => row.Count);
            emailColumn = Enumerable.Range(0, width)
                .Select(column => (column, count: table.Skip(hasHeader ? 1 : 0).Count(row => column < row.Count && row[column].Contains('@'))))
                .OrderByDescending(entry => entry.count)
                .First().column;
            if (table.Skip(hasHeader ? 1 : 0).All(row => emailColumn >= row.Count || !row[emailColumn].Contains('@')))
            {
                throw new InvalidOperationException(
                    "Non trovo la colonna Email. Il file deve avere le colonne Cognome, Nome, Email (con o senza intestazioni).");
            }
        }

        var remaining = Enumerable.Range(0, table.Max(row => row.Count)).Where(column => column != emailColumn).ToList();
        if (lastColumn < 0) lastColumn = remaining.Where(column => column != firstColumn).FirstOrDefault(-1);
        if (firstColumn < 0) firstColumn = remaining.Where(column => column != lastColumn).FirstOrDefault(-1);
        if (lastColumn < 0 || firstColumn < 0)
        {
            throw new InvalidOperationException("Il file deve avere almeno tre colonne: Cognome, Nome, Email.");
        }

        // Senza intestazioni la prima riga contiene già dei dati.
        var firstDataRow = hasHeader ? 1 : 0;
        if (table.Count <= firstDataRow)
        {
            throw new InvalidOperationException("Il file non contiene righe di dati.");
        }

        var rows = new List<UserImportRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = firstDataRow; index < table.Count; index++)
        {
            string Cell(int column) => column < table[index].Count ? table[index][column].Trim() : string.Empty;
            var lastName = Cell(lastColumn);
            var firstName = Cell(firstColumn);
            var email = Cell(emailColumn).ToLowerInvariant();
            if (lastName.Length == 0 && firstName.Length == 0 && email.Length == 0)
            {
                continue;
            }

            if (email.Length > 0 && !seen.Add(email))
            {
                continue; // stessa email già presente nel file
            }

            string? error = null;
            if (email.Length == 0 || email.Length > MaximumLength ||
                !(System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
                  string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Email mancante o non valida (massimo 64 caratteri).";
            }
            else if (firstName.Length > MaximumLength || lastName.Length > MaximumLength)
            {
                error = "Nome o cognome più lunghi di 64 caratteri.";
            }

            rows.Add(new UserImportRow(index + 1, firstName, lastName, email, error));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("Il file non contiene persone.");
        }

        return rows;
    }

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    internal static List<List<string>> ReadXlsx(Stream stream)
    {
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new InvalidOperationException("Il file non contiene fogli.");
        var range = sheet.RangeUsed() ?? throw new InvalidOperationException("Il foglio è vuoto.");
        var table = new List<List<string>>();
        foreach (var row in range.Rows())
        {
            table.Add(row.Cells().Select(cell =>
                cell.HasHyperlink && cell.GetHyperlink().IsExternal &&
                cell.GetHyperlink().ExternalAddress is { } link && (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp)
                    ? link.OriginalString
                    : cell.GetString()).ToList());
        }

        return table;
    }

    internal static List<List<string>> ReadCsv(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        var firstLine = text.Split('\n', 2)[0];
        var delimiter = new[] { ';', ',', '\t' }.MaxBy(candidate => firstLine.Count(c => c == candidate));
        var table = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                table.Add(row);
                row = [];
            }
            else field.Append(c);
        }

        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            table.Add(row);
        }

        return table;
    }

    public async Task<IReadOnlyList<UserImportItem>> AnalyzeAsync(
        IReadOnlyList<UserImportRow> rows,
        int? eventId,
        UserImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await AnalyzeAsync(connection, null, rows, eventId, options ?? new UserImportOptions(), cancellationToken);
    }

    public async Task<UserImportResult> ImportAsync(
        IReadOnlyList<UserImportRow> rows,
        int? eventId,
        UserImportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var items = await AnalyzeAsync(connection, transaction, rows, eventId, options ?? new UserImportOptions(), cancellationToken);
        int added = 0, filled = 0, registered = 0, notifications = 0, pizza = 0;
        foreach (var item in items.Where(item => item.HasChanges))
        {
            var userId = item.UserId;
            if (item.AddUser)
            {
                await using var insert = new SqlCommand("""
                    INSERT INTO community.Users
                        (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
                    VALUES (NULLIF(@firstName, N''), NULLIF(@lastName, N''), @email, NULL, @allow, 0, 0);
                    SELECT CONVERT(int, SCOPE_IDENTITY());
                    """, connection, transaction);
                AddNameParameters(insert, item);
                insert.Parameters.Add("@allow", SqlDbType.Bit).Value = item.SetNotifications;
                userId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
                added++;
                if (item.SetNotifications)
                {
                    await AddConsentAsync(connection, transaction, userId!.Value, cancellationToken);
                    notifications++;
                }
            }
            else
            {
                if (item.FillNames)
                {
                    await using var update = new SqlCommand("""
                        UPDATE community.Users
                        SET FirstName = CASE WHEN NULLIF(LTRIM(RTRIM(FirstName)), N'') IS NULL AND @firstName <> N'' THEN @firstName ELSE FirstName END,
                            LastName = CASE WHEN NULLIF(LTRIM(RTRIM(LastName)), N'') IS NULL AND @lastName <> N'' THEN @lastName ELSE LastName END
                        WHERE ID = @id;
                        """, connection, transaction);
                    AddNameParameters(update, item);
                    update.Parameters.Add("@id", SqlDbType.Int).Value = userId!.Value;
                    await update.ExecuteNonQueryAsync(cancellationToken);
                    filled++;
                }

                if (item.SetNotifications)
                {
                    await using var allow = new SqlCommand(
                        "UPDATE community.Users SET AllowEventsNotifications = 1 WHERE ID = @id;", connection, transaction);
                    allow.Parameters.Add("@id", SqlDbType.Int).Value = userId!.Value;
                    await allow.ExecuteNonQueryAsync(cancellationToken);
                    await AddConsentAsync(connection, transaction, userId.Value, cancellationToken);
                    notifications++;
                }
            }

            if ((item.Register || item.SetPizza) && eventId is int id)
            {
                await using var register = new SqlCommand("""
                    UPDATE community.UserEventRegistration
                    SET Status = CASE WHEN @register = 1 THEN N'Registered' ELSE Status END,
                        IsEatingPizza = CASE WHEN @pizza = 1 THEN 1 ELSE IsEatingPizza END
                    WHERE UserID = @userId AND EventID = @eventId;
                    IF @@ROWCOUNT = 0
                        INSERT INTO community.UserEventRegistration (UserID, EventID, RegistrationDate, Status, IsEatingPizza, LactoseIntolerant)
                        VALUES (@userId, @eventId, community.LocalNowDateTime(), N'Registered', @pizza, 0);
                    """, connection, transaction);
                register.Parameters.Add("@userId", SqlDbType.Int).Value = userId!.Value;
                register.Parameters.Add("@eventId", SqlDbType.Int).Value = id;
                register.Parameters.Add("@register", SqlDbType.Bit).Value = item.Register;
                register.Parameters.Add("@pizza", SqlDbType.Bit).Value = item.SetPizza;
                await register.ExecuteNonQueryAsync(cancellationToken);
                if (item.Register) registered++;
                if (item.SetPizza) pizza++;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new UserImportResult(added, filled, registered, notifications, pizza);
    }

    private static async Task AddConsentAsync(
        SqlConnection connection, SqlTransaction transaction, int userId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO community.UserConsentHistory (UserID, ConsentType, IsGranted, PolicyVersion, Source)
            VALUES (@userId, N'EventNotifications', 1, N'1', N'Import');
            """, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddNameParameters(SqlCommand command, UserImportItem item)
    {
        command.Parameters.Add("@firstName", SqlDbType.NVarChar, MaximumLength).Value = item.FirstName;
        command.Parameters.Add("@lastName", SqlDbType.NVarChar, MaximumLength).Value = item.LastName;
        command.Parameters.Add("@email", SqlDbType.NVarChar, MaximumLength).Value = item.Email;
    }

    private sealed record Existing(int Id, string FirstName, string LastName, bool Allow);

    private sealed record Registration(string Status, bool Pizza);

    private static async Task<IReadOnlyList<UserImportItem>> AnalyzeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<UserImportRow> rows,
        int? eventId,
        UserImportOptions options,
        CancellationToken cancellationToken)
    {
        var users = new Dictionary<string, Existing>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(
            "SELECT ID, LOWER(LTRIM(RTRIM(EMail))), ISNULL(FirstName, N''), ISNULL(LastName, N''), AllowEventsNotifications FROM community.Users WHERE IsDeleted = 0;",
            connection, transaction))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                users[reader.GetString(1)] = new Existing(reader.GetInt32(0), reader.GetString(2).Trim(), reader.GetString(3).Trim(), reader.GetBoolean(4));
            }
        }

        var registrations = new Dictionary<int, Registration>();
        if (eventId is int id)
        {
            await using (var exists = new SqlCommand("SELECT COUNT(*) FROM community.Events WHERE ID = @id;", connection, transaction))
            {
                exists.Parameters.Add("@id", SqlDbType.Int).Value = id;
                if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0)
                {
                    throw new InvalidOperationException("L'evento non esiste.");
                }
            }

            await using var command = new SqlCommand(
                "SELECT UserID, Status, IsEatingPizza FROM community.UserEventRegistration WHERE EventID = @id;", connection, transaction);
            command.Parameters.Add("@id", SqlDbType.Int).Value = id;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                registrations[reader.GetInt32(0)] = new Registration(reader.GetString(1), reader.GetBoolean(2));
            }
        }

        var items = new List<UserImportItem>();
        foreach (var row in rows)
        {
            var item = new UserImportItem
            {
                RowNumber = row.RowNumber,
                Name = $"{row.LastName} {row.FirstName}".Trim(),
                Email = row.Email,
                FirstName = row.FirstName,
                LastName = row.LastName
            };
            items.Add(item);
            if (row.Error is not null)
            {
                item.Status = "Non valido";
                item.Note = row.Error;
                continue;
            }

            var notes = new List<string>();
            if (users.TryGetValue(row.Email, out var existing))
            {
                item.UserId = existing.Id;
                item.FillNames = (existing.FirstName.Length == 0 && row.FirstName.Length > 0) ||
                                 (existing.LastName.Length == 0 && row.LastName.Length > 0);
                if (item.FillNames) notes.Add("nome da completare");
                if (options.SetNotifications && !existing.Allow)
                {
                    item.SetNotifications = true;
                    notes.Add("comunicazioni: sì");
                }
                var different = (existing.FirstName.Length > 0 && row.FirstName.Length > 0 &&
                                 !string.Equals(existing.FirstName, row.FirstName, StringComparison.OrdinalIgnoreCase)) ||
                                (existing.LastName.Length > 0 && row.LastName.Length > 0 &&
                                 !string.Equals(existing.LastName, row.LastName, StringComparison.OrdinalIgnoreCase));
                if (different) notes.Add($"in anagrafica: {existing.LastName} {existing.FirstName} (non modificato)");
            }
            else
            {
                item.AddUser = true;
                notes.Add("nuovo utente");
                if (options.SetNotifications)
                {
                    item.SetNotifications = true;
                    notes.Add("comunicazioni: sì");
                }
            }

            if (eventId is not null)
            {
                if (item.UserId is int userId && registrations.TryGetValue(userId, out var registration) && registration.Status == "Registered")
                {
                    if (options.SetPizza && !registration.Pizza)
                    {
                        item.SetPizza = true;
                        item.Status = "Pizza da impostare";
                        notes.Add("già iscritto");
                    }
                    else
                    {
                        item.Status = "Già iscritto";
                    }
                }
                else
                {
                    item.Register = true;
                    item.SetPizza = options.SetPizza;
                    item.Status = "Da iscrivere";
                    if (options.SetPizza) notes.Add("pizza: sì");
                }
            }
            else
            {
                item.Status = item.AddUser ? "Nuovo" : item.FillNames || item.SetNotifications ? "Da aggiornare" : "Esistente";
            }

            item.Note = string.Join("; ", notes);
        }

        return items;
    }
}
