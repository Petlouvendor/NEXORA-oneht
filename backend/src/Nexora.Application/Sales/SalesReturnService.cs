using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Inventory;
using Nexora.Domain.Common;
using Nexora.Domain.Inventory;
using Nexora.Domain.Sales;

namespace Nexora.Application.Sales;

/// <summary>Retours et remboursements (Phase 6 §22-24) : réintégration stock + écriture de correction.</summary>
public sealed class SalesReturnService(
    ISalesReturnStore store,
    IStockLedger stock,
    IFinancialEngine engine,
    ICustomerCreditMemoWriter creditMemos,
    ICashSessionStore sessions,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ILogger<SalesReturnService> logger)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    /// <summary>Modes de remboursement acceptés (miroir de <c>sale_return_method_chk</c>).</summary>
    public static readonly IReadOnlySet<string> RefundMethods =
        new HashSet<string>(["cash", "card", "bank_transfer", "store_credit", "credit_note", "original"]);

    /// <summary>
    /// Exécute un retour. Appelé directement par un détenteur de <c>pos.refund</c> (Manager), ou à
    /// l'approbation d'une demande de retour d'un caissier (guide §10) — jamais par le caissier seul.
    /// </summary>
    /// <remarks>
    /// Audit 2026-10 : un remboursement en espèces sortait de la trésorerie sans mouvement de la session de caisse
    /// (A-002) — la clôture constatait un faux manquant imputé au caissier, puis l'écart comptabilisé faisait baisser
    /// la caisse une seconde fois. Il écrit désormais un mouvement <c>sale_refund</c> dans la session quand l'argent
    /// sort du tiroir de cette session. Les contrôles de quantité retournable et de reste dû se font sous verrou de
    /// la vente, dans la transaction (A-005) : deux retours simultanés ne peuvent plus dépasser la vente.
    /// </remarks>
    public async Task<Result<ReturnResult>> ReturnAsync(ReturnCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Lines.Count == 0) return Error.Validation("pos.no_lines", "Aucune ligne à retourner.");
        if (string.IsNullOrWhiteSpace(cmd.Reason)) return Error.Validation("pos.reason_required", "Motif obligatoire (§22).");
        if (!RefundMethods.Contains(cmd.RefundMethod))
            return Error.Validation("pos.refund_method", $"Mode de remboursement « {cmd.RefundMethod} » inconnu.");

        var existing = await store.FindReturnByOperationAsync(cmd.OperationUuid, ct);
        if (existing is { } dup)
            return Error.Conflict("pos.return_replayed", $"Retour déjà enregistré ({dup}).");

        var sale = await store.GetSaleWithLinesAsync(cmd.SaleId, ct);
        if (sale is null) return Error.NotFound("pos.sale_not_found", "Vente source introuvable (§23 : lien obligatoire).");
        foreach (var input in cmd.Lines)
        {
            var src = sale.Lines.FirstOrDefault(l => l.Id == input.SaleLineId);
            if (src is null)
                return Error.Validation("pos.return_line_not_in_sale", "Un article retourné n'appartient pas à la vente d'origine.");
            if (input.Quantity <= 0m)
                return Error.Validation("pos.return_quantity", $"Quantité de retour invalide pour « {src.Description} ».");
        }

        // Espèces : l'argent sort d'un compte précis (sans compte, l'écriture créditait le compte d'attente et aucune
        // trésorerie ne bougeait — A-005) ; s'il sort du tiroir d'une session, cette session doit être ouverte.
        Guid? drawerSession = null;
        if (cmd.RefundMethod == "cash")
        {
            if (cmd.RefundFinancialAccountId is null)
                return Error.Validation("pos.refund_account_required",
                    "Un remboursement en espèces exige le compte (caisse) d'où sort l'argent.");
            if (cmd.CashSessionId is { } sid)
            {
                var session = await sessions.GetSessionAsync(sid, ct);
                if (session is null || session.CompanyId != Company)
                    return Error.NotFound("pos.cash_session_not_found", "Session de caisse introuvable.");
                if (session.Status != "open")
                    return Error.Conflict("pos.cash_session_closed",
                        "La session de caisse est close ou en cours de clôture : remboursez depuis une session ouverte.");
                var register = await sessions.GetRegisterAsync(session.CashRegisterId, ct);
                if (register is not null && register.FinancialAccountId == cmd.RefundFinancialAccountId)
                {
                    if (register.CurrencyId != sale.CurrencyId)
                        return Error.Validation("pos.refund_currency",
                            "La caisse de la session n'est pas dans la devise de la vente.");
                    drawerSession = sid;
                }
            }
        }

        var currency = await store.CurrencyCodeAsync(sale.CurrencyId, ct);

        return await uow.ExecuteInTransactionAsync<Result<ReturnResult>>(async token =>
        {
            // Verrou de la vente : les retours d'une même vente passent l'un après l'autre ; tout ce qui suit est
            // relu sous ce verrou (statut, quantités déjà retournées, reste dû).
            await store.LockSaleAsync(cmd.SaleId, token);
            var locked = await store.GetSaleWithLinesAsync(cmd.SaleId, token) ?? sale;

            // Seule une vente validée (encaissée) se retourne : ni panier en attente, ni vente annulée
            // ou déjà intégralement remboursée.
            if (locked.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded))
                return Error.Conflict("pos.sale_not_returnable", "Vente non retournable.");

            // Guide §10.2 : jamais plus que la quantité achetée non encore retournée — retours
            // précédents compris (un retour partiel suivi d'un autre ne peut pas dépasser la vente).
            var alreadyReturned = await store.ReturnedQuantitiesAsync(cmd.SaleId, token);
            foreach (var group in cmd.Lines.GroupBy(l => l.SaleLineId))
            {
                var src = locked.Lines.First(l => l.Id == group.Key);
                var requested = group.Sum(l => l.Quantity);
                var remaining = src.Quantity - alreadyReturned.GetValueOrDefault(src.Id);
                if (requested > remaining)
                    return Error.Validation("pos.return_exceeds_sold",
                        $"« {src.Description} » : {requested} demandé(s), {remaining} encore retournable(s) sur {src.Quantity} vendu(s).");
            }

            // Module clients §7, §11 : un retour sur une vente à crédit réduit d'abord la dette née de cette
            // vente ; ce qui dépasse le reste dû devient un avoir disponible du client. Avant, l'écriture
            // créditait le compte client sans que la créance ne baisse : le client restait débiteur.
            // Lu dans la transaction : son verrou FOR UPDATE tient jusqu'à la validation (il était relâché aussitôt).
            var receivable = await store.ReceivableBalanceForSaleAsync(cmd.SaleId, token);
            var toCustomerAccount = cmd.RefundMethod is "store_credit" or "credit_note"
                                    || (cmd.RefundMethod == "original" && receivable is not null);
            if (toCustomerAccount && locked.CustomerId is null)
                return Error.Validation("pos.refund_needs_customer",
                    "Un avoir ou un remboursement sur compte exige une vente rattachée à un client.");
            var imputable = cmd.RefundMethod is "original" or "credit_note" && receivable is not null;

            var now = clock.UtcNow;
            var ret = new SaleReturn
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                SaleId = cmd.SaleId,
                DocumentNumber = await store.NextNumberAsync("sale_return", token),
                Status = "completed",
                CurrencyId = locked.CurrencyId,
                Reason = cmd.Reason,
                RefundMethod = cmd.RefundMethod,
                SourceOperationUuid = cmd.OperationUuid,
                CashSessionId = cmd.CashSessionId,
                ReturnedAt = now,
                CreatedBy = tenant.UserId,
            };

            decimal refundNet = 0m, refundTax = 0m, cogsRestocked = 0m;
            var movements = new List<StockMovementRequest>();

            foreach (var input in cmd.Lines)
            {
                var srcLine = locked.Lines.First(l => l.Id == input.SaleLineId);   // contrôlé ci-dessus

                var ratio = input.Quantity / srcLine.Quantity;
                var lineNet = Rounding.Money((srcLine.LineTotal - srcLine.TaxAmount) * ratio);
                var lineTax = Rounding.Money(srcLine.TaxAmount * ratio);
                refundNet += lineNet;
                refundTax += lineTax;

                ret.Lines.Add(new SaleReturnLine
                {
                    Id = Guid.NewGuid(), SaleReturnId = ret.Id, CompanyId = Company,
                    SaleLineId = srcLine.Id, ProductId = srcLine.ProductId, Quantity = input.Quantity,
                    UnitPrice = srcLine.UnitPrice, LineTotal = Rounding.Money(lineNet + lineTax),
                    TaxAmount = lineTax, UnitCost = srcLine.UnitCost, LotId = srcLine.LotId, Restock = input.Restock,
                });

                if (input.Restock && srcLine.InventoryLocationId is { } loc)
                {
                    cogsRestocked += Rounding.Money(srcLine.UnitCost * input.Quantity);
                    movements.Add(new StockMovementRequest(
                        srcLine.ProductId, loc, MovementType.SaleReturn, input.Quantity, srcLine.UnitCost,
                        // UUID unique par mouvement : seul le premier le porte (retour multi-lignes).
                        "sale_return", ret.Id, srcLine.LotId, now, movements.Count == 0 ? cmd.OperationUuid : null));
                }
            }

            // Vrai seulement si un article est réellement revenu en stock (il était toujours vrai — A-005).
            ret.Restocked = movements.Count > 0;
            ret.RefundTotal = Rounding.Money(refundNet + refundTax);
            await store.AddReturnAsync(ret, token);
            if (movements.Count > 0) await stock.PostManyAsync(movements, token);

            // L'argent rendu sort du tiroir : l'attendu de clôture de la session le retranche (A-002).
            if (drawerSession is { } drawer && ret.RefundTotal > 0m)
                await sessions.AddMovementAsync(new CashMovement
                {
                    Id = Guid.NewGuid(), CompanyId = Company, CashSessionId = drawer,
                    MovementType = "sale_refund", Amount = -ret.RefundTotal, CurrencyId = locked.CurrencyId,
                    ReferenceType = "sale_return", ReferenceId = ret.Id, Note = $"Retour {ret.DocumentNumber}",
                    OccurredAt = now, UserId = tenant.UserId, CreatedAt = now,
                }, token);

            var relief = imputable ? Math.Min(ret.RefundTotal, receivable!.Remaining) : 0m;
            var posted = await engine.HandleAsync(new SaleRefundRecorded(
                Company, cmd.OperationUuid, now, ret.Id, locked.CustomerId,
                new Money(refundNet, currency), new Money(refundTax, currency),
                cmd.RefundMethod, cmd.RefundFinancialAccountId, relief > 0m,
                cogsRestocked > 0m ? new Money(cogsRestocked, currency) : null)
            {
                ReceivableRelief = new Money(relief, currency),
                RemainderToStoreCredit = toCustomerAccount,
            }, token);
            if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);

            if (toCustomerAccount && ret.RefundTotal > 0m)
                await creditMemos.IssueForReturnAsync(locked.CustomerId!.Value, ret.Id, locked.CurrencyId, ret.RefundTotal,
                    receivable?.ReceivableId, relief, cmd.Reason, token);

            // Statut sur le cumul (retours précédents + celui-ci), pas sur le seul retour courant.
            var totalSold = locked.Lines.Sum(l => l.Quantity);
            var totalReturned = alreadyReturned.Values.Sum() + cmd.Lines.Sum(l => l.Quantity);
            await store.SetSaleStatusAsync(cmd.SaleId,
                totalReturned >= totalSold ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded, token);

            logger.LogInformation("Retour {Ret} sur vente {Sale} — {Reason}", ret.DocumentNumber, locked.DocumentNumber, cmd.Reason);
            return Result.Success(new ReturnResult(
                ret.Id, ret.DocumentNumber, refundNet, refundTax, ret.RefundTotal, posted.Value!.JournalEntryId));
        }, ct);
    }
}
