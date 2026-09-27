using System.Text.Json;
using Nexora.Contracts.Intelligence;

namespace Nexora.Contracts.Mobile;

// =============================================================================
// Mobile, terrain & multi-appareils (Phase 44 §12-13, §20, §22-23, §25-29, §32, §46, §48).
// Complète SyncContracts.cs (Phase 10) : le moteur offline existant est réutilisé tel quel,
// pas redupliqué — ces contrats couvrent ce qui manquait (pièces jointes, tournées,
// interventions, diagnostics, bootstrap mobile).
// =============================================================================

// --- §22-23 Pièces jointes minimales (photo/signature — Phase 45 généralisera) ----------

/// <summary><c>kind</c> ∈ photo|signature. <c>contentBase64</c> ≤ 5 Mo (capture mobile seulement).</summary>
public sealed record UploadAttachmentRequest(string Kind, string? ReferenceType, Guid? ReferenceId, string ContentBase64, string ContentType);

public sealed record AttachmentDto(
    Guid Id, string Kind, string? ReferenceType, Guid? ReferenceId, string ContentType,
    int SizeBytes, string Sha256Hash, DateTimeOffset CapturedAt);

// --- §12, §52 Tournées de livraison ------------------------------------------------------

public sealed record CreateDeliveryStopInput(
    Guid? SalesOrderId, Guid? CustomerId, string Address, decimal? Latitude, decimal? Longitude, string? ContactPhone);

public sealed record CreateDeliveryRouteRequest(
    Guid? DriverUserId, DateOnly RouteDate, string? Vehicle, IReadOnlyList<CreateDeliveryStopInput> Stops);

/// <summary><c>status</c> ∈ pending|en_route|arrived|delivered|failed|returned.</summary>
public sealed record DeliveryStopDto(
    Guid Id, Guid DeliveryRouteId, int SequenceNo, Guid? SalesOrderId, Guid? CustomerId, string Address,
    decimal? Latitude, decimal? Longitude, string? ContactPhone, string Status,
    Guid? SignatureAttachmentId, Guid? PhotoAttachmentId, decimal? AmountCollected,
    DateTimeOffset? DeliveredAt, string? FailureReason);

/// <summary><c>status</c> ∈ planned|in_progress|completed|cancelled.</summary>
public sealed record DeliveryRouteDto(
    Guid Id, Guid? DriverUserId, DateOnly RouteDate, string? Vehicle, string Status,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, IReadOnlyList<DeliveryStopDto> Stops);

/// <summary>§28 : rejouable hors ligne — <c>OperationUuid</c> = idempotence, comme une vente (Phase 10 §13).
/// Solde la commande liée via <c>OrderService.CompleteOrderAsync</c> (aucun double moteur, §52).</summary>
public sealed record CompleteDeliveryStopRequest(
    Guid OperationUuid, decimal? Latitude, decimal? Longitude, Guid? SignatureAttachmentId,
    Guid? PhotoAttachmentId, decimal? AmountCollected, Guid? PaymentFinancialAccountId);

public sealed record FailDeliveryStopRequest(string Reason);

// --- §13 Interventions techniciens ---------------------------------------------------------

public sealed record ChecklistItemInput(string Label, bool Done);
public sealed record PartUsedInput(Guid ProductId, decimal Quantity);

public sealed record CreateInterventionRequest(Guid? TechnicianUserId, Guid? CustomerId, string EquipmentDescription, DateTimeOffset? ScheduledAt);

/// <summary>§28 : rejouable hors ligne, comme une livraison — <c>OperationUuid</c> = idempotence.</summary>
public sealed record CompleteInterventionRequest(
    Guid OperationUuid, IReadOnlyList<ChecklistItemInput> Checklist, IReadOnlyList<PartUsedInput> PartsUsed,
    int TimeSpentMinutes, string ReportText, Guid? SignatureAttachmentId);

/// <summary><c>status</c> ∈ scheduled|in_progress|completed|cancelled.</summary>
public sealed record FieldInterventionDto(
    Guid Id, Guid? TechnicianUserId, Guid? CustomerId, string EquipmentDescription,
    JsonElement Checklist, JsonElement PartsUsed, DateTimeOffset? ScheduledAt, DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt, int? TimeSpentMinutes, string Status, Guid? SignatureAttachmentId, string ReportText);

// --- §46 Diagnostics minimisés ---------------------------------------------------------------

public sealed record ReportCrashRequest(
    Guid? DeviceId, string AppVersion, string Os, string OsVersion, string? CorrelationId, string Message, string? StackExcerpt);

public sealed record CrashReportDto(Guid Id, Guid? DeviceId, string AppVersion, string Os, string Message, DateTimeOffset OccurredAt);

// --- §20, §32 Appareil — jeton push (étend Phase 10 SyncContracts.cs) ----------------------

public sealed record RegisterPushTokenRequest(string PushToken, string PushProvider);

// --- §48 Config & bootstrap mobile -------------------------------------------------------------

/// <summary>Politique offline (Phase 10) + ce que le client mobile peut faire hors ligne (§25-27) —
/// jamais une nouvelle source de vérité, une lecture agrégée pour paramétrer l'app.</summary>
public sealed record MobileConfigDto(
    bool AllowOfflineSales, bool AllowOfflineCredit, decimal OfflineCreditLimit, int MaxOfflineMinutes,
    IReadOnlyList<string> SupportedOfflineEntityTypes, bool PushEnabled, string MinAppVersion);

/// <summary>§25 « données autorisées en cache » — strictement le périmètre de l'appelant (ses
/// tournées, ses interventions, ses tâches), jamais une extraction complète de la base.</summary>
public sealed record MobileBootstrapDto(
    IReadOnlyList<DeliveryStopDto> MyDeliveryStops, IReadOnlyList<FieldInterventionDto> MyInterventions,
    IReadOnlyList<TaskDto> MyTasks, int UnreadNotifications, DateTimeOffset GeneratedAt);
