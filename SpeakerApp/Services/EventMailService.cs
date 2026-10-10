using System.Data;
using Microsoft.Data.SqlClient;
using EventsCommon.Models;

namespace SpeakerApp.Services;

public sealed class EventMailService(SqlConnectionFactory connections, SmtpEmailSender emailSender)
{
    public const int MaxRecipients = 500;

    public static readonly IReadOnlyList<(string Key, string Label)> Audiences =
    [
        ("all", "Utenti registrati"),
        ("awaiting", "Utenti non confermati"),
        ("registered", "Utenti confermati"),
        ("lactose", "Utenti intolleranti al lattosio"),
        ("checkedin", "Con check-in effettuato"),
        ("notcheckedin", "Confermati senza check-in"),
        ("speakers", "Speaker dell'evento")
    ];

    public async Task<IReadOnlyList<MailRecipient>> GetRecipientsAsync(
        int eventId,
        string audience,
        CancellationToken cancellationToken = default)
    {
        string sql;
        if (audience == "speakers")
        {
            sql = """
                SELECT DISTINCT account.EMail,
                       LTRIM(RTRIM(ISNULL(account.FirstName, N'') + N' ' + ISNULL(account.LastName, N'')))
                FROM community.EventSpeakers sp
                INNER JOIN community.Speakers account ON account.ID = sp.SpeakerID
                WHERE sp.EventID = @eventId AND account.EMail LIKE N'%_@_%.__%'
                ORDER BY account.EMail;
                """;
        }
        else
        {
            var filter = audience switch
            {
                "all" => "registration.Status <> N'Cancelled'",
                "registered" => "registration.Status = N'Registered'",
                "lactose" => "registration.Status <> N'Cancelled' AND registration.LactoseIntolerant = 1",
                "checkedin" => "registration.Status = N'Registered' AND registration.CheckedInAt IS NOT NULL",
                "notcheckedin" => "registration.Status = N'Registered' AND registration.CheckedInAt IS NULL",
                "awaiting" => "registration.Status = N'AwaitingConfirmation'",
                _ => throw new ArgumentException("Destinatari non validi.", nameof(audience))
            };
            sql = $"""
                SELECT DISTINCT users.EMail,
                       LTRIM(RTRIM(ISNULL(users.FirstName, N'') + N' ' + ISNULL(users.LastName, N'')))
                FROM community.UserEventRegistration registration
                INNER JOIN community.Users users ON users.ID = registration.UserID
                WHERE registration.EventID = @eventId AND {filter}
                  AND users.EMail LIKE N'%_@_%.__%'
                  AND users.IsDeleted = 0
                  AND users.AllowEventsNotifications = 1
                ORDER BY users.EMail;
                """;
        }

        await using var connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@eventId", SqlDbType.Int).Value = eventId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var recipients = new List<MailRecipient>();
        while (await reader.ReadAsync(cancellationToken))
        {
            recipients.Add(new MailRecipient(reader.GetString(0), reader.GetString(1)));
        }

        return recipients;
    }

    public async Task<MailSendResult> SendAsync(
        EventDefinition eventInfo,
        string audience,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        subject = (subject ?? string.Empty).Trim();
        body = (body ?? string.Empty).Trim();
        if (subject.Length is 0 or > 200)
        {
            throw new ArgumentException("L'oggetto è obbligatorio e non può superare 200 caratteri.");
        }

        if (body.Length is 0 or > 10_000)
        {
            throw new ArgumentException("Il messaggio è obbligatorio e non può superare 10.000 caratteri.");
        }

        var recipients = await GetRecipientsAsync(eventInfo.Id, audience, cancellationToken);
        if (recipients.Count == 0)
        {
            throw new ArgumentException("Nessun destinatario per la selezione scelta.");
        }

        if (recipients.Count > MaxRecipients)
        {
            throw new ArgumentException($"Troppi destinatari ({recipients.Count}): il massimo per invio è {MaxRecipients}.");
        }

        return await emailSender.SendBulkAsync(
            recipients,
            subject.Replace("{evento}", eventInfo.Name, StringComparison.OrdinalIgnoreCase),
            recipient => body
                .Replace("{evento}", eventInfo.Name, StringComparison.OrdinalIgnoreCase)
                .Replace("{nome}", recipient.Name, StringComparison.OrdinalIgnoreCase),
            cancellationToken);
    }
}
