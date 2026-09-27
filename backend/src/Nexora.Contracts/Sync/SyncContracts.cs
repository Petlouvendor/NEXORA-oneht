using System.Text.Json;

namespace Nexora.Contracts.Sync;

// --- Push d'un lot d'opérations offline (§11-14) ----------------------

public sealed record SyncOperationInput(
    Guid OperationUuid,
    string EntityType,
    string OperationType,
    JsonElement Payload,
    int Priority = 5,
    long? ClientSequence = null,
    DateTimeOffset? ClientCreatedAt = null);

public sealed record PushBatchRequest(Guid DeviceId, IReadOnlyList<SyncOperationInput> Operations);

public sealed record SyncOperationResult(
    Guid OperationUuid, string Status, Guid? ResultEntityId, Guid? ConflictId, string? Message);

public sealed record PushBatchResult(
    Guid BatchId, int Confirmed, int Conflicts, int Failed,
    IReadOnlyList<SyncOperationResult> Operations);

// --- Appareils (§12, §31) ------------------------------------------

public sealed record RegisterDeviceRequest(
    string DeviceIdentifier, string Name, string DeviceType, Guid? PosTerminalId, string? PublicKey);

public sealed record SyncDeviceDto(
    Guid Id, string DeviceIdentifier, string Name, string DeviceType, string Status,
    DateTimeOffset? LastSyncAt, DateTimeOffset? LastSeenAt, int PendingOperations);

public sealed record RevokeDeviceRequest(string Reason);

// --- Conflits (§15-17) --------------------------------------------

public sealed record SyncConflictDto(
    Guid Id, Guid SyncOperationId, string ConflictType, string EntityType, Guid? EntityId,
    string Resolution, string? Detail, string? RecommendedAction);

public sealed record ResolveConflictRequest(string Resolution, string? Note);

// --- Politique offline (§10, §17) --------------------------------

public sealed record OfflinePolicyDto(
    Guid CompanyId, bool AllowOfflineSales, bool AllowOfflineCredit, decimal OfflineCreditLimit,
    bool AllowOfflineDiscount, bool AllowOfflineRefund, int MaxOfflineMinutes,
    int PendingAlertThreshold, IReadOnlyList<string> AllowedPaymentMethods, bool AutoSync,
    bool AllowOfflineCustomerPayment = true);

public sealed record SetOfflinePolicyRequest(
    bool? AllowOfflineSales, bool? AllowOfflineCredit, decimal? OfflineCreditLimit,
    bool? AllowOfflineDiscount, bool? AllowOfflineRefund, int? MaxOfflineMinutes,
    int? PendingAlertThreshold, IReadOnlyList<string>? AllowedPaymentMethods, bool? AutoSync,
    bool? AllowOfflineCustomerPayment = null);

// --- Centre de synchronisation (§27, §30) -----------------------

public sealed record SyncCenterDto(
    DateTimeOffset GeneratedAt,
    int PendingOperations, int SyncedOperations, int FailedOperations, int OpenConflicts,
    DateTimeOffset? LastSyncAt, int? MinutesSinceLastSync, bool PendingThresholdExceeded,
    IReadOnlyList<SyncDeviceDto> Devices,
    IReadOnlyList<SyncConflictDto> Conflicts,
    IReadOnlyList<string> RecommendedActions);
