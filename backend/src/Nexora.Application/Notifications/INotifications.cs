using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;

namespace Nexora.Application.Notifications;

/// <summary>
/// Moteur de notifications (Phase 34 §2-5). Transforme une demande d'émission en notifications
/// internes + mises en file des canaux externes, en appliquant : déduplication (§30), préférences
/// par destinataire (canaux, priorité minimale, heures calmes §5), rendu de gabarit (§22-23),
/// périmètre entreprise (§37). Rien n'est envoyé « en clair » — chaque canal externe passe par
/// l'outbox et le distributeur (§28-31).
/// </summary>
public interface INotificationService
{
    Task<NotifyResultDto> NotifyAsync(NotifyRequest request, CancellationToken ct = default);

    Task<NotificationPageDto> ListAsync(NotificationQuery query, CancellationToken ct = default);
    Task<int> UnreadCountAsync(CancellationToken ct = default);
    Task<Result> MarkReadAsync(Guid id, bool read, CancellationToken ct = default);
    Task<int> MarkAllReadAsync(CancellationToken ct = default);
    Task<Result> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct = default);
    Task<Result> SetArchivedAsync(Guid id, bool archived, CancellationToken ct = default);

    /// <summary>Push direct vers les appareils mobiles enregistrés d'un destinataire (Phase 44 §20) —
    /// contourne le routage par préférence/catégorie (pas de <c>notification_preference.push</c> cette
    /// phase, volontairement, pour ne pas rouvrir les écrans de préférences) : toujours envoyé si
    /// l'appareil a un jeton actif. Renvoie le nombre d'appareils mis en file.</summary>
    Task<int> SendPushAsync(IReadOnlyList<Guid> userIds, string title, string body, string? actionUrl = null, CancellationToken ct = default);
}

/// <summary>Préférences de notification de l'utilisateur courant (Phase 34 §5).</summary>
public interface INotificationPreferenceService
{
    Task<IReadOnlyList<NotificationPreferenceDto>> ListAsync(CancellationToken ct = default);
    Task<Result<NotificationPreferenceDto>> UpsertAsync(UpsertNotificationPreferenceRequest request, CancellationToken ct = default);
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Gabarits de message versionnés par canal &amp; langue (Phase 34 §22-23).</summary>
public interface IMessageTemplateService
{
    Task<IReadOnlyList<MessageTemplateDto>> ListAsync(CancellationToken ct = default);
    Task<Result<Guid>> UpsertAsync(UpsertMessageTemplateRequest request, CancellationToken ct = default);
    Task<Result<RenderedMessageDto>> RenderAsync(RenderTemplateRequest request, CancellationToken ct = default);
}

/// <summary>
/// Distributeur de l'outbox (Phase 34 §28-31) : reprend les remises dues, tente le canal,
/// applique un backoff exponentiel, place en dead-letter après <c>max_attempts</c> et notifie
/// alors un administrateur. Piloté par le planificateur de fond (Phase 16).
/// </summary>
public interface INotificationDispatcher
{
    Task<int> DispatchDueAsync(CancellationToken ct = default);
}

/// <summary>
/// Canal de sortie remplaçable (Phase 34 §3). Les implémentations réelles (email, SMS, WhatsApp,
/// push) restent isolées derrière cette interface ; l'implémentation par défaut journalise.
/// </summary>
public interface INotificationChannel
{
    /// <summary>Nom du canal — doit correspondre à <c>notification_outbox.channel</c>.</summary>
    string Channel { get; }

    Task<ChannelSendResult> SendAsync(OutboxMessageDto message, CancellationToken ct = default);
}

/// <summary><paramref name="Transient"/> = réessayable ; sinon échec définitif → dead-letter.</summary>
public sealed record ChannelSendResult(bool Sent, bool Transient = false, string? Error = null)
{
    public static ChannelSendResult Ok() => new(true);
    public static ChannelSendResult Retry(string error) => new(false, true, error);
    public static ChannelSendResult Fail(string error) => new(false, false, error);
}
