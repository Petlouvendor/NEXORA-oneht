using Nexora.Application.Finance.Events;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Posting;

public interface IPostingRuleSet
{
    /// <summary>Construit le brouillon d'écriture équilibré pour un événement (Phase 4 §7, §9-19).</summary>
    Task<JournalEntryDraft> BuildAsync(FinancialEvent evt, PostingContext ctx, CancellationToken ct = default);
}

/// <summary>
/// Règles de génération comptable centralisées (§16 : POS/Achats ne les réimplémentent pas).
/// Chaque règle produit une écriture équilibrée en devise fonctionnelle.
/// </summary>
public sealed class PostingRuleSet : IPostingRuleSet
{
    public async Task<JournalEntryDraft> BuildAsync(FinancialEvent evt, PostingContext ctx, CancellationToken ct = default)
    {
        var draft = evt switch
        {
            CashSaleRecorded e => await CashSale(e, ctx, ct),
            CreditSaleRecorded e => await CreditSale(e, ctx, ct),
            SupplierInvoiceRecorded e => await SupplierInvoice(e, ctx, ct),
            GoodsReceiptRecorded e => await GoodsReceipt(e, ctx, ct),
            CashPurchaseRecorded e => await CashPurchase(e, ctx, ct),
            CustomerPaymentReceived e => await CustomerPayment(e, ctx, ct),
            SupplierPaymentMade e => await SupplierPayment(e, ctx, ct),
            ExpenseRecorded e => await Expense(e, ctx, ct),
            CashCountRecorded e => await CashCount(e, ctx, ct),
            FinancialTransferRecorded e => await Transfer(e, ctx, ct),
            BankChargeRecorded e => await BankCharge(e, ctx, ct),
            ReceivableWrittenOff e => await ReceivableWriteOff(e, ctx, ct),
            InventoryWriteOffRecorded e => await InventoryWriteOff(e, ctx, ct),
            InventoryRevaluationRecorded e => await InventoryRevaluation(e, ctx, ct),
            SupplierReturnRecorded e => await SupplierReturn(e, ctx, ct),
            PosSaleRecorded e => await PosSale(e, ctx, ct),
            SaleRefundRecorded e => await SaleRefund(e, ctx, ct),
            CustomerCreditApplied e => await CustomerCreditApplication(e, ctx, ct),
            CustomerCreditRefunded e => await CustomerCreditRefund(e, ctx, ct),
            CustomerExpenseBilled e => await CustomerExpense(e, ctx, ct),
            CustomerCreditOpened e => await CustomerCreditOpening(e, ctx, ct),
            EmployeeAdvancePaid e => await EmployeeAdvancePayment(e, ctx, ct),
            EmployeeAdvanceRepaid e => await EmployeeAdvanceRepayment(e, ctx, ct),
            PayrollApproved e => await Payroll(e, ctx, ct),
            SalaryPaid e => await SalaryPayment(e, ctx, ct),
            EmployeeExpenseApproved e => await EmployeeExpense(e, ctx, ct),
            EmployeeExpensePaid e => await EmployeeExpensePayment(e, ctx, ct),
            _ => throw new NotSupportedException($"Aucune règle de génération pour {evt.GetType().Name}."),
        };
        draft.EnsureBalanced();
        return draft;
    }

    private static async Task<JournalEntryDraft> CashSale(CashSaleRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("VT", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "sale", e.SaleId, "Vente au comptant");

        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        d.Debit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Total, totalBase, rateId);
        d.Credit(await ctx.Account(AccountKeys.SalesRevenue, ct), e.Net, netBase, rateId);
        if (!e.Tax.IsZero)
            d.Credit(await ctx.Account(AccountKeys.SalesTaxOutput, ct), e.Tax, taxBase, rateId);

        await AddCogs(d, e.CostOfGoods, ctx, e.OccurredAt, ct);
        return d;
    }

    private static async Task<JournalEntryDraft> CreditSale(CreditSaleRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("VT", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "sale", e.SaleId, "Vente à crédit");

        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        d.Debit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.Total, totalBase, rateId,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        d.Credit(await ctx.Account(AccountKeys.SalesRevenue, ct), e.Net, netBase, rateId);
        if (!e.Tax.IsZero)
            d.Credit(await ctx.Account(AccountKeys.SalesTaxOutput, ct), e.Tax, taxBase, rateId);

        await AddCogs(d, e.CostOfGoods, ctx, e.OccurredAt, ct);
        return d;
    }

    private static async Task AddCogs(JournalEntryDraft d, Money? cogs, PostingContext ctx, DateTimeOffset at, CancellationToken ct)
    {
        if (cogs is not { } c || c.IsZero) return;
        var (cogsBase, rateId) = await ctx.ToFunctional(c, at, ct);
        d.Debit(await ctx.Account(AccountKeys.Cogs, ct), c, cogsBase, rateId, "Coût des marchandises vendues");
        d.Credit(await ctx.Account(AccountKeys.Inventory, ct), c, cogsBase, rateId);
    }

    private static async Task<JournalEntryDraft> SupplierInvoice(SupplierInvoiceRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("AC", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "supplier_invoice", e.SupplierInvoiceId, "Facture fournisseur");

        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        await DebitPurchase(d, ctx, e.Net, netBase, rateId, e.StockNet, e.StockAccrued, e.CapitaliseToInventory, e.OccurredAt, ct);
        if (!e.Tax.IsZero)
            d.Debit(await ctx.Account(AccountKeys.PurchaseTaxInput, ct), e.Tax, taxBase, rateId);
        d.Credit(await ctx.Account(AccountKeys.AccountsPayable, ct), e.Total, totalBase, rateId,
            thirdPartyType: "supplier", thirdPartyId: e.SupplierId);
        return d;
    }

    /// <summary>
    /// Réception (compte 4081) : D Stock / C Fournisseurs — factures non parvenues, au coût de réception. Le stock
    /// et le compte 3110 avancent ensemble ; la facture soldera le 4081.
    /// </summary>
    private static async Task<JournalEntryDraft> GoodsReceipt(GoodsReceiptRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("AC", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "goods_receipt", e.GoodsReceiptId, "Réception de marchandises");
        var (valueBase, rateId) = await ctx.ToFunctional(e.Value, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.Inventory, ct), e.Value, valueBase, rateId);
        d.Credit(await ctx.Account(AccountKeys.GoodsReceivedNotInvoiced, ct), e.Value, valueBase, rateId,
            thirdPartyType: "supplier", thirdPartyId: e.SupplierId);
        return d;
    }

    /// <summary>
    /// Débit d'un achat facturé. Marchandises stockées : solde du 4081 pour leur coût de réception
    /// (<paramref name="stockAccrued"/>), et l'écart entre prix facturé et coût de réception en charge (écart sur prix
    /// d'achat). Le reste de la facture (frais, services, articles non stockés) : stock ou achats selon
    /// <paramref name="capitalise"/>. Sans montants stockés, comportement d'avant le 4081.
    /// </summary>
    private static async Task DebitPurchase(JournalEntryDraft d, PostingContext ctx, Money net, Money netBase, Guid? rateId,
        Money? stockNet, Money? stockAccrued, bool capitalise, DateTimeOffset at, CancellationToken ct)
    {
        var stock = stockNet ?? Money.Zero(net.Currency);
        var accrued = stockAccrued ?? stock;
        var variance = stock - accrued;
        var other = net - stock;
        var (accruedBase, _) = await ctx.ToFunctional(accrued, at, ct);
        var (varianceBase, _) = await ctx.ToFunctional(variance, at, ct);
        var (otherBase, _) = await ctx.ToFunctional(other, at, ct);
        // Arrondi de conversion : l'écart d'un centime va à la première ligne non nulle (écart, autre, 4081).
        var rest = netBase - accruedBase - varianceBase - otherBase;
        if (!rest.IsZero)
        {
            if (!variance.IsZero) varianceBase += rest;
            else if (!other.IsZero) otherBase += rest;
            else accruedBase += rest;
        }
        if (!accrued.IsZero)
            d.Debit(await ctx.Account(AccountKeys.GoodsReceivedNotInvoiced, ct), accrued, accruedBase, rateId,
                description: "Marchandises reçues facturées");
        if (!variance.IsZero)
            d.Debit(await ctx.Account(AccountKeys.PurchasePriceVariance, ct), variance, varianceBase, rateId,
                description: "Écart sur prix d'achat");
        if (!other.IsZero)
            d.Debit(await ctx.Account(capitalise ? AccountKeys.Inventory : AccountKeys.Purchases, ct), other, otherBase, rateId);
    }

    private static async Task<JournalEntryDraft> CashPurchase(CashPurchaseRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("AC", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "cash_purchase", e.ReferenceId, "Achat au comptant");

        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        await DebitPurchase(d, ctx, e.Net, netBase, rateId, e.StockNet, e.StockAccrued, e.CapitaliseToInventory, e.OccurredAt, ct);
        if (!e.Tax.IsZero)
            d.Debit(await ctx.Account(AccountKeys.PurchaseTaxInput, ct), e.Tax, taxBase, rateId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Total, totalBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> CustomerPayment(CustomerPaymentReceived e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("CA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "payment", null, "Encaissement client");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        d.Credit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.Amount, amountBase, rateId,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        return d;
    }

    private static async Task<JournalEntryDraft> SupplierPayment(SupplierPaymentMade e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("BQ", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "payment", null, "Règlement fournisseur");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.AccountsPayable, ct), e.Amount, amountBase, rateId,
            thirdPartyType: "supplier", thirdPartyId: e.SupplierId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> Expense(ExpenseRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("CA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "expense", e.ExpenseId, "Dépense");
        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        var expenseAccount = e.ExpenseAccountId ?? await ctx.Account(AccountKeys.ExpenseDefault, ct);
        d.Debit(expenseAccount, e.Net, netBase, rateId);
        if (!e.Tax.IsZero)
            d.Debit(await ctx.Account(AccountKeys.PurchaseTaxInput, ct), e.Tax, taxBase, rateId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Total, totalBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> CashCount(CashCountRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "cash_session", e.CashSessionId, "Écart de caisse");
        var variance = e.Variance;                       // + = excédent, - = manquant
        var (varBase, rateId) = await ctx.ToFunctional(variance.Abs(), e.OccurredAt, ct);
        var cash = await ctx.TenderAccount(e.CashFinancialAccountId, ct);
        var shortOver = await ctx.Account(AccountKeys.CashShortOver, ct);

        if (variance.IsPositive)   // excédent : caisse débitée, produit crédité
        {
            d.Debit(cash, variance.Abs(), varBase, rateId);
            d.Credit(shortOver, variance.Abs(), varBase, rateId);
        }
        else                        // manquant : charge débitée, caisse créditée
        {
            d.Debit(shortOver, variance.Abs(), varBase, rateId);
            d.Credit(cash, variance.Abs(), varBase, rateId);
        }
        return d;
    }

    private static async Task<JournalEntryDraft> Transfer(FinancialTransferRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "financial_transfer", e.OperationUuid, "Transfert de trésorerie");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.TenderAccount(e.ToFinancialAccountId, ct), e.Amount, amountBase, rateId);
        d.Credit(await ctx.TenderAccount(e.FromFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> BankCharge(BankChargeRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("BQ", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "bank_charge", e.OperationUuid, e.Description ?? "Frais bancaires");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.BankCharges, ct), e.Amount, amountBase, rateId);
        d.Credit(await ctx.TenderAccount(e.BankFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> ReceivableWriteOff(ReceivableWrittenOff e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "receivable_write_off", e.ReceivableId, $"Radiation de créance — {e.Reason}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.BadDebt, ct), e.Amount, amountBase, rateId, "Créance irrécouvrable");
        d.Credit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.Amount, amountBase, rateId,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        return d;
    }

    private static async Task<JournalEntryDraft> InventoryWriteOff(InventoryWriteOffRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "stock_adjustment", e.AdjustmentId, $"Sortie de stock — {e.Kind}");
        var (valueBase, rateId) = await ctx.ToFunctional(e.Value.Abs(), e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.InventoryLoss, ct), e.Value.Abs(), valueBase, rateId);
        d.Credit(await ctx.Account(AccountKeys.Inventory, ct), e.Value.Abs(), valueBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> InventoryRevaluation(InventoryRevaluationRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "stock_adjustment", e.AdjustmentId, "Écart d'inventaire");
        var (deltaBase, rateId) = await ctx.ToFunctional(e.ValuationDelta.Abs(), e.OccurredAt, ct);
        var inventory = await ctx.Account(AccountKeys.Inventory, ct);
        var adjustment = await ctx.Account(AccountKeys.InventoryAdjustment, ct);

        if (e.ValuationDelta.IsPositive)   // stock trouvé : actif +, écart crédité (produit)
        {
            d.Debit(inventory, e.ValuationDelta.Abs(), deltaBase, rateId);
            d.Credit(adjustment, e.ValuationDelta.Abs(), deltaBase, rateId);
        }
        else                                // manquant : écart débité (charge), actif -
        {
            d.Debit(adjustment, e.ValuationDelta.Abs(), deltaBase, rateId);
            d.Credit(inventory, e.ValuationDelta.Abs(), deltaBase, rateId);
        }
        return d;
    }

    private static async Task<JournalEntryDraft> PosSale(PosSaleRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("VT", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "sale", e.SaleId, e.CreditAmount.IsZero ? "Vente POS" : "Vente POS (crédit/mixte)");

        var (netBase, rateId) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        foreach (var t in e.Tenders.Where(t => !t.Amount.IsZero))
        {
            var (tBase, tRate) = await ctx.ToFunctional(t.Amount, e.OccurredAt, ct);
            d.Debit(await ctx.TenderAccount(t.FinancialAccountId, ct), t.Amount, tBase, tRate, "Encaissement");
        }
        if (!e.CreditAmount.IsZero)
        {
            var (creditBase, _) = await ctx.ToFunctional(e.CreditAmount, e.OccurredAt, ct);
            d.Debit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.CreditAmount, creditBase, rateId,
                "Vente à crédit", thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        }

        d.Credit(await ctx.Account(AccountKeys.SalesRevenue, ct), e.Net, netBase, rateId);
        if (!e.Tax.IsZero)
            d.Credit(await ctx.Account(AccountKeys.SalesTaxOutput, ct), e.Tax, taxBase, rateId);

        await AddCogs(d, e.CostOfGoods, ctx, e.OccurredAt, ct);
        return d;
    }

    private static async Task<JournalEntryDraft> SaleRefund(SaleRefundRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "sale_return", e.SaleReturnId, "Retour / remboursement client");

        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        d.Debit(await ctx.Account(AccountKeys.SalesReturns, ct), e.Net, netBase, rateId, "Retour sur ventes");
        if (!e.Tax.IsZero)
            d.Debit(await ctx.Account(AccountKeys.SalesTaxOutput, ct), e.Tax, taxBase, rateId, "TVA sur retour");

        // Module clients §7, §11 : la part imputable sur la créance de la vente solde cette créance ;
        // le reliquat devient un avoir disponible (store_credit) ou sort de la trésorerie.
        var relief = e.AgainstReceivable ? (e.ReceivableRelief ?? e.Total) : Money.Zero(e.Total.Currency);
        var reliefBase = Money.Zero(ctx.FunctionalCurrency);
        if (!relief.IsZero)
        {
            (reliefBase, _) = await ctx.ToFunctional(relief, e.OccurredAt, ct);
            d.Credit(await ctx.Account(AccountKeys.AccountsReceivable, ct), relief, reliefBase, rateId,
                thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        }
        var rest = e.Total.Subtract(relief);
        var restBase = new Money(totalBase.Amount - reliefBase.Amount, ctx.FunctionalCurrency);
        if (!rest.IsZero)
        {
            if (e.RefundMethod == "store_credit" || e.RemainderToStoreCredit)
                d.Credit(await ctx.Account(AccountKeys.StoreCredit, ct), rest, restBase, rateId,
                    thirdPartyType: "customer", thirdPartyId: e.CustomerId);
            else if (e.TenderFinancialAccountId is { } tender)
                d.Credit(await ctx.TenderAccount(tender, ct), rest, restBase, rateId);
            else
                d.Credit(await ctx.Account(AccountKeys.Suspense, ct), rest, restBase, rateId);
        }

        if (e.CostOfGoodsRestocked is { } cogs && !cogs.IsZero)
        {
            var (cogsBase, _) = await ctx.ToFunctional(cogs, e.OccurredAt, ct);
            d.Debit(await ctx.Account(AccountKeys.Inventory, ct), cogs, cogsBase, rateId, "Réintégration stock");
            d.Credit(await ctx.Account(AccountKeys.Cogs, ct), cogs, cogsBase, rateId);
        }
        return d;
    }

    // --- Module clients & comptes clients ------------------------------------------------------

    private static async Task<JournalEntryDraft> CustomerCreditApplication(CustomerCreditApplied e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "customer_credit_memo", e.CreditMemoId, "Imputation d'un avoir client");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(e.SourceAccountKey, ct), e.Amount, amountBase, rateId, "Avoir utilisé",
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        d.Credit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.Amount, amountBase, rateId,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        return d;
    }

    private static async Task<JournalEntryDraft> CustomerCreditRefund(CustomerCreditRefunded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("CA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "customer_refund", e.RefundId, "Remboursement d'avoir client");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(e.SourceAccountKey, ct), e.Amount, amountBase, rateId, "Avoir remboursé",
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> CustomerExpense(CustomerExpenseBilled e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("VT", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "customer_expense", e.CustomerExpenseId, $"Refacturation — {e.Description}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.Amount, amountBase, rateId,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        d.Credit(await ctx.Account(AccountKeys.SalesRevenue, ct), e.Amount, amountBase, rateId, "Frais refacturés");
        return d;
    }

    private static async Task<JournalEntryDraft> CustomerCreditOpening(CustomerCreditOpened e, PostingContext ctx, CancellationToken ct)
    {
        // Reprise de solde : la contrepartie reste en compte d'attente jusqu'au rapprochement comptable.
        var d = new JournalEntryDraft("OD", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "customer_credit", e.OperationUuid, $"Reprise de solde client — {e.Reason}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.AccountsReceivable, ct), e.Amount, amountBase, rateId,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId);
        d.Credit(await ctx.Account(AccountKeys.Suspense, ct), e.Amount, amountBase, rateId, "Reprise de solde à rapprocher");
        return d;
    }

    // --- Module paie, RH & avances (docs/module-paie-rh-avances.md §32) --------------------------

    private static async Task<JournalEntryDraft> EmployeeAdvancePayment(EmployeeAdvancePaid e, PostingContext ctx, CancellationToken ct)
    {
        // §14, §39 : une avance récupérable est une créance sur l'employé, jamais une charge salariale.
        var d = new JournalEntryDraft("PA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "employee_advance", e.AdvanceId, $"{(e.IsLoan ? "Prêt au personnel" : "Avance sur salaire")} {e.Reference}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(e.IsLoan ? AccountKeys.EmployeeLoans : AccountKeys.EmployeeAdvances, ct), e.Amount, amountBase, rateId,
            e.Reference, thirdPartyType: "employee", thirdPartyId: e.EmployeeId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> EmployeeAdvanceRepayment(EmployeeAdvanceRepaid e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("PA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "advance_repayment", e.RepaymentId, $"Remboursement {(e.IsLoan ? "de prêt" : "d'avance")} {e.Reference}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        d.Credit(await ctx.Account(e.IsLoan ? AccountKeys.EmployeeLoans : AccountKeys.EmployeeAdvances, ct), e.Amount, amountBase, rateId,
            e.Reference, thirdPartyType: "employee", thirdPartyId: e.EmployeeId);
        return d;
    }

    /// <summary>
    /// Paie approuvée (§32) : Dr rémunérations (brut net des absences et avantages en nature) + Dr charges
    /// patronales + Dr frais à rembourser / Cr organismes (retenues salariales et patronales) + Cr avances et
    /// prêts au personnel (récupérations) + Cr autres retenues + Cr rémunérations dues (net, par employé).
    /// Les montants par employé portent le tiers « employé » : chaque écriture se rapproche du dossier RH.
    /// </summary>
    private static async Task<JournalEntryDraft> Payroll(PayrollApproved e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("PA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "payroll_run", e.RunId, $"Paie {e.RunNumber} — {e.PeriodLabel}");
        var salary = await ctx.Account(AccountKeys.SalaryExpense, ct);
        var employer = await ctx.Account(AccountKeys.EmployerChargesExpense, ct);
        var reimbursements = await ctx.Account(AccountKeys.EmployeeReimbursementsPayable, ct);
        var withholdings = await ctx.Account(AccountKeys.PayrollWithholdings, ct);
        var advances = await ctx.Account(AccountKeys.EmployeeAdvances, ct);
        var loans = await ctx.Account(AccountKeys.EmployeeLoans, ct);
        var others = await ctx.Account(AccountKeys.PayrollOtherDeductions, ct);
        var payable = await ctx.Account(AccountKeys.SalariesPayable, ct);

        foreach (var group in e.Employees.GroupBy(x => x.Currency))
        {
            var currency = group.Key;
            // Lignes collectives (charges, organismes, autres retenues) cumulées par devise.
            decimal Sum(Func<PayrollEmployeePosting, decimal> f) => group.Sum(f);
            var lines = new List<(bool Debit, Guid Account, decimal Amount, string Label, Guid? Employee)>
            {
                (true, salary, Sum(x => x.SalaryExpense), "Rémunérations brutes", null),
                (true, employer, Sum(x => x.EmployerContributions), "Charges patronales", null),
                (false, withholdings, Sum(x => x.EmployeeWithholdings + x.EmployerContributions), "Retenues et cotisations à reverser", null),
                (false, others, Sum(x => x.OtherDeductions), "Autres retenues à reverser", null),
            };
            foreach (var x in group)
            {
                lines.Add((true, reimbursements, x.Reimbursements, $"Frais remboursés — {x.EmployeeLabel}", x.EmployeeId));
                lines.Add((false, advances, x.AdvanceRecovery, $"Récupération d'avance — {x.EmployeeLabel}", x.EmployeeId));
                lines.Add((false, loans, x.LoanRecovery, $"Échéance de prêt — {x.EmployeeLabel}", x.EmployeeId));
                lines.Add((false, payable, x.Net, $"Net à payer — {x.EmployeeLabel}", x.EmployeeId));
            }

            // Conversion ligne à ligne ; l'arrondi de change éventuel est porté par la dernière ligne « net à payer »
            // pour que l'écriture reste équilibrée en devise de référence.
            var converted = new List<(bool Debit, Guid Account, Money Amount, Money Base, Guid? Rate, string Label, Guid? Employee)>();
            foreach (var l in lines.Where(l => l.Amount != 0m))
            {
                var money = new Money(l.Amount, currency);
                var (b, rate) = await ctx.ToFunctional(money, e.OccurredAt, ct);
                converted.Add((l.Debit, l.Account, money, b, rate, l.Label, l.Employee));
            }
            var gap = converted.Where(c => c.Debit).Sum(c => c.Base.Amount) - converted.Where(c => !c.Debit).Sum(c => c.Base.Amount);
            if (gap != 0m)
            {
                var i = converted.FindLastIndex(c => !c.Debit && c.Account == payable);
                if (i < 0) throw new InvalidOperationException("Paie sans net à payer : écriture impossible à équilibrer.");
                var c = converted[i];
                converted[i] = c with { Base = new Money(c.Base.Amount + gap, ctx.FunctionalCurrency) };
            }
            foreach (var c in converted)
            {
                if (c.Debit) d.Debit(c.Account, c.Amount, c.Base, c.Rate, c.Label, c.Employee is null ? null : "employee", c.Employee);
                else d.Credit(c.Account, c.Amount, c.Base, c.Rate, c.Label, c.Employee is null ? null : "employee", c.Employee);
            }
        }
        return d;
    }

    private static async Task<JournalEntryDraft> SalaryPayment(SalaryPaid e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("PA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "salary_payment", e.SalaryPaymentId, $"Paiement de salaire {e.Reference}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.SalariesPayable, ct), e.Amount, amountBase, rateId, e.Reference,
            thirdPartyType: "employee", thirdPartyId: e.EmployeeId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> EmployeeExpense(EmployeeExpenseApproved e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("PA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "employee_expense_claim", e.ClaimId, $"Note de frais — {e.Description}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(e.ExpenseAccountId ?? await ctx.Account(AccountKeys.ExpenseDefault, ct), e.Amount, amountBase, rateId, e.Description);
        d.Credit(await ctx.Account(AccountKeys.EmployeeReimbursementsPayable, ct), e.Amount, amountBase, rateId,
            "Frais à rembourser", thirdPartyType: "employee", thirdPartyId: e.EmployeeId);
        return d;
    }

    private static async Task<JournalEntryDraft> EmployeeExpensePayment(EmployeeExpensePaid e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("PA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "employee_expense_claim", e.ClaimId, $"Remboursement de frais {e.Reference}");
        var (amountBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.Account(AccountKeys.EmployeeReimbursementsPayable, ct), e.Amount, amountBase, rateId, e.Reference,
            thirdPartyType: "employee", thirdPartyId: e.EmployeeId);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, amountBase, rateId);
        return d;
    }

    private static async Task<JournalEntryDraft> SupplierReturn(SupplierReturnRecorded e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("AC", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "supplier_return", e.SupplierReturnId, "Retour fournisseur");
        var (totalBase, rateId) = await ctx.ToFunctional(e.Total, e.OccurredAt, ct);
        var (netBase, _) = await ctx.ToFunctional(e.Net, e.OccurredAt, ct);
        var (taxBase, _) = await ctx.ToFunctional(e.Tax, e.OccurredAt, ct);

        if (e.StockCost is { } cost)
        {
            var (costBase, _) = await ctx.ToFunctional(cost, e.OccurredAt, ct);
            if (!e.Invoiced && !e.AgainstPayable)
            {
                // Retour avant facture : la réception est annulée pour ce qui repart, au coût de sortie du stock ; le
                // fournisseur ne facturera que ce qui reste.
                d.Debit(await ctx.Account(AccountKeys.GoodsReceivedNotInvoiced, ct), cost, costBase, rateId,
                    thirdPartyType: "supplier", thirdPartyId: e.SupplierId);
                d.Credit(await ctx.Account(AccountKeys.Inventory, ct), cost, costBase, rateId);
                return d;
            }
        }

        // Contrepartie : dette fournisseur si retour sur achat à crédit, sinon compte d'attente (avoir à recevoir).
        var counterpart = e.AgainstPayable ? AccountKeys.AccountsPayable : AccountKeys.Suspense;
        d.Debit(await ctx.Account(counterpart, ct), e.Total, totalBase, rateId,
            thirdPartyType: "supplier", thirdPartyId: e.SupplierId);
        if (e.StockCost is { } stockCost)
        {
            // Retour après facture : le stock sort à son coût ; l'écart avec le prix crédité par le fournisseur va en
            // écart sur prix d'achat (crédit si l'avoir dépasse le coût).
            var (stockBase, _) = await ctx.ToFunctional(stockCost, e.OccurredAt, ct);
            var gap = e.Net - stockCost;
            if (gap.IsZero) stockBase = netBase;   // arrondi de conversion absorbé par la sortie de stock
            d.Credit(await ctx.Account(AccountKeys.Inventory, ct), stockCost, stockBase, rateId);
            if (!gap.IsZero)
                d.Credit(await ctx.Account(AccountKeys.PurchasePriceVariance, ct), gap, netBase - stockBase, rateId,
                    description: "Écart sur prix d'achat (retour)");
        }
        else
            d.Credit(await ctx.Account(AccountKeys.Inventory, ct), e.Net, netBase, rateId);
        if (!e.Tax.IsZero)
            d.Credit(await ctx.Account(AccountKeys.PurchaseTaxInput, ct), e.Tax, taxBase, rateId);
        return d;
    }
}
