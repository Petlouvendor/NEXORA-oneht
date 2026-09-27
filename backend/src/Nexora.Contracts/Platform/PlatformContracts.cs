namespace Nexora.Contracts.Platform;

// =============================================================================
// Administration SaaS & monétisation (Phase 38). La facturation de NEXORA est
// distincte de la comptabilité des entreprises clientes (§2, §43-44).
// =============================================================================

// --- §4-6 Catalogue : plans -------------------------------------------

public sealed record PlanDto(
    Guid Id, string Code, string Name, string Tier, string? Description,
    decimal PriceAmount, string CurrencyCode, string Period, int TrialDays,
    IReadOnlyDictionary<string, long> Quotas,
    IReadOnlyDictionary<string, string> Features,
    bool IsPublic, int SortOrder, string Status);

public sealed record UpsertPlanRequest(
    string Code, string Name, string Tier, string? Description,
    decimal PriceAmount, string CurrencyCode, string Period, int TrialDays,
    IReadOnlyDictionary<string, long>? Quotas,
    IReadOnlyDictionary<string, string>? Features,
    bool IsPublic = true, int SortOrder = 0, string Status = "active");

// --- §20 Add-ons -----------------------------------------------------

public sealed record AddonDto(
    Guid Id, string Code, string Name, string Kind, string? QuotaKey, long QuotaDelta,
    decimal UnitPrice, string CurrencyCode, string Period, bool IsPublic, string Status);

public sealed record UpsertAddonRequest(
    string Code, string Name, string Kind, string? QuotaKey, long QuotaDelta,
    decimal UnitPrice, string CurrencyCode, string Period = "monthly", bool IsPublic = true, string Status = "active");

// --- §40 Coupons ----------------------------------------------------

public sealed record CouponDto(
    Guid Id, string Code, string? Description, string Kind, decimal Value, string? CurrencyCode,
    string Duration, int? MaxRedemptions, int RedeemedCount, DateTimeOffset? ExpiresAt, string Status);

public sealed record UpsertCouponRequest(
    string Code, string? Description, string Kind, decimal Value, string? CurrencyCode,
    string Duration = "once", int? MaxRedemptions = null, DateTimeOffset? ExpiresAt = null, string Status = "active");

// --- §7-9 Compte client -------------------------------------------

public sealed record CustomerDto(
    Guid Id, string CustomerRef, string LegalName, string? PrimaryContactName, string? PrimaryContactEmail,
    string? BillingEmail, string? Phone, string CountryCode, string BillingCurrency, string? TaxId,
    bool TaxExempt, string? CommercialTerms, string Status,
    IReadOnlyList<Guid> CompanyIds, DateTimeOffset CreatedAt);

public sealed record CreateCustomerRequest(
    string LegalName, string? PrimaryContactName = null, string? PrimaryContactEmail = null, string? BillingEmail = null,
    string? Phone = null, string CountryCode = "HT", string BillingCurrency = "USD", string? TaxId = null,
    bool TaxExempt = false, string? CommercialTerms = null);

public sealed record UpdateCustomerRequest(
    string? LegalName, string? PrimaryContactName, string? PrimaryContactEmail, string? BillingEmail,
    string? Phone, string? CountryCode, string? BillingCurrency, string? TaxId, bool? TaxExempt,
    string? CommercialTerms, string? Status);

public sealed record LinkCompanyRequest(Guid CompanyId);

// --- §10-19 Abonnement -------------------------------------------

public sealed record SubscriptionAddonDto(Guid Id, Guid AddonId, string AddonCode, int Quantity, decimal UnitPrice);

public sealed record SubscriptionDto(
    Guid Id, Guid CustomerId, Guid PlanId, string PlanCode, string PlanName, string Tier,
    string Status, string Period, decimal PriceAmount, string CurrencyCode,
    DateTimeOffset? TrialEndsAt, DateTimeOffset CurrentPeriodStart, DateTimeOffset CurrentPeriodEnd,
    bool AutoRenew, bool CancelAtPeriodEnd, DateTimeOffset? CanceledAt, DateTimeOffset? GraceEndsAt,
    DateTimeOffset? SuspendedAt, string? SuspendReason, int PaymentFailures, DateTimeOffset? ActivatedAt,
    IReadOnlyList<SubscriptionAddonDto> Addons, DateTimeOffset CreatedAt);

public sealed record CreateSubscriptionRequest(
    Guid PlanId, string? Period = null, bool StartTrial = true, string? CouponCode = null,
    IReadOnlyDictionary<string, long>? QuotasOverride = null, decimal? PriceOverride = null);

public sealed record ChangePlanRequest(Guid PlanId, string? CouponCode = null, bool PreviewOnly = false);

public sealed record AddonChangeRequest(Guid AddonId, int Quantity);

public sealed record CancelSubscriptionRequest(bool Immediate = false, string? Reason = null);

public sealed record SuspendSubscriptionRequest(string Reason);

/// <summary>Impact d'un downgrade avant confirmation (§18).</summary>
public sealed record PlanChangeImpactDto(
    bool Allowed, string Direction, decimal ProrationAmount, string CurrencyCode,
    IReadOnlyList<QuotaBreachDto> QuotaBreaches,
    IReadOnlyList<string> FeaturesLost,
    string Summary);

public sealed record QuotaBreachDto(string Resource, long Limit, long CurrentUsage);

// --- §13, §41-44 Facturation NEXORA ---------------------------

public sealed record InvoiceLineDto(string LineType, string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

public sealed record InvoiceDto(
    Guid Id, string Number, Guid CustomerId, Guid? SubscriptionId, string Kind, string Status,
    string CurrencyCode, DateOnly IssueDate, DateOnly DueDate, DateOnly? PeriodStart, DateOnly? PeriodEnd,
    decimal SubtotalAmount, decimal DiscountAmount, decimal TaxAmount, decimal TaxRate, decimal TotalAmount,
    decimal PaidAmount, string? CouponCode, Guid? ParentInvoiceId, DateTimeOffset? PaidAt,
    IReadOnlyList<InvoiceLineDto> Lines, DateTimeOffset CreatedAt);

public sealed record CreditNoteRequest(decimal? Amount = null, string? Reason = null);

// --- §15-16, §54 Paiements NEXORA ----------------------------

public sealed record PaymentDto(
    Guid Id, Guid? CustomerId, Guid? InvoiceId, string Method, string? Provider, string? ProviderRef,
    decimal Amount, string CurrencyCode, string Status, string? FailureReason, bool ReviewRequired,
    bool IsManual, DateTimeOffset ReceivedAt, DateTimeOffset? ReconciledAt, decimal RefundedAmount);

public sealed record RecordManualPaymentRequest(
    Guid CustomerId, Guid? InvoiceId, decimal Amount, string CurrencyCode, string Method = "bank_transfer",
    string? ProviderRef = null, string? Provider = null);

/// <summary>Événement du fournisseur de paiement (webhook §54). Idempotent par <c>EventId</c>.</summary>
public sealed record PaymentWebhookEvent(
    string EventId, string EventType, string? Provider, string? CustomerRef, string? InvoiceNumber,
    Guid? CustomerId, Guid? InvoiceId, decimal Amount, string CurrencyCode, string? ProviderRef,
    string Outcome);   // succeeded | failed | ambiguous

public sealed record PaymentMethodDto(
    Guid Id, string Method, string? Provider, string? Brand, string? Last4, int? ExpMonth, int? ExpYear,
    bool IsDefault, string Status);

public sealed record AddPaymentMethodRequest(
    string Method, string Provider, string ProviderToken, string? Brand = null, string? Last4 = null,
    int? ExpMonth = null, int? ExpYear = null, bool MakeDefault = true);

// --- §21-24 Quotas & usage -----------------------------------

public sealed record QuotaLineDto(string Resource, long Limit, long Used, decimal Percent, bool Exceeded);

public sealed record QuotaReportDto(
    Guid CustomerId, string PlanCode, DateOnly PeriodStart, IReadOnlyList<QuotaLineDto> Lines);

public sealed record QuotaCheckResult(bool Allowed, string Resource, long Limit, long Used, long Requested, string? Message);

public sealed record RecordUsageRequest(string Resource, long Delta);

// --- §27-29 Licences -----------------------------------------

public sealed record LicenseDto(
    Guid Id, Guid CustomerId, Guid? SubscriptionId, string Scope, string? SubjectRef, string Status,
    int GraceDays, DateTimeOffset ActivatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? LastIssuedAt,
    DateTimeOffset? RevokedAt, string? RevokeReason);

public sealed record IssueLicenseRequest(Guid CustomerId, string Scope = "organization", string? SubjectRef = null, int GraceDays = 7, int ValidityDays = 35);

public sealed record RevokeLicenseRequest(string Reason);

/// <summary>Jeton de licence offline signé (§28). <c>token</c> = base64url(payload).base64url(hmacSHA256).</summary>
public sealed record LicenseTokenDto(string Token, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, DateTimeOffset HardExpiresAt);

public sealed record LicenseVerifyRequest(string Token);

public sealed record LicenseVerifyResult(
    bool Valid, string Reason, Guid? LicenseId, Guid? CustomerId, string? Scope,
    DateTimeOffset? ExpiresAt, bool InGracePeriod);

// --- §37-38 Support ----------------------------------------

public sealed record SupportTicketDto(
    Guid Id, string TicketRef, Guid? CustomerId, Guid? CompanyId, string Subject, string Priority,
    string Status, string? RequesterEmail, Guid? AssignedTo, bool Escalated, DateTimeOffset? ResolvedAt,
    IReadOnlyList<SupportMessageDto> Messages, DateTimeOffset CreatedAt);

public sealed record SupportMessageDto(Guid Id, Guid? AuthorUserId, string Body, bool IsInternal, DateTimeOffset CreatedAt);

public sealed record CreateTicketRequest(string Subject, string Priority = "p3", Guid? CustomerId = null, Guid? CompanyId = null, string? RequesterEmail = null, string? Message = null);

public sealed record TicketReplyRequest(string Body, bool Internal = false);

public sealed record UpdateTicketRequest(string? Status = null, string? Priority = null, Guid? AssignedTo = null, bool? Escalated = null);

public sealed record SupportAccessGrantDto(
    Guid Id, Guid CustomerId, Guid? CompanyId, Guid StaffUserId, string Reason, string Scope,
    DateTimeOffset? ApprovedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt, DateTimeOffset CreatedAt);

public sealed record RequestSupportAccessRequest(Guid CustomerId, Guid StaffUserId, string Reason, Guid? CompanyId = null, string Scope = "read", int Hours = 24);

// --- §55 Métriques SaaS (≠ KPI clients) --------------------

public sealed record SaasMetricsDto(
    decimal Mrr, decimal Arr, string CurrencyCode,
    int ActiveSubscriptions, int TrialingSubscriptions, int PastDueSubscriptions, int SuspendedSubscriptions,
    int CanceledLast30d, decimal ChurnRatePct, decimal TrialConversionPct, decimal Arpu,
    decimal PaymentSuccessRatePct, int OpenTickets, int PaymentsAwaitingReview, DateTimeOffset GeneratedAt);
