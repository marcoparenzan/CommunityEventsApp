using System.Data;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace EventsCommon.Services;

public sealed class EventCatalogService(SqlConnectionFactory connections)
{
    public async Task<IReadOnlyList<EventDefinition>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT ID, Year, Moniker, IsCurrent, IsPublished, Name,
                   EventDate, CallForPapersOpenAt, CallForPapersCloseAt, CallForPapersUrl,
                   SessionizeMoniker, Description, SessionizeJsonUri, CheckInToken,
                   IsCancelled, IsLocked
            FROM community.Events
            ORDER BY EventDate, Name;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<EventDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<EventDefinition?> GetAsync(int year, string moniker, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT ID, Year, Moniker, IsCurrent, IsPublished, Name,
                   EventDate, CallForPapersOpenAt, CallForPapersCloseAt, CallForPapersUrl,
                   SessionizeMoniker, Description, SessionizeJsonUri, CheckInToken,
                   IsCancelled, IsLocked
            FROM community.Events
            WHERE Year = @year AND Moniker = @moniker AND IsPublished = 1;
            """, connection);
        command.Parameters.Add("@year", SqlDbType.Int).Value = year;
        command.Parameters.Add("@moniker", SqlDbType.NVarChar, 80).Value = moniker;
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SingleRow, cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEvent(reader) : null;
    }

    public async Task<IReadOnlyList<EventDefinition>> GetUpcomingAsync(DateTime today, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT ID, Year, Moniker, IsCurrent, IsPublished, Name,
                   EventDate, CallForPapersOpenAt, CallForPapersCloseAt, CallForPapersUrl,
                   SessionizeMoniker, Description, SessionizeJsonUri, CheckInToken,
                   IsCancelled, IsLocked
            FROM community.Events
            WHERE IsPublished = 1 AND IsCancelled = 0 AND EventDate >= @today
            ORDER BY EventDate, Name;
            """, connection);
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<EventDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<EventDefinition>> GetPastAsync(DateTime today, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT ID, Year, Moniker, IsCurrent, IsPublished, Name,
                   EventDate, CallForPapersOpenAt, CallForPapersCloseAt, CallForPapersUrl,
                   SessionizeMoniker, Description, SessionizeJsonUri, CheckInToken,
                   IsCancelled, IsLocked
            FROM community.Events
            WHERE IsPublished = 1 AND EventDate < @today
            ORDER BY EventDate DESC, Name;
            """, connection);
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<EventDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<EventDefinition>> GetCancelledUpcomingAsync(
        DateTime today,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT ID, Year, Moniker, IsCurrent, IsPublished, Name,
                   EventDate, CallForPapersOpenAt, CallForPapersCloseAt, CallForPapersUrl,
                   SessionizeMoniker, Description, SessionizeJsonUri, CheckInToken,
                   IsCancelled, IsLocked
            FROM community.Events
            WHERE IsPublished = 1 AND IsCancelled = 1 AND EventDate >= @today
            ORDER BY EventDate, Name;
            """, connection);
        command.Parameters.Add("@today", SqlDbType.Date).Value = today.Date;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var events = new List<EventDefinition>();
        while (await reader.ReadAsync(cancellationToken))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    public async Task<IReadOnlyList<EventSponsor>> GetSponsorsAsync(EventDefinition eventDefinition, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT s.SponsorKey, s.Name, s.Url, s.Tier, s.MediaAssetID
            FROM community.EventSponsors es
            INNER JOIN community.Sponsors s ON s.ID = es.SponsorID
            WHERE es.EventID = @eventId
            ORDER BY es.SortOrder, s.Name;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventDefinition.Id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var sponsors = new List<EventSponsor>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sponsors.Add(new EventSponsor
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Url = reader.GetString(2),
                Tier = reader.GetString(3),
                MediaAssetId = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                LogoUrl = reader.IsDBNull(4) ? string.Empty : $"/media/{reader.GetInt32(4)}"
            });
        }

        return sponsors;
    }

    public async Task<IReadOnlyList<Session>> GetSessionsAsync(int eventId, CancellationToken cancellationToken = default)
    {
        var sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using (var command = new SqlCommand("""
            SELECT SessionizeSessionID, Title, COALESCE(NULLIF(SpeakerDescription, N''), Description), StartsAt, EndsAt, Room,
                   VideoUrl, PresentationUrl, RepositoryUrl
            FROM community.EventSessions
            WHERE EventID = @eventId AND IsActive = 1
            ORDER BY StartsAt, Room, Title;
            """, connection))
        {
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                sessions.Add(id, new Session
                {
                    Id = id,
                    Title = reader.GetString(1),
                    Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    StartsAt = reader.GetDateTimeOffset(3),
                    EndsAt = reader.GetDateTimeOffset(4),
                    Room = reader.GetString(5),
                    VideoUrl = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    PresentationUrl = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    RepositoryUrl = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    Speakers = []
                });
            }
        }

        if (sessions.Count == 0)
        {
            return [];
        }

        await using (var command = new SqlCommand("""
            SELECT es.SessionizeSessionID, sp.SessionizeSpeakerID, sp.Name
            FROM community.EventSessionSpeakers es
            INNER JOIN community.EventSpeakers sp ON sp.ID = es.EventSpeakerID
            WHERE es.EventID = @eventId
            ORDER BY sp.Name;
            """, connection))
        {
            command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var sessionId = reader.GetString(0);
                if (sessions.TryGetValue(sessionId, out var session))
                {
                    session.Speakers.Add(new Speaker
                    {
                        Id = Guid.TryParse(reader.GetString(1), out var speakerId) ? speakerId : Guid.Empty,
                        Name = reader.GetString(2)
                    });
                }
            }
        }

        return sessions.Values.ToArray();
    }

    public async Task<int> GetEventSpeakerCountAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT COUNT(DISTINCT SessionizeSpeakerID)
            FROM community.EventSpeakers
            WHERE EventID = @eventId;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<bool> UpdateSessionResourcesAsync(
        int eventId,
        string sessionizeSessionId,
        string? videoUrl,
        string? presentationUrl,
        string? repositoryUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionizeSessionId) || sessionizeSessionId.Length > 80)
        {
            throw new ArgumentException("ID della sessione non valido.", nameof(sessionizeSessionId));
        }

        ValidateResourceUrl(videoUrl, nameof(videoUrl));
        ValidateResourceUrl(presentationUrl, nameof(presentationUrl));
        ValidateResourceUrl(repositoryUrl, nameof(repositoryUrl));

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.EventSessions
            SET VideoUrl = @videoUrl,
                PresentationUrl = @presentationUrl,
                RepositoryUrl = @repositoryUrl
            WHERE EventID = @eventId
              AND SessionizeSessionID = @sessionizeSessionId
              AND IsActive = 1;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@videoUrl", SqlDbType.NVarChar, 512).Value = NormalizeResourceUrl(videoUrl);
        command.Parameters.Add("@presentationUrl", SqlDbType.NVarChar, 512).Value = NormalizeResourceUrl(presentationUrl);
        command.Parameters.Add("@repositoryUrl", SqlDbType.NVarChar, 512).Value = NormalizeResourceUrl(repositoryUrl);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@sessionizeSessionId", SqlDbType.NVarChar, 80).Value = sessionizeSessionId.Trim();
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<EventSpeakerProfile>> GetEventSpeakersAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT sp.SessionizeSpeakerID,
                   COALESCE(NULLIF(sp.SessionizeMoniker, N''),
                            (SELECT TOP (1) other.SessionizeMoniker FROM community.EventSpeakers other
                             WHERE other.SessionizeSpeakerID = sp.SessionizeSpeakerID AND other.SessionizeMoniker <> N''
                             ORDER BY other.LastSyncedAt DESC), N''),
                   sp.FirstName, sp.LastName, sp.Name,
                   sp.Bio, sp.TagLine, sp.ProfilePictureUrl, sp.LinkedInUrl,
                   COALESCE(NULLIF(sp.SessionizeUrl, N''),
                            (SELECT TOP (1) other.SessionizeUrl FROM community.EventSpeakers other
                             WHERE other.SessionizeSpeakerID = sp.SessionizeSpeakerID AND other.SessionizeUrl <> N''
                             ORDER BY other.LastSyncedAt DESC), N''),
                   es.Title
            FROM community.EventSpeakers sp
            LEFT JOIN community.EventSessionSpeakers ess
                ON ess.EventID = sp.EventID AND ess.EventSpeakerID = sp.ID
            LEFT JOIN community.EventSessions es
                ON es.EventID = ess.EventID AND es.SessionizeSessionID = ess.SessionizeSessionID
                AND es.IsActive = 1
            WHERE sp.EventID = @eventId
            ORDER BY sp.Name, es.StartsAt, es.Title;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var speakers = new Dictionary<Guid, EventSpeakerProfile>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id))
            {
                throw new InvalidOperationException("Il database contiene un ID Sessionize non valido per un relatore.");
            }

            if (!speakers.TryGetValue(id, out var profile))
            {
                var firstName = reader.GetString(2);
                var lastName = reader.GetString(3);
                profile = new EventSpeakerProfile
                {
                    Id = id,
                    SessionizeMoniker = reader.GetString(1),
                    FirstName = firstName,
                    LastName = lastName,
                    FullName = string.IsNullOrWhiteSpace($"{firstName} {lastName}")
                        ? reader.GetString(4)
                        : $"{firstName} {lastName}".Trim(),
                    Bio = reader.GetString(5),
                    TagLine = reader.GetString(6),
                    ProfilePictureUrl = reader.GetString(7),
                    LinkedInUrl = reader.GetString(8),
                    SessionizeUrl = reader.GetString(9)
                };
                speakers.Add(id, profile);
            }

            if (!reader.IsDBNull(10))
            {
                profile.Sessions.Add(reader.GetString(10));
            }
        }

        return speakers.Values.OrderBy(speaker => speaker.FullName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<PublicSpeakerProfile?> GetPublicSpeakerProfileAsync(
        string speakerKey,
        CancellationToken cancellationToken = default)
    {
        var sessionizeMoniker = Guid.TryParse(speakerKey, out var parsedSpeakerId)
            ? string.Empty
            : speakerKey;
        var speakerId = Guid.TryParse(speakerKey, out parsedSpeakerId)
            ? parsedSpeakerId.ToString("D")
            : string.Empty;
        if (string.IsNullOrWhiteSpace(speakerKey) ||
            speakerKey.Length > 256 ||
            (string.IsNullOrEmpty(speakerId) &&
             speakerKey.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.')))
        {
            return null;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT speaker.SessionizeSpeakerID, speaker.FirstName, speaker.LastName, speaker.Name,
                   speaker.TagLine, speaker.Bio, speaker.ProfilePictureUrl, speaker.LinkedInUrl,
                   speaker.SessionizeUrl, eventInfo.Year, eventInfo.Moniker, eventInfo.Name,
                   CONVERT(char(10), eventInfo.EventDate, 23), session.Title, session.Room, session.StartsAt,
                   session.SessionizeSessionID
            FROM community.EventSpeakers speaker
            INNER JOIN community.Events eventInfo ON eventInfo.ID = speaker.EventID
            LEFT JOIN community.EventSessionSpeakers sessionSpeaker
                ON sessionSpeaker.EventID = speaker.EventID AND sessionSpeaker.EventSpeakerID = speaker.ID
            LEFT JOIN community.EventSessions session
                ON session.EventID = sessionSpeaker.EventID
               AND session.SessionizeSessionID = sessionSpeaker.SessionizeSessionID
               AND session.IsActive = 1
            WHERE ((@speakerId <> N'' AND speaker.SessionizeSpeakerID = @speakerId)
                   OR (@sessionizeMoniker <> N'' AND speaker.SessionizeSpeakerID IN (
                       SELECT known.SessionizeSpeakerID FROM community.EventSpeakers known
                       WHERE known.SessionizeMoniker = @sessionizeMoniker)))
              AND eventInfo.IsPublished = 1
            ORDER BY eventInfo.EventDate DESC, session.StartsAt, session.Title;
            """, connection);
        command.Parameters.Add("@speakerId", SqlDbType.NVarChar, 80).Value = speakerId;
        command.Parameters.Add("@sessionizeMoniker", SqlDbType.NVarChar, 256).Value = sessionizeMoniker;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        PublicSpeakerProfile? profile = null;
        var sessions = new List<PublicSpeakerSession>();
        var sessionKeys = new HashSet<(int Year, string Moniker, string SessionId)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!Guid.TryParse(reader.GetString(0), out var id))
            {
                throw new InvalidOperationException("Il database contiene un ID Sessionize non valido per un relatore.");
            }

            var firstName = reader.GetString(1);
            var lastName = reader.GetString(2);
            if (profile is not null && profile.Id != id)
            {
                throw new InvalidOperationException(
                    $"Il moniker Sessionize '{sessionizeMoniker}' è associato a più profili speaker.");
            }

            profile ??= new PublicSpeakerProfile(
                id,
                string.IsNullOrWhiteSpace($"{firstName} {lastName}")
                    ? reader.GetString(3)
                    : $"{firstName} {lastName}".Trim(),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                sessions);

            if (!reader.IsDBNull(13))
            {
                var eventYear = reader.GetInt32(9);
                var eventMoniker = reader.GetString(10);
                var sessionizeSessionId = reader.GetString(16);
                var title = reader.GetString(13);
                if (sessionKeys.Add((eventYear, eventMoniker, sessionizeSessionId)))
                {
                    sessions.Add(new PublicSpeakerSession(
                        eventYear,
                        eventMoniker,
                        reader.GetString(11),
                        reader.GetString(12),
                        title,
                        reader.GetString(14),
                        reader.IsDBNull(15) ? null : reader.GetDateTimeOffset(15)));
                }
            }
        }

        return profile;
    }

    public async Task SaveAsync(EventDefinition item, CancellationToken cancellationToken = default)
    {
        if (item.Year is < 2000 or > 9999 ||
            string.IsNullOrWhiteSpace(item.Moniker) ||
            item.Moniker.Length > 80 ||
            item.Moniker.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-') ||
            string.IsNullOrWhiteSpace(item.Name) ||
            item.Name.Length > 128 ||
            !DateTime.TryParse(item.EventDate, out _))
        {
            throw new ArgumentException("I dati obbligatori dell'evento non sono validi.", nameof(item));
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Events
            SET Year = @year, Moniker = @moniker,
                IsPublished = @isPublished,
                IsCurrent = CASE WHEN @isPublished = 0 THEN 0 ELSE IsCurrent END,
                Name = @name,
                EventDate = @eventDate,
                CallForPapersOpenAt = @cfpOpen, CallForPapersCloseAt = @cfpClose,
                CallForPapersUrl = @cfpUrl,
                SessionizeMoniker = @sessionizeMoniker, SessionizeJsonUri = @sessionizeJsonUri,
                Description = @description
            WHERE ID = @id;
            IF @@ROWCOUNT = 0
            BEGIN
                INSERT INTO community.Events
                    (Year, Moniker, IsCurrent, IsPublished, Name, EventDate,
                     CallForPapersOpenAt, CallForPapersCloseAt, CallForPapersUrl,
                     SessionizeMoniker, SessionizeJsonUri, Description)
                VALUES
                    (@year, @moniker, 0, @isPublished, @name, @eventDate,
                     @cfpOpen, @cfpClose, @cfpUrl, @sessionizeMoniker, @sessionizeJsonUri, @description);
            END;
            """, connection);
        AddEventParameters(command, item);
        command.Parameters.Add("@id", SqlDbType.Int).Value = item.Id;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> SetPublishedAsync(int id, bool isPublished, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Events
            SET IsPublished = @isPublished,
                IsCurrent = CASE WHEN @isPublished = 0 THEN 0 ELSE IsCurrent END
            WHERE ID = @id;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@isPublished", SqlDbType.Bit).Value = isPublished;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<bool> SetCancelledAsync(int id, bool isCancelled, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Events
            SET IsCancelled = @isCancelled,
                IsCurrent = CASE WHEN @isCancelled = 1 THEN 0 ELSE IsCurrent END
            WHERE ID = @id;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@isCancelled", SqlDbType.Bit).Value = isCancelled;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    /// <summary>Blocks (or unblocks) changes to registrations by participants for an event.</summary>
    public async Task<bool> SetLockedAsync(int id, bool isLocked, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Events SET IsLocked = @isLocked WHERE ID = @id;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        command.Parameters.Add("@isLocked", SqlDbType.Bit).Value = isLocked;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    /// <summary>
    /// Replaces the check-in signature of the event with a new random one and returns it. Called every time a QR code is
    /// generated for printing: the QR codes printed before stop working.
    /// </summary>
    public async Task<Guid?> RotateCheckInTokenAsync(int eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Events
            SET CheckInToken = NEWID()
            OUTPUT inserted.CheckInToken
            WHERE ID = @id AND IsPublished = 1 AND IsCancelled = 0;
            """, connection);
        command.Parameters.Add("@id", SqlDbType.Int).Value = eventId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid token ? token : null;
    }

    public async Task<bool> SetCurrentAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE community.Events SET IsCurrent = 0;
            UPDATE community.Events
            SET IsCurrent = 1
            WHERE ID = @id AND IsPublished = 1 AND IsCancelled = 0;
            SELECT @@ROWCOUNT;
            """, connection, transaction);
        command.Parameters.Add("@id", SqlDbType.Int).Value = id;
        var affected = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (affected == 1)
        {
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        await transaction.RollbackAsync(cancellationToken);
        return false;
    }

    private static EventDefinition ReadEvent(SqlDataReader reader)
    {
        return new EventDefinition
        {
            Id = reader.GetInt32(0),
            Year = reader.GetInt32(1),
            Moniker = reader.GetString(2),
            IsCurrent = reader.GetBoolean(3),
            IsPublished = reader.GetBoolean(4),
            Name = reader.GetString(5),
            EventDate = reader.GetDateTime(6).ToString("yyyy-MM-dd"),
            CallForPapersOpenAt = reader.IsDBNull(7) ? string.Empty : reader.GetDateTimeOffset(7).ToString("O"),
            CallForPapersCloseAt = reader.IsDBNull(8) ? string.Empty : reader.GetDateTimeOffset(8).ToString("O"),
            CallForPapersUrl = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            SessionizeUrl = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            Description = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            SessionizeJsonUri = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            CheckInToken = reader.GetGuid(13),
            IsCancelled = reader.GetBoolean(14),
            IsLocked = reader.GetBoolean(15)
        };
    }

    private static void AddEventParameters(SqlCommand command, EventDefinition item)
    {
        command.Parameters.Add("@year", SqlDbType.Int).Value = item.Year;
        command.Parameters.Add("@moniker", SqlDbType.NVarChar, 80).Value = item.Moniker;
        command.Parameters.Add("@isPublished", SqlDbType.Bit).Value = item.IsPublished;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = item.Name;
        command.Parameters.Add("@eventDate", SqlDbType.DateTime).Value = DateTime.Parse(item.EventDate);
        command.Parameters.Add("@cfpOpen", SqlDbType.DateTimeOffset).Value = ParseOptionalDate(item.CallForPapersOpenAt);
        command.Parameters.Add("@cfpClose", SqlDbType.DateTimeOffset).Value = ParseOptionalDate(item.CallForPapersCloseAt);
        command.Parameters.Add("@cfpUrl", SqlDbType.NVarChar, 512).Value = item.CallForPapersUrl;
        command.Parameters.Add("@sessionizeMoniker", SqlDbType.NVarChar, 64).Value = item.SessionizeUrl;
        command.Parameters.Add("@sessionizeJsonUri", SqlDbType.NVarChar, 128).Value = item.SessionizeJsonUri;
        command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = RichText.ToHtml(item.Description);
    }

    private static object ParseOptionalDate(string value) =>
        DateTimeOffset.TryParse(value, out var date) ? date : DBNull.Value;

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

    private static string NormalizeResourceUrl(string? value) => value?.Trim() ?? string.Empty;
}
