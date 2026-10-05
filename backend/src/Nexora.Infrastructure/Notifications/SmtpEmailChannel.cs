using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Nexora.Application.Integrations;
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
    public string ProviderName => "SMTP";

    public bool IsConfigured
    {
        get { var o = opts.Value; return o.Enabled && !string.IsNullOrWhiteSpace(o.Host) && !string.IsNullOrWhiteSpace(o.FromAddress); }
    }

    public string ConfigurationSummary
    {
        get
        {
            var o = opts.Value;
            if (!IsConfigured)
                return "Non configuré : renseignez Notifications:Smtp (Enabled, Host, Port, FromAddress, UserName, Password) dans la configuration du serveur.";
            return $"{o.Host}:{o.Port} · {(o.UseSsl ? "SSL/TLS" : "STARTTLS")} · expéditeur {o.FromAddress}"
                   + (string.IsNullOrWhiteSpace(o.UserName) ? " · sans authentification" : $" · utilisateur {NotificationMasks.Secret(o.UserName)}");
        }
    }

    public async Task<ChannelTestResultDto> TestAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return new ChannelTestResultDto(Channel, ConnectionStatus.NotConfigured, ConfigurationSummary, 0);
        var o = opts.Value;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(o.Host, o.Port, o.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, cts.Token);
            if (!string.IsNullOrWhiteSpace(o.UserName)) await client.AuthenticateAsync(o.UserName, o.Password, cts.Token);
            await client.DisconnectAsync(true, cts.Token);
            return new ChannelTestResultDto(Channel, ConnectionStatus.Connected,
                $"Serveur {o.Host} joint{(string.IsNullOrWhiteSpace(o.UserName) ? "" : " et authentification réussie")} (aucun message envoyé).", (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.Timeout, $"Le serveur {o.Host}:{o.Port} n'a pas répondu en 15 secondes.", (int)sw.ElapsedMilliseconds);
        }
        catch (AuthenticationException e)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.AuthenticationFailed, $"Authentification SMTP refusée : {e.Message}", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.Unavailable, $"Serveur {o.Host}:{o.Port} injoignable : {e.Message}", (int)sw.ElapsedMilliseconds);
        }
    }

    public async Task<ChannelSendResult> SendAsync(OutboxMessageDto m, CancellationToken ct = default)
    {
        var o = opts.Value;

        // Fournisseur non configuré : rien ne part, et on le dit (ni « envoyé », ni panne à escalader).
        if (!IsConfigured)
        {
            logger.LogInformation("[email-smtp] fournisseur non configuré — rien n'est envoyé → {Subject}", m.Subject);
            return ChannelSendResult.Unconfigured("Fournisseur e-mail non configuré : le message n'a pas été envoyé.");
        }

        // Une adresse absente ne viendra pas avec le temps : échec définitif, pas de relances inutiles.
        if (string.IsNullOrWhiteSpace(m.Address))
            return ChannelSendResult.Fail("Le destinataire n'a pas d'adresse e-mail.");

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
