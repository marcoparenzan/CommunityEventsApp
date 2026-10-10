using System.Globalization;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using EventsCommon.Models;

namespace EventsCommon.Services;

public sealed class SmtpEmailSender(IConfiguration configuration)
{
    /// <summary>Footer added to every email: how to stop event communications or delete the account.</summary>
    private string Footer()
    {
        var baseUrl = configuration["Application:PublicBaseUrl"];
        var profile = string.IsNullOrWhiteSpace(baseUrl)
            ? "il tuo profilo sul sito 1nn0va"
            : new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "account/profile").AbsoluteUri;
        return "\r\n\r\n--\r\nVuoi non ricevere più le comunicazioni sugli eventi o cancellarti dal sistema? " +
               $"Accedi a {profile}: puoi disattivare le comunicazioni oppure scegliere \"Cancella il mio account\".";
    }

    public async Task SendPinAsync(string recipient, string pin, string? loginUrl = null, CancellationToken cancellationToken = default)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(GetFromAddress()));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "Il tuo codice di accesso 1nn0va";
        message.Body = new TextPart("plain")
        {
            Text = string.IsNullOrWhiteSpace(loginUrl)
                ? $"Il tuo codice di accesso 1nn0va è: {pin}\r\n\r\nInseriscilo nel modulo di accesso. Il codice resta valido fino alla richiesta di un nuovo codice." + Footer()
                : $"Il tuo codice di accesso 1nn0va è: {pin}\r\n\r\nPuoi inserirlo nel modulo di accesso oppure aprire questo link e premere il pulsante di conferma:\r\n\r\n{loginUrl}\r\n\r\nIl codice resta valido fino alla richiesta di un nuovo codice. Se non hai chiesto tu di accedere, ignora questa email." + Footer()
        };

        await SendAsync(message, cancellationToken);
    }

    /// <summary>Tells a person how to log in to the personal area (the PIN is requested from the site).</summary>
    public async Task SendLoginInvitationAsync(string recipient, string? firstName, CancellationToken cancellationToken = default)
    {
        var baseUrl = configuration["Application:PublicBaseUrl"];
        var loginUrl = string.IsNullOrWhiteSpace(baseUrl)
            ? "il sito 1nn0va"
            : new Uri(new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/"), "account").AbsoluteUri;
        var greeting = string.IsNullOrWhiteSpace(firstName) ? "Ciao," : $"Ciao {firstName.Trim()},";
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(GetFromAddress()));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = "Accedi alla tua area personale 1nn0va";
        message.Body = new TextPart("plain")
        {
            Text = $"{greeting}\r\n\r\nper accedere alla tua area personale degli eventi 1nn0va apri {loginUrl}, " +
                   "inserisci la tua email e il PIN che ti verrà inviato: da lì puoi vedere gli eventi, " +
                   "gestire le tue iscrizioni e consultare agenda e materiali.\r\n\r\n1nn0va" + Footer()
        };

        await SendAsync(message, cancellationToken);
    }

    public async Task SendEventActionConfirmationAsync(
        string recipient,
        string eventName,
        string eventDate,
        string confirmationUrl,
        bool isCheckIn,
        CancellationToken cancellationToken = default)
    {
        var formattedDate = DateOnly.TryParseExact(
            eventDate,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate)
            ? parsedDate.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("it-IT"))
            : eventDate;
        var actionText = isCheckIn ? "confermare il tuo check-in" : "confermare la tua iscrizione";
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(GetFromAddress()));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = isCheckIn
            ? $"Conferma check-in: {eventName}"
            : $"Conferma iscrizione: {eventName}";
        message.Body = new TextPart("plain")
        {
            Text = $"Ciao,\r\n\r\nper {actionText} a {eventName} ({formattedDate}), apri il link e premi il pulsante di conferma:\r\n\r\n{confirmationUrl}\r\n\r\nIl link scade tra 30 minuti. Se non hai richiesto questa operazione, ignora questa email.\r\n\r\n1nn0va" + Footer()
        };

        await SendAsync(message, cancellationToken);
    }

    private async Task SendAsync(MimeMessage message, CancellationToken cancellationToken)
    {
        var host = configuration["Email:Smtp:SMTPHostName"];
        var portValue = configuration["Email:Smtp:SMTPPort"];
        var username = configuration["Email:Smtp:Username"];
        var password = configuration["Email:Smtp:Password"];

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new EmailDeliveryException("Configura Email:Smtp:SMTPHostName (impostazione Email__Smtp__SMTPHostName su Azure).");
        }

        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        {
            throw new EmailDeliveryException("Email:Smtp:SMTPPort deve essere una porta valida.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new EmailDeliveryException(
                "Configura Email:Smtp:Username e Email:Smtp:Password (secrets.json in sviluppo, impostazioni Email__Smtp__Username e Email__Smtp__Password su Azure).");
        }

        using var client = new SmtpClient
        {
            Timeout = 20_000
        };

        try
        {
            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(username, password, cancellationToken);
            await client.SendAsync(message, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EmailDeliveryException("Errore durante l'invio dell'email.", ex);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, CancellationToken.None);
            }
        }
    }

    public async Task<MailSendResult> SendBulkAsync(
        IReadOnlyList<MailRecipient> recipients,
        string subject,
        Func<MailRecipient, string> bodyFor,
        CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient { Timeout = 20_000 };
        var sent = 0;
        var failed = 0;
        try
        {
            await ConnectAsync(client, cancellationToken);
            var from = MailboxAddress.Parse(GetFromAddress());
            foreach (var recipient in recipients)
            {
                try
                {
                    var message = new MimeMessage();
                    message.From.Add(from);
                    message.To.Add(string.IsNullOrWhiteSpace(recipient.Name)
                        ? MailboxAddress.Parse(recipient.Email)
                        : new MailboxAddress(recipient.Name, recipient.Email));
                    message.Subject = subject;
                    message.Body = new TextPart("plain") { Text = bodyFor(recipient) + Footer() };
                    await client.SendAsync(message, cancellationToken);
                    sent++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception) when (client.IsConnected)
                {
                    failed++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EmailDeliveryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new EmailDeliveryException("Errore durante l'invio delle email.", ex);
        }
        finally
        {
            if (client.IsConnected)
            {
                await client.DisconnectAsync(true, CancellationToken.None);
            }
        }

        return new MailSendResult(sent, failed);
    }

    private async Task ConnectAsync(SmtpClient client, CancellationToken cancellationToken)
    {
        var host = configuration["Email:Smtp:SMTPHostName"];
        var portValue = configuration["Email:Smtp:SMTPPort"];
        var username = configuration["Email:Smtp:Username"];
        var password = configuration["Email:Smtp:Password"];

        if (string.IsNullOrWhiteSpace(host))
        {
            throw new EmailDeliveryException("Configura Email:Smtp:SMTPHostName (impostazione Email__Smtp__SMTPHostName su Azure).");
        }

        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        {
            throw new EmailDeliveryException("Email:Smtp:SMTPPort deve essere una porta valida.");
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            throw new EmailDeliveryException(
                "Configura Email:Smtp:Username e Email:Smtp:Password (secrets.json in sviluppo, impostazioni Email__Smtp__Username e Email__Smtp__Password su Azure).");
        }

        await client.ConnectAsync(host, port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(username, password, cancellationToken);
    }

    private string GetFromAddress()
    {
        var fromAddress = configuration["Email:Smtp:FromAddress"];
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            throw new EmailDeliveryException(
                "Configura Email:Smtp:FromAddress (secrets.json in sviluppo, impostazione Email__Smtp__FromAddress su Azure).");
        }

        return fromAddress;
    }
}
