using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>
/// Remboursement d'un avoir client (§7 « diminue un avoir et génère un décaissement », §29) :
/// demandé → approuvé par une autre personne (<c>credit.refund</c>) → exécuté (sortie de trésorerie,
/// écriture Dr avoir / Cr trésorerie). Le montant ne peut jamais dépasser le disponible de l'avoir,
/// demandes en cours comprises.
/// </summary>
public sealed class SqlCustomerRefundService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IPermissionService permissions,
    IFinancialEngine engine,
    IPosApprovalNotifier notifier,
    IDateTimeProvider clock,
    ILogger<SqlCustomerRefundService> logger) : ICustomerRefundService
{
    private const string Kind = "Remboursement d'avoir";
    private Guid Company => CustomerAccountRules.Company(tenant);
    private Guid UserId => CustomerAccountRules.User(tenant);

    private sealed record Row(
        Guid Id, string RefundNumber, Guid CustomerId, string CustomerName, Guid CreditMemoId, string MemoNumber,
        string Currency, decimal Amount, string RefundMethod, Guid? FinancialAccountId, string Status, string Reason,
        Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName, DateTimeOffset? DecidedAt,
        string? DecisionNote, string? ExecutedByName, DateTimeOffset? ExecutedAt, Guid? JournalEntryId, Guid OperationUuid);

    private IQueryable<Row> Query() => db.Database.SqlQuery<Row>($"""
        SELECT r.id AS "Id", r.refund_number AS "RefundNumber", r.customer_id AS "CustomerId", c.name AS "CustomerName",
               r.credit_memo_id AS "CreditMemoId", m.memo_number AS "MemoNumber", cur.code AS "Currency", r.amount AS "Amount",
               r.refund_method AS "RefundMethod", r.financial_account_id AS "FinancialAccountId", r.status AS "Status",
               r.reason AS "Reason", r.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName",
               r.requested_at AS "RequestedAt", dc.full_name AS "DecidedByName", r.decided_at AS "DecidedAt",
               r.decision_note AS "DecisionNote", ex.full_name AS "ExecutedByName", r.executed_at AS "ExecutedAt",
               r.journal_entry_id AS "JournalEntryId", r.operation_uuid AS "OperationUuid"
        FROM nexora.customer_refund r
        JOIN nexora.customer c ON c.id = r.customer_id
        JOIN nexora.customer_credit_memo m ON m.id = r.credit_memo_id
        JOIN nexora.currency cur ON cur.id = r.currency_id
        LEFT JOIN nexora.app_user rq ON rq.id = r.requested_by
        LEFT JOIN nexora.app_user dc ON dc.id = r.decided_by
        LEFT JOIN nexora.app_user ex ON ex.id = r.executed_by
        WHERE r.company_id = {Company}
        """);

    private static CustomerRefundDto ToDto(Row r) => new(r.Id, r.RefundNumber, r.CustomerId, r.CustomerName, r.CreditMemoId,
        r.MemoNumber, r.Currency, r.Amount, r.RefundMethod, r.FinancialAccountId, r.Status, r.Reason, r.RequestedByName,
        r.RequestedAt, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.ExecutedByName, r.ExecutedAt, r.JournalEntryId);

    public async Task<CustomerRefundDto?> GetAsync(Guid refundId, CancellationToken ct = default)
        => await Query().Where(r => r.Id == refundId).FirstOrDefaultAsync(ct) is { } row ? ToDto(row) : null;

    public async Task<IReadOnlyList<CustomerRefundDto>> ListAsync(Guid? customerId, string? status, CancellationToken ct = default)
    {
        var q = Query();
        if (customerId is { } c) q = q.Where(r => r.CustomerId == c);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        return (await q.OrderByDescending(r => r.RequestedAt).Take(500).ToListAsync(ct)).Select(ToDto).ToList();
    }

    // ------------------------------------------------------------------ Demande

    private sealed record MemoRow(Guid Id, Guid CustomerId, string Status, decimal Remaining, decimal Reserved, Guid CurrencyId, string MemoNumber);

    public async Task<Result<CustomerRefundDto>> RequestAsync(RequestCustomerRefundRequest r, CancellationToken ct = default)
    {
        var existing = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.customer_refund WHERE operation_uuid = {r.OperationUuid} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (existing is { } replay) return Result.Success((await GetAsync(replay, ct))!);

        var amount = CustomerAccountRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("credit.refund_amount", "Le montant à rembourser doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("credit.refund_reason", "Le motif du remboursement est obligatoire.");
        if (!CustomerAccountRules.RefundMethods.Contains(r.RefundMethod))
            return Error.Validation("credit.refund_method", $"Mode de remboursement « {r.RefundMethod} » inconnu.");

        var id = Guid.NewGuid();
        var number = await CustomerAccountRules.NextNumberAsync(db, Company, "customer_refund", "RMB-", ct);
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var memo = await LockMemoAsync(r.CreditMemoId, token);
                var free = memo.Remaining - memo.Reserved;
                if (amount > free)
                    throw new AccountRefusal(Error.Validation("credit.refund_exceeds",
                        $"Disponible à rembourser sur l'avoir {memo.MemoNumber} : {Math.Max(0m, free)} (demandes en cours comprises)."));
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.customer_refund
                        (id, company_id, refund_number, customer_id, credit_memo_id, currency_id, amount, refund_method,
                         reason, status, operation_uuid, requested_by)
                    VALUES ({id}, {Company}, {number}, {memo.CustomerId}, {memo.Id}, {memo.CurrencyId}, {amount}, {r.RefundMethod},
                            {r.Reason.Trim()}, 'requested', {r.OperationUuid}, {UserId})
                    """, token);
                return 0;
            }, ct);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }

        var dto = (await GetAsync(id, ct))!;
        await notifier.NotifyApproversAsync(Permissions.Credit.Refund, Kind, number,
            $"{dto.CustomerName} — {amount:N2} {dto.Currency} sur l'avoir {dto.MemoNumber} ({r.RefundMethod}). Motif : {r.Reason.Trim()}",
            $"/customers/{dto.CustomerId}?tab=returns", "customer_refund", id, ct);
        return Result.Success(dto);
    }

    private async Task<MemoRow> LockMemoAsync(Guid memoId, CancellationToken ct)
    {
        var memo = await db.Database.SqlQuery<MemoRow>($"""
            SELECT m.id AS "Id", m.customer_id AS "CustomerId", m.status AS "Status", m.remaining_amount AS "Remaining",
                   COALESCE((SELECT SUM(x.amount) FROM nexora.customer_refund x
                             WHERE x.credit_memo_id = m.id AND x.status IN ('requested','approved')), 0) AS "Reserved",
                   m.currency_id AS "CurrencyId", m.memo_number AS "MemoNumber"
            FROM nexora.customer_credit_memo m
            WHERE m.id = {memoId} AND m.company_id = {Company}
            FOR UPDATE OF m
            """).FirstOrDefaultAsync(ct)
            ?? throw new AccountRefusal(Error.NotFound("credit.memo_not_found", "Avoir introuvable."));
        if (memo.Status is "cancelled" or "used")
            throw new AccountRefusal(Error.Conflict("credit.memo_unavailable", "Cet avoir n'a plus de montant disponible."));
        return memo;
    }

    // ------------------------------------------------------------------ Décision

    public async Task<Result<CustomerRefundDto>> ApproveAsync(Guid refundId, CustomerDecisionRequest decision, CancellationToken ct = default)
        => await DecideAsync(refundId, approve: true, decision.Note, ct);

    public async Task<Result<CustomerRefundDto>> RejectAsync(Guid refundId, CustomerDecisionRequest decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Note))
            return Error.Validation("credit.decision_note_required", "Indiquez le motif du refus.");
        return await DecideAsync(refundId, approve: false, decision.Note, ct);
    }

    private async Task<Result<CustomerRefundDto>> DecideAsync(Guid refundId, bool approve, string? note, CancellationToken ct)
    {
        var row = await Query().Where(r => r.Id == refundId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("credit.refund_not_found", "Remboursement introuvable.");
        if (row.Status != "requested") return Error.Conflict("credit.refund_closed", "Cette demande a déjà été traitée.");
        // §19 : personne ne valide sa propre demande (contrainte aussi portée par la base).
        if (row.RequestedBy == UserId)
            return Error.Forbidden("credit.self_approval", "Vous ne pouvez pas valider votre propre demande de remboursement.");

        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_refund
               SET status = {(approve ? "approved" : "rejected")}, decided_by = {UserId}, decided_at = now(),
                   decision_note = {note?.Trim()}
             WHERE id = {refundId} AND company_id = {Company} AND status = 'requested'
            """, ct);
        if (updated != 1) return Error.Conflict("credit.refund_closed", "Cette demande a déjà été traitée.");

        await notifier.NotifyRequesterAsync(row.RequestedBy, Kind, row.RefundNumber, approve, note?.Trim(),
            $"/customers/{row.CustomerId}?tab=returns", "customer_refund", refundId, ct);
        return Result.Success((await GetAsync(refundId, ct))!);
    }

    // ------------------------------------------------------------------ Exécution (décaissement)

    private sealed record AccountRow(Guid Id, string Status, Guid CurrencyId);
    private sealed record GlRow(string GlAccountKey);

    public async Task<Result<CustomerRefundDto>> ExecuteAsync(Guid refundId, ExecuteCustomerRefundRequest r, CancellationToken ct = default)
    {
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        if (!granted.Contains(Permissions.Credit.Refund) && !granted.Contains(Permissions.Credit.ReceivePayment))
            return Error.Forbidden("credit.refund_forbidden", "Décaisser un remboursement exige credit.refund ou credit.receive_payment.");

        var row = await Query().Where(x => x.Id == refundId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("credit.refund_not_found", "Remboursement introuvable.");
        if (row.Status != "approved")
            return Error.Conflict("credit.refund_not_approved", row.Status == "executed"
                ? "Ce remboursement a déjà été décaissé." : "Le remboursement doit d'abord être approuvé.");

        var account = await db.Database.SqlQuery<AccountRow>($"""
            SELECT id AS "Id", status AS "Status", currency_id AS "CurrencyId" FROM nexora.financial_account
            WHERE id = {r.FinancialAccountId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (account is null || account.Status != "active")
            return Error.Validation("credit.financial_account", "Compte de décaissement introuvable ou inactif.");

        Guid? sessionCashAccount = null;
        if (r.CashSessionId is { } sid)
        {
            sessionCashAccount = await db.Database.SqlQuery<Guid?>($"""
                SELECT cr.financial_account_id AS "Value"
                FROM nexora.cash_session cs JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
                WHERE cs.id = {sid} AND cs.company_id = {Company} AND cs.status = 'open'
                """).FirstOrDefaultAsync(ct);
            if (sessionCashAccount is null) return Error.Conflict("credit.cash_session_closed", "La session de caisse n'est pas ouverte.");
        }

        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var memo = await LockMemoAsync(row.CreditMemoId, token);
                if (account.CurrencyId != memo.CurrencyId)
                    throw new AccountRefusal(Error.Validation("credit.financial_account", "Le compte de décaissement n'est pas dans la devise de l'avoir."));
                if (row.Amount > memo.Remaining)
                    throw new AccountRefusal(Error.Conflict("credit.refund_exceeds",
                        $"L'avoir {memo.MemoNumber} n'a plus que {memo.Remaining} disponible (utilisé entre-temps)."));
                var gl = await db.Database.SqlQuery<GlRow>($"""
                    SELECT gl_account_key AS "GlAccountKey" FROM nexora.customer_credit_memo WHERE id = {memo.Id}
                    """).FirstAsync(token);

                var posted = await engine.HandleAsync(new CustomerCreditRefunded(
                    Company, row.OperationUuid, now, row.Id, row.CustomerId, new Money(row.Amount, row.Currency),
                    gl.GlAccountKey, r.FinancialAccountId) { UserId = tenant.UserId }, token);
                if (!posted.IsSuccess) throw new AccountRefusal(posted.Error!);

                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.customer_credit_application
                        (company_id, credit_memo_id, refund_id, currency_id, amount, journal_entry_id, applied_at, created_by)
                    VALUES ({Company}, {memo.Id}, {row.Id}, {memo.CurrencyId}, {row.Amount}, {posted.Value!.JournalEntryId}, {now}, {UserId})
                    """, token);
                var updated = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.customer_refund
                       SET status = 'executed', executed_by = {UserId}, executed_at = {now},
                           financial_account_id = {r.FinancialAccountId}, cash_session_id = {r.CashSessionId},
                           journal_entry_id = {posted.Value.JournalEntryId}
                     WHERE id = {row.Id} AND company_id = {Company} AND status = 'approved'
                    """, token);
                if (updated != 1) throw new AccountRefusal(Error.Conflict("credit.refund_closed", "Ce remboursement a été traité entre-temps."));

                if (r.CashSessionId is { } session && sessionCashAccount == r.FinancialAccountId)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.cash_movement
                            (company_id, cash_session_id, movement_type, amount, currency_id, reference_type, reference_id, note, occurred_at, user_id)
                        VALUES ({Company}, {session}, 'payout', {-row.Amount}, {memo.CurrencyId}, 'customer_refund', {row.Id},
                                {"Remboursement d'avoir " + row.RefundNumber}, {now}, {UserId})
                        """, token);
                return 0;
            }, ct);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Décaissement refusé pour le remboursement {Refund}", refundId);
            return Error.Conflict("credit.refund_failed", ex.Message);
        }

        logger.LogInformation("Remboursement {Refund} exécuté ({Amount} {Currency})", row.RefundNumber, row.Amount, row.Currency);
        return Result.Success((await GetAsync(refundId, ct))!);
    }

    public async Task<Result<CustomerRefundDto>> CancelAsync(Guid refundId, CustomerCancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("credit.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await Query().Where(x => x.Id == refundId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("credit.refund_not_found", "Remboursement introuvable.");
        if (row.RequestedBy != UserId && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Permissions.Credit.Refund))
            return Error.Forbidden("credit.refund_forbidden", "Seul le demandeur ou un approbateur peut annuler cette demande.");
        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_refund
               SET status = 'cancelled', decision_note = {"Annulée : " + r.Reason.Trim()}
             WHERE id = {refundId} AND company_id = {Company} AND status IN ('requested','approved')
            """, ct);
        if (updated != 1) return Error.Conflict("credit.refund_closed", "Seule une demande non exécutée peut être annulée.");
        return Result.Success((await GetAsync(refundId, ct))!);
    }
}
