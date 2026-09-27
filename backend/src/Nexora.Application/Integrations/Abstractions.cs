using Nexora.Contracts.Integrations;
using Nexora.Domain.Common;

namespace Nexora.Application.Integrations;

/// <summary>
/// Intégrations, paiements & écosystème (Phase 12). Principe : le cœur métier ne connaît aucun
/// fournisseur — il passe par un connecteur au contrat commun. Une erreur externe devient une
/// anomalie tracée, jamais un échec silencieux ni une transaction interne corrompue (§2, §14).
/// </summary>
public interface IIntegrationRegistry
{
    Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken ct = default);
    Task<Result<ConnectorDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<Guid>> UpsertAsync(UpsertConnectorRequest request, CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task<Result<ConnectorTestResult>> TestConnectionAsync(Guid id, CancellationToken ct = default);
    Task<Result<RotateSecretResult>> RotateSecretAsync(Guid id, CancellationToken ct = default);
}

/// <summary>
/// Passerelle de paiement indépendante du fournisseur (§7-11). CreateIntent → pending ;
/// la confirmation vient d'un webhook signé (ou d'un rappel simulé §42) et déclenche un
/// <c>payment</c> réel via le moteur financier. Le montant fournisseur n'est jamais cru
/// aveuglément (§12).
/// </summary>
public interface IPaymentGateway
{
    Task<Result<PaymentIntentDto>> CreateIntentAsync(CreatePaymentIntentRequest request, CancellationToken ct = default);
    Task<Result<PaymentIntentDto>> GetIntentAsync(Guid intentId, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentIntentDto>> ListIntentsAsync(string? status, CancellationToken ct = default);
    Task<Result<PaymentIntentDto>> ProviderCallbackAsync(Guid intentId, ProviderCallbackRequest request, CancellationToken ct = default);
    Task<Result<PaymentIntentDto>> RefundAsync(Guid intentId, RefundPaymentRequest request, CancellationToken ct = default);
    /// <summary>Passe en 'expired' les intentions échues non confirmées (§10, §14). Renvoie le nombre traité.</summary>
    Task<int> ExpireStaleAsync(CancellationToken ct = default);
}

/// <summary>
/// Réception des webhooks fournisseurs (§12-13). Endpoint public : authentifie par signature
/// HMAC, déduplique par event id fournisseur, journalise le résultat, puis dispatche.
/// </summary>
public interface IWebhookIngestService
{
    Task<WebhookIngestResult> IngestAsync(string connectorCode, string? signature, string rawBody, CancellationToken ct = default);
    Task<IReadOnlyList<WebhookEventDto>> ListAsync(string? status, CancellationToken ct = default);
}

/// <summary>Rapprochement ventes/paiements internes ↔ intentions fournisseur (§15).</summary>
public interface IPaymentReconciliationService
{
    Task<PaymentReconciliationReport> ReconcileAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}

/// <summary>Import et rapprochement des relevés bancaires (§16-17). Réutilise bank_statement.</summary>
public interface IBankReconciliationService
{
    Task<Result<ImportStatementResult>> ImportStatementAsync(ImportStatementRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<BankStatementDto>> ListStatementsAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<StatementLineDto>>> LinesAsync(Guid statementId, CancellationToken ct = default);
    Task<Result<int>> AutoMatchAsync(Guid statementId, CancellationToken ct = default);
    Task<Result> MatchLineAsync(Guid lineId, MatchLineRequest request, CancellationToken ct = default);
    Task<Result> ValidateAsync(Guid statementId, CancellationToken ct = default);
}

/// <summary>
/// Webhooks sortants de l'API publique NEXORA (§31-32). Émission → livraison signée →
/// nouvelle tentative avec back-off jusqu'à épuisement. Idempotence côté consommateur
/// via l'entête d'event id.
/// </summary>
public interface IOutboundWebhookService
{
    Task<Result<Guid>> RegisterAsync(RegisterOutboundWebhookRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<OutboundWebhookDto>> ListAsync(CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default);
    /// <summary>Fan-out d'un événement vers les abonnés ; crée les livraisons puis tente une première remise. Renvoie le nombre d'abonnés touchés.</summary>
    Task<int> EmitAsync(string eventType, Guid eventId, object payload, CancellationToken ct = default);
    /// <summary>Rejoue les livraisons dues (pending/failed dont next_attempt_at est passé). Renvoie le nombre tenté.</summary>
    Task<int> DeliverDueAsync(CancellationToken ct = default);
    Task<Result> RetryAsync(Guid deliveryId, CancellationToken ct = default);
    Task<IReadOnlyList<OutboundDeliveryDto>> DeliveriesAsync(Guid? outboundWebhookId, CancellationToken ct = default);
}

/// <summary>Centre des intégrations : connecteurs, paiements en attente, webhooks en erreur (§39-40).</summary>
public interface IIntegrationCenter
{
    Task<IntegrationCenterDto> OverviewAsync(CancellationToken ct = default);
}

/// <summary>
/// Transport HTTP d'un webhook sortant. Isolé pour la testabilité : l'implémentation par défaut
/// poste réellement, avec un raccourci bac à sable (§42) pour l'hôte <c>sim.webhook.local</c>.
/// </summary>
public interface IOutboundWebhookDispatcher
{
    Task<(int? StatusCode, string? Error)> SendAsync(string url, string signature, string body, CancellationToken ct = default);
}
