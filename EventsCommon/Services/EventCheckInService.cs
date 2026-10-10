using System.Data;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace EventsCommon.Services;

public sealed class EventCheckInService(
    SqlConnectionFactory connections,
    SmtpEmailSender emailSender,
    IConfiguration configuration,
    IWebHostEnvironment environment,
    ILogger<EventCheckInService> logger)
{
    public Task<EventCheckInResponse> RequestSelfCheckInAsync(
        Guid eventToken,
        string? email,
        bool isEatingPizza,
        bool lactoseIntolerant,
        CancellationToken cancellationToken = default) =>
        RequestConfirmationAsync(
            eventToken,
            null,
            email,
            isEatingPizza,
            lactoseIntolerant,
            null,
            null,
            "CheckIn",
            cancellationToken);

    public Task<EventCheckInResponse> AddParticipantAsync(
        int eventId,
        string? email,
        bool isEatingPizza,
        bool lactoseIntolerant,
        string? firstName = null,
        string? lastName = null,
        CancellationToken cancellationToken = default) =>
        RequestConfirmationAsync(
            null,
            eventId,
            email,
            isEatingPizza,
            lactoseIntolerant,
            firstName,
            lastName,
            "Registration",
            cancellationToken);

    public async Task<EventCheckInResponse> AddConfirmedParticipantAsync(
        int eventId,
        string? email,
        bool isEatingPizza,
        bool lactoseIntolerant,
        string? firstName,
        string? lastName,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = NormalizeEmail(email);
        firstName = NormalizeName(firstName);
        lastName = NormalizeName(lastName);

        int userId;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken))
        {
            await using (var upsert = new SqlCommand("""
                DECLARE @userId int = (SELECT ID FROM community.Users WITH (UPDLOCK, HOLDLOCK) WHERE EMail = @email AND IsDeleted = 0);
                IF @userId IS NULL
                BEGIN
                    INSERT INTO community.Users
                        (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
                    VALUES (@firstName, @lastName, @email, NULL, 0, 0, 0);
                    SET @userId = CONVERT(int, SCOPE_IDENTITY());
                END
                ELSE
                    UPDATE community.Users
                    SET FirstName = COALESCE(@firstName, FirstName),
                        LastName = COALESCE(@lastName, LastName),
                        ToBeConfirmed = 0
                    WHERE ID = @userId;
                SELECT @userId;
                """, connection, transaction))
            {
                upsert.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
                upsert.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value =
                    firstName is null ? DBNull.Value : firstName;
                upsert.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value =
                    lastName is null ? DBNull.Value : lastName;
                userId = Convert.ToInt32(await upsert.ExecuteScalarAsync(cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
        }

        return await RegisterExistingAsync(eventId, userId, isEatingPizza, lactoseIntolerant, cancellationToken);
    }

    public async Task<EventCheckInResponse> SendConfirmationAsync(
        int eventId,
        int registrationId,
        CancellationToken cancellationToken = default)
    {
        string email;
        bool isEatingPizza;
        bool lactoseIntolerant;
        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var command = new SqlCommand("""
            SELECT users.EMail, registration.IsEatingPizza, registration.LactoseIntolerant
            FROM community.UserEventRegistration registration
            INNER JOIN community.Users users ON users.ID = registration.UserID
            WHERE registration.ID = @registrationId
              AND users.IsDeleted = 0
              AND registration.EventID = @eventId
              AND registration.Status <> N'Cancelled'
              AND registration.CheckedInAt IS NULL;
            """, connection))
        {
            command.Parameters.Add("@registrationId", SqlDbType.Int).Value = registrationId;
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new(false, "Partecipante non trovato o check-in già effettuato.");
            }

            email = reader.GetString(0);
            isEatingPizza = reader.GetBoolean(1);
            lactoseIntolerant = reader.GetBoolean(2);
        }

        return await AddParticipantAsync(eventId, email, isEatingPizza, lactoseIntolerant, null, null, cancellationToken);
    }

    public async Task<EventCheckInResponse> ConfirmAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128)
        {
            return new(false, "Il link di conferma non è valido o è scaduto.");
        }

        byte[] tokenHash;
        try
        {
            tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        }
        catch (ArgumentException)
        {
            return new(false, "Il link di conferma non è valido o è scaduto.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        long confirmationId;
        int registrationId;
        int userId;
        string purpose;
        await using (var command = new SqlCommand("""
            SELECT TOP (1) confirmation.ID, confirmation.RegistrationID, confirmation.UserID,
                   confirmation.Purpose, registration.Status
            FROM community.EventRegistrationConfirmations confirmation WITH (UPDLOCK, HOLDLOCK)
            INNER JOIN community.UserEventRegistration registration
                ON registration.ID = confirmation.RegistrationID
            INNER JOIN community.Events eventInfo ON eventInfo.ID = confirmation.EventID
            WHERE confirmation.TokenHash = @tokenHash
              AND confirmation.ConsumedAt IS NULL
              AND confirmation.ExpiresAt > community.LocalNow()
              AND eventInfo.IsPublished = 1
              AND eventInfo.IsCancelled = 0
              AND CONVERT(date, eventInfo.EventDate) >= CONVERT(date, community.LocalNowDateTime())
              AND (confirmation.Purpose <> N'CheckIn'
                   OR (eventInfo.IsCurrent = 1
                       AND CONVERT(date, eventInfo.EventDate) = CONVERT(date, community.LocalNowDateTime())));
            """, connection, transaction))
        {
            command.Parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = tokenHash;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) ||
                string.Equals(reader.GetString(4), "Cancelled", StringComparison.OrdinalIgnoreCase))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "Il link di conferma non è valido o è scaduto.");
            }

            confirmationId = reader.GetInt64(0);
            registrationId = reader.GetInt32(1);
            userId = reader.GetInt32(2);
            purpose = reader.GetString(3);
        }

        await using (var consume = new SqlCommand("""
            UPDATE community.EventRegistrationConfirmations
            SET ConsumedAt = community.LocalNow()
            WHERE ID = @confirmationId AND ConsumedAt IS NULL;
            UPDATE community.Users
            SET ToBeConfirmed = 0
            WHERE ID = @userId;
            UPDATE community.UserEventRegistration
            SET Status = N'Registered',
                CheckedInAt = CASE
                    WHEN @purpose = N'CheckIn' THEN COALESCE(CheckedInAt, community.LocalNow())
                    ELSE CheckedInAt
                END
            WHERE ID = @registrationId AND Status <> N'Cancelled';
            SELECT @@ROWCOUNT;
            """, connection, transaction))
        {
            consume.Parameters.Add("@confirmationId", SqlDbType.BigInt).Value = confirmationId;
            consume.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            consume.Parameters.Add("@registrationId", SqlDbType.Int).Value = registrationId;
            consume.Parameters.Add("@purpose", SqlDbType.NVarChar, 16).Value = purpose;
            if (Convert.ToInt32(await consume.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "L'iscrizione è stata annullata e non può essere confermata.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new(true, purpose == "CheckIn"
            ? "Check-in completato. Ti aspettiamo all'evento!"
            : "Indirizzo email e iscrizione confermati.");
    }

    public async Task<IReadOnlyList<EventAttendeeResponse>> GetAttendeesAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT registration.ID, users.EMail, users.FirstName, users.LastName, registration.Status,
                   registration.IsEatingPizza, registration.LactoseIntolerant, registration.CheckedInAt
            FROM community.UserEventRegistration registration
            INNER JOIN community.Users users ON users.ID = registration.UserID
            WHERE registration.EventID = @eventId
              AND registration.Status <> N'Cancelled'
            ORDER BY registration.CheckedInAt DESC, users.EMail;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var attendees = new List<EventAttendeeResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            attendees.Add(new EventAttendeeResponse(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetBoolean(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetDateTimeOffset(7)));
        }

        return attendees;
    }

    public async Task<int> GetCheckedInCountAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM community.UserEventRegistration
            WHERE EventID = @eventId AND Status = N'Registered' AND CheckedInAt IS NOT NULL;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<IReadOnlyList<PersonSearchResponse>> SearchPeopleAsync(
        int eventId,
        string? query,
        CancellationToken cancellationToken = default)
    {
        var terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(4)
            .ToArray();
        if (terms.Length == 0 || terms.Sum(term => term.Length) < 2)
        {
            return [];
        }

        var filters = new StringBuilder();
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand { Connection = connection };
        for (var index = 0; index < terms.Length; index++)
        {
            filters.Append($" AND (users.FirstName LIKE @t{index} ESCAPE N'!' OR users.LastName LIKE @t{index} ESCAPE N'!' OR users.EMail LIKE @t{index} ESCAPE N'!')");
            var escaped = terms[index].Replace("!", "!!").Replace("%", "!%").Replace("_", "!_").Replace("[", "![");
            command.Parameters.Add($"@t{index}", SqlDbType.NVarChar, 140).Value = $"%{escaped}%";
        }

        command.CommandText = $"""
            SELECT TOP (20) users.ID, users.EMail, users.FirstName, users.LastName, users.ToBeConfirmed,
                   CASE WHEN EXISTS (
                       SELECT 1 FROM community.UserEventRegistration registration
                       WHERE registration.UserID = users.ID AND registration.EventID = @eventId
                         AND registration.Status <> N'Cancelled') THEN 1 ELSE 0 END
            FROM community.Users users
            WHERE users.IsDeleted = 0{filters}
            ORDER BY users.LastName, users.FirstName, users.EMail;
            """;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var people = new List<PersonSearchResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            people.Add(new PersonSearchResponse(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                !reader.GetBoolean(4),
                reader.GetInt32(5) == 1));
        }

        return people;
    }

    public async Task<EventCheckInResponse> RegisterExistingAsync(
        int eventId,
        int userId,
        bool isEatingPizza,
        bool lactoseIntolerant,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await using (var eventCommand = new SqlCommand("""
            SELECT 1
            FROM community.Events WITH (UPDLOCK, HOLDLOCK)
            WHERE ID = @eventId AND IsPublished = 1 AND IsCancelled = 0
              AND CONVERT(date, EventDate) >= CONVERT(date, community.LocalNowDateTime());
            """, connection, transaction))
        {
            eventCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            if (await eventCommand.ExecuteScalarAsync(cancellationToken) is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "Questo evento non è attualmente aperto alle registrazioni.");
            }
        }

        bool confirmed;
        await using (var userCommand = new SqlCommand("""
            SELECT ToBeConfirmed FROM community.Users WITH (UPDLOCK, HOLDLOCK) WHERE ID = @userId AND IsDeleted = 0;
            """, connection, transaction))
        {
            userCommand.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            var result = await userCommand.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "Persona non trovata in anagrafica.");
            }

            confirmed = !Convert.ToBoolean(result);
        }

        if (!confirmed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "L'indirizzo email di questa persona non è confermato: usa \"Aggiungi partecipante\" per inviare la conferma.");
        }

        var registrationId = 0;
        string? status = null;
        await using (var registrationCommand = new SqlCommand("""
            SELECT ID, Status
            FROM community.UserEventRegistration WITH (UPDLOCK, HOLDLOCK)
            WHERE UserID = @userId AND EventID = @eventId AND Status <> N'Cancelled';
            """, connection, transaction))
        {
            registrationCommand.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            registrationCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await registrationCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                registrationId = reader.GetInt32(0);
                status = reader.GetString(1);
            }
        }

        if (status == "Registered")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "La persona risulta già iscritta a questo evento.");
        }

        await using (var write = new SqlCommand(registrationId == 0
            ? """
              INSERT INTO community.UserEventRegistration
                  (UserID, EventID, RegistrationDate, Status, IsEatingPizza, LactoseIntolerant)
              VALUES (@userId, @eventId, community.LocalNowDateTime(), N'Registered', @isEatingPizza, @lactoseIntolerant);
              """
            : """
              UPDATE community.UserEventRegistration
              SET Status = N'Registered', IsEatingPizza = @isEatingPizza, LactoseIntolerant = @lactoseIntolerant
              WHERE ID = @registrationId;
              """, connection, transaction))
        {
            write.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            write.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            write.Parameters.Add("@registrationId", SqlDbType.Int).Value = registrationId;
            write.Parameters.Add("@isEatingPizza", SqlDbType.Bit).Value = isEatingPizza;
            write.Parameters.Add("@lactoseIntolerant", SqlDbType.Bit).Value = isEatingPizza && lactoseIntolerant;
            await write.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new(true, "Partecipazione registrata.");
    }

    public async Task<bool> ManualCheckInAsync(
        int eventId,
        int registrationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE registration
            SET CheckedInAt = community.LocalNow()
            FROM community.UserEventRegistration registration
            INNER JOIN community.Users users ON users.ID = registration.UserID
            INNER JOIN community.Events eventInfo ON eventInfo.ID = registration.EventID
            WHERE registration.ID = @registrationId
              AND registration.EventID = @eventId
              AND registration.Status = N'Registered'
              AND registration.CheckedInAt IS NULL
              AND users.ToBeConfirmed = 0
              AND users.IsDeleted = 0
              AND eventInfo.IsPublished = 1
              AND eventInfo.IsCancelled = 0
              AND eventInfo.IsCurrent = 1
              AND CONVERT(date, eventInfo.EventDate) = CONVERT(date, community.LocalNowDateTime());
            """, connection);
        command.Parameters.Add("@registrationId", SqlDbType.Int).Value = registrationId;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Self check-in of the signed-in participant. <paramref name="signature"/> is the secret read from the printed QR code
    /// (Events.CheckInToken): without the current one nobody can check in.
    /// </summary>
    public async Task<bool> CheckInAuthenticatedUserAsync(
        int eventId,
        int userId,
        Guid signature,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE registration
            SET CheckedInAt = community.LocalNow()
            FROM community.UserEventRegistration registration
            INNER JOIN community.Events eventInfo ON eventInfo.ID = registration.EventID
            INNER JOIN community.Users users ON users.ID = registration.UserID
            WHERE registration.EventID = @eventId
              AND registration.UserID = @userId
              AND registration.Status = N'Registered'
              AND registration.CheckedInAt IS NULL
              AND users.ToBeConfirmed = 0
              AND users.IsDeleted = 0
              AND eventInfo.IsPublished = 1
              AND eventInfo.IsCancelled = 0
              AND eventInfo.IsCurrent = 1
              AND eventInfo.CheckInToken = @signature
              AND CONVERT(date, eventInfo.EventDate) = CONVERT(date, community.LocalNowDateTime());
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@signature", SqlDbType.UniqueIdentifier).Value = signature;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<EventCheckInResponse> RequestConfirmationAsync(
        Guid? eventToken,
        int? eventId,
        string? email,
        bool isEatingPizza,
        bool lactoseIntolerant,
        string? firstName,
        string? lastName,
        string purpose,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        firstName = NormalizeName(firstName);
        lastName = NormalizeName(lastName);
        var publicBaseUrl = GetPublicBaseUrl();
        var rawToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        int resolvedEventId;
        string eventName;
        string eventDate;
        await using (var eventCommand = new SqlCommand("""
            SELECT ID, Name, EventDate
            FROM community.Events WITH (UPDLOCK, HOLDLOCK)
            WHERE IsPublished = 1
              AND IsCancelled = 0
              AND ((@eventToken IS NOT NULL AND CheckInToken = @eventToken)
                OR (@eventId IS NOT NULL AND ID = @eventId))
              AND CONVERT(date, EventDate) >= CONVERT(date, community.LocalNowDateTime())
              AND (@purpose <> N'CheckIn'
                   OR (IsCurrent = 1 AND CONVERT(date, EventDate) = CONVERT(date, community.LocalNowDateTime())));
            """, connection, transaction))
        {
            eventCommand.Parameters.Add("@eventToken", SqlDbType.UniqueIdentifier).Value =
                eventToken is null ? DBNull.Value : eventToken.Value;
            eventCommand.Parameters.Add("@eventId", SqlDbType.Int).Value =
                eventId is null ? DBNull.Value : eventId.Value;
            eventCommand.Parameters.Add("@purpose", SqlDbType.NVarChar, 16).Value = purpose;
            await using var reader = await eventCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                await reader.CloseAsync();
                await transaction.RollbackAsync(cancellationToken);
                return new(false, "Questo evento non è attualmente aperto alle registrazioni o al check-in.");
            }

            resolvedEventId = reader.GetInt32(0);
            eventName = reader.GetString(1);
            eventDate = reader.GetDateTime(2).ToString("yyyy-MM-dd");
        }

        int userId;
        await using (var userCommand = new SqlCommand("""
            SELECT ID
            FROM community.Users WITH (UPDLOCK, HOLDLOCK)
            WHERE EMail = @email AND IsDeleted = 0;
            """, connection, transaction))
        {
            userCommand.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
            var result = await userCommand.ExecuteScalarAsync(cancellationToken);
            if (result is null)
            {
                await using var insertUser = new SqlCommand("""
                    INSERT INTO community.Users
                        (FirstName, LastName, EMail, LastPin, AllowEventsNotifications, RequestCancellation, ToBeConfirmed)
                    VALUES (@firstName, @lastName, @email, NULL, 0, 0, 1);
                    SELECT CONVERT(int, SCOPE_IDENTITY());
                    """, connection, transaction);
                insertUser.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
                insertUser.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value =
                    firstName is null ? DBNull.Value : firstName;
                insertUser.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value =
                    lastName is null ? DBNull.Value : lastName;
                userId = Convert.ToInt32(await insertUser.ExecuteScalarAsync(cancellationToken));
            }
            else
            {
                userId = Convert.ToInt32(result);
                if (firstName is not null && lastName is not null)
                {
                    await using var updateUser = new SqlCommand("""
                        UPDATE community.Users
                        SET FirstName = @firstName, LastName = @lastName
                        WHERE ID = @userId;
                        """, connection, transaction);
                    updateUser.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value = firstName;
                    updateUser.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value = lastName;
                    updateUser.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
                    await updateUser.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        int registrationId;
        string? existingStatus;
        DateTimeOffset? checkedInAt;
        await using (var registrationCommand = new SqlCommand("""
            SELECT ID, Status, CheckedInAt
            FROM community.UserEventRegistration WITH (UPDLOCK, HOLDLOCK)
            WHERE UserID = @userId AND EventID = @eventId AND Status <> N'Cancelled';
            """, connection, transaction))
        {
            registrationCommand.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            registrationCommand.Parameters.Add("@eventId", SqlDbType.Int).Value = resolvedEventId;
            await using var reader = await registrationCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                registrationId = reader.GetInt32(0);
                existingStatus = reader.GetString(1);
                checkedInAt = reader.IsDBNull(2) ? null : reader.GetDateTimeOffset(2);
            }
            else
            {
                registrationId = 0;
                existingStatus = null;
                checkedInAt = null;
            }
        }

        if (purpose == "CheckIn" && checkedInAt is not null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "Il check-in per questo evento risulta già completato.");
        }
        if (purpose == "Registration" && existingStatus == "Registered")
        {
            await transaction.RollbackAsync(cancellationToken);
            return new(false, "L'utente risulta già iscritto a questo evento.");
        }

        if (registrationId == 0)
        {
            await using var insertRegistration = new SqlCommand("""
                INSERT INTO community.UserEventRegistration
                    (UserID, EventID, RegistrationDate, Status, IsEatingPizza, LactoseIntolerant)
                VALUES
                    (@userId, @eventId, community.LocalNowDateTime(), N'AwaitingConfirmation', @isEatingPizza, @lactoseIntolerant);
                SELECT CONVERT(int, SCOPE_IDENTITY());
                """, connection, transaction);
            AddRegistrationParameters(insertRegistration, userId, resolvedEventId, isEatingPizza, lactoseIntolerant);
            registrationId = Convert.ToInt32(await insertRegistration.ExecuteScalarAsync(cancellationToken));
        }
        else
        {
            await using var updateRegistration = new SqlCommand("""
                UPDATE community.UserEventRegistration
                SET Status = N'AwaitingConfirmation',
                    RegistrationDate = community.LocalNowDateTime(),
                    IsEatingPizza = @isEatingPizza,
                    LactoseIntolerant = @lactoseIntolerant
                WHERE ID = @registrationId;
                """, connection, transaction);
            updateRegistration.Parameters.Add("@registrationId", SqlDbType.Int).Value = registrationId;
            updateRegistration.Parameters.Add("@isEatingPizza", SqlDbType.Bit).Value = isEatingPizza;
            updateRegistration.Parameters.Add("@lactoseIntolerant", SqlDbType.Bit).Value = lactoseIntolerant;
            await updateRegistration.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var invalidate = new SqlCommand("""
            UPDATE community.EventRegistrationConfirmations
            SET ConsumedAt = community.LocalNow()
            WHERE RegistrationID = @registrationId AND ConsumedAt IS NULL;
            INSERT INTO community.EventRegistrationConfirmations
                (RegistrationID, UserID, EventID, TokenHash, Purpose, ExpiresAt)
            VALUES
                (@registrationId, @userId, @eventId, @tokenHash, @purpose, DATEADD(minute, 30, community.LocalNow()));
            """, connection, transaction))
        {
            invalidate.Parameters.Add("@registrationId", SqlDbType.Int).Value = registrationId;
            invalidate.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
            invalidate.Parameters.Add("@eventId", SqlDbType.Int).Value = resolvedEventId;
            invalidate.Parameters.Add("@tokenHash", SqlDbType.Binary, 32).Value = tokenHash;
            invalidate.Parameters.Add("@purpose", SqlDbType.NVarChar, 16).Value = purpose;
            await invalidate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var confirmationUrl = new Uri(publicBaseUrl, $"checkin/confirm/{rawToken}").AbsoluteUri;
        try
        {
            await emailSender.SendEventActionConfirmationAsync(
                normalizedEmail,
                eventName,
                eventDate,
                confirmationUrl,
                purpose == "CheckIn",
                cancellationToken);
            return new(true, "Ti abbiamo inviato un link di conferma. Il check-in sarà completato solo dopo averlo aperto entro 30 minuti.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Could not send the {Purpose} confirmation email for event {EventId}.",
                purpose,
                resolvedEventId);
            return new(false, "Non è stato possibile inviare l'email: l'iscrizione resta in attesa e il check-in non è confermato.", true);
        }
    }

    private Uri GetPublicBaseUrl()
    {
        var configuredUrl = configuration["Application:PublicBaseUrl"];
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var baseUrl) ||
            !string.IsNullOrEmpty(baseUrl.UserInfo) ||
            !string.IsNullOrEmpty(baseUrl.Query) ||
            !string.IsNullOrEmpty(baseUrl.Fragment) ||
            (!string.Equals(baseUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(environment.IsDevelopment() &&
               string.Equals(baseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
               baseUrl.IsLoopback)))
        {
            throw new InvalidOperationException(
                "Configura Application:PublicBaseUrl con l'URL HTTPS pubblico dell'applicazione (in sviluppo è consentito localhost HTTP).");
        }

        return new Uri(baseUrl.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
    }

    private static void AddRegistrationParameters(
        SqlCommand command,
        int userId,
        int eventId,
        bool isEatingPizza,
        bool lactoseIntolerant)
    {
        command.Parameters.Add("@userId", SqlDbType.Int).Value = userId;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@isEatingPizza", SqlDbType.Bit).Value = isEatingPizza;
        command.Parameters.Add("@lactoseIntolerant", SqlDbType.Bit).Value = lactoseIntolerant;
    }

    private static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Inserisci un indirizzo email valido.");
        }

        var normalized = email.Trim().ToLowerInvariant();
        if (normalized.Length > 128)
        {
            throw new ArgumentException("L'indirizzo email è troppo lungo.");
        }

        try
        {
            return string.Equals(new MailAddress(normalized).Address, normalized, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : throw new ArgumentException("Inserisci un indirizzo email valido.");
        }
        catch (FormatException)
        {
            throw new ArgumentException("Inserisci un indirizzo email valido.");
        }

    }

    private static string? NormalizeName(string? name)
    {
        if (name is null)
        {
            return null;
        }

        var normalizedName = name.Trim();
        if (normalizedName.Length is 0 or > 64)
        {
            throw new ArgumentException("Nome e cognome sono obbligatori e non possono superare 64 caratteri.");
        }

        return normalizedName;
    }
}
