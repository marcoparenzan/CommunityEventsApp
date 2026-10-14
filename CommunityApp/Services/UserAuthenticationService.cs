using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace CommunityApp.Services;

public sealed record AccountProfile(string FirstName, string LastName, string Email, bool AllowEventNotifications);

public enum AccountDeletionOutcome { NotFound, Deleted, Anonymized }

public sealed class UserAuthenticationService(ILogger<UserAuthenticationService> logger, 
    SqlConnectionFactory connections,
    SmtpEmailSender emailSender,
    IConfiguration configuration)
{
    private const int MaximumPinAttempts = 5;
    private const int ConsentChangedLogTypeId = 1001;
    private const string ConsentType = "EventNotifications";
    private const string PolicyVersion = "1";

    public async Task RequestPinAsync(
        string firstName,
        string lastName,
        string? email,
        bool allowEventNotifications,
        string? loginBaseUrl = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("A login PIN was requested.");
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(firstName) || firstName.Trim().Length > 64 ||
            string.IsNullOrWhiteSpace(lastName) || lastName.Trim().Length > 64)
        {
            throw new ArgumentException("Nome e cognome sono obbligatori (massimo 64 caratteri).");
        }

        var pin = CreatePin();
        var pinHash = HashPin(pin);
        int userId;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            await using (var command = new SqlCommand("""
                SELECT ID
                FROM community.Users WITH (UPDLOCK, HOLDLOCK)
                WHERE EMail = @email AND IsDeleted = 0;
                """, connection, transaction))
            {
                command.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    userId = reader.GetInt32(0);
                    await reader.CloseAsync();
                }
                else
                {
                    await reader.CloseAsync();
                    await using var insert = new SqlCommand("""
                        INSERT INTO community.Users (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
                        VALUES (NULL, NULL, @email, NULL, 0, 0, 1);
                        SELECT CONVERT(int, SCOPE_IDENTITY());
                        """, connection, transaction);
                    insert.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
                    userId = Convert.ToInt32(await insert.ExecuteScalarAsync(cancellationToken));
                }
            }

            await using (var invalidate = new SqlCommand("""
                UPDATE community.EmailPinChallenges SET ConsumedAt = community.LocalNow()
                WHERE UserID = @userId AND ConsumedAt IS NULL;
                INSERT INTO community.EmailPinChallenges
                    (UserID, PinHash, FirstName, LastName, AllowEventNotifications)
                VALUES (@userId, @pinHash, @firstName, @lastName, @allowNotifications);
                """, connection, transaction))
            {
                invalidate.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
                invalidate.Parameters.Add("@pinHash", SqlDbType.Binary, 32).Value = pinHash;
                invalidate.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value = firstName.Trim();
                invalidate.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value = lastName.Trim();
                invalidate.Parameters.Add("@allowNotifications", SqlDbType.Bit).Value = allowEventNotifications;
                await invalidate.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        var loginUrl = string.IsNullOrWhiteSpace(loginBaseUrl)
            ? null
            : $"{loginBaseUrl.TrimEnd('/')}/account/confirm?email={Uri.EscapeDataString(normalizedEmail)}&pin={pin}";
        await emailSender.SendPinAsync(normalizedEmail, pin, loginUrl, cancellationToken);
    }

    public async Task<ClaimsPrincipal?> VerifyPinAsync(
        string? email,
        string? pin,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("A login PIN is being verified.");
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 16 || pin.Any(character => character is < '0' or > '9'))
        {
            logger.LogWarning("Login refused: the PIN has an invalid format.");
            return null;
        }

        var requestedHash = HashPin(pin);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int? userId = null;
        long challengeId = 0;
        byte[]? expectedHash = null;
        byte attempts = 0;
        string firstName = string.Empty;
        string lastName = string.Empty;
        bool allowNotifications = false;
        await using (var command = new SqlCommand("""
            SELECT TOP (1) c.ID, c.UserID, c.PinHash, c.AttemptCount,
                   c.FirstName, c.LastName, c.AllowEventNotifications
            FROM community.EmailPinChallenges c WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN community.Users u ON u.ID = c.UserID
            WHERE u.EMail = @email AND u.IsDeleted = 0 AND c.ConsumedAt IS NULL
            ORDER BY c.CreatedAt DESC, c.ID DESC;
            """, connection, transaction))
        {
            command.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                challengeId = reader.GetInt64(0);
                userId = reader.GetInt32(1);
                expectedHash = (byte[])reader[2];
                attempts = reader.GetByte(3);
                firstName = reader.GetString(4);
                lastName = reader.GetString(5);
                allowNotifications = reader.GetBoolean(6);
            }
        }

        if (userId is null || expectedHash is null || attempts >= MaximumPinAttempts ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, requestedHash))
        {
            if (challengeId != 0 && attempts < MaximumPinAttempts)
            {
                await using var increment = new SqlCommand(
                    "UPDATE community.EmailPinChallenges SET AttemptCount = AttemptCount + 1 WHERE ID = @id;",
                    connection,
                    transaction);
                increment.Parameters.Add("@id", SqlDbType.BigInt).Value = challengeId;
                await increment.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogWarning(
                "Login refused: wrong PIN, no PIN requested or too many attempts (user {UserId}, challenge {ChallengeId}, attempts {Attempts}).",
                userId, challengeId, attempts);
            return null;
        }

        bool previousConsent;
        bool hasConsentHistory;
        await using (var updateUser = new SqlCommand("""
            SELECT u.AllowEventsNotifications,
                   CONVERT(bit, CASE WHEN EXISTS (
                       SELECT 1 FROM community.UserConsentHistory h
                       WHERE h.UserID = u.ID AND h.ConsentType = @consentType) THEN 1 ELSE 0 END)
            FROM community.Users u WITH (UPDLOCK, HOLDLOCK)
            WHERE u.ID = @userId AND u.IsDeleted = 0;
            """, connection, transaction))
        {
            updateUser.Parameters.Add("@consentType", SqlDbType.NVarChar, 50).Value = ConsentType;
            updateUser.Parameters.Add("@userId", SqlDbType.Int).Value = userId.Value;
            await using var reader = await updateUser.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("L'account collegato al PIN non esiste più.");
            }

            previousConsent = reader.GetBoolean(0);
            hasConsentHistory = reader.GetBoolean(1);
        }
        await using (var updateUser = new SqlCommand("""
            UPDATE community.Users
            SET FirstName = @firstName, LastName = @lastName,
                AllowEventsNotifications = @allowNotifications, ToBeConfirmed = 0
            WHERE ID = @userId;
            """, connection, transaction))
        {
            updateUser.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value = firstName;
            updateUser.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value = lastName;
            updateUser.Parameters.Add("@allowNotifications", SqlDbType.Bit).Value = allowNotifications;
            updateUser.Parameters.Add("@userId", SqlDbType.Int).Value = userId.Value;
            await updateUser.ExecuteNonQueryAsync(cancellationToken);
        }
        if (!hasConsentHistory || previousConsent != allowNotifications)
        {
            await AddConsentHistoryAsync(connection, transaction, userId.Value, allowNotifications, cancellationToken);
        }

        bool isSuperUser;
        bool canCheckIn;
        await using (var speakerPermissions = new SqlCommand("""
            SELECT CONVERT(bit, CASE WHEN EXISTS (
                       SELECT 1 FROM community.Speakers s
                       WHERE LOWER(LTRIM(RTRIM(s.EMail))) = @email AND s.IsSuperUser = 1
                   ) THEN 1 ELSE 0 END),
                   CONVERT(bit, CASE WHEN EXISTS (
                       SELECT 1 FROM community.Speakers s
                       WHERE LOWER(LTRIM(RTRIM(s.EMail))) = @email
                         AND (s.CanCheckIn = 1 OR s.IsSuperUser = 1)
                   ) THEN 1 ELSE 0 END);
            """, connection, transaction))
        {
            speakerPermissions.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
            await using var reader = await speakerPermissions.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Non è stato possibile leggere i permessi dell'account.");
            }

            isSuperUser = reader.GetBoolean(0);
            canCheckIn = reader.GetBoolean(1);
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Login completed for user {UserId} (superuser={SuperUser}, checkin={CanCheckIn}).", userId, isSuperUser, canCheckIn);
        var claims = new[]
        {
            new Claim("account_type", "participant"),
            new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            new Claim(ClaimTypes.Name, $"{firstName} {lastName}".Trim()),
            new Claim(ClaimTypes.Email, normalizedEmail),
            new Claim("is_super_user", isSuperUser ? "true" : "false"),
            new Claim("can_check_in", canCheckIn ? "true" : "false")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "EventsCookie"));
    }

    /// <summary>False when the account no longer exists or was deleted (used to end long sign-ins).</summary>
    public async Task<bool> IsActiveAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT COUNT(*) FROM community.Users WHERE ID = @id AND IsDeleted = 0;", connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = userId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    /// <summary>Profile data shown in the user's own page.</summary>
    public async Task<AccountProfile?> GetProfileAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT ISNULL(FirstName, N''), ISNULL(LastName, N''), EMail, AllowEventsNotifications
            FROM community.Users WHERE ID = @userId AND IsDeleted = 0;
            """, connection);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AccountProfile(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }

    /// <summary>Turns the event communications on or off for the user and records the consent change.</summary>
    public async Task<bool> SetEventNotificationsAsync(int userId, bool allow, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("User {UserId} sets event notifications to {Allow}.", userId, allow);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        bool previous;
        await using (var read = new SqlCommand(
            "SELECT AllowEventsNotifications FROM community.Users WITH (UPDLOCK, HOLDLOCK) WHERE ID = @userId AND IsDeleted = 0;",
            connection, transaction))
        {
            read.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            var value = await read.ExecuteScalarAsync(cancellationToken);
            if (value is null)
            {
                return false;
            }

            previous = Convert.ToBoolean(value);
        }

        if (previous != allow)
        {
            await using (var update = new SqlCommand(
                "UPDATE community.Users SET AllowEventsNotifications = @allow WHERE ID = @userId;", connection, transaction))
            {
                update.Parameters.Add("@allow", SqlDbType.Bit).Value = allow;
                update.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await AddConsentHistoryAsync(connection, transaction, userId, allow, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Deletes the account. Who never took part in an event (no check-in) is removed completely;
    /// who already took part stays in the registry with the IsDeleted flag and anonymized personal data,
    /// so the participation records keep their relation.
    /// </summary>
    public async Task<AccountDeletionOutcome> DeleteAccountAsync(int userId, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("User {UserId} requested the deletion of their account.", userId);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        bool participated;
        await using (var check = new SqlCommand("""
            SELECT CASE WHEN NOT EXISTS (SELECT 1 FROM community.Users WITH (UPDLOCK, HOLDLOCK) WHERE ID = @userId AND IsDeleted = 0) THEN -1
                        WHEN EXISTS (SELECT 1 FROM community.UserEventRegistration WHERE UserID = @userId AND CheckedInAt IS NOT NULL) THEN 1
                        ELSE 0 END;
            """, connection, transaction))
        {
            check.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            var state = Convert.ToInt32(await check.ExecuteScalarAsync(cancellationToken));
            if (state < 0)
            {
                return AccountDeletionOutcome.NotFound;
            }

            participated = state == 1;
        }

        // Dati senza valore storico: sempre rimossi.
        await using (var cleanup = new SqlCommand("""
            DELETE FROM community.EventRegistrationConfirmations WHERE UserID = @userId;
            DELETE FROM community.EmailPinChallenges WHERE UserID = @userId;
            """, connection, transaction))
        {
            cleanup.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!participated)
        {
            await using var remove = new SqlCommand("""
                DELETE FROM community.UserEventRegistration WHERE UserID = @userId;
                DELETE FROM community.UserConsentHistory WHERE UserID = @userId;
                DELETE FROM community.UserLogs WHERE IdUser = @userId;
                DELETE FROM community.Users WHERE ID = @userId;
                """, connection, transaction);
            remove.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            await remove.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AccountDeletionOutcome.Deleted;
        }

        await using (var anonymize = new SqlCommand("""
            UPDATE community.UserEventRegistration SET Status = N'Cancelled'
            WHERE UserID = @userId AND CheckedInAt IS NULL AND Status <> N'Cancelled';
            UPDATE community.Users
            SET IsDeleted = 1, DeletedAt = community.LocalNow(), RequestCancellation = 1,
                FirstName = NULL, LastName = NULL, LastPin = NULL,
                EMail = N'deleted-' + CONVERT(nvarchar(20), ID) + N'@deleted.invalid',
                AllowEventsNotifications = 0, ToBeConfirmed = 0
            WHERE ID = @userId;
            """, connection, transaction))
        {
            anonymize.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            await anonymize.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return AccountDeletionOutcome.Anonymized;
    }

    /// <summary>Updates the meal preferences of an existing, not cancelled registration of the user.</summary>
    public async Task<bool> UpdatePreferencesAsync(
        int userId,
        int eventId,
        bool isEatingPizza,
        bool lactoseIntolerant,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Updating the event preferences of a registration.");
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE registration
            SET IsEatingPizza = @isEatingPizza,
                LactoseIntolerant = CASE WHEN @isEatingPizza = 1 THEN @lactoseIntolerant ELSE 0 END
            FROM community.UserEventRegistration registration
            INNER JOIN community.Events eventInfo ON eventInfo.ID = registration.EventID
            WHERE registration.UserID = @userId
              AND registration.EventID = @eventId
              AND registration.Status <> N'Cancelled'
              AND eventInfo.IsLocked = 0
              AND eventInfo.IsCancelled = 0
              AND CONVERT(date, eventInfo.EventDate) >= CONVERT(date, community.LocalNowDateTime());
            """, connection);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@isEatingPizza", SqlDbType.Bit).Value = isEatingPizza;
        command.Parameters.Add("@lactoseIntolerant", SqlDbType.Bit).Value = lactoseIntolerant;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ParticipantEventRegistration>> GetRegistrationsAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT e.ID, e.Year, e.Moniker, e.Name, e.EventDate, r.Status, r.RegistrationDate,
                   r.CheckedInAt, r.IsEatingPizza, r.LactoseIntolerant
            FROM community.UserEventRegistration r
            INNER JOIN community.Events e ON e.ID = r.EventID
            WHERE r.UserID = @userId AND r.Status <> N'Cancelled'
            ORDER BY e.EventDate DESC;
            """, connection);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var registrations = new List<ParticipantEventRegistration>();
        while (await reader.ReadAsync(cancellationToken))
        {
            registrations.Add(new ParticipantEventRegistration
            {
                EventId = reader.GetInt32(0),
                EventYear = reader.GetInt32(1),
                EventMoniker = reader.GetString(2),
                EventName = reader.GetString(3),
                EventDate = DateOnly.FromDateTime(reader.GetDateTime(4)),
                Status = reader.GetString(5),
                RegisteredAt = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
                CheckedInAt = reader.IsDBNull(7) ? null : reader.GetDateTimeOffset(7),
                IsEatingPizza = reader.GetBoolean(8),
                LactoseIntolerant = reader.GetBoolean(9)
            });
        }

        return registrations;
    }

    public async Task<bool> CancelEventRegistrationAsync(
        int userId,
        int eventId,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Cancelling an event registration.");
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE registration
            SET Status = N'Cancelled'
            FROM community.UserEventRegistration registration
            INNER JOIN community.Events eventInfo ON eventInfo.ID = registration.EventID
            WHERE registration.UserID = @userId
              AND registration.EventID = @eventId
              AND registration.Status <> N'Cancelled'
              AND registration.CheckedInAt IS NULL
              AND eventInfo.EventDate >= CONVERT(date, community.LocalNowDateTime());
            """, connection);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task AddConsentHistoryAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int userId,
        bool isGranted,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            INSERT INTO community.UserConsentHistory (UserID, ConsentType, IsGranted, PolicyVersion, Source)
            VALUES (@userId, @consentType, @isGranted, @policyVersion, N'Web');
            """, connection, transaction);
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@consentType", SqlDbType.NVarChar, 50).Value = ConsentType;
        command.Parameters.Add("@isGranted", SqlDbType.Bit).Value = isGranted;
        command.Parameters.Add("@policyVersion", SqlDbType.NVarChar, 50).Value = PolicyVersion;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var legacyLog = new SqlCommand("""
            INSERT INTO community.UserLogs (IdUser, LogTypeId, Description, ValueString, Timstamp)
            VALUES (@userId, @logTypeId, @description, @value, community.LocalNow());
            """, connection, transaction);
        legacyLog.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        legacyLog.Parameters.Add("@logTypeId", SqlDbType.Int).Value = ConsentChangedLogTypeId;
        legacyLog.Parameters.Add("@description", SqlDbType.NVarChar, 256).Value = "Consenso comunicazioni eventi aggiornato";
        legacyLog.Parameters.Add("@value", SqlDbType.NVarChar, 64).Value = isGranted ? "Granted" : "Withdrawn";
        await legacyLog.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Indirizzo email non valido.");
        }

        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length > 64)
        {
            throw new ArgumentException("Indirizzo email non valido.");
        }

        try
        {
            return string.Equals(new MailAddress(normalized).Address, normalized, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : throw new ArgumentException("Indirizzo email non valido.");
        }
        catch (FormatException)
        {
            throw new ArgumentException("Indirizzo email non valido.");
        }
    }

    private byte[] HashPin(string pin)
    {
        var key = configuration["Authentication:PinHashKey"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            throw new InvalidOperationException("Authentication:PinHashKey deve contenere almeno 32 byte casuali.");
        }

        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(pin));
    }

    private static string CreatePin()
    {
        Span<char> pin = stackalloc char[16];
        for (var index = 0; index < pin.Length; index++)
        {
            pin[index] = (char)('0' + RandomNumberGenerator.GetInt32(0, 10));
        }

        return new string(pin);
    }
}
