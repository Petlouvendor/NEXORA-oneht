using Nexora.Contracts.Customers;
using Nexora.Domain.Common;

namespace Nexora.Application.Customers;

// =============================================================================
// Module « Gestion clients & comptes clients » (docs/module-clients-comptes-clients.md).
// Aucune de ces interfaces ne stocke un solde : il est toujours reconstruit depuis le journal
// financier client (customer_ledger_entry), lui-même alimenté par la base à chaque mouvement.
// =============================================================================

/// <summary>
/// Émission d'avoirs clients (§7, §11). Utilisé par le retour de vente et par le versement
/// excédentaire ; l'imputation automatique sur la créance de la vente ne passe pas d'écriture
/// (le retour a déjà crédité le compte client).
/// </summary>
public interface ICustomerCreditMemoWriter
{
    /// <summary>Avoir de <paramref name="amount"/> dont <paramref name="appliedToReceivable"/> est imputé tout
    /// de suite sur <paramref name="receivableId"/> ; le reste est disponible (compte <c>store_credit</c>).</summary>
    Task<Guid> IssueForReturnAsync(Guid customerId, Guid saleReturnId, Guid currencyId, decimal amount,
        Guid? receivableId, decimal appliedToReceivable, string? reason, CancellationToken ct = default);

    /// <summary>Excédent d'un versement converti en avoir disponible (porté en compte d'attente).</summary>
    Task<Guid> IssueForOverpaymentAsync(Guid customerId, Guid paymentId, Guid currencyId, decimal amount,
        CancellationToken ct = default);

    /// <summary>Acompte d'une commande client (phase 4) : avoir rattaché à la commande, porté en compte
    /// d'attente comme l'encaissement qui l'a créé ; imputé sur la vente à l'exécution.</summary>
    Task<Guid> IssueForOrderDepositAsync(Guid customerId, Guid paymentId, Guid salesOrderId, Guid currencyId, decimal amount,
        string orderNumber, CancellationToken ct = default);

    /// <summary>Acompte encore disponible d'une commande (avoirs « order_deposit » non consommés).</summary>
    Task<decimal> OrderDepositAvailableAsync(Guid salesOrderId, CancellationToken ct = default);

    /// <summary>Avoir disponible du client dans cette devise.</summary>
    Task<decimal> AvailableAsync(Guid customerId, string currency, CancellationToken ct = default);

    /// <summary>Paiement par avoir (§7, §15) : impute les avoirs disponibles, du plus ancien au plus récent,
    /// sur la créance indiquée (écriture Dr compte de l'avoir / Cr clients). Renvoie le montant imputé.
    /// Avec <paramref name="preferSalesOrderId"/>, les acomptes de cette commande passent en premier.</summary>
    Task<decimal> ApplyToReceivableAsync(Guid customerId, Guid receivableId, string currency, decimal amount,
        DateTimeOffset at, CancellationToken ct = default, Guid? preferSalesOrderId = null);
}

/// <summary>Versements (§9), annulation contrôlée (§22) et imputation d'avoirs (§7).</summary>
public interface ICustomerAccountService
{
    Task<Result<CustomerPaymentDto>> RecordPaymentAsync(Guid customerId, RecordCustomerPaymentRequest request, CancellationToken ct = default);
    Task<Result<CustomerPaymentDto>> RecordCreditPaymentAsync(Guid receivableId, RecordCreditPaymentRequest request, CancellationToken ct = default);
    /// <summary>Contre-passe un versement : affectations neutralisées, extourne comptable, sortie de
    /// trésorerie. Le versement reste visible, au statut <c>void</c>.</summary>
    Task<Result<CustomerPaymentDto>> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest request, CancellationToken ct = default);
    Task<Result<ApplyCreditMemoResultDto>> ApplyCreditMemoAsync(Guid creditMemoId, ApplyCreditMemoRequest request, CancellationToken ct = default);
    /// <summary>§22 POST /customers/{id}/credits : crédit hors vente (reprise de solde), motivé et audité.</summary>
    Task<Result<CustomerCreditDetailDto>> OpenCreditAsync(Guid customerId, OpenCustomerCreditRequest request, CancellationToken ct = default);
    /// <summary>§8 : échéancier du reste dû ; remplace l'échéancier actif (versionné, jamais effacé).</summary>
    Task<Result<CustomerCreditDetailDto>> ScheduleAsync(Guid receivableId, ScheduleInstallmentsRequest request, CancellationToken ct = default);
}

/// <summary>
/// Dépassement de limite de crédit au POS (§12) : demande du caissier → approbation par le workflow
/// configuré (Phase 11, <c>approval_policy</c> type <c>credit_sale</c> : niveaux, approbateurs distincts)
/// → la vente de CE panier est autorisée une fois, pour au plus le montant approuvé.
/// </summary>
public interface ICreditOverrideService
{
    Task<Result<CreditOverrideDto>> RequestAsync(CreditOverrideRequestInput request, CancellationToken ct = default);
    Task<Result<CreditOverrideDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<CreditOverrideDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<Result<CreditOverrideDto>> ApproveAsync(Guid id, string? note, CancellationToken ct = default);
    Task<Result<CreditOverrideDto>> RejectAsync(Guid id, string? note, CancellationToken ct = default);
}

/// <summary>Remboursement d'avoir (§7, §29) : demandé → approuvé (≠ demandeur) → exécuté.</summary>
public interface ICustomerRefundService
{
    Task<Result<CustomerRefundDto>> RequestAsync(RequestCustomerRefundRequest request, CancellationToken ct = default);
    Task<Result<CustomerRefundDto>> ApproveAsync(Guid refundId, CustomerDecisionRequest decision, CancellationToken ct = default);
    Task<Result<CustomerRefundDto>> RejectAsync(Guid refundId, CustomerDecisionRequest decision, CancellationToken ct = default);
    Task<Result<CustomerRefundDto>> ExecuteAsync(Guid refundId, ExecuteCustomerRefundRequest request, CancellationToken ct = default);
    Task<Result<CustomerRefundDto>> CancelAsync(Guid refundId, CustomerCancelRequest request, CancellationToken ct = default);
    Task<CustomerRefundDto?> GetAsync(Guid refundId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerRefundDto>> ListAsync(Guid? customerId, string? status, CancellationToken ct = default);
}

/// <summary>Dépenses imputées (§10) : brouillon → en attente → validée (≠ auteur) / rejetée ;
/// une dépense validée ne s'annule que par contre-écriture.</summary>
public interface ICustomerExpenseService
{
    Task<Result<CustomerExpenseDto>> CreateAsync(Guid customerId, CreateCustomerExpenseRequest request, CancellationToken ct = default);
    Task<Result<CustomerExpenseDto>> SubmitAsync(Guid expenseId, CancellationToken ct = default);
    Task<Result<CustomerExpenseDto>> ApproveAsync(Guid expenseId, CustomerDecisionRequest decision, CancellationToken ct = default);
    Task<Result<CustomerExpenseDto>> RejectAsync(Guid expenseId, CustomerDecisionRequest decision, CancellationToken ct = default);
    Task<Result<CustomerExpenseDto>> CancelAsync(Guid expenseId, CustomerCancelRequest request, CancellationToken ct = default);
    Task<CustomerExpenseDto?> GetAsync(Guid expenseId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerExpenseDto>> ListAsync(Guid? customerId, string? status, CancellationToken ct = default);
}

/// <summary>Actions de recouvrement (§13) — journal sans modification ni suppression.</summary>
public interface ICollectionActionService
{
    Task<Result<CollectionActionDto>> LogAsync(Guid customerId, CreateCollectionActionRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<CollectionActionDto>> ListAsync(Guid? customerId, bool onlyDue, CancellationToken ct = default);
}

/// <summary>Lectures du compte client (§7, §8, §9, §14, §15, §17). Toutes filtrées par entreprise active.</summary>
public interface ICustomerAccountQueries
{
    Task<IReadOnlyList<CustomerLookupDto>> LookupAsync(string query, int limit = 10, CancellationToken ct = default);
    Task<CustomerAccountSummaryDto?> SummaryAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    /// <summary>Journal d'UNE devise (défaut : devise du client) — deux devises ne se cumulent jamais (§21).</summary>
    Task<CustomerLedgerDto?> LedgerAsync(Guid customerId, DateOnly? from, DateOnly? to, string? currency = null, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerCreditDto>> CreditsAsync(Guid? customerId, string? status, CancellationToken ct = default);
    Task<CustomerCreditDetailDto?> CreditAsync(Guid receivableId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerPaymentDto>> PaymentsAsync(Guid? customerId, Guid? receivableId, CancellationToken ct = default);
    Task<CustomerPaymentDto?> PaymentAsync(Guid paymentId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerPurchaseDto>> PurchasesAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken ct = default);
    Task<IReadOnlyList<CreditMemoDto>> CreditMemosAsync(Guid? customerId, bool onlyAvailable, CancellationToken ct = default);
    Task<CreditMemoDto?> CreditMemoAsync(Guid creditMemoId, CancellationToken ct = default);
    Task<IReadOnlyList<CustomerTimelineEventDto>> TimelineAsync(Guid customerId, int limit = 200, CancellationToken ct = default);
    /// <summary>Comptes d'encaissement actifs et moyens de paiement (formulaire de versement / remboursement).</summary>
    Task<CustomerPaymentOptionsDto> PaymentOptionsAsync(CancellationToken ct = default);
}

/// <summary>Rapports clients (§26), exportables selon <c>customers.export</c>.</summary>
public interface ICustomerReports
{
    IReadOnlyList<CustomerReportDefinitionDto> Catalog();
    Task<Result<CustomerReportDto>> RunAsync(string key, DateOnly? from, DateOnly? to, CancellationToken ct = default);
}

/// <summary>Alertes crédit (§25) — job <c>credit.scan_alerts</c>, dédupliquées.</summary>
public interface ICreditAlertScanner
{
    Task<int> ScanAsync(CancellationToken ct = default);
}
