using Nexora.Contracts.Platform;
using Nexora.Domain.Common;

namespace Nexora.Application.Platform;

// =============================================================================
// Administration SaaS & monétisation (Phase 38). Couche plateforme, distincte de
// la comptabilité des entreprises clientes (§2, §43-44).
// =============================================================================

/// <summary>Noms canoniques des événements billing (§51). Journalisés dans <c>billing_event</c>.</summary>
public static class PlatformEvents
{
    public const string TrialStarted = "TrialStarted";
    public const string TrialEndingSoon = "TrialEndingSoon";
    public const string TrialExpired = "TrialExpired";
    public const string SubscriptionCreated = "SubscriptionCreated";
    public const string SubscriptionUpgraded = "SubscriptionUpgraded";
    public const string SubscriptionDowngraded = "SubscriptionDowngraded";
    public const string PaymentSucceeded = "PaymentSucceeded";
    public const string PaymentFailed = "PaymentFailed";
    public const string InvoiceIssued = "InvoiceIssued";
    public const string InvoicePaid = "InvoicePaid";
    public const string InvoiceOverdue = "InvoiceOverdue";
    public const string SubscriptionSuspended = "SubscriptionSuspended";
    public const string SubscriptionCanceled = "SubscriptionCancelled";
    public const string SubscriptionReactivated = "SubscriptionReactivated";
    public const string QuotaThresholdReached = "QuotaThresholdReached";
    public const string LicenseActivated = "LicenseActivated";
    public const string LicenseRevoked = "LicenseRevoked";
}

/// <summary>Résout l'organisation cliente à laquelle se rattache l'entreprise active (portail §45).</summary>
public interface IPlatformScope
{
    Task<Guid?> CurrentCustomerIdAsync(CancellationToken ct = default);
}

/// <summary>Catalogue commercial : plans, add-ons, coupons (§4-6, §20, §39-40).</summary>
public interface IPlatformCatalogService
{
    Task<IReadOnlyList<PlanDto>> ListPlansAsync(bool publicOnly, CancellationToken ct = default);
    Task<Result<PlanDto>> UpsertPlanAsync(UpsertPlanRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<AddonDto>> ListAddonsAsync(bool publicOnly, CancellationToken ct = default);
    Task<Result<AddonDto>> UpsertAddonAsync(UpsertAddonRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CouponDto>> ListCouponsAsync(CancellationToken ct = default);
    Task<Result<CouponDto>> UpsertCouponAsync(UpsertCouponRequest request, CancellationToken ct = default);
}

/// <summary>Comptes clients / organisations (§7-9).</summary>
public interface ICustomerAccountService
{
    Task<IReadOnlyList<CustomerDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<CustomerDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<CustomerDto>> CreateAsync(CreateCustomerRequest request, CancellationToken ct = default);
    Task<Result<CustomerDto>> UpdateAsync(Guid id, UpdateCustomerRequest request, CancellationToken ct = default);
    Task<Result<CustomerDto>> LinkCompanyAsync(Guid id, Guid companyId, CancellationToken ct = default);
    Task<Result> UnlinkCompanyAsync(Guid id, Guid companyId, CancellationToken ct = default);
}

/// <summary>Cycle de vie de l'abonnement (§10-19, §29-33). Ledger d'événements séparé (§44).</summary>
public interface ISubscriptionService
{
    Task<Result<SubscriptionDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<SubscriptionDto>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default);

    Task<Result<SubscriptionDto>> CreateAsync(Guid customerId, CreateSubscriptionRequest request, CancellationToken ct = default);

    /// <summary>Active un abonnement après confirmation de paiement (§19, §29). Émet la facture de période + la licence.</summary>
    Task<Result<SubscriptionDto>> ActivateAsync(Guid id, CancellationToken ct = default);

    Task<Result<PlanChangeImpactDto>> PreviewPlanChangeAsync(Guid id, Guid targetPlanId, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<SubscriptionDto>> UpgradeAsync(Guid id, ChangePlanRequest request, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<SubscriptionDto>> DowngradeAsync(Guid id, ChangePlanRequest request, Guid? restrictToCustomer, CancellationToken ct = default);

    Task<Result<SubscriptionDto>> SetAddonAsync(Guid id, AddonChangeRequest request, Guid? restrictToCustomer, CancellationToken ct = default);

    Task<Result<SubscriptionDto>> CancelAsync(Guid id, CancelSubscriptionRequest request, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<SubscriptionDto>> ReactivateAsync(Guid id, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<SubscriptionDto>> SuspendAsync(Guid id, SuspendSubscriptionRequest request, CancellationToken ct = default);

    /// <summary>Balayage périodique (§11, §17) : essais expirés, renouvellements dus, grâce → suspension. Idempotent.</summary>
    Task<int> RunDueAsync(CancellationToken ct = default);
}

/// <summary>Facturation NEXORA : factures, avoirs, paiements, webhook idempotent (§13-16, §54).</summary>
public interface IPlatformBillingService
{
    Task<IReadOnlyList<InvoiceDto>> ListInvoicesAsync(Guid customerId, CancellationToken ct = default);
    Task<Result<InvoiceDto>> GetInvoiceAsync(Guid invoiceId, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<InvoiceDto>> IssueCreditNoteAsync(Guid invoiceId, CreditNoteRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentDto>> ListPaymentsAsync(Guid customerId, CancellationToken ct = default);
    Task<IReadOnlyList<PaymentDto>> ListPaymentsAwaitingReviewAsync(CancellationToken ct = default);
    Task<Result<PaymentDto>> RecordManualPaymentAsync(RecordManualPaymentRequest request, CancellationToken ct = default);
    Task<Result<PaymentDto>> ConfirmPaymentAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>Traite un événement du fournisseur de paiement — idempotent par <c>EventId</c> ;
    /// un événement ambigu passe en revue manuelle sans jamais suspendre (§54).</summary>
    Task<Result<PaymentDto>> HandlePaymentEventAsync(PaymentWebhookEvent evt, CancellationToken ct = default);

    /// <summary>Réconciliation quotidienne (§54) : marque les factures échues, relance, nettoie les paiements orphelins.</summary>
    Task<int> ReconcileAsync(CancellationToken ct = default);

    Task<IReadOnlyList<PaymentMethodDto>> ListPaymentMethodsAsync(Guid customerId, CancellationToken ct = default);
    Task<Result<PaymentMethodDto>> AddPaymentMethodAsync(Guid customerId, AddPaymentMethodRequest request, CancellationToken ct = default);
}

/// <summary>Quotas & usage (§21-24). Le plan + les add-ons donnent les limites ; l'usage est compté par ressource.</summary>
public interface IQuotaService
{
    Task<Result<QuotaReportDto>> GetReportAsync(Guid customerId, CancellationToken ct = default);
    Task<QuotaCheckResult> CheckAsync(Guid customerId, string resource, long requested, CancellationToken ct = default);
    Task<Result> RecordUsageAsync(Guid customerId, string resource, long delta, CancellationToken ct = default);
}

/// <summary>Licences organisation / entreprise / utilisateur / terminal / module (§27-29) + jeton offline signé (§28).</summary>
public interface ILicenseService
{
    Task<IReadOnlyList<LicenseDto>> ListForCustomerAsync(Guid customerId, CancellationToken ct = default);
    Task<Result<LicenseDto>> IssueAsync(IssueLicenseRequest request, CancellationToken ct = default);
    Task<Result<LicenseDto>> RevokeAsync(Guid id, RevokeLicenseRequest request, CancellationToken ct = default);
    Task<Result<LicenseTokenDto>> IssueOfflineTokenAsync(Guid id, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<LicenseVerifyResult> VerifyOfflineTokenAsync(string token, CancellationToken ct = default);
}

/// <summary>Support plateforme : tickets et accès assisté avec consentement (§37-38).</summary>
public interface ISupportDeskService
{
    Task<IReadOnlyList<SupportTicketDto>> ListTicketsAsync(Guid? customerId, string? status, CancellationToken ct = default);
    Task<Result<SupportTicketDto>> GetTicketAsync(Guid id, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<SupportTicketDto>> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default);
    Task<Result<SupportTicketDto>> ReplyAsync(Guid id, TicketReplyRequest request, Guid? restrictToCustomer, CancellationToken ct = default);
    Task<Result<SupportTicketDto>> UpdateTicketAsync(Guid id, UpdateTicketRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<SupportAccessGrantDto>> ListAccessGrantsAsync(Guid customerId, CancellationToken ct = default);
    Task<Result<SupportAccessGrantDto>> RequestAccessAsync(RequestSupportAccessRequest request, CancellationToken ct = default);
    Task<Result<SupportAccessGrantDto>> ApproveAccessAsync(Guid id, CancellationToken ct = default);
    Task<Result> RevokeAccessAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Métriques SaaS pour piloter la plateforme (§55) — jamais confondues avec les KPI des clients.</summary>
public interface ISaasMetricsService
{
    Task<SaasMetricsDto> GetAsync(CancellationToken ct = default);
}
