using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Api.V2010.Account;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Canal SMS réel via Twilio (Phase 34 §3).
/// Si le canal n'est pas activé ou mal configuré, la remise est journalisée et retournée comme
/// erreur définitive (Fail) pour éviter un retry infini sur les environnements sans Twilio.
///
/// Configuration requise (section <c>Notifications:Twilio</c>) :
///   AccountSid  → "ACxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"
///   AuthToken   → "(token secret)"
///   FromNumber  → "+33100000000"  (numéro Twilio E.164)
///   Enabled     → true
/// </summary>
public sealed class TwilioSmsChannel(
    IOptions<TwilioOptions> opts,
    ILogger<TwilioSmsChannel> logger) : INotificationChannel
{
    public string Channel => "sms";

    public async Task<ChannelSendResult> SendAsync(OutboxMessageDto m, CancellationToken ct = default)
    {
        var o = opts.Value;

        if (!o.Enabled || string.IsNullOrWhiteSpace(o.AccountSid) || string.IsNullOrWhiteSpace(o.FromNumber))
        {
            logger.LogInformation("[sms-twilio] canal non configuré — log seulement → {Address} · {Subject}",
                m.Address, m.Subject);
            return ChannelSendResult.Fail("Canal Twilio SMS non configuré (Notifications:Twilio:Enabled=false).");
        }

        if (string.IsNullOrWhiteSpace(m.Address))
            return ChannelSendResult.Retry($"Numéro de téléphone absent pour la notification {m.Id}.");

        // Twilio SDK utilise une initialisation globale thread-safe.
        TwilioClient.Init(o.AccountSid, o.AuthToken);

        // Le corps SMS est toujours du texte brut ; on tronque à 1 600 caractères (10 segments SMS).
        var body = m.Body.Length > 1_600 ? m.Body[..1_597] + "…" : m.Body;

        try
        {
            var message = await MessageResource.CreateAsync(
                to: new Twilio.Types.PhoneNumber(m.Address),
                from: new Twilio.Types.PhoneNumber(o.FromNumber),
                body: body);

            if (message.ErrorCode is not null)
            {
                logger.LogWarning("[sms-twilio] erreur Twilio {Code} → {Address} : {Message}",
                    message.ErrorCode, m.Address, message.ErrorMessage);
                // Erreurs 3xxxx (numéro invalide) → définitives ; 2xxxx (limits) → réessayables.
                var transient = message.ErrorCode >= 30_000 && message.ErrorCode < 40_000;
                return transient
                    ? ChannelSendResult.Retry($"Twilio {message.ErrorCode}: {message.ErrorMessage}")
                    : ChannelSendResult.Fail($"Twilio {message.ErrorCode}: {message.ErrorMessage}");
            }

            logger.LogInformation("[sms-twilio] envoyé SID={Sid} → {Address}", message.Sid, m.Address);
            return ChannelSendResult.Ok();
        }
        catch (ApiException ex)
        {
            // 4xx client → Fail ; réseau/5xx → Retry.
            logger.LogWarning(ex, "[sms-twilio] exception API → {Address}", m.Address);
            return ex.Status >= 500
                ? ChannelSendResult.Retry(ex.Message)
                : ChannelSendResult.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[sms-twilio] exception inattendue → {Address}", m.Address);
            return ChannelSendResult.Retry(ex.Message);
        }
    }
}
