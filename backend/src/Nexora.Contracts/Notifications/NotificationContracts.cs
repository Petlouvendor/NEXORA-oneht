namespace Nexora.Contracts.Notifications;

// =============================================================================
// Moteur de notifications & communication (Phase 34 §3-5, §22-23, §28-31, §45).
// =============================================================================

// --- §4 Centre de notifications ---------------------------------------

public sealed record NotificationDto(
    Guid Id,
    string Category,
    string Source,
    int Priority,
    string Severity,
    string Title,
    string Message,
    string? ActionUrl,
    string? ReferenceType,
    Guid? ReferenceId,
    Guid? CompanyId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt,
    DateTimeOffset? PinnedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? ExpiresAt);

public sealed record NotificationPageDto(
    IReadOnlyList<NotificationDto> Items, int Total, int Unread);

/// <summary>Filtre du centre : <c>state</c> ∈ unread | pinned | archived | all.</summary>
public sealed record NotificationQuery(string? State = null, string? Category = null, int Page = 1, int PageSize = 30);

// --- §2, §9 Émission d'une notification -------------------------------

/// <summary>
/// Demande d'émission. Le moteur applique la déduplication (<paramref name="DedupKey"/>),
/// les préférences de chaque destinataire (canaux, priorité mini, heures calmes) et le
/// rendu du gabarit avant d'écrire la notification interne et de mettre en file les canaux externes.
/// </summary>
public sealed record NotifyRequest(
    string Category,
    IReadOnlyList<Guid> Recipients,
    string TemplateKey,
    IReadOnlyDictionary<string, string?> Variables,
    int Priority = 3,
    string Severity = "info",
    string Source = "system",
    string? ActionUrl = null,
    string? ReferenceType = null,
    Guid? ReferenceId = null,
    string? DedupKey = null,
    DateTimeOffset? ExpiresAt = null,
    string Locale = "fr");

public sealed record NotifyResultDto(int NotificationsCreated, int OutboxQueued, int Skipped);

// --- §5 Préférences ---------------------------------------------------

public sealed record NotificationPreferenceDto(
    Guid Id,
    Guid? CompanyId,
    string? Category,
    bool InApp,
    bool Email,
    bool Sms,
    bool Webhook,
    int MinPriority,
    string Digest,
    string? QuietFrom,
    string? QuietTo);

public sealed record UpsertNotificationPreferenceRequest(
    Guid? CompanyId,
    string? Category,
    bool InApp = true,
    bool Email = false,
    bool Sms = false,
    bool Webhook = false,
    int MinPriority = 5,
    string Digest = "instant",
    string? QuietFrom = null,
    string? QuietTo = null);

// --- §22-23 Gabarits ------------------------------------------------

public sealed record MessageTemplateDto(
    Guid Id,
    Guid? CompanyId,
    string Key,
    string Channel,
    string Locale,
    string Subject,
    string Body,
    string? Description,
    IReadOnlyList<string> Variables,
    int Version,
    string Status,
    bool IsSystem);

public sealed record UpsertMessageTemplateRequest(
    string Key,
    string Channel,
    string Locale,
    string Subject,
    string Body,
    string? Description,
    IReadOnlyList<string>? Variables,
    string Status = "published");

public sealed record RenderTemplateRequest(
    string Key,
    string Channel,
    string Locale,
    IReadOnlyDictionary<string, string?> Variables);

public sealed record RenderedMessageDto(string Subject, string Body, IReadOnlyList<string> MissingVariables);

// --- §28-31 File de remise -----------------------------------------

public sealed record OutboxMessageDto(
    Guid Id,
    string Channel,
    string Category,
    int Priority,
    string Subject,
    string Body,
    string? Address,
    string Status,
    int Attempts,
    int MaxAttempts,
    DateTimeOffset NextAttemptAt,
    string? LastError,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt);

// --- Finalisation phase 16 : fournisseurs, tests, journal des envois ---------

/// <summary>Canal et son fournisseur : configuré ou non, résumé sans secret, dernier test réel.</summary>
public sealed record ChannelProviderDto(
    string Channel, string Provider, bool Configured, string ConfigurationSummary,
    string? LastTestStatus, string? LastTestMessage, DateTimeOffset? LastTestedAt, int NotConfiguredWaiting);

/// <summary>Statut : CONNECTED, AUTHENTICATION_FAILED, TIMEOUT, UNAVAILABLE ou NOT_CONFIGURED.</summary>
public sealed record ChannelTestResultDto(string Channel, string Status, string Message, int LatencyMs);

public sealed record SendTestMessageRequest(string Channel, string Address);

/// <summary>Un envoi (adresse partiellement masquée).</summary>
public sealed record OutboxEntryDto(
    Guid Id, string Channel, string Category, string Subject, string? AddressMasked, string? RecipientName,
    string Status, int Attempts, int MaxAttempts, DateTimeOffset NextAttemptAt, string? LastError, string? Provider,
    DateTimeOffset? LastAttemptAt, DateTimeOffset CreatedAt, DateTimeOffset? SentAt);

public sealed record DeliveryLogDto(int Attempt, string Status, string? Provider, string? Error, int? DurationMs, DateTimeOffset CreatedAt);

public sealed record DeliverySummaryDto(
    bool SchedulerEnabled, int Pending, int Sending, int Sent, int Failed, int Dead, int NotConfigured, DateTimeOffset? OldestPendingAt);
