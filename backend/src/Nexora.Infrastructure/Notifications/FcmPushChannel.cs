using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Application.Integrations;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;

namespace Nexora.Infrastructure.Notifications;

/// <summary>
/// Canal push réel via Firebase Cloud Messaging (Phase 34 §3, Phase 44 §20).
/// Supporte les jetons FCM individuels (appareils mobiles Android / iOS / Web Push).
///
/// Le champ <c>Address</c> de l'outbox contient le jeton FCM de l'appareil cible.
/// Si le canal n'est pas activé ou mal configuré, la remise est journalisée et retournée comme
/// erreur définitive (Fail) pour éviter un retry infini.
///
/// Configuration requise (section <c>Notifications:Fcm</c>) :
///   ServiceAccountJson → chemin vers le fichier JSON du compte de service Firebase
///                        OU contenu JSON inline (pratique pour les variables d'environnement)
///   Enabled            → true
/// </summary>
public sealed class FcmPushChannel : INotificationChannel, IDisposable
{
    private readonly FcmOptions _opts;
    private readonly ILogger<FcmPushChannel> _logger;

    /// <summary>Instance Firebase partagée par la durée de vie du canal (Singleton).</summary>
    private FirebaseApp? _app;
    private FirebaseMessaging? _messaging;
    private readonly Lock _initLock = new();
    private bool _initFailed;

    public string Channel => "push";
    public string ProviderName => "Firebase Cloud Messaging";
    public bool IsConfigured => _opts.Enabled && !string.IsNullOrWhiteSpace(_opts.ServiceAccountJson);

    public string ConfigurationSummary => !IsConfigured
        ? "Non configuré : renseignez Notifications:Fcm (Enabled, ServiceAccountJson) dans la configuration du serveur."
        : _opts.ServiceAccountJson.TrimStart().StartsWith('{')
            ? "Compte de service fourni dans la configuration (contenu masqué)."
            : $"Compte de service : fichier {System.IO.Path.GetFileName(_opts.ServiceAccountJson)}";

    /// <summary>
    /// Test sans rien envoyer : message « à blanc » (dryRun) vers un jeton fictif. Si Firebase authentifie la
    /// demande, il refuse le jeton (attendu) ; s'il refuse le compte de service, c'est une erreur d'authentification.
    /// </summary>
    public async Task<ChannelTestResultDto> TestAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return new ChannelTestResultDto(Channel, ConnectionStatus.NotConfigured, ConfigurationSummary, 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var messaging = GetMessaging();
        if (messaging is null)
            return new ChannelTestResultDto(Channel, ConnectionStatus.AuthenticationFailed,
                "Compte de service Firebase illisible ou invalide : vérifiez Notifications:Fcm:ServiceAccountJson.", 0);
        try
        {
            await messaging.SendAsync(new Message { Token = "nexora-test-connexion", Notification = new Notification { Title = "Test" } },
                dryRun: true, ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
            return new ChannelTestResultDto(Channel, ConnectionStatus.Connected, "Firebase a accepté la demande à blanc (aucun message envoyé).", (int)sw.ElapsedMilliseconds);
        }
        catch (FirebaseMessagingException e) when (e.ErrorCode is ErrorCode.Unauthenticated or ErrorCode.PermissionDenied)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.AuthenticationFailed, $"Firebase refuse le compte de service : {e.Message}", (int)sw.ElapsedMilliseconds);
        }
        catch (FirebaseMessagingException e) when (e.ErrorCode is ErrorCode.InvalidArgument or ErrorCode.NotFound
                                                   || e.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.Connected,
                "Firebase a authentifié la demande et refusé le jeton fictif, comme prévu (aucun message envoyé).", (int)sw.ElapsedMilliseconds);
        }
        catch (TimeoutException)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.Timeout, "Firebase n'a pas répondu en 15 secondes.", (int)sw.ElapsedMilliseconds);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new ChannelTestResultDto(Channel, ConnectionStatus.Unavailable, $"Firebase injoignable : {e.Message}", (int)sw.ElapsedMilliseconds);
        }
    }

    public FcmPushChannel(IOptions<FcmOptions> opts, ILogger<FcmPushChannel> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public async Task<ChannelSendResult> SendAsync(OutboxMessageDto m, CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            _logger.LogInformation("[push-fcm] fournisseur non configuré — rien n'est envoyé → {Subject}", m.Subject);
            return ChannelSendResult.Unconfigured("Fournisseur push non configuré : le message n'a pas été envoyé.");
        }

        if (string.IsNullOrWhiteSpace(m.Address))
            return ChannelSendResult.Fail("Le destinataire n'a pas d'appareil enregistré pour les notifications push.");

        var messaging = GetMessaging();
        if (messaging is null)
            return ChannelSendResult.Fail("Initialisation Firebase échouée — vérifiez Notifications:Fcm:ServiceAccountJson.");

        // Construction du message FCM.
        // Le titre provient du Subject ; le corps du Body (texte brut, 1 000 car max pour Android).
        var body = m.Body.Length > 1_000 ? m.Body[..997] + "…" : m.Body;
        var fcmMessage = new Message
        {
            Token = m.Address,
            Notification = new Notification
            {
                Title = m.Subject,
                Body = body,
            },
            // Données additionnelles : l'UI peut naviguer vers la bonne page à la réception.
            Data = new Dictionary<string, string>
            {
                ["notificationId"] = m.Id.ToString(),
                ["category"] = m.Category,
            },
            // Android : priorité haute pour les notifications importantes. L'échelle va de 1 (la plus haute) à 5 :
            // l'ancien test « >= 3 » donnait la priorité haute aux messages les moins importants.
            Android = new AndroidConfig
            {
                Priority = m.Priority <= 2 ? Priority.High : Priority.Normal,
            },
            // APNs (iOS) : badge + son par défaut.
            Apns = new ApnsConfig
            {
                Aps = new Aps
                {
                    Badge = 1,
                    Sound = "default",
                },
            },
        };

        try
        {
            var messageId = await messaging.SendAsync(fcmMessage, ct);
            _logger.LogInformation("[push-fcm] envoyé MessageId={MessageId} → {Token}",
                messageId, Truncate(m.Address));
            return ChannelSendResult.Ok();
        }
        catch (FirebaseMessagingException ex)
        {
            _logger.LogWarning(ex, "[push-fcm] exception FCM ErrorCode={ErrorCode} → {Token}",
                ex.MessagingErrorCode, Truncate(m.Address));

            return ex.MessagingErrorCode switch
            {
                // Jeton invalide ou révoqué → erreur définitive (l'application doit nettoyer ce token).
                MessagingErrorCode.Unregistered or
                MessagingErrorCode.InvalidArgument => ChannelSendResult.Fail(
                    $"FCM {ex.MessagingErrorCode}: {ex.Message}"),

                // Quota ou serveur → réessayable.
                MessagingErrorCode.QuotaExceeded or
                MessagingErrorCode.Unavailable or
                MessagingErrorCode.Internal => ChannelSendResult.Retry(
                    $"FCM {ex.MessagingErrorCode}: {ex.Message}"),

                _ => ChannelSendResult.Retry($"FCM {ex.MessagingErrorCode}: {ex.Message}"),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[push-fcm] exception inattendue → {Token}", Truncate(m.Address));
            return ChannelSendResult.Retry(ex.Message);
        }
    }

    // -------------------------------------------------------------------------

    /// <summary>Initialise Firebase une seule fois (thread-safe). Retourne null si échoué.</summary>
    private FirebaseMessaging? GetMessaging()
    {
        if (_messaging is not null) return _messaging;
        if (_initFailed) return null;

        lock (_initLock)
        {
            if (_messaging is not null) return _messaging;
            if (_initFailed) return null;

            try
            {
                GoogleCredential credential;
                var raw = _opts.ServiceAccountJson.Trim();

                if (raw.StartsWith('{'))
                {
                    // JSON inline (variable d'environnement, secret manager).
                    credential = GoogleCredential.FromJson(raw)
                        .CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
                }
                else
                {
                    // Chemin vers un fichier sur disque.
                    credential = GoogleCredential.FromFile(raw)
                        .CreateScoped("https://www.googleapis.com/auth/firebase.messaging");
                }

                // Firebase SDK interdit d'avoir deux apps par défaut dans le même process :
                // on réutilise celle qui existe déjà si une autre partie du code l'a créée.
                _app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
                {
                    Credential = credential,
                });

                _messaging = FirebaseMessaging.GetMessaging(_app);
                _logger.LogInformation("[push-fcm] Firebase initialisé depuis la configuration.");
                return _messaging;
            }
            catch (Exception ex)
            {
                _initFailed = true;
                _logger.LogError(ex, "[push-fcm] initialisation Firebase échouée — canal push désactivé.");
                return null;
            }
        }
    }

    private static string Truncate(string token) =>
        token.Length > 12 ? token[..8] + "…" + token[^4..] : token;

    public void Dispose()
    {
        _app?.Delete();
        _app = null;
        _messaging = null;
    }
}
