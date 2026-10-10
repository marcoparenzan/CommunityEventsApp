using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace SpeakerApp.Services;

public sealed class SessionizeSyncService(
    IHttpClientFactory httpClientFactory,
    SqlConnectionFactory connections,
    EventCatalogService eventCatalog)
{
    public async Task<int> SyncAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var eventInfo = (await eventCatalog.GetAllAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == eventId);
        if (eventInfo is null || string.IsNullOrWhiteSpace(eventInfo.SessionizeJsonUri))
        {
            throw new InvalidOperationException("Evento non trovato o privo dell'URL JSON Sessionize.");
        }

        var sessionizeUri = new Uri(ValidateSessionizeUri(eventInfo.SessionizeJsonUri));
        var client = httpClientFactory.CreateClient("Sessionize");
        var sessionizeData = await client.GetFromJsonAsync<SessionizeAllResponse>(sessionizeUri, cancellationToken)
            ?? throw new InvalidOperationException("Sessionize ha restituito una risposta vuota o non valida.");
        if (sessionizeData.Rooms.Count == 0)
        {
            throw new InvalidOperationException("Sessionize non ha restituito le aule dell'evento.");
        }

        var speakerProfilesUri = new Uri(
            sessionizeUri.AbsoluteUri[..sessionizeUri.AbsoluteUri.LastIndexOf("/view/All", StringComparison.OrdinalIgnoreCase)] + "/view/Speakers");
        var speakerProfiles = await client.GetFromJsonAsync<SpeakerInfo[]>(speakerProfilesUri, cancellationToken)
            ?? throw new InvalidOperationException("Sessionize non ha restituito i profili dei relatori.");
        var speakerProfilesById = speakerProfiles.ToDictionary(speaker => speaker.Id);
        var rooms = sessionizeData.Rooms.ToDictionary(room => room.Id);
        var speakers = sessionizeData.Speakers.ToDictionary(speaker => speaker.Id);
        var incoming = sessionizeData.Sessions
            .Select(session => MapSession(session, rooms, speakers))
            .Where(session => !session.IsServiceSession)
            .ToArray();
        if (incoming.Length == 0)
        {
            throw new InvalidOperationException("Sessionize non ha restituito sessioni; l'agenda esistente è stata lasciata invariata.");
        }

        foreach (var speakerId in incoming.SelectMany(session => session.Speakers).Select(speaker => speaker.Id).Distinct())
        {
            if (!speakerProfilesById.ContainsKey(speakerId))
            {
                throw new InvalidOperationException(
                    $"Sessionize non ha restituito il profilo del relatore '{speakers[speakerId].FullName}'.");
            }
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        await using (var setup = new SqlCommand("""
            CREATE TABLE #SyncedSessions (SessionizeSessionID nvarchar(80) NOT NULL PRIMARY KEY);
            CREATE TABLE #SyncedSpeakers (SessionizeSpeakerID nvarchar(80) NOT NULL PRIMARY KEY);
            """, connection, transaction))
        {
            await setup.ExecuteNonQueryAsync(cancellationToken);
        }

        var eventSpeakerIds = new Dictionary<Guid, long>();
        foreach (var profile in speakerProfiles)
        {
            if (profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.FullName))
            {
                throw new InvalidOperationException("Sessionize ha restituito un profilo relatore senza ID o nome valido.");
            }

            var speakerId = profile.Id.ToString("D");
            var eventSpeakerId = await UpsertSpeakerAsync(
                connection,
                transaction,
                eventId,
                speakerId,
                profile.FullName,
                profile,
                cancellationToken);
            eventSpeakerIds.Add(profile.Id, eventSpeakerId);
            await using var markSpeakerSynced = new SqlCommand(
                "INSERT INTO #SyncedSpeakers (SessionizeSpeakerID) VALUES (@speakerId);",
                connection,
                transaction);
            markSpeakerSynced.Parameters.Add("@speakerId", SqlDbType.NVarChar, 80).Value = speakerId;
            await markSpeakerSynced.ExecuteNonQueryAsync(cancellationToken);
        }

        var synchronizedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in incoming)
        {
            var sessionId = ReadId(session.Id);
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 80)
            {
                throw new InvalidOperationException("La risposta Sessionize contiene una sessione senza ID valido.");
            }

            if (!synchronizedIds.Add(sessionId))
            {
                throw new InvalidOperationException($"Sessionize ha restituito l'ID sessione duplicato '{sessionId}'.");
            }

            await UpsertSessionAsync(connection, transaction, eventId, sessionId, session, cancellationToken);
            await using (var markSynced = new SqlCommand(
                "INSERT INTO #SyncedSessions (SessionizeSessionID) VALUES (@sessionId);",
                connection,
                transaction))
            {
                markSynced.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = sessionId;
                await markSynced.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var clearSpeakerLinks = new SqlCommand("""
                DELETE FROM community.EventSessionSpeakers
                WHERE EventID = @eventId AND SessionizeSessionID = @sessionId;
                """, connection, transaction))
            {
                clearSpeakerLinks.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
                clearSpeakerLinks.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = sessionId;
                await clearSpeakerLinks.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var speaker in session.Speakers ?? [])
            {
                if (!eventSpeakerIds.TryGetValue(speaker.Id, out var eventSpeakerId))
                {
                    throw new InvalidOperationException(
                        $"Sessionize non ha restituito il profilo del relatore '{speaker.Name}'.");
                }

                await using var link = new SqlCommand("""
                    INSERT INTO community.EventSessionSpeakers (EventID, SessionizeSessionID, EventSpeakerID)
                    VALUES (@eventId, @sessionId, @eventSpeakerId);
                    """, connection, transaction);
                link.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
                link.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = sessionId;
                link.Parameters.Add("@eventSpeakerId", SqlDbType.BigInt).Value = eventSpeakerId;
                await link.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var retireRemoved = new SqlCommand("""
            UPDATE community.EventSessions
            SET IsActive = 0, LastSyncedAt = community.LocalNow()
            WHERE EventID = @eventId
              AND IsActive = 1
              AND NOT EXISTS (
                  SELECT 1 FROM #SyncedSessions currentSessions
                  WHERE currentSessions.SessionizeSessionID = EventSessions.SessionizeSessionID);
            """, connection, transaction))
        {
            retireRemoved.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await retireRemoved.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var removeRetiredSessionLinks = new SqlCommand("""
            DELETE FROM community.EventSessionSpeakers
            WHERE EventID = @eventId
              AND NOT EXISTS (
                  SELECT 1 FROM community.EventSessions activeSessions
                  WHERE activeSessions.EventID = EventSessionSpeakers.EventID
                    AND activeSessions.SessionizeSessionID = EventSessionSpeakers.SessionizeSessionID
                    AND activeSessions.IsActive = 1);
            DELETE FROM community.EventSpeakers
            WHERE EventID = @eventId
              AND NOT EXISTS (
                  SELECT 1 FROM #SyncedSpeakers currentSpeakers
                  WHERE currentSpeakers.SessionizeSpeakerID = EventSpeakers.SessionizeSpeakerID);
            """, connection, transaction))
        {
            removeRetiredSessionLinks.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await removeRetiredSessionLinks.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return synchronizedIds.Count;
    }

    private static Session MapSession(
        SessionizeAllSession source,
        IReadOnlyDictionary<long, SessionizeAllRoom> rooms,
        IReadOnlyDictionary<Guid, SessionizeAllSpeaker> speakers)
    {
        if (!rooms.TryGetValue(source.RoomId, out var room))
        {
            throw new InvalidOperationException(
                $"Sessionize ha restituito una sessione associata all'aula sconosciuta '{source.RoomId}'.");
        }

        var sessionSpeakers = new List<Speaker>();
        foreach (var speakerIdValue in source.Speakers)
        {
            if (!Guid.TryParse(speakerIdValue, out var speakerId) ||
                !speakers.TryGetValue(speakerId, out var speaker) ||
                string.IsNullOrWhiteSpace(speaker.FullName))
            {
                throw new InvalidOperationException(
                    $"Sessionize ha restituito un relatore non riconciliabile per la sessione '{source.Id}'.");
            }

            sessionSpeakers.Add(new Speaker { Id = speakerId, Name = speaker.FullName });
        }

        return new Session
        {
            Id = source.Id,
            Title = source.Title,
            Description = source.Description,
            StartsAt = source.StartsAt,
            EndsAt = source.EndsAt,
            IsServiceSession = source.IsServiceSession,
            Speakers = sessionSpeakers,
            RoomId = room.Id,
            Room = room.Name
        };
    }

    public static string ValidateSessionizeUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "sessionize.com", StringComparison.OrdinalIgnoreCase) ||
            !System.Text.RegularExpressions.Regex.IsMatch(
                uri.AbsolutePath,
                "^/api/v2/[A-Za-z0-9]+/view/All/?$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "L'URL Sessionize deve avere il formato https://sessionize.com/api/v2/{id}/view/All.");
        }

        return uri.AbsoluteUri;
    }

    private static async Task UpsertSessionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int eventId,
        string sessionId,
        Session session,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            UPDATE community.EventSessions
            SET Title = @title, Description = @description, StartsAt = @startsAt,
                EndsAt = @endsAt, Room = @room, IsActive = 1, LastSyncedAt = community.LocalNow()
            WHERE EventID = @eventId AND SessionizeSessionID = @sessionId;
            IF @@ROWCOUNT = 0
                INSERT INTO community.EventSessions
                    (EventID, SessionizeSessionID, Title, Description, StartsAt, EndsAt, Room)
                VALUES (@eventId, @sessionId, @title, @description, @startsAt, @endsAt, @room);
            """, connection, transaction);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = sessionId;
        command.Parameters.Add("@title", SqlDbType.NVarChar, 512).Value = session.Title ?? string.Empty;
        command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = session.Description ?? string.Empty;
        command.Parameters.Add("@startsAt", SqlDbType.DateTimeOffset).Value = session.StartsAt;
        command.Parameters.Add("@endsAt", SqlDbType.DateTimeOffset).Value = session.EndsAt;
        command.Parameters.Add("@room", SqlDbType.NVarChar, 128).Value = session.Room ?? string.Empty;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> UpsertSpeakerAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        int eventId,
        string speakerId,
        string name,
        SpeakerInfo profile,
        CancellationToken cancellationToken)
    {
        int? linkedSpeakerId = null;
        await using (var byId = new SqlCommand(
            "SELECT TOP (1) ID FROM community.Speakers WHERE SessionizeSpeakerID = @speakerId;", connection, transaction))
        {
            byId.Parameters.Add("@speakerId", SqlDbType.NVarChar, 80).Value = speakerId;
            if (await byId.ExecuteScalarAsync(cancellationToken) is int boundId)
            {
                linkedSpeakerId = boundId;
            }
        }

        linkedSpeakerId ??= await ResolveSpeakerAccountIdAsync(
            connection,
            transaction,
            name,
            profile.FirstName,
            profile.LastName,
            cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.EventSpeakers
            SET SpeakerID = COALESCE(@linkedSpeakerId, SpeakerID),
                Name = @name, FirstName = @firstName, LastName = @lastName,
                Bio = @bio, TagLine = @tagLine, ProfilePictureUrl = @profilePictureUrl,
                LinkedInUrl = @linkedInUrl, SessionizeUrl = @sessionizeUrl,
                SessionizeMoniker = @sessionizeMoniker,
                LastSyncedAt = community.LocalNow()
            WHERE EventID = @eventId AND SessionizeSpeakerID = @speakerId;
            IF @@ROWCOUNT = 0
                INSERT INTO community.EventSpeakers
                    (EventID, SessionizeSpeakerID, SpeakerID, Name, FirstName, LastName, Bio, TagLine,
                     ProfilePictureUrl, LinkedInUrl, SessionizeUrl, SessionizeMoniker)
                VALUES
                    (@eventId, @speakerId, @linkedSpeakerId, @name, @firstName, @lastName, @bio, @tagLine,
                     @profilePictureUrl, @linkedInUrl, @sessionizeUrl, @sessionizeMoniker);
            SELECT ID FROM community.EventSpeakers WHERE EventID = @eventId AND SessionizeSpeakerID = @speakerId;
            """, connection, transaction);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@speakerId", SqlDbType.NVarChar, 80).Value = speakerId;
        command.Parameters.Add("@linkedSpeakerId", SqlDbType.Int).Value = linkedSpeakerId is int id ? id : DBNull.Value;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 256).Value = name;
        command.Parameters.Add("@firstName", SqlDbType.NVarChar, 128).Value = profile.FirstName ?? string.Empty;
        command.Parameters.Add("@lastName", SqlDbType.NVarChar, 128).Value = profile.LastName ?? string.Empty;
        command.Parameters.Add("@bio", SqlDbType.NVarChar, -1).Value = profile.Bio ?? string.Empty;
        command.Parameters.Add("@tagLine", SqlDbType.NVarChar, 512).Value = profile.TagLine ?? string.Empty;
        command.Parameters.Add("@profilePictureUrl", SqlDbType.NVarChar, 2048).Value = GetSafeProfilePictureUrl(profile);
        command.Parameters.Add("@linkedInUrl", SqlDbType.NVarChar, 2048).Value = GetSafeLinkedInUrl(profile);
        var sessionizeUrl = GetSafeSessionizeUrl(profile);
        command.Parameters.Add("@sessionizeUrl", SqlDbType.NVarChar, 2048).Value = sessionizeUrl;
        command.Parameters.Add("@sessionizeMoniker", SqlDbType.NVarChar, 256).Value =
            GetSessionizeMoniker(sessionizeUrl);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<int?> ResolveSpeakerAccountIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sessionizeName,
        string? firstName,
        string? lastName,
        CancellationToken cancellationToken)
    {
        await using (var lookupByName = new SqlCommand("""
            SELECT TOP (2) ID
            FROM community.Speakers WITH (UPDLOCK, HOLDLOCK)
            WHERE NULLIF(LTRIM(RTRIM(SessionizeName)), N'') = @sessionizeName;
            """, connection, transaction))
        {
            lookupByName.Parameters.Add("@sessionizeName", SqlDbType.NVarChar, 256).Value = sessionizeName;
            await using var reader = await lookupByName.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var speakerId = reader.GetInt32(0);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Il nome Sessionize '{sessionizeName}' corrisponde a più account speaker. Correggere community.Speakers.SessionizeName.");
                }

                return speakerId;
            }
        }

        // Nessun SessionizeName corrispondente: ripiega sul nome e cognome dell'account
        // (se univoco, memorizza SessionizeName); se non esiste, crea l'account con email <notset>.
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            return null;
        }

        int? matchedId = null;
        await using (var lookupByFullName = new SqlCommand("""
            SELECT TOP (2) ID
            FROM community.Speakers WITH (UPDLOCK, HOLDLOCK)
            WHERE LTRIM(RTRIM(FirstName)) = @firstName
              AND LTRIM(RTRIM(LastName)) = @lastName;
            """, connection, transaction))
        {
            lookupByFullName.Parameters.Add("@firstName", SqlDbType.NVarChar, 128).Value = firstName.Trim();
            lookupByFullName.Parameters.Add("@lastName", SqlDbType.NVarChar, 128).Value = lastName.Trim();
            await using var reader = await lookupByFullName.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                matchedId = reader.GetInt32(0);
                if (await reader.ReadAsync(cancellationToken))
                {
                    return null;
                }
            }
        }

        if (matchedId is null)
        {
            await using var create = new SqlCommand("""
                INSERT INTO community.Speakers
                    (FirstName, LastName, EMail, LastPin, SessionizeName, IsSuperUser, CanCheckIn)
                OUTPUT INSERTED.ID
                VALUES (@firstName, @lastName, N'<notset>', N'', @sessionizeName, 0, 0);
                """, connection, transaction);
            create.Parameters.Add("@firstName", SqlDbType.NVarChar, 64).Value = Truncate(firstName.Trim(), 64);
            create.Parameters.Add("@lastName", SqlDbType.NVarChar, 64).Value = Truncate(lastName.Trim(), 64);
            create.Parameters.Add("@sessionizeName", SqlDbType.NVarChar, 64).Value = Truncate(sessionizeName, 64);
            return Convert.ToInt32(await create.ExecuteScalarAsync(cancellationToken));
        }

        if (matchedId is int accountId)
        {
            await using var remember = new SqlCommand("""
                UPDATE community.Speakers
                SET SessionizeName = @sessionizeName
                WHERE ID = @id AND NULLIF(LTRIM(RTRIM(SessionizeName)), N'') IS NULL;
                """, connection, transaction);
            remember.Parameters.Add("@id", SqlDbType.Int).Value = accountId;
            remember.Parameters.Add("@sessionizeName", SqlDbType.NVarChar, 64).Value = Truncate(sessionizeName, 64);
            await remember.ExecuteNonQueryAsync(cancellationToken);
        }

        return matchedId;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string GetSafeProfilePictureUrl(SpeakerInfo profile) =>
        Uri.TryCreate(profile.ProfilePicture?.ToString(), UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : string.Empty;

    private static string GetSafeLinkedInUrl(SpeakerInfo profile)
    {
        var linkedInUrl = profile.Links?
            .FirstOrDefault(link => string.Equals(link.LinkType, "LinkedIn", StringComparison.OrdinalIgnoreCase))
            ?.Url;
        return Uri.TryCreate(linkedInUrl, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               (string.Equals(uri.Host, "linkedin.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".linkedin.com", StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : string.Empty;
    }

    private static string GetSafeSessionizeUrl(SpeakerInfo profile)
    {
        var sessionizeUrl = profile.Links?
            .FirstOrDefault(link => string.Equals(link.LinkType, "Sessionize", StringComparison.OrdinalIgnoreCase))
            ?.Url;
        return Uri.TryCreate(sessionizeUrl, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               (string.Equals(uri.Host, "sessionize.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".sessionize.com", StringComparison.OrdinalIgnoreCase))
            ? uri.AbsoluteUri
            : string.Empty;
    }

    private static string GetSessionizeMoniker(string sessionizeUrl)
    {
        if (!Uri.TryCreate(sessionizeUrl, UriKind.Absolute, out var uri))
        {
            return string.Empty;
        }

        var moniker = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault();
        if (string.IsNullOrWhiteSpace(moniker) ||
            moniker.Length > 256 ||
            moniker.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            return string.Empty;
        }

        return moniker;
    }

    private static string ReadId(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } json => json.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Number } json => json.GetRawText(),
        JsonElement json => json.GetRawText(),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };
}
