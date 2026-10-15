using System.Data;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace SpeakerApp.Services;

public sealed class EventReportService(SqlConnectionFactory connections)
{
    public async Task<EventSummaryReport> GetSummaryAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT
                ISNULL(SUM(CASE WHEN Status = N'Registered' THEN 1 ELSE 0 END), 0),
                ISNULL(SUM(CASE WHEN Status = N'AwaitingConfirmation' THEN 1 ELSE 0 END), 0),
                ISNULL(SUM(CASE WHEN Status = N'Registered' AND CheckedInAt IS NOT NULL THEN 1 ELSE 0 END), 0),
                ISNULL(SUM(CASE WHEN Status = N'Cancelled' THEN 1 ELSE 0 END), 0),
                ISNULL(SUM(CASE WHEN Status <> N'Cancelled' AND IsEatingPizza = 1 THEN 1 ELSE 0 END), 0),
                ISNULL(SUM(CASE WHEN Status <> N'Cancelled' AND IsEatingPizza = 1 AND LactoseIntolerant = 1 THEN 1 ELSE 0 END), 0)
            FROM community.UserEventRegistration
            WHERE EventID = @eventId;

            SELECT COUNT(*), COUNT(SpeakerID)
            FROM community.EventSpeakers
            WHERE EventID = @eventId;

            SELECT COUNT(*),
                   ISNULL(SUM(CASE WHEN ISNULL(VideoUrl, N'') <> N'' THEN 1 ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN ISNULL(PresentationUrl, N'') <> N'' THEN 1 ELSE 0 END), 0),
                   ISNULL(SUM(CASE WHEN ISNULL(RepositoryUrl, N'') <> N'' THEN 1 ELSE 0 END), 0)
            FROM community.EventSessions
            WHERE EventID = @eventId AND IsActive = 1;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var registered = reader.GetInt32(0);
        var awaiting = reader.GetInt32(1);
        var checkedIn = reader.GetInt32(2);
        var cancelled = reader.GetInt32(3);
        var eating = reader.GetInt32(4);
        var lactose = reader.GetInt32(5);
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var speakers = reader.GetInt32(0);
        var linked = reader.GetInt32(1);
        await reader.NextResultAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new EventSummaryReport(
            registered, awaiting, checkedIn, cancelled, eating, lactose,
            speakers, linked,
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    public async Task<IReadOnlyList<ReportSessionRow>> GetSessionsAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT s.Title, s.Room, s.StartsAt,
                   ISNULL((
                       SELECT STRING_AGG(sp.Name, N', ') WITHIN GROUP (ORDER BY sp.Name)
                       FROM community.EventSessionSpeakers link
                       INNER JOIN community.EventSpeakers sp ON sp.ID = link.EventSpeakerID
                       WHERE link.EventID = s.EventID AND link.SessionizeSessionID = s.SessionizeSessionID), N''),
                   CASE WHEN ISNULL(s.VideoUrl, N'') <> N'' THEN 1 ELSE 0 END,
                   CASE WHEN ISNULL(s.PresentationUrl, N'') <> N'' THEN 1 ELSE 0 END,
                   CASE WHEN ISNULL(s.RepositoryUrl, N'') <> N'' THEN 1 ELSE 0 END
            FROM community.EventSessions s
            WHERE s.EventID = @eventId AND s.IsActive = 1
            ORDER BY s.Room, s.StartsAt, s.Title;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ReportSessionRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ReportSessionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetDateTimeOffset(2),
                reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5) == 1,
                reader.GetInt32(6) == 1));
        }

        return rows;
    }

    /// <summary>Speaker of the event with the T-shirt size they indicated (null when not indicated).</summary>
    public async Task<IReadOnlyList<(string Name, string? Size)>> GetTShirtSizesAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT Name, TShirtSize FROM community.EventSpeakers WHERE EventID = @eventId ORDER BY Name;", connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(string, string?)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        }

        return rows;
    }

    public async Task<IReadOnlyList<ReportSpeakerRow>> GetSpeakersAsync(
        int eventId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT sp.Name, ISNULL(account.EMail, N''),
                   (SELECT COUNT(*)
                    FROM community.EventSessionSpeakers link
                    INNER JOIN community.EventSessions s
                        ON s.EventID = link.EventID AND s.SessionizeSessionID = link.SessionizeSessionID AND s.IsActive = 1
                    WHERE link.EventSpeakerID = sp.ID)
            FROM community.EventSpeakers sp
            LEFT JOIN community.Speakers account ON account.ID = sp.SpeakerID
            WHERE sp.EventID = @eventId
            ORDER BY sp.Name;
            """, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ReportSpeakerRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ReportSpeakerRow(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return rows;
    }
}
