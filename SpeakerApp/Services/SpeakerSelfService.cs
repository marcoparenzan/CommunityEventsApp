using System.Data;
using Microsoft.Data.SqlClient;

namespace SpeakerApp.Services;

public sealed record MySessionItem(
    string SessionId, string Title, string Room, DateTimeOffset StartsAt,
    string Description, bool DescriptionEdited, string PresentationUrl, string RepositoryUrl, bool HasCover = false);

public sealed record MyEventItem(
    int EventId, int Year, string Moniker, string Name, DateTime EventDate, bool IsPublished,
    bool AttendsDinner, int DinnerGuests, int? HotelId, IReadOnlyList<MySessionItem> Sessions,
    string? TemplateFileName = null,
    string HotelNote = "", string? TShirtSize = null, bool CollectsTShirts = false, string WhatsAppUrl = "");

public sealed record HotelOption(int Id, string Name, string Url);

/// <summary>What a speaker can change about their own events and sessions (always limited to their own account).</summary>
public sealed class SpeakerSelfService(SqlConnectionFactory connections, ILogger<SpeakerSelfService> logger)
{
    public const int MaximumDinnerGuests = 10;
    public const int MaximumDescriptionLength = 8000;
    public const int MaximumHotelNoteLength = 256;

    /// <summary>The name of the "Altro" entry of the hotels: it enables the free note.</summary>
    public const string OtherHotelName = "Altro";

    /// <summary>European sizes accepted for the T-shirt; "N/A" = not needed.</summary>
    public static readonly string[] TShirtSizes = ["XS", "S", "M", "L", "XL", "XXL", "3XL", "N/A"];

    public async Task<IReadOnlyList<HotelOption>> GetHotelsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT ID, Nome, Url FROM community.Alberghi ORDER BY Nome;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var hotels = new List<HotelOption>();
        while (await reader.ReadAsync(cancellationToken))
        {
            hotels.Add(new HotelOption(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return hotels;
    }

    public async Task<IReadOnlyList<MyEventItem>> GetMyEventsAsync(int speakerId, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);

        var sessionsByEvent = new Dictionary<int, List<MySessionItem>>();
        await using (var sessionsCommand = new SqlCommand("""
            SELECT DISTINCT session.EventID, session.SessionizeSessionID, session.Title, session.Room, session.StartsAt,
                   COALESCE(NULLIF(session.SpeakerDescription, N''), session.Description, N''),
                   CASE WHEN NULLIF(session.SpeakerDescription, N'') IS NULL THEN 0 ELSE 1 END,
                   ISNULL(session.PresentationUrl, N''), ISNULL(session.RepositoryUrl, N''),
                   CASE WHEN session.CoverAssetID IS NULL THEN 0 ELSE 1 END
            FROM community.EventSpeakers eventSpeaker
            INNER JOIN community.EventSessionSpeakers sessionSpeaker
                ON sessionSpeaker.EventID = eventSpeaker.EventID AND sessionSpeaker.EventSpeakerID = eventSpeaker.ID
            INNER JOIN community.EventSessions session
                ON session.EventID = sessionSpeaker.EventID AND session.SessionizeSessionID = sessionSpeaker.SessionizeSessionID
            WHERE eventSpeaker.SpeakerID = @speakerId AND session.IsActive = 1;
            """, connection))
        {
            sessionsCommand.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
            await using var reader = await sessionsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventId = reader.GetInt32(0);
                if (!sessionsByEvent.TryGetValue(eventId, out var list))
                {
                    sessionsByEvent[eventId] = list = [];
                }

                list.Add(new MySessionItem(
                    reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetDateTimeOffset(4),
                    reader.GetString(5), reader.GetInt32(6) == 1, reader.GetString(7), reader.GetString(8), reader.GetInt32(9) == 1));
            }
        }

        var events = new List<MyEventItem>();
        await using (var eventsCommand = new SqlCommand("""
            SELECT e.ID, e.[Year], e.Moniker, e.Name, e.EventDate, e.IsPublished,
                   MAX(CONVERT(int, eventSpeaker.PartecipaCenaSpeaker)),
                   MAX(eventSpeaker.PartecipantiAggiuntiviCenaSpeaker),
                   MAX(eventSpeaker.AlbergoID),
                   template.FileName,
                   MAX(eventSpeaker.AlbergoNote), MAX(eventSpeaker.TShirtSize), e.CollectsTShirtSizes, e.SpeakerWhatsAppUrl
            FROM community.EventSpeakers eventSpeaker
            INNER JOIN community.Events e ON e.ID = eventSpeaker.EventID
            LEFT JOIN community.MediaAssets template ON template.ID = e.SlideTemplateAssetID
            WHERE eventSpeaker.SpeakerID = @speakerId
            GROUP BY e.ID, e.[Year], e.Moniker, e.Name, e.EventDate, e.IsPublished, template.FileName, e.CollectsTShirtSizes, e.SpeakerWhatsAppUrl
            ORDER BY e.EventDate DESC;
            """, connection))
        {
            eventsCommand.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
            await using var reader = await eventsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var eventId = reader.GetInt32(0);
                events.Add(new MyEventItem(
                    eventId, reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetDateTime(4), reader.GetBoolean(5),
                    reader.GetInt32(6) == 1, reader.GetInt32(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                    sessionsByEvent.TryGetValue(eventId, out var sessions) ? sessions.OrderBy(s => s.StartsAt).ToList() : [],
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetBoolean(12), reader.GetString(13)));
            }
        }

        return events;
    }

    /// <summary>Speaker dinner and hotel of an event. Throws <see cref="InvalidOperationException"/> with a user-facing message.</summary>
    public async Task<bool> SaveEventInfoAsync(
        int speakerId, int eventId, bool attendsDinner, int guests, int? hotelId, string? hotelNote, string? tShirtSize,
        CancellationToken cancellationToken = default)
    {
        if (guests < 0 || guests > MaximumDinnerGuests)
        {
            throw new InvalidOperationException($"Gli accompagnatori alla cena possono essere da 0 a {MaximumDinnerGuests}.");
        }

        hotelNote = (hotelNote ?? string.Empty).Trim();
        if (hotelNote.Length > MaximumHotelNoteLength)
        {
            throw new InvalidOperationException($"La nota sull'albergo può avere al massimo {MaximumHotelNoteLength} caratteri.");
        }

        tShirtSize = string.IsNullOrWhiteSpace(tShirtSize) ? null : tShirtSize.Trim().ToUpperInvariant();
        if (tShirtSize is not null && !TShirtSizes.Contains(tShirtSize))
        {
            throw new InvalidOperationException("La taglia della maglia non è valida.");
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        var isOtherHotel = false;
        if (hotelId is int id)
        {
            await using var check = new SqlCommand("SELECT Nome FROM community.Alberghi WHERE ID = @id;", connection);
            check.Parameters.Add("@id", SqlDbType.Int).Value = id;
            var name = await check.ExecuteScalarAsync(cancellationToken) as string
                       ?? throw new InvalidOperationException("L'albergo selezionato non esiste.");
            isOtherHotel = name.Equals(OtherHotelName, StringComparison.OrdinalIgnoreCase);
        }

        if (!isOtherHotel)
        {
            hotelNote = string.Empty;
        }

        await using var command = new SqlCommand("""
            UPDATE community.EventSpeakers
            SET PartecipaCenaSpeaker = @attends,
                PartecipantiAggiuntiviCenaSpeaker = CASE WHEN @attends = 1 THEN @guests ELSE 0 END,
                AlbergoID = @hotel,
                AlbergoNote = @hotelNote,
                TShirtSize = @tShirt
            WHERE EventID = @eventId AND SpeakerID = @speakerId;
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@attends", SqlDbType.Bit).Value = attendsDinner;
        command.Parameters.Add("@guests", SqlDbType.Int).Value = guests;
        command.Parameters.Add("@hotel", SqlDbType.Int).Value = hotelId is int value ? value : DBNull.Value;
        command.Parameters.Add("@hotelNote", SqlDbType.NVarChar, MaximumHotelNoteLength).Value = hotelNote;
        command.Parameters.Add("@tShirt", SqlDbType.NVarChar, 8).Value = tShirtSize is null ? DBNull.Value : tShirtSize;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        var saved = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
        logger.LogInformation(
            "Speaker {SpeakerId} saved the event info of event {EventId}: dinner={Dinner}, guests={Guests}, hotel={Hotel}, tshirt={TShirt}, saved={Saved}.",
            speakerId, eventId, attendsDinner, guests, hotelId, tShirtSize, saved);
        return saved;
    }

    /// <summary>
    /// Saves description and links of one of the speaker's own sessions. The description is stored apart from the one
    /// imported from Sessionize, so the next synchronization does not overwrite it; an empty text restores Sessionize's.
    /// </summary>
    public async Task<bool> SaveSessionAsync(
        int speakerId, int eventId, string sessionId, string? description, string? presentationUrl, string? repositoryUrl,
        CancellationToken cancellationToken = default)
    {
        description = (description ?? string.Empty).Trim();
        presentationUrl = (presentationUrl ?? string.Empty).Trim();
        repositoryUrl = (repositoryUrl ?? string.Empty).Trim();
        if (description.Length > MaximumDescriptionLength)
        {
            throw new InvalidOperationException($"La descrizione può avere al massimo {MaximumDescriptionLength} caratteri.");
        }

        ValidateLink(presentationUrl, "slide");
        ValidateLink(repositoryUrl, "repository");

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            UPDATE session
            SET SpeakerDescription = CASE WHEN @description = N'' OR @description = ISNULL(session.Description, N'') THEN NULL ELSE @description END,
                PresentationUrl = @presentationUrl,
                RepositoryUrl = @repositoryUrl
            FROM community.EventSessions session
            WHERE session.EventID = @eventId AND session.SessionizeSessionID = @sessionId AND session.IsActive = 1
              AND EXISTS (
                  SELECT 1
                  FROM community.EventSessionSpeakers sessionSpeaker
                  INNER JOIN community.EventSpeakers eventSpeaker
                      ON eventSpeaker.ID = sessionSpeaker.EventSpeakerID AND eventSpeaker.EventID = sessionSpeaker.EventID
                  WHERE sessionSpeaker.EventID = session.EventID
                    AND sessionSpeaker.SessionizeSessionID = session.SessionizeSessionID
                    AND eventSpeaker.SpeakerID = @speakerId);
            SELECT @@ROWCOUNT;
            """, connection);
        command.Parameters.Add("@description", SqlDbType.NVarChar, -1).Value = description;
        command.Parameters.Add("@presentationUrl", SqlDbType.NVarChar, 512).Value = presentationUrl;
        command.Parameters.Add("@repositoryUrl", SqlDbType.NVarChar, 512).Value = repositoryUrl;
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        command.Parameters.Add("@sessionId", SqlDbType.NVarChar, 80).Value = sessionId.Trim();
        command.Parameters.Add("@speakerId", SqlDbType.Int).Value = speakerId;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static void ValidateLink(string value, string name)
    {
        if (value.Length == 0)
        {
            return;
        }

        if (value.Length > 512 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"Il link alle {name} deve essere un URL HTTPS valido (massimo 512 caratteri).");
        }
    }
}
