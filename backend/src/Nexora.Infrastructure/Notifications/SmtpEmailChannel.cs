using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Canal email réel via MailKit / SMTP (Phase 34 §3).
/// Compatible avec tous les relais SMTP standard : SendGrid, Mailgun, Amazon SES, Brevo, serveur
/// propre. Si le canal n'est pas activé (<c>Notifications:Smtp:Enabled = false</c>) ou si la
/// configuration est incomplète, la remise est journalisée et retournée comme erreur non-réessayable
/// (évite de bloquer l'outbox indéfiniment sur un environnement de dev sans SMTP).
/// </summary>
public sealed class SmtpEmailChannel(
    IOptions<SmtpOptions> opts,
    ILogger<SmtpEmailChannel> logger) : INotificationChannel
{
    public string Channel => "email";

    public async Task<ChannelSendResult> SendAsync(OutboxMessageDto m, CancellationToken ct = default)
    {
        var o = opts.Value;

        // Canal désactivé ou mal configuré → pas de retry (Fail, non Retry) :
        // l'outbox continuerait à replanifier indéfiniment sans jamais réussir.
        if (!o.Enabled || string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.FromAddress))
        {
            logger.LogInformation("[email-smtp] canal non configuré — log seulement → {Address} · {Subject}",
                m.Address, m.Subject);
            return ChannelSendResult.Fail("Canal SMTP non configuré (Notifications:Smtp:Enabled=false).");
        }

        if (string.IsNullOrWhiteSpace(m.Address))
            return ChannelSendResult.Retry($"Adresse email absente pour la notification {m.Id}.");

        var message = BuildMessage(o.FromAddress, m);

        try
        {
            using var client = new SmtpClient();
            var sslMode = o.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(o.Host, o.Port, sslMode, ct);

            if (!string.IsNullOrWhiteSpace(o.UserName))
                await client.AuthenticateAsync(o.UserName, o.Password, ct);

            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            logger.LogInformation("[email-smtp] envoyé → {Address} · {Subject}", m.Address, m.Subject);
            return ChannelSendResult.Ok();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Erreurs réseau / auth → réessayable ; erreurs de format → définitives.
            var transient = ex is MailKit.Net.Smtp.SmtpCommandException smtpEx
                            && (int)smtpEx.StatusCode >= 400 && (int)smtpEx.StatusCode < 500
                ? false   // 4xx permanent
                : true;   // erreur réseau, timeout, auth temporaire → réessayer

            logger.LogWarning(ex, "[email-smtp] échec d'envoi → {Address} · {Subject}", m.Address, m.Subject);
            return transient
                ? ChannelSendResult.Retry(ex.Message)
                : ChannelSendResult.Fail(ex.Message);
        }
    }

    /// <summary>Construit un <see cref="MimeMessage"/> HTML + texte brut depuis le DTO outbox.</summary>
    private static MimeMessage BuildMessage(string fromAddress, OutboxMessageDto m)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(fromAddress));
        message.To.Add(MailboxAddress.Parse(m.Address!));   // adresse vérifiée non vide par SendAsync
        message.Subject = m.Subject;

        // Le corps est stocké soit en HTML pur (commence par "<") soit en texte brut.
        var bodyIsHtml = m.Body.TrimStart().StartsWith('<');

        var bodyBuilder = new BodyBuilder();
        if (bodyIsHtml)
        {
            bodyBuilder.HtmlBody = m.Body;
            // Version texte brut de secours : suppression naïve des balises HTML.
            bodyBuilder.TextBody = System.Text.RegularExpressions.Regex.Replace(m.Body, "<[^>]+>", " ")
                .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">").Trim();
        }
        else
        {
            bodyBuilder.TextBody = m.Body;
        }

        message.Body = bodyBuilder.ToMessageBody();
        return message;
    }
}
