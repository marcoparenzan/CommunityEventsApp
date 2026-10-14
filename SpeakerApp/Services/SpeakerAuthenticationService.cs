using System.Data;
using System.Net.Mail;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace SpeakerApp.Services;

public sealed class SpeakerAuthenticationService(ILogger<SpeakerAuthenticationService> logger, 
    SqlConnectionFactory connections,
    SmtpEmailSender emailSender,
    IConfiguration configuration) : ISpeakerAuthenticationService
{
    private const int MaximumPinAttempts = 5;

    public async Task RequestPinAsync(string? email, string? loginBaseUrl = null, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("A speaker login PIN was requested.");
        var normalizedEmail = NormalizeEmail(email);
        var pin = CreatePin();
        var pinHash = HashPin(pin);
        int? speakerId = null;

        await using (var connection = await connections.OpenAsync(cancellationToken))
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken))
        {
            await using (var lookup = new SqlCommand("""
                SELECT TOP (2) s.ID
                FROM community.Speakers s WITH (UPDLOCK, HOLDLOCK)
                WHERE LOWER(LTRIM(RTRIM(s.EMail))) = @email;
                """, connection, transaction))
            {
                lookup.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
                await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    speakerId = reader.GetInt32(0);
                    if (await reader.ReadAsync(cancellationToken))
                    {
                        throw new InvalidOperationException(
                            "L'indirizzo email è associato a più profili speaker sincronizzati.");
                    }
                }
            }

            if (speakerId is int existingSpeakerId)
            {
                await using var challenge = new SqlCommand("""
                    UPDATE community.SpeakerEmailPinChallenges
                    SET ConsumedAt = community.LocalNow()
                    WHERE SpeakerID = @speakerId AND ConsumedAt IS NULL;

                    INSERT INTO community.SpeakerEmailPinChallenges (SpeakerID, PinHash)
                    VALUES (@speakerId, @pinHash);
                    """, connection, transaction);
                challenge.Parameters.Add("@speakerId", SqlDbType.Int).Value = existingSpeakerId;
                challenge.Parameters.Add("@pinHash", SqlDbType.Binary, 32).Value = pinHash;
                await challenge.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        if (speakerId is not null)
        {
            var loginUrl = string.IsNullOrWhiteSpace(loginBaseUrl)
                ? null
                : $"{loginBaseUrl.TrimEnd('/')}/confirm?email={Uri.EscapeDataString(normalizedEmail)}&pin={pin}";
            await emailSender.SendPinAsync(normalizedEmail, pin, loginUrl, cancellationToken);
        }
    }

    public async Task<ClaimsPrincipal?> VerifyPinAsync(
        string? email,
        string? pin,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("A speaker login PIN is being verified.");
        var normalizedEmail = NormalizeEmail(email);
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 16 || pin.Any(character => character is < '0' or > '9'))
        {
            logger.LogWarning("Speaker login refused: the PIN has an invalid format.");
            return null;
        }

        var requestedHash = HashPin(pin);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        int? speakerId = null;
        string firstName = string.Empty;
        string lastName = string.Empty;
        long challengeId = 0;
        byte attempts = 0;
        byte[]? expectedHash = null;
        bool isSuperUser = false;
        bool canCheckIn = false;

        await using (var lookup = new SqlCommand("""
            SELECT TOP (1) s.ID, s.FirstName, s.LastName, c.ID, c.PinHash, c.AttemptCount,
                   s.IsSuperUser, s.CanCheckIn
            FROM community.Speakers s
            INNER JOIN community.SpeakerEmailPinChallenges c
                ON c.SpeakerID = s.ID AND c.ConsumedAt IS NULL
            WHERE LOWER(LTRIM(RTRIM(s.EMail))) = @email
            ORDER BY c.CreatedAt DESC, c.ID DESC;
            """, connection, transaction))
        {
            lookup.Parameters.Add("@email", SqlDbType.NVarChar, 128).Value = normalizedEmail;
            await using var reader = await lookup.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                speakerId = reader.GetInt32(0);
                firstName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                lastName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                challengeId = reader.GetInt64(3);
                expectedHash = (byte[])reader[4];
                attempts = reader.GetByte(5);
                isSuperUser = reader.GetBoolean(6);
                canCheckIn = reader.GetBoolean(7);
            }
        }

        if (speakerId is null || expectedHash is null || attempts >= MaximumPinAttempts ||
            !CryptographicOperations.FixedTimeEquals(expectedHash, requestedHash))
        {
            if (challengeId != 0 && attempts < MaximumPinAttempts)
            {
                await using var increment = new SqlCommand(
                    "UPDATE community.SpeakerEmailPinChallenges SET AttemptCount = AttemptCount + 1 WHERE ID = @id;",
                    connection,
                    transaction);
                increment.Parameters.Add("@id", SqlDbType.BigInt).Value = challengeId;
                await increment.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogWarning(
                "Speaker login refused: wrong PIN, no PIN requested or too many attempts (speaker {SpeakerId}, challenge {ChallengeId}, attempts {Attempts}).",
                speakerId, challengeId, attempts);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Speaker login completed for speaker {SpeakerId}.", speakerId);
        var claims = new[]
        {
            new Claim("account_type", "speaker"),
            new Claim("speaker_id", speakerId.Value.ToString()),
            new Claim(ClaimTypes.Name, $"{firstName} {lastName}".Trim()),
            new Claim(ClaimTypes.Email, normalizedEmail),
            new Claim("is_speaker", "true"),
            new Claim("is_super_user", isSuperUser ? "true" : "false"),
            new Claim("can_check_in", canCheckIn ? "true" : "false")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "EventsCookie"));
    }

    public async Task<SpeakerAccountResponse?> GetAccountAsync(
        int speakerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        string firstName;
        string lastName;
        string email;
        await using (var speaker = new SqlCommand("""
            SELECT s.FirstName, s.LastName, s.EMail
            FROM community.Speakers s
            WHERE s.ID = @speakerId;
            """, connection))
        {
            speaker.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
            await using var reader = await speaker.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            firstName = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            lastName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            email = reader.GetString(2);
        }

        var events = new List<SpeakerEventResponse>();
        await using (var command = new SqlCommand("""
            SELECT e.ID, e.Year, e.Moniker, e.Name, e.IsPublished
            FROM community.EventSpeakers es
            INNER JOIN community.Events e ON e.ID = es.EventID
            WHERE es.SpeakerID = @speakerId
            GROUP BY e.ID, e.Year, e.Moniker, e.Name, e.IsPublished, e.EventDate
            ORDER BY e.EventDate DESC, e.Name;
            """, connection))
        {
            command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(new SpeakerEventResponse(
                    reader.GetInt32(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetBoolean(4)));
            }
        }

        string? publicProfileKey;
        await using (var keyCommand = new SqlCommand("""
            SELECT TOP (1) COALESCE(
                       NULLIF(es.SessionizeMoniker, N''),
                       (SELECT TOP (1) other.SessionizeMoniker FROM community.EventSpeakers other
                        WHERE other.SessionizeSpeakerID = es.SessionizeSpeakerID AND other.SessionizeMoniker <> N''
                        ORDER BY other.LastSyncedAt DESC),
                       es.SessionizeSpeakerID)
            FROM community.EventSpeakers es
            INNER JOIN community.Events e ON e.ID = es.EventID
            WHERE es.SpeakerID = @speakerId AND e.IsPublished = 1
            ORDER BY e.EventDate DESC;
            """, connection))
        {
            keyCommand.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
            publicProfileKey = await keyCommand.ExecuteScalarAsync(cancellationToken) as string;
        }

        return new SpeakerAccountResponse(firstName, lastName, email, events, publicProfileKey);
    }

    public async Task<IReadOnlyList<SpeakerSessionResponse>> GetSessionsAsync(
        int speakerId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT eventInfo.ID, eventInfo.Name, CONVERT(char(10), eventInfo.EventDate, 23),
                   session.SessionizeSessionID, session.Title, session.Room, session.StartsAt,
                   session.PresentationUrl, session.RepositoryUrl
            FROM community.EventSpeakers eventSpeaker
            INNER JOIN community.EventSessionSpeakers sessionSpeaker
                ON sessionSpeaker.EventID = eventSpeaker.EventID
               AND sessionSpeaker.EventSpeakerID = eventSpeaker.ID
            INNER JOIN community.EventSessions session
                ON session.EventID = sessionSpeaker.EventID
               AND session.SessionizeSessionID = sessionSpeaker.SessionizeSessionID
            INNER JOIN community.Events eventInfo ON eventInfo.ID = eventSpeaker.EventID
            WHERE eventSpeaker.SpeakerID = @speakerId
              AND session.IsActive = 1
            ORDER BY session.StartsAt DESC, session.Title;
            """, connection);
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sessions = new List<SpeakerSessionResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new SpeakerSessionResponse(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetDateTimeOffset(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return sessions;
    }

    public async Task<bool> UpdateSessionLinksAsync(
        int speakerId,
        UpdateSpeakerSessionLinksRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Updating the links of a session.");
        if (string.IsNullOrWhiteSpace(request.SessionizeSessionId) ||
            request.SessionizeSessionId.Length > 80)
        {
            throw new ArgumentException("ID della sessione non valido.", nameof(request));
        }

        ValidateResourceUrl(request.PresentationUrl, nameof(request.PresentationUrl));
        ValidateResourceUrl(request.RepositoryUrl, nameof(request.RepositoryUrl));

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE session
            SET PresentationUrl = @presentationUrl,
                RepositoryUrl = @repositoryUrl
            FROM community.EventSessions session
            WHERE session.EventID = @eventId
              AND session.SessionizeSessionID = @sessionizeSessionId
              AND session.IsActive = 1
              AND EXISTS (
                  SELECT 1
                  FROM community.EventSessionSpeakers sessionSpeaker
                  INNER JOIN community.EventSpeakers eventSpeaker
                      ON eventSpeaker.ID = sessionSpeaker.EventSpeakerID
                     AND eventSpeaker.EventID = sessionSpeaker.EventID
                  WHERE sessionSpeaker.EventID = session.EventID
                    AND sessionSpeaker.SessionizeSessionID = session.SessionizeSessionID
                    AND eventSpeaker.SpeakerID = @speakerId);
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@presentationUrl", SqlDbType.NVarChar, 512).Value = request.PresentationUrl?.Trim() ?? string.Empty;
        command.Parameters.Add("@repositoryUrl", SqlDbType.NVarChar, 512).Value = request.RepositoryUrl?.Trim() ?? string.Empty;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = request.EventId;
        command.Parameters.Add("@sessionizeSessionId", SqlDbType.NVarChar, 80).Value = request.SessionizeSessionId.Trim();
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
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

    private static string NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Indirizzo email non valido.");
        }

        var normalized = email.Trim().ToLowerInvariant();
        try
        {
            return normalized.Length <= 64 &&
                   string.Equals(new MailAddress(normalized).Address, normalized, StringComparison.OrdinalIgnoreCase)
                ? normalized
                : throw new ArgumentException("Indirizzo email non valido.");
        }
        catch (FormatException)
        {
            throw new ArgumentException("Indirizzo email non valido.");
        }
    }

    private static void ValidateResourceUrl(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Length > 512 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Inserisci un URL HTTPS valido (massimo 512 caratteri).", parameterName);
        }
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
