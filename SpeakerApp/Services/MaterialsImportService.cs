using System.Data;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

public sealed record MaterialsImportRow(
    int RowNumber, string SessionId, string Title,
    string LastName, string FirstName, string Email,
    string Slide, string Repository, string? Error);

public sealed class MaterialsImportItem
{
    public int RowNumber { get; init; }
    /// <summary>What the file row refers to: the speaker (Cognome Nome / email) or the session title.</summary>
    public string Label { get; init; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string SessionTitle { get; set; } = string.Empty;
    public string Slide { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public bool Update { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

/// <summary>
/// Imports slide and repository links of the sessions of one event. A row identifies the session by
/// Session Id or Titolo, or through its speaker (Email, or Cognome + Nome). Empty rows and rows without links are skipped.
/// </summary>
public sealed class MaterialsImportService(ILogger<MaterialsImportService> logger, SqlConnectionFactory connections)
{
    private static readonly string[] SessionIdHeaders = ["sessionid", "idsessione"];
    private static readonly string[] TitleHeaders = ["title", "titolo", "sessione", "session", "titolosessione"];
    private static readonly string[] LastNameHeaders = ["lastname", "cognome", "surname", "familyname"];
    private static readonly string[] FirstNameHeaders = ["firstname", "nome", "givenname"];
    private static readonly string[] EmailHeaders = ["email", "emailaddress", "mail", "indirizzoemail"];
    private static readonly string[] SlideHeaders = ["slide", "slides", "presentation", "presentationlink", "presentazione", "linkslide"];
    private static readonly string[] RepositoryHeaders = ["repository", "repo", "github", "repositorylink", "linkcodice"];

    private static string Normalize(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static IReadOnlyList<MaterialsImportRow> Parse(Stream stream, string fileName)
    {
        var table = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? UserImportService.ReadCsv(stream)
            : UserImportService.ReadXlsx(stream);
        if (table.Count < 2)
        {
            throw new InvalidOperationException("Il file non contiene righe di dati.");
        }

        var header = table[0].Select(Normalize).ToList();
        int Find(string[] names) => header.FindIndex(h => names.Contains(h));
        var idColumn = Find(SessionIdHeaders);
        var titleColumn = Find(TitleHeaders);
        var lastColumn = Find(LastNameHeaders);
        var firstColumn = Find(FirstNameHeaders);
        var emailColumn = Find(EmailHeaders);
        var slideColumn = Find(SlideHeaders);
        var repoColumn = Find(RepositoryHeaders);

        var canIdentify = idColumn >= 0 || titleColumn >= 0 || emailColumn >= 0 || (lastColumn >= 0 && firstColumn >= 0);
        if (!canIdentify)
        {
            throw new InvalidOperationException(
                "Serve una colonna per riconoscere la sessione o lo speaker: Cognome e Nome, Email, Session Id oppure Titolo.");
        }

        if (slideColumn < 0 && repoColumn < 0)
        {
            throw new InvalidOperationException("Servono colonne con i link: Slide e/o Repo (link HTTPS).");
        }

        var rows = new List<MaterialsImportRow>();
        for (var index = 1; index < table.Count; index++)
        {
            string Cell(int column) => column >= 0 && column < table[index].Count ? table[index][column].Trim() : string.Empty;
            var slide = Cell(slideColumn);
            var repository = Cell(repoColumn);
            var id = Cell(idColumn);
            var title = Cell(titleColumn);
            var last = Cell(lastColumn);
            var first = Cell(firstColumn);
            var email = Cell(emailColumn).ToLowerInvariant();

            // Righe vuote, o senza alcun link da importare: si saltano.
            if (slide.Length == 0 && repository.Length == 0)
            {
                continue;
            }

            string? error = null;
            if (id.Length == 0 && title.Length == 0 && email.Length == 0 && (last.Length == 0 || first.Length == 0))
            {
                error = "Speaker o sessione non indicati.";
            }
            else if (!IsValidLink(slide) || !IsValidLink(repository))
            {
                error = "Link non valido: servono URL HTTPS (massimo 512 caratteri).";
            }

            rows.Add(new MaterialsImportRow(index + 1, id, title, last, first, email, slide, repository, error));
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("Il file non contiene righe con link da importare.");
        }

        return rows;
    }

    private static bool IsValidLink(string value) =>
        value.Length == 0 ||
        (value.Length <= 512 && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);

    public async Task<IReadOnlyList<MaterialsImportItem>> AnalyzeAsync(
        IReadOnlyList<MaterialsImportRow> rows,
        int eventId,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Analyzing a materials import.");
        await using var connection = await connections.OpenAsync(cancellationToken);
        return await AnalyzeAsync(connection, null, rows, eventId, cancellationToken);
    }

    /// <summary>Returns the number of sessions updated. Empty cells never erase existing links.</summary>
    public async Task<int> ImportAsync(
        IReadOnlyList<MaterialsImportRow> rows,
        int eventId,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Importing materials.");
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var items = await AnalyzeAsync(connection, transaction, rows, eventId, cancellationToken);
        var updated = 0;
        foreach (var item in items.Where(item => item.Update))
        {
            await using var command = new SqlCommand("""
                UPDATE community.EventSessions
                SET PresentationUrl = CASE WHEN @slide <> N'' THEN @slide ELSE PresentationUrl END,
                    RepositoryUrl = CASE WHEN @repository <> N'' THEN @repository ELSE RepositoryUrl END
                WHERE EventID = @eventId AND SessionizeSessionID = @sessionId AND IsActive = 1;
                """, connection, transaction);
            command.Parameters.Add("@slide", SqlDbType.NVarChar, 512).Value = item.Slide;
            command.Parameters.Add("@repository", SqlDbType.NVarChar, 512).Value = item.Repository;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            command.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = item.SessionId;
            updated += await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private sealed record SessionRow(string Id, string Title, string Slide, string Repository);

    private sealed record SessionSpeaker(string SessionId, string FirstName, string LastName, string Email);

    private static async Task<IReadOnlyList<MaterialsImportItem>> AnalyzeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        IReadOnlyList<MaterialsImportRow> rows,
        int eventId,
        CancellationToken cancellationToken)
    {
        var sessions = new List<SessionRow>();
        await using (var command = new SqlCommand("""
            SELECT SessionizeSessionID, Title, ISNULL(PresentationUrl, N''), ISNULL(RepositoryUrl, N'')
            FROM community.EventSessions WHERE EventID = @eventId AND IsActive = 1;
            """, connection, transaction))
        {
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(new SessionRow(reader.GetString(0), reader.GetString(1).Trim(), reader.GetString(2), reader.GetString(3)));
            }
        }

        // Speaker di ogni sessione (nome dal profilo Sessionize, email dall'account speaker collegato).
        var speakers = new List<SessionSpeaker>();
        await using (var command = new SqlCommand("""
            SELECT links.SessionizeSessionID, ISNULL(es.FirstName, N''), ISNULL(es.LastName, N''), ISNULL(sp.EMail, N'')
            FROM community.EventSessionSpeakers links
            INNER JOIN community.EventSpeakers es ON es.ID = links.EventSpeakerID
            LEFT JOIN community.Speakers sp ON sp.ID = es.SpeakerID
            WHERE links.EventID = @eventId;
            """, connection, transaction))
        {
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                speakers.Add(new SessionSpeaker(reader.GetString(0), reader.GetString(1).Trim(), reader.GetString(2).Trim(), reader.GetString(3).Trim()));
            }
        }

        var items = new List<MaterialsImportItem>();
        var targeted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var speakerName = $"{row.LastName} {row.FirstName}".Trim();
            var item = new MaterialsImportItem
            {
                RowNumber = row.RowNumber,
                Label = speakerName.Length > 0 ? speakerName : row.Email.Length > 0 ? row.Email : row.Title.Length > 0 ? row.Title : row.SessionId,
                SessionId = row.SessionId,
                Slide = row.Slide,
                Repository = row.Repository
            };
            items.Add(item);
            if (row.Error is not null)
            {
                item.Status = "Non valido";
                item.Note = row.Error;
                continue;
            }

            SessionRow? session = null;
            if (row.SessionId.Length > 0)
            {
                session = sessions.FirstOrDefault(s => string.Equals(s.Id, row.SessionId, StringComparison.OrdinalIgnoreCase));
            }

            if (session is null && row.Title.Length > 0)
            {
                var byTitle = sessions.Where(s => string.Equals(s.Title, row.Title, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (byTitle.Length > 1)
                {
                    item.Status = "Ambigua";
                    item.Note = "Più sessioni hanno questo titolo: usa il Session Id.";
                    continue;
                }

                session = byTitle.FirstOrDefault();
            }

            if (session is null && (row.Email.Length > 0 || (row.LastName.Length > 0 && row.FirstName.Length > 0)))
            {
                // Lo speaker si riconosce dall'email o, in mancanza, da cognome e nome.
                bool Matches(SessionSpeaker s) =>
                    (row.Email.Length > 0 && string.Equals(s.Email, row.Email, StringComparison.OrdinalIgnoreCase)) ||
                    (row.LastName.Length > 0 && row.FirstName.Length > 0 &&
                     string.Equals(s.LastName, row.LastName, StringComparison.OrdinalIgnoreCase) &&
                     string.Equals(s.FirstName, row.FirstName, StringComparison.OrdinalIgnoreCase));
                var candidates = speakers.Where(Matches).Select(s => s.SessionId).Distinct(StringComparer.Ordinal).ToArray();
                if (candidates.Length > 1)
                {
                    item.Status = "Più sessioni";
                    item.Note = "Lo speaker ha più sessioni in questo evento: aggiungi una colonna Session Id o Titolo.";
                    continue;
                }

                if (candidates.Length == 1)
                {
                    session = sessions.FirstOrDefault(s => s.Id == candidates[0]);
                }
            }

            if (session is null)
            {
                item.Status = "Non trovata";
                item.Note = "Nessuna sessione di questo evento corrisponde (sincronizza prima l'agenda, o controlla nome ed email).";
                continue;
            }

            item.SessionId = session.Id;
            item.SessionTitle = session.Title;
            if (!targeted.Add(session.Id))
            {
                item.Status = "Duplicata";
                item.Note = "La sessione compare già in un'altra riga del file.";
                continue;
            }

            var changes = new List<string>();
            if (row.Slide.Length > 0 && row.Slide != session.Slide) changes.Add("slide");
            if (row.Repository.Length > 0 && row.Repository != session.Repository) changes.Add("repo");
            item.Update = changes.Count > 0;
            item.Status = item.Update ? "Da aggiornare" : "Invariato";
            item.Note = item.Update ? "cambia: " + string.Join(", ", changes) : string.Empty;
        }

        return items;
    }
}
