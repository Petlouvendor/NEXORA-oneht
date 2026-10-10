using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Posting;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;

namespace Nexora.Application.Finance.Engine;

public sealed record PostingResult(
    Guid JournalEntryId,
    string EntryNumber,
    Guid? PaymentId = null,
    Guid? ReceivableId = null,
    Guid? PayableId = null,
    bool WasReplay = false);

public interface IFinancialEngine
{
    Task<Result<PostingResult>> HandleAsync(FinancialEvent evt, CancellationToken ct = default);
}

/// <summary>
/// Cœur du moteur (Phase 4 §3) : événement → règle → transaction atomique
/// (écriture + créance/dette + trésorerie). Idempotent via l'identifiant d'opération (§33).
/// </summary>
public sealed class FinancialEngine(
    IPostingRuleSet rules,
    IAccountResolver accounts,
    IExchangeRateProvider fx,
    ICompanyCurrencyContext currency,
    IAccountingPeriods periods,
    IDocumentNumberGenerator numbers,
    IJournalEntryStore journalStore,
    IReceivablePayableStore subledger,
    IPaymentStore payments,
    IFinancialAccountStore financialAccounts,
    IUnitOfWork uow,
    ITenantContext tenant,
    IBusinessCalendar calendar,
    ILogger<FinancialEngine> logger) : IFinancialEngine
{
    public async Task<Result<PostingResult>> HandleAsync(FinancialEvent evt, CancellationToken ct = default)
    {
        // Idempotence (§33)
        var existing = await journalStore.FindByOperationAsync(evt.OperationUuid, ct);
        if (existing is { } id)
        {
            logger.LogInformation("Événement {Op} déjà traité → écriture {Entry}", evt.OperationUuid, id);
            var replay = await journalStore.GetWithLinesAsync(id, ct);
            return Result.Success(new PostingResult(id, replay?.EntryNumber ?? "", WasReplay: true));
        }

        var func = await currency.FunctionalCurrencyAsync(ct);
        var decimals = await currency.DecimalPlacesAsync(func, ct);
        var ctx = new PostingContext(accounts, fx, func, decimals, await calendar.ZoneAsync(ct));
        var date = ctx.DateOf(evt.OccurredAt);

        AccountingPeriod period;
        try
        {
            period = await periods.ResolveOpenPeriodAsync(date, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("accounting.period_closed", ex.Message);
        }

        try
        {
            return await uow.ExecuteInTransactionAsync(async token =>
            {
                var draft = evt switch
                {
                    CustomerPaymentReceived p => await BuildCustomerPaymentDraft(p, ctx, token),
                    SupplierPaymentMade p => await BuildSupplierPaymentDraft(p, ctx, token),
                    _ => await rules.BuildAsync(evt, ctx, token),
                };

                var entry = await PersistEntryAsync(evt, draft, period, token);
                var result = await ApplySideEffectsAsync(evt, entry, ctx, token);
                return Result.Success(result with { JournalEntryId = entry.Id, EntryNumber = entry.EntryNumber });
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Échec de génération pour {Event} ({Op})", evt.GetType().Name, evt.OperationUuid);
            return Error.Validation("finance.posting_failed", ex.Message);
        }
    }

    // ---------------------------------------------------------------------

    private async Task<JournalEntry> PersistEntryAsync(
        FinancialEvent evt, JournalEntryDraft draft, AccountingPeriod period, CancellationToken ct)
    {
        var journalId = await journalStore.ResolveJournalAsync(draft.JournalCode, ct);
        var number = await numbers.NextAsync($"journal:{draft.JournalCode}", null, ct);

        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            CompanyId = evt.CompanyId,
            JournalId = journalId,
            AccountingPeriodId = period.Id,
            EntryNumber = number,
            EntryDate = draft.Date,
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            Description = draft.Description,
            Status = JournalEntryStatus.Posted,
            PostedAt = DateTimeOffset.UtcNow,
            PostedBy = tenant.UserId,
            SourceOperationUuid = evt.OperationUuid,
            CreatedBy = tenant.UserId,
        };

        var n = 1;
        foreach (var l in draft.Lines)
        {
            entry.Lines.Add(new JournalEntryLine
            {
                Id = Guid.NewGuid(),
                CompanyId = evt.CompanyId,
                AccountId = l.AccountId,
                LineNumber = n++,
                Description = l.Description,
                Debit = l.Debit,
                Credit = l.Credit,
                CurrencyId = await currency.ResolveCurrencyIdAsync(l.Currency, ct),
                ExchangeRateId = l.ExchangeRateId,
                BaseDebit = l.BaseDebit,
                BaseCredit = l.BaseCredit,
                ThirdPartyType = l.ThirdPartyType,
                ThirdPartyId = l.ThirdPartyId,
                CostCenterId = l.CostCenterId,
            });
        }

        return await journalStore.AddAsync(entry, ct);
    }

    private async Task<PostingResult> ApplySideEffectsAsync(
        FinancialEvent evt, JournalEntry entry, PostingContext ctx, CancellationToken ct)
    {
        switch (evt)
        {
            case CreditSaleRecorded e:
            {
                var r = await subledger.AddReceivableAsync(new CustomerReceivable
                {
                    Id = Guid.NewGuid(),
                    CompanyId = e.CompanyId,
                    SaleId = e.SaleId,
                    CustomerId = e.CustomerId,
                    CurrencyId = await currency.ResolveCurrencyIdAsync(e.Total.Currency, ct),
                    OriginalAmount = e.Total.Amount,
                    PaidAmount = 0m,
                    IssueDate = entry.EntryDate,
                    DueDate = e.DueDate,
                    Status = ReceivableStatus.Open,
                }, ct);
                return new PostingResult(entry.Id, entry.EntryNumber, ReceivableId: r.Id);
            }

            case SupplierInvoiceRecorded e:
            {
                var p = await subledger.AddPayableAsync(new SupplierPayable
                {
                    Id = Guid.NewGuid(),
                    CompanyId = e.CompanyId,
                    SupplierInvoiceId = e.SupplierInvoiceId,
                    SupplierId = e.SupplierId,
                    CurrencyId = await currency.ResolveCurrencyIdAsync(e.Total.Currency, ct),
                    OriginalAmount = e.Total.Amount,
                    PaidAmount = 0m,
                    IssueDate = entry.EntryDate,
                    DueDate = e.DueDate,
                    Status = ReceivableStatus.Open,
                }, ct);
                return new PostingResult(entry.Id, entry.EntryNumber, PayableId: p.Id);
            }

            case CashSaleRecorded e:
                await MoveCashAsync(e.TenderFinancialAccountId, e.Total.Amount, "deposit", "sale", e.SaleId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case PosSaleRecorded e:
            {
                // Idempotence portée par journal_entry.source_operation_uuid + sale.source_operation_uuid ;
                // les mouvements de trésorerie par moyen ne portent pas l'uuid (plusieurs par vente).
                foreach (var t in e.Tenders.Where(t => !t.Amount.IsZero))
                    await MoveCashAsync(t.FinancialAccountId, t.Amount.Amount, "deposit", "sale", e.SaleId, null, ct);
                Guid? recvId = null;
                if (!e.CreditAmount.IsZero && e.CustomerId is { } cust)
                {
                    var r = await subledger.AddReceivableAsync(new CustomerReceivable
                    {
                        Id = Guid.NewGuid(),
                        CompanyId = e.CompanyId,
                        SaleId = e.SaleId,
                        CustomerId = cust,
                        CurrencyId = await currency.ResolveCurrencyIdAsync(e.CreditAmount.Currency, ct),
                        OriginalAmount = e.CreditAmount.Amount,
                        PaidAmount = 0m,
                        IssueDate = entry.EntryDate,
                        DueDate = e.DueDate,
                        Status = ReceivableStatus.Open,
                    }, ct);
                    recvId = r.Id;
                }
                return new PostingResult(entry.Id, entry.EntryNumber, ReceivableId: recvId);
            }

            case SaleRefundRecorded e:
                if (e is { AgainstReceivable: false, RefundMethod: "cash" or "card" or "bank_transfer", TenderFinancialAccountId: { } acc })
                    await MoveCashAsync(acc, -e.Total.Amount, "withdrawal", "sale_return", e.SaleReturnId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case CashPurchaseRecorded e:
                await MoveCashAsync(e.TenderFinancialAccountId, -e.Total.Amount, "withdrawal", "cash_purchase", e.ReferenceId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case ExpenseRecorded e:
                await MoveCashAsync(e.TenderFinancialAccountId, -e.Total.Amount, "withdrawal", "expense", e.ExpenseId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case BankChargeRecorded e:
                await MoveCashAsync(e.BankFinancialAccountId, -e.Amount.Amount, "fee", "bank_charge", null, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case ReceivableWrittenOff:
                // L'écriture solde la créance dans le grand livre ; le sous-ledger est mis à jour
                // par CollectionsService (statut WRITTEN_OFF, historique du montant original conservé).
                return new PostingResult(entry.Id, entry.EntryNumber);

            case CashCountRecorded e:
                await MoveCashAsync(e.CashFinancialAccountId, e.Variance.Amount, "adjustment", "cash_session", e.CashSessionId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case FinancialTransferRecorded e:
                await MoveCashAsync(e.FromFinancialAccountId, -e.Amount.Amount, "transfer_out", "financial_transfer", null, e.OperationUuid, ct);
                await MoveCashAsync(e.ToFinancialAccountId, e.Amount.Amount, "transfer_in", "financial_transfer", null, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case CustomerPaymentReceived e:
                return await ApplyPaymentAsync(e, entry, PaymentDirection.Inbound, e.CustomerId, null, e.Amount, e.TenderFinancialAccountId, e.Allocations, e.Reference, ct);

            case CustomerExpenseBilled e:
            {
                // Module clients §10 : la dépense refacturée devient un crédit client comme un autre
                // (numéro CRD-…, échéance, versements, relances) ; son origine reste la dépense.
                var r = await subledger.AddReceivableAsync(new CustomerReceivable
                {
                    Id = Guid.NewGuid(),
                    CompanyId = e.CompanyId,
                    CustomerId = e.CustomerId,
                    CurrencyId = await currency.ResolveCurrencyIdAsync(e.Amount.Currency, ct),
                    OriginalAmount = e.Amount.Amount,
                    PaidAmount = 0m,
                    IssueDate = entry.EntryDate,
                    DueDate = e.DueDate,
                    Status = ReceivableStatus.Open,
                    SourceType = ReceivableSource.CustomerExpense,
                    SourceId = e.CustomerExpenseId,
                }, ct);
                return new PostingResult(entry.Id, entry.EntryNumber, ReceivableId: r.Id);
            }

            case CustomerCreditOpened e:
            {
                var r = await subledger.AddReceivableAsync(new CustomerReceivable
                {
                    Id = Guid.NewGuid(),
                    CompanyId = e.CompanyId,
                    CustomerId = e.CustomerId,
                    CurrencyId = await currency.ResolveCurrencyIdAsync(e.Amount.Currency, ct),
                    OriginalAmount = e.Amount.Amount,
                    PaidAmount = 0m,
                    IssueDate = entry.EntryDate,
                    DueDate = e.DueDate,
                    Status = ReceivableStatus.Open,
                    SourceType = ReceivableSource.Opening,
                    SourceId = e.OperationUuid,
                }, ct);
                return new PostingResult(entry.Id, entry.EntryNumber, ReceivableId: r.Id);
            }

            case CustomerCreditRefunded e:
                await MoveCashAsync(e.TenderFinancialAccountId, -e.Amount.Amount, "withdrawal", "customer_refund", e.RefundId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            // Module paie (§32) : chaque versement / remboursement est un mouvement de trésorerie référencé.
            case EmployeeAdvancePaid e:
                await MoveCashAsync(e.TenderFinancialAccountId, -e.Amount.Amount, "withdrawal", "employee_advance", e.AdvanceId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case EmployeeAdvanceRepaid e:
                await MoveCashAsync(e.TenderFinancialAccountId, e.Amount.Amount, "deposit", "advance_repayment", e.RepaymentId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case SalaryPaid e:
                await MoveCashAsync(e.TenderFinancialAccountId, -e.Amount.Amount, "withdrawal", "salary_payment", e.SalaryPaymentId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case EmployeeExpensePaid e:
                await MoveCashAsync(e.TenderFinancialAccountId, -e.Amount.Amount, "withdrawal", "employee_expense_claim", e.ClaimId, e.OperationUuid, ct);
                return new PostingResult(entry.Id, entry.EntryNumber);

            case SupplierPaymentMade e:
                return await ApplyPaymentAsync(e, entry, PaymentDirection.Outbound, null, e.SupplierId, e.Amount, e.TenderFinancialAccountId, e.Allocations, e.Reference, ct);

            default:
                return new PostingResult(entry.Id, entry.EntryNumber);
        }
    }

    private async Task MoveCashAsync(
        Guid financialAccountId, decimal signedAmount, string type, string refType, Guid? refId, Guid? opUuid, CancellationToken ct)
    {
        var acc = await financialAccounts.GetAsync(financialAccountId, ct)
            ?? throw new InvalidOperationException($"Compte financier {financialAccountId} introuvable.");

        await financialAccounts.AddTransactionAsync(new FinancialTransaction
        {
            Id = Guid.NewGuid(),
            CompanyId = acc.CompanyId,
            FinancialAccountId = financialAccountId,
            TransactionType = type,
            Amount = signedAmount,
            CurrencyId = acc.CurrencyId,
            ReferenceType = refType,
            ReferenceId = refId,
            OccurredAt = DateTimeOffset.UtcNow,
            Status = "cleared",
            SourceOperationUuid = opUuid,
            CreatedBy = tenant.UserId,
        }, ct);

        await financialAccounts.AdjustBalanceAsync(financialAccountId, signedAmount, ct);
    }

    private async Task<PostingResult> ApplyPaymentAsync(
        FinancialEvent evt, JournalEntry entry, PaymentDirection direction,
        Guid? customerId, Guid? supplierId, Money amount, Guid tenderAccountId,
        IReadOnlyList<AllocationInstruction> allocations, string? reference, CancellationToken ct)
    {
        var number = await numbers.NextAsync("payment", null, ct);
        var currencyId = await currency.ResolveCurrencyIdAsync(amount.Currency, ct);
        var customerPayment = evt as CustomerPaymentReceived;
        var payment = await payments.AddAsync(new Payment
        {
            Id = Guid.NewGuid(),
            CompanyId = evt.CompanyId,
            PaymentNumber = number,
            Direction = direction,
            PartyType = customerId is not null ? "customer" : "supplier",
            CustomerId = customerId,
            SupplierId = supplierId,
            FinancialAccountId = tenderAccountId,
            CurrencyId = currencyId,
            Amount = amount.Amount,
            Reference = reference,
            Status = "completed",
            ReceivedAt = evt.OccurredAt,
            SourceOperationUuid = evt.OperationUuid,
            CreatedBy = tenant.UserId,
            PaymentMethodId = customerPayment?.PaymentMethodId,
            CashSessionId = customerPayment?.CashSessionId,
            Note = customerPayment?.Note,
        }, ct);

        foreach (var a in allocations)
        {
            decimal? open;
            if (a.IsReceivable)
            {
                var r = await subledger.GetReceivableAsync(a.TargetId, ct);
                // Module clients §23 : un versement ne solde jamais la créance d'un autre client.
                if (r is not null && r.CustomerId != customerId)
                    throw new InvalidOperationException($"La créance {a.TargetId} n'appartient pas à ce client.");
                open = r?.RemainingAmount;
            }
            else
            {
                var p = await subledger.GetPayableAsync(a.TargetId, ct);
                if (p is not null && p.SupplierId != supplierId)
                    throw new InvalidOperationException($"La dette {a.TargetId} n'appartient pas à ce fournisseur.");
                open = p?.RemainingAmount;
            }

            if (open is null)
                throw new InvalidOperationException($"Créance/dette {a.TargetId} introuvable.");
            if (a.Amount > open)
                throw new InvalidOperationException(
                    $"Affectation {a.Amount} > solde ouvert {open} de {a.TargetId} (§15).");

            await payments.AddAllocationAsync(new PaymentAllocation
            {
                Id = Guid.NewGuid(),
                CompanyId = evt.CompanyId,
                PaymentId = payment.Id,
                ReceivableId = a.IsReceivable ? a.TargetId : null,
                PayableId = a.IsReceivable ? null : a.TargetId,
                AllocatedAmount = a.Amount,
                CurrencyId = currencyId,
                AllocatedAt = evt.OccurredAt,
                CreatedBy = tenant.UserId,
            }, ct);
        }

        var signed = direction == PaymentDirection.Inbound ? amount.Amount : -amount.Amount;
        await MoveCashAsync(tenderAccountId, signed,
            direction == PaymentDirection.Inbound ? "deposit" : "withdrawal",
            "payment", payment.Id, evt.OperationUuid, ct);

        return new PostingResult(entry.Id, entry.EntryNumber,
            PaymentId: payment.Id,
            ReceivableId: allocations.FirstOrDefault(x => x.IsReceivable)?.TargetId,
            PayableId: allocations.FirstOrDefault(x => !x.IsReceivable)?.TargetId);
    }

    // --- Brouillons de paiement avec écart de change (§22) ----------------

    private async Task<JournalEntryDraft> BuildCustomerPaymentDraft(CustomerPaymentReceived e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("CA", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "payment", null, "Encaissement client");
        var (tenderBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Debit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, tenderBase, rateId);

        await AddArApReliefWithFx(d, ctx, e.Allocations, e.OccurredAt, isReceivable: true,
            thirdPartyType: "customer", thirdPartyId: e.CustomerId, tenderBase: tenderBase, tenderTxn: e.Amount, ct);
        return d;
    }

    private async Task<JournalEntryDraft> BuildSupplierPaymentDraft(SupplierPaymentMade e, PostingContext ctx, CancellationToken ct)
    {
        var d = new JournalEntryDraft("BQ", ctx.DateOf(e.OccurredAt), ctx.FunctionalCurrency,
            "payment", null, "Règlement fournisseur");
        var (tenderBase, rateId) = await ctx.ToFunctional(e.Amount, e.OccurredAt, ct);
        d.Credit(await ctx.TenderAccount(e.TenderFinancialAccountId, ct), e.Amount, tenderBase, rateId);

        await AddArApReliefWithFx(d, ctx, e.Allocations, e.OccurredAt, isReceivable: false,
            thirdPartyType: "supplier", thirdPartyId: e.SupplierId, tenderBase: tenderBase, tenderTxn: e.Amount, ct);
        return d;
    }

    private async Task AddArApReliefWithFx(
        JournalEntryDraft d, PostingContext ctx, IReadOnlyList<AllocationInstruction> allocations,
        DateTimeOffset at, bool isReceivable, string thirdPartyType, Guid thirdPartyId,
        Money tenderBase, Money tenderTxn, CancellationToken ct)
    {
        var control = await ctx.Account(isReceivable ? AccountKeys.AccountsReceivable : AccountKeys.AccountsPayable, ct);
        var totalReliefBase = Money.Zero(ctx.FunctionalCurrency);
        var totalTxn = Money.Zero(tenderTxn.Currency);

        foreach (var a in allocations)
        {
            DateOnly issueDate;
            string ccy;
            if (isReceivable)
            {
                var r = await subledger.GetReceivableAsync(a.TargetId, ct)
                    ?? throw new InvalidOperationException($"Créance {a.TargetId} introuvable.");
                issueDate = r.IssueDate; ccy = tenderTxn.Currency;
            }
            else
            {
                var p = await subledger.GetPayableAsync(a.TargetId, ct)
                    ?? throw new InvalidOperationException($"Dette {a.TargetId} introuvable.");
                issueDate = p.IssueDate; ccy = tenderTxn.Currency;
            }

            var alloc = new Money(a.Amount, ccy);
            var issueAt = BusinessDate.Noon(issueDate);
            var (reliefBase, _) = await ctx.ToFunctional(alloc, issueAt, ct);   // solde relevé au coût d'origine

            if (isReceivable) d.Credit(control, alloc, reliefBase, description: "Règlement créance",
                thirdPartyType: thirdPartyType, thirdPartyId: thirdPartyId);
            else d.Debit(control, alloc, reliefBase, description: "Règlement dette",
                thirdPartyType: thirdPartyType, thirdPartyId: thirdPartyId);

            totalReliefBase = totalReliefBase.Add(reliefBase);
            totalTxn = totalTxn.Add(alloc);
        }

        // Reliquat non affecté → compte d'attente (acompte / avance) (§15)
        var unallocatedTxn = tenderTxn.Subtract(totalTxn);
        var unallocBase = Money.Zero(ctx.FunctionalCurrency);
        if (!unallocatedTxn.IsZero)
        {
            (unallocBase, _) = await ctx.ToFunctional(unallocatedTxn, at, ct);
            var suspense = await ctx.Account(AccountKeys.Suspense, ct);
            if (isReceivable) d.Credit(suspense, unallocatedTxn, unallocBase, description: "Acompte client");
            else d.Debit(suspense, unallocatedTxn, unallocBase, description: "Avance fournisseur");
        }

        // Écart de change = valeur au taux du paiement - valeur au taux d'émission (§22)
        var fxDiff = tenderBase.Amount - totalReliefBase.Amount - unallocBase.Amount;
        if (fxDiff != 0m)
        {
            var money = new Money(Math.Abs(fxDiff), ctx.FunctionalCurrency);
            var gain = await ctx.Account(AccountKeys.FxGain, ct);
            var loss = await ctx.Account(AccountKeys.FxLoss, ct);
            // Encaissement : tender debit > AR credit  → produit de change (crédit fx_gain)
            // Décaissement : tender credit < AP debit  → à équilibrer symétriquement
            var favourable = isReceivable ? fxDiff > 0m : fxDiff < 0m;
            if (favourable)
                d.Credit(gain, money, money, description: "Gain de change");
            else
                d.Debit(loss, money, money, description: "Perte de change");
        }
    }
}
