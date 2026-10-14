using System.Data;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

public sealed record UserEventBadge(string EventName, DateTime EventDate, string Status, bool Attended);

public sealed record UserListItem(
    int Id, string FirstName, string LastName, string Email,
    bool AllowEventNotifications, bool ToBeConfirmed, int Registrations, int CheckIns,
    IReadOnlyList<UserEventBadge>? Events = null);

public sealed record UserRegistrationItem(
    int Id, int EventId, string EventName, DateTime EventDate, string Status,
    DateTimeOffset? CheckedInAt, bool IsEatingPizza, bool LactoseIntolerant);

public sealed record EventOption(int Id, string Name, DateTime Date);

/// <summary>Editable fields of one UserEventRegistration row.</summary>
public sealed class UserRegistrationEdit
{
    public int Id { get; set; }
    public string Status { get; set; } = "Registered";
    public bool Attended { get; set; }
    public bool IsEatingPizza { get; set; }
    public bool LactoseIntolerant { get; set; }
}

public sealed record UserListPage(IReadOnlyList<UserListItem> Items, int Total, IReadOnlyList<string> Letters);

public sealed class UserDraft
{
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool AllowEventNotifications { get; set; }
    /// <summary>Only when adding: send the "how to log in" email right after creating the user.</summary>
    public bool SendLoginEmail { get; set; }
}

/// <summary>Staff management of the participants registry (community.Users): search, add and edit.</summary>
public sealed class UserManagementService(ILogger<UserManagementService> logger, SqlConnectionFactory connections, SmtpEmailSender emailSender)
{
    public const int PageSize = 50;
    private const int MaximumLength = 64;

    // Iniziale per le schede: cognome, o email se manca; tutto ciò che non è A-Z va sotto "#".
    private const string LetterKey = "UPPER(LEFT(LTRIM(COALESCE(NULLIF(LTRIM(RTRIM(u.LastName)), N''), u.EMail)), 1))";

    public async Task<UserListPage> SearchAsync(
        string? search,
        string? letter,
        int page,
        CancellationToken cancellationToken = default)
    {
        search = (search ?? string.Empty).Trim();
        letter = (letter ?? string.Empty).Trim().ToUpperInvariant();
        var where = $"""
            u.IsDeleted = 0
            AND (@search = N'' OR u.EMail LIKE @like OR u.FirstName LIKE @like OR u.LastName LIKE @like)
            AND (@letter = N'' OR (@letter = N'#' AND {LetterKey} NOT LIKE N'[A-Z]') OR {LetterKey} = @letter)
            """;

        await using var connection = await connections.OpenAsync(cancellationToken);
        void AddFilters(SqlCommand command)
        {
            command.Parameters.Add("@search", SqlDbType.NVarChar, 128).Value = search;
            command.Parameters.Add("@like", SqlDbType.NVarChar, 140).Value = "%" + search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
            command.Parameters.Add("@letter", SqlDbType.NVarChar, 1).Value = letter.Length > 0 ? letter[..1] : string.Empty;
        }

        int total;
        await using (var count = new SqlCommand($"SELECT COUNT(*) FROM community.Users u WHERE {where};", connection))
        {
            AddFilters(count);
            total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<UserListItem>();
        await using (var list = new SqlCommand($"""
            SELECT u.ID, ISNULL(u.FirstName, N''), ISNULL(u.LastName, N''), u.EMail, u.AllowEventsNotifications, u.ToBeConfirmed,
                   (SELECT COUNT(*) FROM community.UserEventRegistration r WHERE r.UserID = u.ID AND r.Status <> N'Cancelled'),
                   (SELECT COUNT(*) FROM community.UserEventRegistration r WHERE r.UserID = u.ID AND r.CheckedInAt IS NOT NULL)
            FROM community.Users u
            WHERE {where}
            ORDER BY CASE WHEN NULLIF(LTRIM(RTRIM(u.LastName)), N'') IS NULL THEN 1 ELSE 0 END, u.LastName, u.FirstName, u.EMail
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;
            """, connection))
        {
            AddFilters(list);
            list.Parameters.Add("@skip", SqlDbType.Int).Value = Math.Max(0, page) * PageSize;
            list.Parameters.Add("@take", SqlDbType.Int).Value = PageSize;
            await using var reader = await list.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new UserListItem(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetBoolean(4), reader.GetBoolean(5), reader.GetInt32(6), reader.GetInt32(7)));
            }
        }

        if (items.Count > 0)
        {
            var badges = new Dictionary<int, List<UserEventBadge>>();
            await using var badgeCommand = new SqlCommand($"""
                SELECT r.UserID, e.Name, e.EventDate, r.Status, CASE WHEN r.CheckedInAt IS NULL THEN 0 ELSE 1 END
                FROM community.UserEventRegistration r
                INNER JOIN community.Events e ON e.ID = r.EventID
                WHERE r.UserID IN ({string.Join(",", items.Select(item => item.Id))}) AND r.Status <> N'Cancelled'
                ORDER BY e.EventDate;
                """, connection);
            await using (var reader = await badgeCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var userId = reader.GetInt32(0);
                    if (!badges.TryGetValue(userId, out var list))
                    {
                        badges[userId] = list = [];
                    }

                    list.Add(new UserEventBadge(reader.GetString(1), reader.GetDateTime(2), reader.GetString(3), reader.GetInt32(4) == 1));
                }
            }

            items = items.Select(item => item with { Events = badges.TryGetValue(item.Id, out var list) ? list : [] }).ToList();
        }

        var letters = new SortedSet<string>(StringComparer.Ordinal);
        await using (var lettersCommand = new SqlCommand($"""
            SELECT DISTINCT CASE WHEN {LetterKey} LIKE N'[A-Z]' THEN {LetterKey} ELSE N'#' END
            FROM community.Users u WHERE u.IsDeleted = 0;
            """, connection))
        {
            await using var reader = await lettersCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                letters.Add(reader.GetString(0));
            }
        }

        // "#" in fondo.
        var ordered = letters.Where(l => l != "#").Concat(letters.Contains("#") ? ["#"] : []).ToList();
        return new UserListPage(items, total, ordered);
    }

    /// <summary>Adds a user. Throws <see cref="InvalidOperationException"/> with a user-facing message on invalid input.</summary>
    public async Task<int> CreateAsync(UserDraft draft, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Creating a user.");
        var (firstName, lastName, email) = Validate(draft);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await EnsureEmailFreeAsync(connection, transaction, email, null, cancellationToken);

        int userId;
        await using (var insert = new SqlCommand("""
            INSERT INTO community.Users (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
            VALUES (NULLIF(@firstName, N''), NULLIF(@lastName, N''), @email, NULL, @allow, 0, 0);
            SELECT CONVERT(int, SCOPE_IDENTITY());
            """, connection, transaction))
        {
            AddParameters(insert, firstName, lastName, email, draft.AllowEventNotifications);
            userId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
        }

        if (draft.AllowEventNotifications)
        {
            await AddConsentAsync(connection, transaction, userId, true, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return userId;
    }

    /// <summary>Sends the "how to log in" email to the user. Returns false when the user does not exist.</summary>
    public async Task<bool> SendLoginEmailAsync(int userId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Sending the login email to user {UserId}.", userId);
        string email;
        string firstName;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var command = new SqlCommand(
            "SELECT EMail, ISNULL(FirstName, N'') FROM community.Users WHERE ID = @id AND IsDeleted = 0;", connection))
        {
            command.Parameters.Add("@id", SqlDbType.Int).Value = userId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            email = reader.GetString(0);
            firstName = reader.GetString(1);
        }

        await emailSender.SendLoginInvitationAsync(email, firstName, cancellationToken);
        return true;
    }

    public async Task<bool> UpdateAsync(int userId, UserDraft draft, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Updating user {UserId}.", userId);
        var (firstName, lastName, email) = Validate(draft);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await EnsureEmailFreeAsync(connection, transaction, email, userId, cancellationToken);

        bool previous;
        await using (var read = new SqlCommand(
            "SELECT AllowEventsNotifications FROM community.Users WITH (UPDLOCK, HOLDLOCK) WHERE ID = @id AND IsDeleted = 0;",
            connection, transaction))
        {
            read.Parameters.Add("@id", SqlDbType.Int).Value = userId;
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                return false;
            }

            previous = Convert.ToBoolean(value);
        }

        await using (var update = new SqlCommand("""
            UPDATE community.Users
            SET FirstName = NULLIF(@firstName, N''), LastName = NULLIF(@lastName, N''), EMail = @email,
                AllowEventsNotifications = @allow
            WHERE ID = @id;
            """, connection, transaction))
        {
            AddParameters(update, firstName, lastName, email, draft.AllowEventNotifications);
            update.Parameters.Add("@id", SqlDbType.Int).Value = userId;
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (previous != draft.AllowEventNotifications)
        {
            await AddConsentAsync(connection, transaction, userId, draft.AllowEventNotifications, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public static readonly string[] Statuses = ["Registered", "AwaitingConfirmation", "Pending", "Cancelled"];

    public async Task<IReadOnlyList<UserRegistrationItem>> GetRegistrationsAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT r.ID, r.EventID, e.Name, e.EventDate, r.Status, r.CheckedInAt, r.IsEatingPizza, r.LactoseIntolerant
            FROM community.UserEventRegistration r
            INNER JOIN community.Events e ON e.ID = r.EventID
            WHERE r.UserID = @userId
            ORDER BY e.EventDate;
            """, connection);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<UserRegistrationItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new UserRegistrationItem(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), reader.GetDateTime(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5), reader.GetBoolean(6), reader.GetBoolean(7)));
        }

        return list;
    }

    public async Task<IReadOnlyList<EventOption>> GetEventsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT ID, Name, EventDate FROM community.Events ORDER BY EventDate DESC;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var list = new List<EventOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new EventOption(reader.GetInt32(0), reader.GetString(1), reader.GetDateTime(2)));
        }

        return list;
    }

    /// <summary>Saves status, attendance and meal preferences of a registration.</summary>
    public async Task<bool> SaveRegistrationAsync(int userId, UserRegistrationEdit edit, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Saving a registration of user {UserId}.", userId);
        if (!Statuses.Contains(edit.Status))
        {
            throw new InvalidOperationException("Stato dell'iscrizione non valido.");
        }

        if (edit.Attended && edit.Status != "Registered")
        {
            throw new InvalidOperationException("Per segnare la presenza l'iscrizione deve essere nello stato «Iscritto».");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.UserEventRegistration
            SET Status = @status,
                CheckedInAt = CASE WHEN @attended = 1 THEN COALESCE(CheckedInAt, community.LocalNow()) ELSE NULL END,
                IsEatingPizza = @pizza,
                LactoseIntolerant = CASE WHEN @pizza = 1 THEN @lactose ELSE 0 END
            WHERE ID = @id AND UserID = @userId;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = edit.Id;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@status", SqlDbType.NVarChar, 50).Value = edit.Status;
        command.Parameters.Add("@attended", SqlDbType.Bit).Value = edit.Attended;
        command.Parameters.Add("@pizza", SqlDbType.Bit).Value = edit.IsEatingPizza;
        command.Parameters.Add("@lactose", SqlDbType.Bit).Value = edit.LactoseIntolerant;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task AddRegistrationAsync(int userId, int eventId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Adding user {UserId} to event {EventId}.", userId, eventId);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var exists = new SqlCommand(
            "SELECT COUNT(*) FROM community.UserEventRegistration WITH (UPDLOCK, HOLDLOCK) WHERE UserID = @userId AND EventID = @eventId;",
            connection, transaction))
        {
            exists.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            exists.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                throw new InvalidOperationException("L'utente è già iscritto a questo evento.");
            }
        }

        await using (var insert = new SqlCommand("""
            INSERT INTO community.UserEventRegistration (UserID, EventID, RegistrationDate, Status, IsEatingPizza, LactoseIntolerant)
            SELECT @userId, ID, community.LocalNowDateTime(), N'Registered', 0, 0 FROM community.Events WHERE ID = @eventId;
            SELECT @@ROWCOUNT;
            """, connection, transaction))
        {
            insert.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            insert.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            if (Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                throw new InvalidOperationException("L'evento non esiste.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteRegistrationAsync(int userId, int registrationId, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("Deleting registration {RegistrationId} of user {UserId}.", registrationId, userId);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using var command = new SqlCommand("""
            DECLARE @owned int = (SELECT COUNT(*) FROM community.UserEventRegistration WHERE ID = @id AND UserID = @userId);
            IF @owned = 1
            BEGIN
                DELETE FROM community.EventRegistrationConfirmations WHERE RegistrationID = @id;
                DELETE FROM community.UserEventRegistration WHERE ID = @id AND UserID = @userId;
            END;
            SELECT @owned;
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = registrationId;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        var removed = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        await transaction.CommitAsync(cancellationToken);
        return removed;
    }

    private static (string FirstName, string LastName, string Email) Validate(UserDraft draft)
    {
        var firstName = (draft.FirstName ?? string.Empty).Trim();
        var lastName = (draft.LastName ?? string.Empty).Trim();
        var email = (draft.Email ?? string.Empty).Trim().ToLowerInvariant();
        if (firstName.Length > MaximumLength || lastName.Length > MaximumLength || email.Length > MaximumLength)
        {
            throw new InvalidOperationException("Nome, cognome ed email possono avere al massimo 64 caratteri.");
        }

        if (email.Length == 0 ||
            !(System.Net.Mail.MailAddress.TryCreate(email, out var address) &&
              string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("L'indirizzo email non è valido.");
        }

        return (firstName, lastName, email);
    }

    private static async Task EnsureEmailFreeAsync(
        SqlConnection connection, SqlTransaction transaction, string email, int? exceptUserId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM community.Users WITH (UPDLOCK, HOLDLOCK) WHERE LOWER(LTRIM(RTRIM(EMail))) = @email AND (@id IS NULL OR ID <> @id);",
            connection, transaction);
        command.Parameters.Add("@email", SqlDbType.NVarChar, MaximumLength).Value = email;
        command.Parameters.Add("@id", SqlDbType.Int).Value = exceptUserId is int id ? id : DBNull.Value;
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            throw new InvalidOperationException("Esiste già un utente con questa email.");
        }
    }

    private static void AddParameters(SqlCommand command, string firstName, string lastName, string email, bool allow)
    {
        command.Parameters.Add("@firstName", SqlDbType.NVarChar, MaximumLength).Value = firstName;
        command.Parameters.Add("@lastName", SqlDbType.NVarChar, MaximumLength).Value = lastName;
        command.Parameters.Add("@email", SqlDbType.NVarChar, MaximumLength).Value = email;
        command.Parameters.Add("@allow", SqlDbType.Bit).Value = allow;
    }

    private static async Task AddConsentAsync(
        SqlConnection connection, SqlTransaction transaction, int userId, bool granted, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO community.UserConsentHistory (UserID, ConsentType, IsGranted, PolicyVersion, Source)
            VALUES (@userId, N'EventNotifications', @granted, N'1', N'Admin');
            """, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@granted", SqlDbType.Bit).Value = granted;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
