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
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ILogger<SalesReturnService> logger)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    /// <summary>Modes de remboursement acceptés (miroir de <c>sale_return_method_chk</c>).</summary>
    public static readonly IReadOnlySet<string> RefundMethods =
        new HashSet<string>(["cash", "card", "bank_transfer", "store_credit", "credit_note", "original"]);

    /// <summary>
    /// Exécute un retour. Appelé directement par un détenteur de <c>pos.refund</c> (Manager), ou à
    /// l'approbation d'une demande de retour d'un caissier (guide §10) — jamais par le caissier seul.
    /// </summary>
    public async Task<Result<ReturnResult>> ReturnAsync(ReturnCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Lines.Count == 0) return Error.Validation("pos.no_lines", "Aucune ligne à retourner.");
        if (string.IsNullOrWhiteSpace(cmd.Reason)) return Error.Validation("pos.reason_required", "Motif obligatoire (§22).");
        if (!RefundMethods.Contains(cmd.RefundMethod))
            return Error.Validation("pos.refund_method", $"Mode de remboursement « {cmd.RefundMethod} » inconnu.");

        var existing = await store.FindReturnByOperationAsync(cmd.OperationUuid, ct);
        if (existing is { } dup)
        {
            var r = await store.GetSaleWithLinesAsync(cmd.SaleId, ct);
            _ = r;
            return Error.Conflict("pos.return_replayed", $"Retour déjà enregistré ({dup}).");
        }

        var sale = await store.GetSaleWithLinesAsync(cmd.SaleId, ct);
        if (sale is null) return Error.NotFound("pos.sale_not_found", "Vente source introuvable (§23 : lien obligatoire).");
        // Seule une vente validée (encaissée) se retourne : ni panier en attente, ni vente annulée
        // ou déjà intégralement remboursée.
        if (sale.Status is not (SaleStatus.Completed or SaleStatus.PartiallyRefunded))
            return Error.Conflict("pos.sale_not_returnable", "Vente non retournable.");

        // Guide §10.2 : jamais plus que la quantité achetée non encore retournée — retours
        // précédents compris (un retour partiel suivi d'un autre ne peut pas dépasser la vente).
        var alreadyReturned = await store.ReturnedQuantitiesAsync(cmd.SaleId, ct);
        foreach (var group in cmd.Lines.GroupBy(l => l.SaleLineId))
        {
            var src = sale.Lines.FirstOrDefault(l => l.Id == group.Key);
            if (src is null)
                return Error.Validation("pos.return_line_not_in_sale", "Un article retourné n'appartient pas à la vente d'origine.");
            if (group.Any(l => l.Quantity <= 0m))
                return Error.Validation("pos.return_quantity", $"Quantité de retour invalide pour « {src.Description} ».");
            var requested = group.Sum(l => l.Quantity);
            var remaining = src.Quantity - alreadyReturned.GetValueOrDefault(src.Id);
            if (requested > remaining)
                return Error.Validation("pos.return_exceeds_sold",
                    $"« {src.Description} » : {requested} demandé(s), {remaining} encore retournable(s) sur {src.Quantity} vendu(s).");
        }

        // Module clients §7, §11 : un retour sur une vente à crédit réduit d'abord la dette née de cette
        // vente ; ce qui dépasse le reste dû devient un avoir disponible du client. Avant, l'écriture
        // créditait le compte client sans que la créance ne baisse : le client restait débiteur.
        var receivable = await store.ReceivableBalanceForSaleAsync(cmd.SaleId, ct);
        var toCustomerAccount = cmd.RefundMethod is "store_credit" or "credit_note"
                                || (cmd.RefundMethod == "original" && receivable is not null);
        if (toCustomerAccount && sale.CustomerId is null)
            return Error.Validation("pos.refund_needs_customer",
                "Un avoir ou un remboursement sur compte exige une vente rattachée à un client.");
        var imputable = cmd.RefundMethod is "original" or "credit_note" && receivable is not null;

        var currency = await store.CurrencyCodeAsync(sale.CurrencyId, ct);

        return await uow.ExecuteInTransactionAsync<Result<ReturnResult>>(async token =>
        {
            var now = clock.UtcNow;
            var ret = new SaleReturn
            {
                Id = Guid.NewGuid(),
                CompanyId = Company,
                SaleId = cmd.SaleId,
                DocumentNumber = await store.NextNumberAsync("sale_return", token),
                Status = "completed",
                CurrencyId = sale.CurrencyId,
                Reason = cmd.Reason,
                RefundMethod = cmd.RefundMethod,
                Restocked = true,
                SourceOperationUuid = cmd.OperationUuid,
                CashSessionId = cmd.CashSessionId,
                ReturnedAt = now,
                CreatedBy = tenant.UserId,
            };

            decimal refundNet = 0m, refundTax = 0m, cogsRestocked = 0m;
            var movements = new List<StockMovementRequest>();

            foreach (var input in cmd.Lines)
            {
                var srcLine = sale.Lines.First(l => l.Id == input.SaleLineId);   // contrôlé ci-dessus

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

            ret.RefundTotal = Rounding.Money(refundNet + refundTax);
            await store.AddReturnAsync(ret, token);
            if (movements.Count > 0) await stock.PostManyAsync(movements, token);

            var relief = imputable ? Math.Min(ret.RefundTotal, receivable!.Remaining) : 0m;
            var posted = await engine.HandleAsync(new SaleRefundRecorded(
                Company, cmd.OperationUuid, now, ret.Id, sale.CustomerId,
                new Money(refundNet, currency), new Money(refundTax, currency),
                cmd.RefundMethod, cmd.RefundFinancialAccountId, relief > 0m,
                cogsRestocked > 0m ? new Money(cogsRestocked, currency) : null)
            {
                ReceivableRelief = new Money(relief, currency),
                RemainderToStoreCredit = toCustomerAccount,
            }, token);
            if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);

            if (toCustomerAccount && ret.RefundTotal > 0m)
                await creditMemos.IssueForReturnAsync(sale.CustomerId!.Value, ret.Id, sale.CurrencyId, ret.RefundTotal,
                    receivable?.ReceivableId, relief, cmd.Reason, token);

            // Statut sur le cumul (retours précédents + celui-ci), pas sur le seul retour courant.
            var totalSold = sale.Lines.Sum(l => l.Quantity);
            var totalReturned = alreadyReturned.Values.Sum() + cmd.Lines.Sum(l => l.Quantity);
            await store.SetSaleStatusAsync(cmd.SaleId,
                totalReturned >= totalSold ? SaleStatus.Refunded : SaleStatus.PartiallyRefunded, token);

            logger.LogInformation("Retour {Ret} sur vente {Sale} — {Reason}", ret.DocumentNumber, sale.DocumentNumber, cmd.Reason);
            return Result.Success(new ReturnResult(
                ret.Id, ret.DocumentNumber, refundNet, refundTax, ret.RefundTotal, posted.Value!.JournalEntryId));
        }, ct);
    }
}
