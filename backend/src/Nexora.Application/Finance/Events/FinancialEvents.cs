using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Events;

/// <summary>
/// Événement financier : conséquence d'une opération métier (Phase 4 §8).
/// Porté par un identifiant d'opération unique pour l'idempotence (§33).
/// </summary>
public abstract record FinancialEvent(Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt)
{
    public Guid? UserId { get; init; }
}

/// <summary>Vente au comptant (§9).</summary>
public sealed record CashSaleRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SaleId, Money Net, Money Tax, Guid TenderFinancialAccountId, Money? CostOfGoods = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>Vente à crédit (§10).</summary>
public sealed record CreditSaleRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SaleId, Guid CustomerId, Money Net, Money Tax, DateOnly? DueDate, Money? CostOfGoods = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>Facture fournisseur à crédit (§12).</summary>
public sealed record SupplierInvoiceRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SupplierInvoiceId, Guid SupplierId, Money Net, Money Tax, DateOnly? DueDate, bool CapitaliseToInventory,
    Money? StockNet = null, Money? StockAccrued = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>
/// Réception de marchandises stockées (compte 4081, décision du 2026-10-09) : le stock entre en comptabilité dès la
/// réception, au coût de réception, en contrepartie de « fournisseurs — factures non parvenues ». La facture solde
/// ensuite ce compte.
/// </summary>
public sealed record GoodsReceiptRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid GoodsReceiptId, Guid SupplierId, Money Value)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Achat au comptant (§11).</summary>
public sealed record CashPurchaseRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid ReferenceId, Money Net, Money Tax, Guid TenderFinancialAccountId, bool CapitaliseToInventory,
    Money? StockNet = null, Money? StockAccrued = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>Règlement reçu d'un client, avec affectations aux créances (§10, §15).</summary>
public sealed record CustomerPaymentReceived(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid CustomerId, Money Amount, Guid TenderFinancialAccountId,
    IReadOnlyList<AllocationInstruction> Allocations, string? Reference = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    // Module clients §9 : moyen de paiement, session de caisse et note conservés sur le versement.
    public Guid? PaymentMethodId { get; init; }
    public Guid? CashSessionId { get; init; }
    public string? Note { get; init; }
}

/// <summary>Règlement effectué à un fournisseur, avec affectations aux dettes (§12, §15).</summary>
public sealed record SupplierPaymentMade(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SupplierId, Money Amount, Guid TenderFinancialAccountId,
    IReadOnlyList<AllocationInstruction> Allocations, string? Reference = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Dépense approuvée (§19).</summary>
public sealed record ExpenseRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid ExpenseId, Money Net, Money Tax, Guid TenderFinancialAccountId, Guid? ExpenseAccountId = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>Écart constaté à la clôture de caisse (§17).</summary>
public sealed record CashCountRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid CashSessionId, Guid CashFinancialAccountId, Money Expected, Money Counted)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Variance => Counted.Subtract(Expected);
}

/// <summary>Transfert entre deux comptes financiers (§8).</summary>
public sealed record FinancialTransferRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid FromFinancialAccountId, Guid ToFinancialAccountId, Money Amount)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Frais bancaires (§8).</summary>
public sealed record BankChargeRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid BankFinancialAccountId, Money Amount, string? Description = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Radiation d'une créance client irrécouvrable (Phase 27 §37).</summary>
public sealed record ReceivableWrittenOff(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid ReceivableId, Guid CustomerId, Money Amount, string Reason)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Sortie de stock pour perte / casse / expiration (Phase 5 §16).</summary>
public sealed record InventoryWriteOffRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid AdjustmentId, Money Value, string Kind)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Réévaluation de stock issue d'un écart d'inventaire (Phase 5 §15, §29).</summary>
public sealed record InventoryRevaluationRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid AdjustmentId, Money ValuationDelta)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Retour fournisseur (Phase 5 §28).</summary>
public sealed record SupplierReturnRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SupplierReturnId, Guid SupplierId, Money Net, Money Tax, bool AgainstPayable,
    Money? StockCost = null, bool Invoiced = true)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>Répartition d'un encaissement par compte financier (Phase 6 §14).</summary>
public sealed record TenderSplit(Guid FinancialAccountId, Money Amount);

/// <summary>
/// Vente POS unique (Phase 6 §15-16, §39) : comptant, à crédit ou mixte.
/// somme(Tenders) + CreditAmount = Net + Tax.
/// </summary>
public sealed record PosSaleRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SaleId, Guid? CustomerId, Money Net, Money Tax,
    IReadOnlyList<TenderSplit> Tenders, Money CreditAmount, DateOnly? DueDate, Money? CostOfGoods = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);
}

/// <summary>Remboursement / avoir sur une vente (Phase 6 §22-23) — écriture de correction.</summary>
public sealed record SaleRefundRecorded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SaleReturnId, Guid? CustomerId, Money Net, Money Tax, string RefundMethod,
    Guid? TenderFinancialAccountId, bool AgainstReceivable, Money? CostOfGoodsRestocked = null)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt)
{
    public Money Total => Net.Add(Tax);

    /// <summary>Part imputée sur la créance de la vente (≤ son reste dû). NULL = tout le retour
    /// (comportement d'origine quand <see cref="AgainstReceivable"/>).</summary>
    public Money? ReceivableRelief { get; init; }

    /// <summary>Le reliquat au-delà de la créance devient un avoir client disponible (compte
    /// <c>store_credit</c>) au lieu d'une sortie de trésorerie — module clients §7, §11.</summary>
    public bool RemainderToStoreCredit { get; init; }
}

/// <summary>Imputation d'un avoir client disponible sur une créance (module clients §7) :
/// Dr compte porteur de l'avoir (store_credit / suspense) / Cr clients.</summary>
public sealed record CustomerCreditApplied(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid CreditMemoId, Guid CustomerId, Money Amount, string SourceAccountKey)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Remboursement d'un avoir client (module clients §7) : Dr compte de l'avoir / Cr trésorerie.</summary>
public sealed record CustomerCreditRefunded(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid RefundId, Guid CustomerId, Money Amount, string SourceAccountKey, Guid TenderFinancialAccountId)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Crédit hors vente — reprise de solde (module clients §22) : Dr clients / Cr compte d'attente,
/// et création du crédit (origine <c>opening</c>, <c>source_id</c> = identifiant d'opération).</summary>
public sealed record CustomerCreditOpened(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid CustomerId, Money Amount, DateOnly? DueDate, string Reason)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Dépense imputée refacturée au client (module clients §10) : Dr clients / Cr produits,
/// et création du crédit correspondant (origine <c>customer_expense</c>).</summary>
public sealed record CustomerExpenseBilled(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid CustomerExpenseId, Guid CustomerId, Money Amount, DateOnly? DueDate, string Description)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

// --- Module paie, RH & avances (docs/module-paie-rh-avances.md §32) ----------------------------

/// <summary>Versement d'une avance, d'un acompte ou d'un prêt au personnel : une créance sur l'employé
/// naît (Dr avances / prêts au personnel, tiers = employé) — jamais une charge salariale (§14, §39).</summary>
public sealed record EmployeeAdvancePaid(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid AdvanceId, Guid EmployeeId, Money Amount, bool IsLoan, Guid TenderFinancialAccountId, string Reference)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Remboursement direct (hors paie) d'une avance ou d'un prêt par l'employé : la créance diminue
/// (Dr trésorerie / Cr avances ou prêts au personnel).</summary>
public sealed record EmployeeAdvanceRepaid(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid RepaymentId, Guid AdvanceId, Guid EmployeeId, Money Amount, bool IsLoan, Guid TenderFinancialAccountId, string Reference)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Montants d'un employé dans une paie approuvée, en devise de la ligne de paie. Équilibre :
/// charge salariale + frais remboursés + charges patronales = retenues légales (salariales + patronales)
/// + récupérations d'avances + échéances de prêts + autres retenues + net à payer.</summary>
public sealed record PayrollEmployeePosting(
    Guid EmployeeId, string EmployeeLabel, string Currency,
    decimal SalaryExpense, decimal Reimbursements, decimal EmployerContributions,
    decimal EmployeeWithholdings, decimal AdvanceRecovery, decimal LoanRecovery, decimal OtherDeductions, decimal Net);

/// <summary>Paie approuvée (§22, §32) : création des obligations salariales dans le journal de paie.</summary>
public sealed record PayrollApproved(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid RunId, string RunNumber, string PeriodLabel, IReadOnlyList<PayrollEmployeePosting> Employees)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Paiement d'un salaire net : sortie de trésorerie qui solde la dette salariale (§24, §32).</summary>
public sealed record SalaryPaid(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid SalaryPaymentId, Guid EmployeeId, Money Amount, Guid TenderFinancialAccountId, string Reference)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Note de frais validée : dette de l'entreprise envers l'employé (§17 « sens financier inverse »).</summary>
public sealed record EmployeeExpenseApproved(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid ClaimId, Guid EmployeeId, Money Amount, Guid? ExpenseAccountId, string Description)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Remboursement direct d'une note de frais : Dr frais à rembourser / Cr trésorerie.</summary>
public sealed record EmployeeExpensePaid(
    Guid CompanyId, Guid OperationUuid, DateTimeOffset OccurredAt,
    Guid ClaimId, Guid EmployeeId, Money Amount, Guid TenderFinancialAccountId, string Reference)
    : FinancialEvent(CompanyId, OperationUuid, OccurredAt);

/// <summary>Consigne d'affectation d'un paiement à une créance/dette (§15).</summary>
public sealed record AllocationInstruction(Guid TargetId, decimal Amount, bool IsReceivable);
