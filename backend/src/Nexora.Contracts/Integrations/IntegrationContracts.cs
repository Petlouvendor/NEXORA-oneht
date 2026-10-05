using System.Text.Json;
using Nexora.Contracts.Finance;

namespace Nexora.Contracts.Integrations;

// --- Connecteurs : registre & configuration (§5-6) --------------------

public sealed record ConnectorDto(
    Guid Id, string Code, string Name, string ConnectorType, string Provider,
    string Environment, string Status, DateTimeOffset? LastSyncAt, string? LastError,
    DateTimeOffset? LastErrorAt, string ConnectorVersion, bool HasSecret,
    JsonElement Config, DateTimeOffset UpdatedAt,
    string Mode = "real", bool CredentialsConfigured = false, string? CredentialsHint = null,
    string? LastTestStatus = null, string? LastTestMessage = null, DateTimeOffset? LastTestedAt = null,
    bool ProviderSupported = false);

/// <summary>Identifiants fournisseur : écriture seule, chiffrés au repos, jamais renvoyés.</summary>
public sealed record SetConnectorCredentialsRequest(string ClientId, string ClientSecret);

/// <summary>Fournisseurs de paiement disponibles et ce qu'ils savent faire.</summary>
public sealed record PaymentProviderInfo(string Key, string Name, bool IsSimulation, bool SupportsRefund, IReadOnlyCollection<string> Currencies);

public sealed record UpsertConnectorRequest(
    string Code, string Name, string ConnectorType, string Provider,
    string Environment, JsonElement? Config);

/// <summary>Résultat d'un test de connexion réel (finalisation phase 15) : CONNECTED, AUTHENTICATION_FAILED,
/// TIMEOUT, UNAVAILABLE ou NOT_CONFIGURED, avec un message compréhensible. <c>Mode</c> : simulation / real.</summary>
public sealed record ConnectorTestResult(bool Ok, string Detail, int LatencyMs, string Status = "NOT_CONFIGURED", string Mode = "real");

/// <summary>Le secret n'est renvoyé qu'une seule fois, à la rotation (§38).</summary>
public sealed record RotateSecretResult(string Secret);

// --- Abstraction des paiements (§7-11) --------------------------------

public sealed record CreatePaymentIntentRequest(
    Guid ConnectorId, string IdempotencyKey, decimal Amount, string Currency,
    string Method, string Purpose, Guid CustomerId, Guid SettlementAccountId,
    Guid? SaleId, IReadOnlyList<AllocationInput>? Allocations, int? ExpiresInMinutes, string? Reference);

public sealed record PaymentIntentDto(
    Guid Id, Guid ConnectorId, string? ProviderReference, decimal Amount, string Currency,
    string Method, string Purpose, string Status, Guid? PaymentId, decimal RefundedAmount,
    DateTimeOffset? ExpiresAt, DateTimeOffset? ConfirmedAt, string? FailureReason, DateTimeOffset CreatedAt,
    string Mode = "simulation", string? RedirectUrl = null, string? ProviderOrderId = null, Guid? CustomerId = null);

/// <summary>Rappel fournisseur simulé (bac à sable §42) : confirmed / failed / cancelled.</summary>
public sealed record ProviderCallbackRequest(string Outcome, decimal? ProviderAmount, string? ProviderReference);

public sealed record RefundPaymentRequest(decimal Amount, string Reason);

// --- Webhooks entrants (§12-13) --------------------------------------

public sealed record WebhookIngestResult(string Status, bool SignatureValid, Guid? WebhookEventId, string Message);

public sealed record WebhookEventDto(
    Guid Id, Guid ConnectorId, string ProviderEventId, string EventType, bool SignatureValid,
    string Status, string? Error, DateTimeOffset ReceivedAt, DateTimeOffset? ProcessedAt);

// --- Rapprochement des paiements (§15) -------------------------------

public sealed record ReconciliationFinding(
    string Kind, Guid IntentId, string? ProviderReference,
    decimal? ExpectedAmount, decimal? ObservedAmount, string Detail);

public sealed record PaymentReconciliationReport(
    DateOnly From, DateOnly To, int IntentsConfirmed, int PaymentsLinked,
    IReadOnlyList<ReconciliationFinding> Findings);

// --- Relevés bancaires (§16-17) ------------------------------------

public sealed record StatementLineInput(DateOnly ValueDate, decimal Amount, string Description, string? Reference);

public sealed record ImportStatementRequest(
    Guid FinancialAccountId, string StatementNumber, DateOnly StatementDate,
    decimal OpeningBalance, decimal ClosingBalance, IReadOnlyList<StatementLineInput> Lines);

public sealed record ImportStatementResult(
    Guid StatementId, int LinesImported, int DuplicatesSkipped, int AutoMatched, int Unmatched);

public sealed record BankStatementDto(
    Guid Id, Guid FinancialAccountId, string? StatementNumber, DateOnly StatementDate,
    decimal OpeningBalance, decimal ClosingBalance, string Status, int LineCount, int MatchedCount);

public sealed record StatementLineDto(
    Guid Id, DateOnly ValueDate, decimal Amount, string? Description, string? Reference,
    string Status, Guid? MatchedTransactionId);

public sealed record MatchLineRequest(Guid TransactionId);

/// <summary>Mouvement du compte proposé pour une ligne de relevé : même montant, pas encore rapproché (phase 7).</summary>
public sealed record StatementCandidateDto(Guid TransactionId, DateTimeOffset OccurredAt, decimal Amount, string TransactionType, string? Label);

// --- Webhooks sortants (§32) --------------------------------------

public sealed record RegisterOutboundWebhookRequest(
    string Name, string TargetUrl, IReadOnlyList<string> SubscribedEvents);

public sealed record SetStatusRequest(string Status);

/// <summary>Émission manuelle d'un événement — outil de test de l'écosystème (§33, §42).</summary>
public sealed record EmitEventRequest(string EventType, JsonElement? Payload);

public sealed record OutboundWebhookDto(
    Guid Id, string Name, string TargetUrl, IReadOnlyList<string> SubscribedEvents,
    string Status, int ConsecutiveFailures, DateTimeOffset? LastDeliveryAt);

public sealed record OutboundDeliveryDto(
    Guid Id, Guid OutboundWebhookId, string EventType, Guid EventId, int Attempts, int MaxAttempts,
    string Status, int? ResponseCode, DateTimeOffset NextAttemptAt, string? LastError, DateTimeOffset? DeliveredAt);

// --- Centre des intégrations (§39-40) ----------------------------

public sealed record ConnectorHealthDto(
    Guid Id, string Name, string ConnectorType, string Provider, string Status,
    DateTimeOffset? LastSyncAt, string? LastError, int PendingPayments, DateTimeOffset? LastWebhookAt);

public sealed record IntegrationCenterDto(
    DateTimeOffset GeneratedAt,
    int ActiveConnectors, int ErrorConnectors,
    int PendingPayments, int FailedWebhooks, int DuplicateWebhooks,
    int PendingOutboundDeliveries, int ExhaustedOutboundDeliveries, int OpenBankExceptions,
    IReadOnlyList<ConnectorHealthDto> Connectors,
    IReadOnlyList<string> RecommendedActions);
