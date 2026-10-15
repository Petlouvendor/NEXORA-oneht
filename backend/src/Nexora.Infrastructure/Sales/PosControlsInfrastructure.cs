using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Domain.Sales;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sales;

/// <summary>
/// Notification des validations Manager (guide §20) : prévient les détenteurs d'une permission
/// d'approbation dans l'entreprise active, et le demandeur de la décision. Le lien ouvre directement
/// l'objet. Un échec de notification ne remet jamais en cause l'opération métier déjà validée.
/// </summary>
public sealed class PosApprovalNotifier(
    NexoraDbContext db, ITenantContext tenant, INotificationService notifications, ILogger<PosApprovalNotifier> logger)
    : IPosApprovalNotifier
{
    private Guid Company => SalesHelpers.Company(tenant);

    public async Task NotifyApproversAsync(string permission, string kind, string number, string summary,
        string actionUrl, string referenceType, Guid referenceId, CancellationToken ct)
    {
        try
        {
            List<Guid> approvers;
            using (tenant.BeginSystemScope())
                approvers = await db.Database.SqlQuery<Guid>($"""
                    SELECT DISTINCT ur.user_id AS "Value"
                    FROM nexora.user_role ur
                    JOIN nexora.role_permission rp ON rp.role_id = ur.role_id
                    JOIN nexora.permission p ON p.id = rp.permission_id
                    JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                    JOIN nexora.app_user u ON u.id = ur.user_id AND u.status = 'active'
                    WHERE p.code = {permission}
                      AND ur.revoked_at IS NULL
                      AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                      AND (ur.valid_until IS NULL OR ur.valid_until > now())
                      AND (sc.company_id = {Company}
                           OR (sc.group_id IS NOT NULL AND sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {Company})))
                      AND ur.user_id <> {tenant.UserId}
                    LIMIT 50
                    """).ToListAsync(ct);
            if (approvers.Count == 0) return;

            await notifications.NotifyAsync(new NotifyRequest(
                Category: "pos.approval",
                Recipients: approvers,
                TemplateKey: "pos.approval_requested",
                Variables: new Dictionary<string, string?>
                {
                    ["request.kind"] = kind, ["request.number"] = number,
                    ["requester.name"] = await UserNameAsync(tenant.UserId, ct), ["request.summary"] = summary,
                },
                Priority: 2, Severity: "warning", Source: "workflow", ActionUrl: actionUrl,
                ReferenceType: referenceType, ReferenceId: referenceId,
                DedupKey: $"{referenceType}:{referenceId}:requested"), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification Manager non émise pour {Type} {Id}", referenceType, referenceId);
        }
    }

    public async Task NotifyRequesterAsync(Guid requester, string kind, string number, bool approved, string? note,
        string actionUrl, string referenceType, Guid referenceId, CancellationToken ct)
    {
        try
        {
            await notifications.NotifyAsync(new NotifyRequest(
                Category: "pos.approval",
                Recipients: [requester],
                TemplateKey: "pos.approval_decided",
                Variables: new Dictionary<string, string?>
                {
                    ["request.kind"] = kind, ["request.number"] = number,
                    ["decision"] = approved ? "approuvé" : "refusé",
                    ["decider.name"] = await UserNameAsync(tenant.UserId, ct), ["decision.note"] = note ?? "",
                },
                Priority: 2, Severity: approved ? "success" : "warning", Source: "workflow", ActionUrl: actionUrl,
                ReferenceType: referenceType, ReferenceId: referenceId,
                DedupKey: $"{referenceType}:{referenceId}:decided"), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification de décision non émise pour {Type} {Id}", referenceType, referenceId);
        }
    }

    private async Task<string> UserNameAsync(Guid? userId, CancellationToken ct)
    {
        using var _ = tenant.BeginSystemScope();
        return await db.Database.SqlQuery<string>($"""
            SELECT COALESCE(full_name, email::text) AS "Value" FROM nexora.app_user WHERE id = {userId}
            """).FirstOrDefaultAsync(ct) ?? "Un utilisateur";
    }
}

/// <summary>Demandes de retour validées par un Manager (guide §10) — voir <see cref="IReturnRequestService"/>.</summary>
public sealed class SqlReturnRequestService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IPermissionService permissions,
    ISalesReturnStore returnStore, SalesReturnService returns, IPosApprovalNotifier notifier) : IReturnRequestService
{
    private const string Kind = "Retour";
    private Guid Company => SalesHelpers.Company(tenant);
    private Guid UserId => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    private sealed record RequestRow(
        Guid Id, string RequestNumber, Guid SaleId, string SaleNumber, string Status, string Reason, string RefundMethod,
        Guid? RefundFinancialAccountId, Guid InventoryLocationId, Guid? CashSessionId, decimal EstimatedRefund,
        string Currency, Guid OperationUuid, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt,
        Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? SaleReturnId);
    private sealed record LineRow(Guid SaleLineId, string Description, decimal Quantity, bool Restock, decimal EstimatedRefund);
    private sealed record PendingRow(Guid SaleLineId, decimal Quantity);

    // ------------------------------------------------------------------ Lecture

    public async Task<ReturnableSaleDto?> FindSaleForReturnAsync(string documentNumber, CancellationToken ct = default)
    {
        var number = (documentNumber ?? "").Trim();
        if (number.Length == 0) return null;
        var sale = await db.Sales.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.CompanyId == Company && s.DocumentNumber == number, ct);
        if (sale is null) return null;

        var currency = await db.Currencies.AsNoTracking().Where(c => c.Id == sale.CurrencyId).Select(c => c.Code).FirstAsync(ct);
        var returned = await returnStore.ReturnedQuantitiesAsync(sale.Id, ct);
        var pending = await PendingQuantitiesAsync(sale.Id, null, ct);

        return new ReturnableSaleDto(sale.Id, sale.DocumentNumber, sale.SoldAt, sale.Status, currency, sale.Total,
            sale.Lines.OrderBy(l => l.Description).Select(l =>
            {
                var r = returned.GetValueOrDefault(l.Id);
                var p = pending.GetValueOrDefault(l.Id);
                var returnable = IsReturnable(sale.Status) ? Math.Max(0m, l.Quantity - r - p) : 0m;
                return new ReturnableSaleLineDto(l.Id, l.ProductId, l.Description, l.Quantity, r, p, returnable, l.UnitPrice, l.LineTotal);
            }).ToList());
    }

    public async Task<IReadOnlyList<ReturnRequestDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var all = await CanApproveAsync(ct);
        var rows = await db.Database.SqlQuery<RequestRow>($"""
            SELECT r.id AS "Id", r.request_number AS "RequestNumber", r.sale_id AS "SaleId", s.document_number AS "SaleNumber",
                   r.status AS "Status", r.reason AS "Reason", r.refund_method AS "RefundMethod",
                   r.refund_financial_account_id AS "RefundFinancialAccountId", r.inventory_location_id AS "InventoryLocationId",
                   r.cash_session_id AS "CashSessionId", r.estimated_refund AS "EstimatedRefund", c.code AS "Currency",
                   r.operation_uuid AS "OperationUuid", r.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName",
                   r.requested_at AS "RequestedAt", r.decided_by AS "DecidedBy", dc.full_name AS "DecidedByName",
                   r.decided_at AS "DecidedAt", r.decision_note AS "DecisionNote", r.sale_return_id AS "SaleReturnId"
            FROM nexora.sale_return_request r
            JOIN nexora.sale s ON s.id = r.sale_id
            JOIN nexora.currency c ON c.id = r.currency_id
            LEFT JOIN nexora.app_user rq ON rq.id = r.requested_by
            LEFT JOIN nexora.app_user dc ON dc.id = r.decided_by
            WHERE r.company_id = {Company}
              AND ({status}::text IS NULL OR r.status = {status}::text)
              AND ({all} OR r.requested_by = {UserId})
            ORDER BY r.requested_at DESC
            LIMIT 200
            """).ToListAsync(ct);
        var list = new List<ReturnRequestDto>(rows.Count);
        foreach (var row in rows) list.Add(await ToDtoAsync(row, ct));
        return list;
    }

    public async Task<Result<ReturnRequestDto>> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var row = await LoadAsync(requestId, ct);
        if (row is null || (row.RequestedBy != UserId && !await CanApproveAsync(ct)))
            return Error.NotFound("pos.return_request_not_found", "Demande de retour introuvable.");
        return Result.Success(await ToDtoAsync(row, ct));
    }

    // ------------------------------------------------------------------ Demande (caissier)

    public async Task<Result<ReturnRequestDto>> RequestAsync(CreateReturnRequest request, CancellationToken ct = default)
    {
        if (request.Lines is null || request.Lines.Count == 0)
            return Error.Validation("pos.no_lines", "Sélectionnez au moins un article à retourner.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Error.Validation("pos.reason_required", "Le motif du retour est obligatoire (§10.1).");
        if (!SalesReturnService.RefundMethods.Contains(request.RefundMethod))
            return Error.Validation("pos.refund_method", $"Mode de remboursement « {request.RefundMethod} » inconnu.");

        // Idempotence : un double envoi renvoie la même demande.
        var replay = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.sale_return_request WHERE operation_uuid = {request.OperationUuid}
            """).ToListAsync(ct);
        if (replay.Count > 0) return await GetAsync(replay[0], ct);

        var sale = await db.Sales.AsNoTracking().Include(s => s.Lines)
            .FirstOrDefaultAsync(s => s.Id == request.SaleId && s.CompanyId == Company, ct);
        if (sale is null) return Error.NotFound("pos.sale_not_found", "Vente d'origine introuvable (§10.2 : lien obligatoire).");
        if (!IsReturnable(sale.Status)) return Error.Conflict("pos.sale_not_returnable", "Cette vente ne peut pas faire l'objet d'un retour.");

        if (request.CashSessionId is { } sessionId && await SessionRefusalAsync(sessionId, ct) is { } refusal)
            return refusal;

        // §10.2 : quantité achetée − déjà retournée − déjà demandée (demandes en attente).
        var returned = await returnStore.ReturnedQuantitiesAsync(sale.Id, ct);
        var pending = await PendingQuantitiesAsync(sale.Id, null, ct);
        var lines = new List<(SaleLine Src, decimal Qty, bool Restock, decimal Refund)>();
        foreach (var group in request.Lines.GroupBy(l => l.SaleLineId))
        {
            var src = sale.Lines.FirstOrDefault(l => l.Id == group.Key);
            if (src is null)
                return Error.Validation("pos.return_line_not_in_sale", "Un article demandé n'appartient pas à la vente d'origine.");
            if (group.Any(l => l.Quantity <= 0m))
                return Error.Validation("pos.return_quantity", $"Quantité de retour invalide pour « {src.Description} ».");
            var qty = group.Sum(l => l.Quantity);
            var remaining = src.Quantity - returned.GetValueOrDefault(src.Id) - pending.GetValueOrDefault(src.Id);
            if (qty > remaining)
                return Error.Validation("pos.return_exceeds_sold",
                    $"« {src.Description} » : {qty} demandé(s), {Math.Max(0m, remaining)} encore retournable(s) sur {src.Quantity} vendu(s).");
            lines.Add((src, qty, group.All(l => l.Restock), EstimateRefund(src, qty)));
        }

        var id = Guid.NewGuid();
        var number = await SalesHelpers.NextNumber(db, tenant, "sale_return_request", ct);
        var estimate = lines.Sum(l => l.Refund);
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.sale_return_request
                    (id, company_id, request_number, sale_id, status, reason, refund_method, refund_financial_account_id,
                     inventory_location_id, cash_session_id, currency_id, estimated_refund, operation_uuid, requested_by)
                VALUES ({id}, {Company}, {number}, {sale.Id}, 'pending_approval', {request.Reason.Trim()}, {request.RefundMethod},
                        {request.RefundFinancialAccountId}, {request.InventoryLocationId}, {request.CashSessionId}, {sale.CurrencyId},
                        {estimate}, {request.OperationUuid}, {UserId})
                """, token);
            foreach (var l in lines)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.sale_return_request_line (company_id, request_id, sale_line_id, quantity, restock, estimated_refund)
                    VALUES ({Company}, {id}, {l.Src.Id}, {l.Qty}, {l.Restock}, {l.Refund})
                    """, token);
            return 0;
        }, ct);

        var currency = await db.Currencies.AsNoTracking().Where(c => c.Id == sale.CurrencyId).Select(c => c.Code).FirstAsync(ct);
        await notifier.NotifyApproversAsync(Permissions.Pos.Refund, Kind, number,
            $"{lines.Sum(l => l.Qty):0.##} article(s) de la vente {sale.DocumentNumber}, remboursement estimé {estimate:0.00} {currency} — motif : {request.Reason.Trim()}",
            $"/pos?approvals=returns&id={id}", "sale_return_request", id, ct);
        return await GetAsync(id, ct);
    }

    // ------------------------------------------------------------------ Décision (Manager)

    public async Task<Result<ReturnRequestDto>> ApproveAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default)
    {
        var (row, refusal) = await DecidableAsync(requestId, ct);
        if (refusal is not null) return refusal;

        var lines = await LinesAsync(row!.Id, ct);
        var command = new ReturnCommand(row.OperationUuid, row.SaleId, row.InventoryLocationId, row.CashSessionId,
            row.Reason, row.RefundMethod, row.RefundFinancialAccountId,
            lines.Select(l => new ReturnLineInput(l.SaleLineId, l.Quantity, l.Restock)).ToList());

        // Approuver = exécuter : stock, remboursement, écriture et statut de la demande sont atomiques.
        var outcome = await uow.ExecuteInTransactionAsync<Result<Guid>>(async token =>
        {
            var executed = await returns.ReturnAsync(command, token);
            if (!executed.IsSuccess) return executed.Error!;
            var updated = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sale_return_request
                   SET status = 'executed', decided_by = {UserId}, decided_at = now(),
                       decision_note = {decision.Note}, sale_return_id = {executed.Value!.SaleReturnId}
                 WHERE id = {row.Id} AND company_id = {Company} AND status = 'pending_approval'
                """, token);
            if (updated != 1) throw new InvalidOperationException("La demande a été traitée entre-temps.");
            return Result.Success(executed.Value.SaleReturnId);
        }, ct);
        if (!outcome.IsSuccess) return outcome.Error!;

        await notifier.NotifyRequesterAsync(row.RequestedBy, Kind, row.RequestNumber, true, decision.Note,
            $"/pos?approvals=returns&id={row.Id}", "sale_return_request", row.Id, ct);
        return await GetAsync(row.Id, ct);
    }

    public async Task<Result<ReturnRequestDto>> RejectAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Note))
            return Error.Validation("pos.decision_note_required", "Indiquez le motif du refus.");
        var (row, refusal) = await DecidableAsync(requestId, ct);
        if (refusal is not null) return refusal;

        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sale_return_request
               SET status = 'rejected', decided_by = {UserId}, decided_at = now(), decision_note = {decision.Note.Trim()}
             WHERE id = {row!.Id} AND company_id = {Company} AND status = 'pending_approval'
            """, ct);
        if (updated != 1) return Error.Conflict("pos.request_closed", "Cette demande a déjà été traitée.");

        await notifier.NotifyRequesterAsync(row.RequestedBy, Kind, row.RequestNumber, false, decision.Note.Trim(),
            $"/pos?approvals=returns&id={row.Id}", "sale_return_request", row.Id, ct);
        return await GetAsync(row.Id, ct);
    }

    // ------------------------------------------------------------------ Aides

    private async Task<(RequestRow? Row, Error? Refusal)> DecidableAsync(Guid requestId, CancellationToken ct)
    {
        if (!await CanApproveAsync(ct))
            return (null, Error.Forbidden("pos.refund_forbidden", "Valider un retour exige la permission pos.refund."));
        var row = await LoadAsync(requestId, ct);
        if (row is null) return (null, Error.NotFound("pos.return_request_not_found", "Demande de retour introuvable."));
        if (row.Status != "pending_approval") return (null, Error.Conflict("pos.request_closed", "Cette demande a déjà été traitée."));
        // §10 : le demandeur ne valide jamais sa propre demande (contrôlé aussi en base).
        if (row.RequestedBy == UserId)
            return (null, Error.Forbidden("pos.self_approval", "Vous ne pouvez pas valider votre propre demande."));
        return (row, null);
    }

    private async Task<bool> CanApproveAsync(CancellationToken ct)
        => (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.Refund);

    /// <summary>Une session de caisse citée doit être ouverte et appartenir au demandeur (§13),
    /// sauf pour un superviseur (<c>pos.manage_sessions</c>).</summary>
    private async Task<Error?> SessionRefusalAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await db.CashSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId && s.CompanyId == Company, ct);
        if (session is null || session.Status != "open")
            return Error.Conflict("pos.session_closed", "La session de caisse est fermée ou introuvable.");
        if (session.UserId != UserId && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.ManageSessions))
            return Error.Forbidden("pos.session_not_owned", "Cette session de caisse n'est pas la vôtre.");
        return null;
    }

    private static bool IsReturnable(string status) => status is SaleStatus.Completed or SaleStatus.PartiallyRefunded;

    /// <summary>Même calcul que le retour exécuté (prorata HT + taxe de la ligne d'origine).</summary>
    private static decimal EstimateRefund(SaleLine src, decimal qty)
    {
        var ratio = qty / src.Quantity;
        var net = Math.Round((src.LineTotal - src.TaxAmount) * ratio, 2, MidpointRounding.ToEven);
        var tax = Math.Round(src.TaxAmount * ratio, 2, MidpointRounding.ToEven);
        return net + tax;
    }

    private async Task<Dictionary<Guid, decimal>> PendingQuantitiesAsync(Guid saleId, Guid? exceptRequest, CancellationToken ct)
        => (await db.Database.SqlQuery<PendingRow>($"""
                SELECT l.sale_line_id AS "SaleLineId", SUM(l.quantity) AS "Quantity"
                FROM nexora.sale_return_request_line l
                JOIN nexora.sale_return_request r ON r.id = l.request_id
                WHERE r.sale_id = {saleId} AND r.status = 'pending_approval' AND r.id IS DISTINCT FROM {exceptRequest}
                GROUP BY l.sale_line_id
                """).ToListAsync(ct))
            .ToDictionary(r => r.SaleLineId, r => r.Quantity);

    private async Task<RequestRow?> LoadAsync(Guid requestId, CancellationToken ct)
        => await db.Database.SqlQuery<RequestRow>($"""
            SELECT r.id AS "Id", r.request_number AS "RequestNumber", r.sale_id AS "SaleId", s.document_number AS "SaleNumber",
                   r.status AS "Status", r.reason AS "Reason", r.refund_method AS "RefundMethod",
                   r.refund_financial_account_id AS "RefundFinancialAccountId", r.inventory_location_id AS "InventoryLocationId",
                   r.cash_session_id AS "CashSessionId", r.estimated_refund AS "EstimatedRefund", c.code AS "Currency",
                   r.operation_uuid AS "OperationUuid", r.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName",
                   r.requested_at AS "RequestedAt", r.decided_by AS "DecidedBy", dc.full_name AS "DecidedByName",
                   r.decided_at AS "DecidedAt", r.decision_note AS "DecisionNote", r.sale_return_id AS "SaleReturnId"
            FROM nexora.sale_return_request r
            JOIN nexora.sale s ON s.id = r.sale_id
            JOIN nexora.currency c ON c.id = r.currency_id
            LEFT JOIN nexora.app_user rq ON rq.id = r.requested_by
            LEFT JOIN nexora.app_user dc ON dc.id = r.decided_by
            WHERE r.id = {requestId} AND r.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private async Task<List<LineRow>> LinesAsync(Guid requestId, CancellationToken ct)
        => await db.Database.SqlQuery<LineRow>($"""
            SELECT l.sale_line_id AS "SaleLineId", sl.description AS "Description", l.quantity AS "Quantity",
                   l.restock AS "Restock", l.estimated_refund AS "EstimatedRefund"
            FROM nexora.sale_return_request_line l
            JOIN nexora.sale_line sl ON sl.id = l.sale_line_id
            WHERE l.request_id = {requestId}
            ORDER BY sl.description
            """).ToListAsync(ct);

    private async Task<ReturnRequestDto> ToDtoAsync(RequestRow r, CancellationToken ct)
        => new(r.Id, r.RequestNumber, r.SaleId, r.SaleNumber, r.Status, r.Reason, r.RefundMethod, r.EstimatedRefund, r.Currency,
            r.RequestedBy, r.RequestedByName, r.RequestedAt, r.DecidedBy, r.DecidedByName, r.DecidedAt, r.DecisionNote,
            r.SaleReturnId,
            (await LinesAsync(r.Id, ct)).Select(l => new ReturnRequestLineDto(l.SaleLineId, l.Description, l.Quantity, l.Restock, l.EstimatedRefund)).ToList());
}

/// <summary>Remises exceptionnelles validées par un Manager (guide §12) — voir <see cref="IDiscountRequestService"/>.</summary>
public sealed class SqlDiscountRequestService(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, IPermissionService permissions,
    IPosLimits limits, ISalesPricing pricing, ISalePricingGuard guard, IPosApprovalNotifier notifier) : IDiscountRequestService
{
    private const string Kind = "Remise exceptionnelle";
    /// <summary>Durée de validité d'une demande / approbation : le temps d'un passage en caisse.</summary>
    private static readonly TimeSpan Validity = TimeSpan.FromHours(2);
    private Guid Company => SalesHelpers.Company(tenant);
    private Guid UserId => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    private sealed record Row(
        Guid Id, string RequestNumber, Guid OperationUuid, Guid ProductId, string ProductName, decimal Quantity,
        decimal LineGross, decimal RequestedAmount, decimal RequestedPercent, string Currency, string Reason, string Status,
        Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt,
        Guid? DecidedBy, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? ConsumedSaleId);

    public async Task<PosLimitsDto> MyLimitsAsync(CancellationToken ct = default)
    {
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var l = await limits.DiscountLimitsAsync(ct);
        var canApprove = granted.Contains(Permissions.Pos.ApproveDiscount);
        return new PosLimitsDto(
            granted.Contains(Permissions.Pos.Discount) ? l.SelfLimitPercent : 0m,
            canApprove ? l.ApprovalLimitPercent : null,
            granted.Contains(Permissions.Pos.Discount), granted.Contains(Permissions.Pos.ChangePrice),
            granted.Contains(Permissions.Pos.SellCredit), granted.Contains(Permissions.Pos.CreditOverride),
            granted.Contains(Permissions.Pos.Return), granted.Contains(Permissions.Pos.Refund),
            canApprove, granted.Contains(Permissions.Pos.ManageSessions));
    }

    public async Task<Result<DiscountRequestDto>> RequestAsync(CreateDiscountRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason))
            return Error.Validation("pos.reason_required", "Le motif de la remise exceptionnelle est obligatoire.");
        if (r.Quantity <= 0m) return Error.Validation("sale.quantity", "La quantité doit être strictement positive.");
        if (r.RequestedDiscount <= 0m) return Error.Validation("pos.discount_amount", "Indiquez le montant de remise demandé.");

        var line = new CheckoutLine(r.ProductId, r.Quantity, null, r.RequestedDiscount, null);
        if (await guard.ValidateProductsAsync([line], ct) is { IsSuccess: false } unsellable) return unsellable.Error!;

        // Montant brut recalculé par le serveur — jamais pris du client (§23).
        var priced = (await pricing.PriceCartAsync(r.InventoryLocationId, r.CustomerId, r.Currency, clock.UtcNow, [line], null, ct))[0];
        var gross = Math.Round(priced.UnitPrice * priced.Quantity, 2);
        if (gross <= 0m)
            return Error.Validation("sale.no_price", $"Aucun prix de vente en vigueur pour « {priced.Description} » en {r.Currency}.");
        if (r.RequestedDiscount > gross)
            return Error.Validation("sale.discount_exceeds_line", $"La remise dépasse le montant de la ligne ({gross}).");
        var percent = Math.Round(r.RequestedDiscount / gross * 100m, 4);

        var own = await limits.DiscountLimitsAsync(ct);
        if (own.SelfLimitPercent is null || percent <= own.SelfLimitPercent.Value + 0.0001m)
            return Error.Validation("pos.discount_within_limit",
                "Cette remise est dans votre limite : aucune validation Manager n'est nécessaire.");

        var currencyId = await db.Currencies.AsNoTracking().Where(c => c.Code == r.Currency).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("pos.currency", $"Devise « {r.Currency} » inconnue.");

        var id = Guid.NewGuid();
        var number = await SalesHelpers.NextNumber(db, tenant, "pos_discount_request", ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.pos_discount_request
                (id, company_id, request_number, operation_uuid, product_id, quantity, currency_id, line_gross,
                 requested_amount, requested_percent, reason, status, requested_by, expires_at)
            VALUES ({id}, {Company}, {number}, {r.OperationUuid}, {r.ProductId}, {r.Quantity}, {currencyId}, {gross},
                    {Math.Round(r.RequestedDiscount, 2)}, {percent}, {r.Reason.Trim()}, 'pending_approval', {UserId},
                    {clock.UtcNow.Add(Validity)})
            """, ct);

        await notifier.NotifyApproversAsync(Permissions.Pos.ApproveDiscount, Kind, number,
            $"{Math.Round(percent, 2)} % ({r.RequestedDiscount:0.00} {r.Currency}) sur « {priced.Description} » × {r.Quantity:0.##} — motif : {r.Reason.Trim()}",
            $"/pos?approvals=discounts&id={id}", "pos_discount_request", id, ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<DiscountRequestDto>> ApproveAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default)
    {
        var (row, refusal) = await DecidableAsync(requestId, ct);
        if (refusal is not null) return refusal;

        // « Le Manager peut approuver selon sa limite » (§12).
        var mine = await limits.DiscountLimitsAsync(ct);
        if (mine.ApprovalLimitPercent is { } max && row!.RequestedPercent > max + 0.0001m)
            return Error.Forbidden("pos.discount_beyond_approver_limit",
                $"Remise de {Math.Round(row.RequestedPercent, 2)} % : au-delà de votre limite d'approbation de {max} %.");

        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.pos_discount_request
               SET status = 'approved', decided_by = {UserId}, decided_at = now(), decision_note = {decision.Note}
             WHERE id = {row!.Id} AND company_id = {Company} AND status = 'pending_approval'
            """, ct);
        if (updated != 1) return Error.Conflict("pos.request_closed", "Cette demande a déjà été traitée.");

        await notifier.NotifyRequesterAsync(row.RequestedBy, Kind, row.RequestNumber, true, decision.Note,
            $"/pos?approvals=discounts&id={row.Id}", "pos_discount_request", row.Id, ct);
        return await GetAsync(row.Id, ct);
    }

    public async Task<Result<DiscountRequestDto>> RejectAsync(Guid requestId, DecideRequest decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Note))
            return Error.Validation("pos.decision_note_required", "Indiquez le motif du refus.");
        var (row, refusal) = await DecidableAsync(requestId, ct);
        if (refusal is not null) return refusal;

        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.pos_discount_request
               SET status = 'rejected', decided_by = {UserId}, decided_at = now(), decision_note = {decision.Note.Trim()}
             WHERE id = {row!.Id} AND company_id = {Company} AND status = 'pending_approval'
            """, ct);
        if (updated != 1) return Error.Conflict("pos.request_closed", "Cette demande a déjà été traitée.");

        await notifier.NotifyRequesterAsync(row.RequestedBy, Kind, row.RequestNumber, false, decision.Note.Trim(),
            $"/pos?approvals=discounts&id={row.Id}", "pos_discount_request", row.Id, ct);
        return await GetAsync(row.Id, ct);
    }

    public async Task<IReadOnlyList<DiscountRequestDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        await ExpireStaleAsync(ct);
        var all = (await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.ApproveDiscount);
        return (await db.Database.SqlQuery<Row>($"""
            SELECT d.id AS "Id", d.request_number AS "RequestNumber", d.operation_uuid AS "OperationUuid", d.product_id AS "ProductId",
                   p.name AS "ProductName", d.quantity AS "Quantity", d.line_gross AS "LineGross", d.requested_amount AS "RequestedAmount",
                   d.requested_percent AS "RequestedPercent", c.code AS "Currency", d.reason AS "Reason", d.status AS "Status",
                   d.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName", d.requested_at AS "RequestedAt",
                   d.expires_at AS "ExpiresAt", d.decided_by AS "DecidedBy", dc.full_name AS "DecidedByName",
                   d.decided_at AS "DecidedAt", d.decision_note AS "DecisionNote", d.consumed_sale_id AS "ConsumedSaleId"
            FROM nexora.pos_discount_request d
            JOIN nexora.product p ON p.id = d.product_id
            JOIN nexora.currency c ON c.id = d.currency_id
            LEFT JOIN nexora.app_user rq ON rq.id = d.requested_by
            LEFT JOIN nexora.app_user dc ON dc.id = d.decided_by
            WHERE d.company_id = {Company}
              AND ({status}::text IS NULL OR d.status = {status}::text)
              AND ({all} OR d.requested_by = {UserId})
            ORDER BY d.requested_at DESC
            LIMIT 200
            """).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<Result<DiscountRequestDto>> GetAsync(Guid requestId, CancellationToken ct = default)
    {
        var row = await LoadAsync(requestId, ct);
        if (row is null || (row.RequestedBy != UserId
                            && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.ApproveDiscount)))
            return Error.NotFound("pos.discount_request_not_found", "Demande de remise introuvable.");
        return Result.Success(ToDto(row));
    }

    private async Task<(Row? Row, Error? Refusal)> DecidableAsync(Guid requestId, CancellationToken ct)
    {
        if (!(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.ApproveDiscount))
            return (null, Error.Forbidden("pos.approve_discount_forbidden", "Valider une remise exige la permission pos.approve_discount."));
        await ExpireStaleAsync(ct);
        var row = await LoadAsync(requestId, ct);
        if (row is null) return (null, Error.NotFound("pos.discount_request_not_found", "Demande de remise introuvable."));
        if (row.Status == "expired") return (null, Error.Conflict("pos.request_expired", "Cette demande a expiré."));
        if (row.Status != "pending_approval") return (null, Error.Conflict("pos.request_closed", "Cette demande a déjà été traitée."));
        if (row.RequestedBy == UserId)
            return (null, Error.Forbidden("pos.self_approval", "Vous ne pouvez pas valider votre propre demande."));
        return (row, null);
    }

    /// <summary>Une demande non traitée (ou une approbation non utilisée) passe « expirée » à l'échéance.</summary>
    private Task<int> ExpireStaleAsync(CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.pos_discount_request SET status = 'expired'
            WHERE company_id = {Company} AND status IN ('pending_approval', 'approved') AND expires_at <= now()
            """, ct);

    private async Task<Row?> LoadAsync(Guid requestId, CancellationToken ct)
        => await db.Database.SqlQuery<Row>($"""
            SELECT d.id AS "Id", d.request_number AS "RequestNumber", d.operation_uuid AS "OperationUuid", d.product_id AS "ProductId",
                   p.name AS "ProductName", d.quantity AS "Quantity", d.line_gross AS "LineGross", d.requested_amount AS "RequestedAmount",
                   d.requested_percent AS "RequestedPercent", c.code AS "Currency", d.reason AS "Reason", d.status AS "Status",
                   d.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName", d.requested_at AS "RequestedAt",
                   d.expires_at AS "ExpiresAt", d.decided_by AS "DecidedBy", dc.full_name AS "DecidedByName",
                   d.decided_at AS "DecidedAt", d.decision_note AS "DecisionNote", d.consumed_sale_id AS "ConsumedSaleId"
            FROM nexora.pos_discount_request d
            JOIN nexora.product p ON p.id = d.product_id
            JOIN nexora.currency c ON c.id = d.currency_id
            LEFT JOIN nexora.app_user rq ON rq.id = d.requested_by
            LEFT JOIN nexora.app_user dc ON dc.id = d.decided_by
            WHERE d.id = {requestId} AND d.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private static DiscountRequestDto ToDto(Row r) => new(
        r.Id, r.RequestNumber, r.OperationUuid, r.ProductId, r.ProductName, r.Quantity, r.LineGross, r.RequestedAmount,
        Math.Round(r.RequestedPercent, 2), r.Currency, r.Reason, r.Status, r.RequestedBy, r.RequestedByName, r.RequestedAt,
        r.ExpiresAt, r.DecidedBy, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.ConsumedSaleId);
}

/// <summary>Lectures des sessions de caisse pour l'écran POS (guide §13) — voir <see cref="ICashSessionQueries"/>.</summary>
public sealed class SqlCashSessionQueries(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : ICashSessionQueries
{
    private Guid Company => SalesHelpers.Company(tenant);

    public async Task<CurrentCashSessionDto?> CurrentAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<CurrentCashSessionDto>($"""
            SELECT s.id AS "SessionId", s.cash_register_id AS "CashRegisterId", r.name AS "RegisterName", c.code AS "Currency",
                   r.financial_account_id AS "FinancialAccountId",
                   (SELECT l.id FROM nexora.inventory_location l
                    WHERE l.store_id = r.store_id AND l.status = 'active' ORDER BY l.name LIMIT 1) AS "InventoryLocationId",
                   s.status AS "Status", s.opened_at AS "OpenedAt",
                   s.opening_balance AS "OpeningBalance",
                   (SELECT COUNT(*)::int FROM nexora.sale x WHERE x.cash_session_id = s.id AND x.status <> 'void') AS "SaleCount"
            FROM nexora.cash_session s
            JOIN nexora.cash_register r ON r.id = s.cash_register_id
            JOIN nexora.currency c ON c.id = r.currency_id
            WHERE s.company_id = {Company} AND s.user_id = {tenant.UserId} AND s.status IN ('open', 'closing')
            ORDER BY s.opened_at DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<CashRegisterDto>> RegistersAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<CashRegisterDto>($"""
            SELECT r.id AS "Id", r.name AS "Name", r.code AS "Code", c.code AS "Currency",
                   r.financial_account_id AS "FinancialAccountId",
                   (SELECT l.id FROM nexora.inventory_location l
                    WHERE l.store_id = r.store_id AND l.status = 'active' ORDER BY l.name LIMIT 1) AS "InventoryLocationId",
                   EXISTS (SELECT 1 FROM nexora.cash_session s
                           WHERE s.cash_register_id = r.id AND s.status IN ('open', 'closing')) AS "HasOpenSession",
                   (SELECT COALESCE(u.full_name, u.email::text) FROM nexora.cash_session s
                    JOIN nexora.app_user u ON u.id = s.user_id
                    WHERE s.cash_register_id = r.id AND s.status IN ('open', 'closing')
                    ORDER BY s.opened_at DESC LIMIT 1) AS "OpenedBy"
            FROM nexora.cash_register r
            JOIN nexora.currency c ON c.id = r.currency_id
            WHERE r.company_id = {Company} AND r.status = 'active'
            ORDER BY r.name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<OpenCashSessionDto>> OpenSessionsAsync(CancellationToken ct = default)
    {
        if (!(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.ManageSessions))
            return [];
        return await db.Database.SqlQuery<OpenCashSessionDto>($"""
            SELECT s.id AS "SessionId", r.name AS "RegisterName", s.user_id AS "CashierId",
                   COALESCE(u.full_name, u.email::text) AS "CashierName", s.status AS "Status", s.opened_at AS "OpenedAt",
                   (SELECT COUNT(*)::int FROM nexora.sale x WHERE x.cash_session_id = s.id AND x.status <> 'void') AS "SaleCount",
                   c.code AS "Currency"
            FROM nexora.cash_session s
            JOIN nexora.cash_register r ON r.id = s.cash_register_id
            JOIN nexora.currency c ON c.id = r.currency_id
            LEFT JOIN nexora.app_user u ON u.id = s.user_id
            WHERE s.company_id = {Company} AND s.status = 'open' AND s.user_id <> {tenant.UserId}
            ORDER BY s.opened_at
            """).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PendingClosingDto>> PendingClosingsAsync(CancellationToken ct = default)
    {
        if (!(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Pos.ManageSessions))
            return [];
        return await db.Database.SqlQuery<PendingClosingDto>($"""
            SELECT s.id AS "SessionId", r.name AS "RegisterName", s.user_id AS "CashierId", u.full_name AS "CashierName",
                   s.opened_at AS "OpenedAt", s.counted_at AS "CountedAt", s.expected_balance AS "ExpectedBalance",
                   s.counted_balance AS "CountedBalance", s.difference AS "Difference", c.code AS "Currency", s.notes AS "Notes"
            FROM nexora.cash_session s
            JOIN nexora.cash_register r ON r.id = s.cash_register_id
            JOIN nexora.currency c ON c.id = r.currency_id
            LEFT JOIN nexora.app_user u ON u.id = s.user_id
            WHERE s.company_id = {Company} AND s.status = 'closing'
            ORDER BY s.counted_at
            """).ToListAsync(ct);
    }
}

/// <summary>Recherche produit du POS (guide §9, §24) — voir <see cref="IPosCatalogQueries"/>.</summary>
public sealed class SqlPosCatalogQueries(
    NexoraDbContext db, ITenantContext tenant, Nexora.Application.Administration.ISettingsService settings) : IPosCatalogQueries
{
    private Guid Company => SalesHelpers.Company(tenant);

    public async Task<PosProductSearchDto> SearchAsync(string term, Guid? inventoryLocationId, CancellationToken ct = default)
    {
        var minChars = (int)Math.Clamp(await NumberSettingAsync("pos.search_min_chars", 2m, ct), 1m, 10m);
        var max = (int)Math.Clamp(await NumberSettingAsync("pos.search_max_results", 8m, ct), 1m, 20m);
        term = (term ?? "").Trim();
        // Rien avant le minimum de caractères : aucune longue liste n'est chargée (§9.1).
        if (term.Length < minChars) return new PosProductSearchDto([], minChars, max);

        var rows = await db.Database.SqlQuery<PosProductDto>($"""
            SELECT p.id AS "ProductId", p.name AS "Name", p.sku AS "Sku",
                   COALESCE(p.barcode, (SELECT b.barcode FROM nexora.product_barcode b
                                        WHERE b.product_id = p.id AND b.is_primary LIMIT 1)) AS "Barcode",
                   dp.amount AS "SalePrice", dp.code AS "Currency",
                   CASE WHEN p.product_type NOT IN ('stockable', 'weight') THEN 'untracked'
                        WHEN COALESCE(sb.available, 0) <= 0 THEN 'out'
                        WHEN rr.reorder_point IS NOT NULL AND sb.available <= rr.reorder_point THEN 'low'
                        ELSE 'in_stock' END AS "Availability",
                   COALESCE((SELECT tx.rate FROM nexora.tax tx WHERE tx.id = p.default_tax_id), 0) AS "TaxRate",
                   COALESCE(dp.tax_included, false) AS "PriceIncludesTax"
            FROM nexora.product p
            JOIN nexora.company co ON co.id = p.company_id
            CROSS JOIN LATERAL (SELECT nexora.search_norm({term}) AS n) q
            LEFT JOIN LATERAL (
                SELECT pp.amount, cur.code, pp.tax_included
                FROM nexora.product_price pp JOIN nexora.currency cur ON cur.id = pp.currency_id
                WHERE pp.product_id = p.id AND pp.price_type = 'sale' AND pp.price_list_id IS NULL
                  AND pp.customer_group_id IS NULL AND pp.quantity_min = 0
                  AND pp.valid_from <= now() AND (pp.valid_to IS NULL OR pp.valid_to > now())
                ORDER BY (pp.currency_id = co.default_currency_id) DESC, pp.valid_from DESC
                LIMIT 1) dp ON true
            LEFT JOIN LATERAL (
                SELECT SUM(sb.quantity_on_hand - sb.quantity_reserved) AS available
                FROM nexora.stock_balance sb
                WHERE sb.product_id = p.id AND ({inventoryLocationId}::uuid IS NULL OR sb.inventory_location_id = {inventoryLocationId})) sb ON true
            LEFT JOIN LATERAL (
                SELECT r.reorder_point FROM nexora.product_reorder_rule r
                WHERE r.product_id = p.id AND r.status = 'active'
                ORDER BY (r.inventory_location_id = {inventoryLocationId}) DESC NULLS LAST LIMIT 1) rr ON true
            WHERE p.company_id = {Company} AND p.status = 'active' AND p.is_sellable
              AND (nexora.search_norm(p.name) LIKE '%' || q.n || '%'
                   OR nexora.search_norm(p.sku) LIKE '%' || q.n || '%'
                   OR nexora.search_norm(coalesce(p.short_name, '')) LIKE '%' || q.n || '%'
                   OR nexora.search_norm(coalesce(p.search_aliases, '')) LIKE '%' || q.n || '%'
                   OR coalesce(p.plu_code, '') = {term}
                   OR p.barcode = {term}
                   OR EXISTS (SELECT 1 FROM nexora.product_barcode b
                              WHERE b.product_id = p.id AND b.barcode = {term} AND b.status = 'active'))
            ORDER BY (p.barcode = {term} OR lower(p.sku) = lower({term}) OR coalesce(p.plu_code, '') = {term}
                      OR EXISTS (SELECT 1 FROM nexora.product_barcode b
                                 WHERE b.product_id = p.id AND b.barcode = {term} AND b.status = 'active')) DESC,
                     (nexora.search_norm(p.name) LIKE q.n || '%') DESC,
                     similarity(nexora.search_norm(p.name || ' ' || p.sku), q.n) DESC,
                     p.name
            LIMIT {max}
            """).ToListAsync(ct);
        return new PosProductSearchDto(rows, minChars, max);
    }

    public async Task<PosProductDto?> ScanAsync(string code, Guid? inventoryLocationId, CancellationToken ct = default)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0) return null;
        var hit = await LookupAsync(code, code, false, inventoryLocationId, ct);
        // Étiquette de balance : article désigné par le préfixe, quantité = poids lu (audit 2026-10, A-015).
        if (hit is not null || !Nexora.Domain.Catalog.ScaleLabel.TryDecode(code, out var prefix, out var kilograms)) return hit;
        var weighed = await LookupAsync(prefix, code, true, inventoryLocationId, ct);
        return weighed is null ? null : weighed with { PackSize = kilograms };
    }

    /// <summary>
    /// Article lu par la caisse : <paramref name="match"/> est le code cherché, le code lu en entier
    /// (<paramref name="scanned"/>) est renvoyé tel quel. <paramref name="weighed"/> : seul un code-barres
    /// d'article à poids intégré correspond (préfixe d'une étiquette de balance).
    /// </summary>
    private async Task<PosProductDto?> LookupAsync(string match, string scanned, bool weighed, Guid? inventoryLocationId,
        CancellationToken ct)
    {
        var code = match;
        return await db.Database.SqlQuery<PosProductDto>($"""
            SELECT p.id AS "ProductId", p.name AS "Name", p.sku AS "Sku", {scanned} AS "Barcode",
                   dp.amount AS "SalePrice", dp.code AS "Currency",
                   CASE WHEN p.product_type NOT IN ('stockable', 'weight') THEN 'untracked'
                        WHEN COALESCE(sb.available, 0) <= 0 THEN 'out'
                        WHEN rr.reorder_point IS NOT NULL AND sb.available <= rr.reorder_point THEN 'low'
                        ELSE 'in_stock' END AS "Availability",
                   COALESCE((SELECT tx.rate FROM nexora.tax tx WHERE tx.id = p.default_tax_id), 0) AS "TaxRate",
                   COALESCE(dp.tax_included, false) AS "PriceIncludesTax",
                   COALESCE((SELECT b.pack_size FROM nexora.product_barcode b
                             WHERE b.product_id = p.id AND b.barcode = {code} AND b.status = 'active' AND b.pack_size > 0
                             LIMIT 1), 1) AS "PackSize"
            FROM nexora.product p
            JOIN nexora.company co ON co.id = p.company_id
            LEFT JOIN LATERAL (
                SELECT pp.amount, cur.code, pp.tax_included
                FROM nexora.product_price pp JOIN nexora.currency cur ON cur.id = pp.currency_id
                WHERE pp.product_id = p.id AND pp.price_type = 'sale' AND pp.price_list_id IS NULL
                  AND pp.customer_group_id IS NULL AND pp.quantity_min = 0
                  AND pp.valid_from <= now() AND (pp.valid_to IS NULL OR pp.valid_to > now())
                ORDER BY (pp.currency_id = co.default_currency_id) DESC, pp.valid_from DESC
                LIMIT 1) dp ON true
            LEFT JOIN LATERAL (
                SELECT SUM(sb.quantity_on_hand - sb.quantity_reserved) AS available
                FROM nexora.stock_balance sb
                WHERE sb.product_id = p.id AND ({inventoryLocationId}::uuid IS NULL OR sb.inventory_location_id = {inventoryLocationId})) sb ON true
            LEFT JOIN LATERAL (
                SELECT r.reorder_point FROM nexora.product_reorder_rule r
                WHERE r.product_id = p.id AND r.status = 'active'
                ORDER BY (r.inventory_location_id = {inventoryLocationId}) DESC NULLS LAST LIMIT 1) rr ON true
            WHERE p.company_id = {Company} AND p.status = 'active' AND p.is_sellable
              AND ((NOT {weighed}
                    AND (p.barcode = {code} OR coalesce(p.plu_code, '') = {code} OR lower(p.sku) = lower({code})
                         OR EXISTS (SELECT 1 FROM nexora.product_barcode b
                                    WHERE b.product_id = p.id AND b.barcode = {code} AND b.status = 'active')))
                   OR ({weighed}
                    AND EXISTS (SELECT 1 FROM nexora.product_barcode b
                                WHERE b.product_id = p.id AND b.barcode = {code} AND b.status = 'active'
                                  AND b.is_weight_embedded)))
            ORDER BY (p.barcode = {code}) DESC
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
    }

    private async Task<decimal> NumberSettingAsync(string key, decimal fallback, CancellationToken ct)
    {
        try
        {
            var effective = await settings.ResolveAsync(key, null, ct);
            return effective.Value.ValueKind == System.Text.Json.JsonValueKind.Number ? effective.Value.GetDecimal() : fallback;
        }
        catch (InvalidOperationException)
        {
            return fallback;
        }
    }
}
