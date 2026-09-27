using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Group;
using Nexora.Contracts.Group;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Group;

/// <summary>Transactions intercompany + rapprochement (Phase 9 §14-15).</summary>
public sealed class SqlIntercompanyService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IIntercompanyService
{
    private static readonly string[] Types = ["sale_purchase", "loan", "transfer", "cost_allocation", "dividend", "other"];

    private sealed record TxRow(
        Guid Id, Guid SourceCompanyId, string SourceCompany, Guid TargetCompanyId, string TargetCompany,
        string TransactionType, decimal Amount, string Currency, DateOnly OccurredAt, string Status, string? MatchStatus);
    private sealed record CandidateRow(Guid Id, decimal Amount, Guid CurrencyId, DateOnly OccurredAt);

    public async Task<Result<Guid>> RecordAsync(Guid groupId, RecordIntercompanyRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var type = request.TransactionType.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return Error.Validation("intercompany.type", $"Type « {type} » invalide.");
        if (request.SourceCompanyId == request.TargetCompanyId)
            return Error.Validation("intercompany.same_company", "Source et destination doivent différer.");
        if (request.Amount < 0m) return Error.Validation("intercompany.amount", "Montant négatif.");

        using var _ = tenant.BeginSystemScope();
        var bothInGroup = await db.Companies.AsNoTracking()
            .CountAsync(c => c.GroupId == groupId && (c.Id == request.SourceCompanyId || c.Id == request.TargetCompanyId), ct);
        if (bothInGroup != 2) return Error.NotFound("intercompany.company", "Les deux entreprises doivent appartenir au groupe.");

        var currencyId = await db.Currencies.AsNoTracking().Where(c => c.Code == request.Currency).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("intercompany.currency", $"Devise « {request.Currency} » inconnue.");

        var id = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(request.OccurredAt.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.intercompany_transaction
                (id, group_id, source_company_id, target_company_id, transaction_type,
                 source_reference_type, target_reference_type, amount, currency_id, status, occurred_at)
            VALUES ({id}, {groupId}, {request.SourceCompanyId}, {request.TargetCompanyId}, {type},
                    {request.SourceReference}, {request.TargetReference}, {request.Amount}, {currencyId}, 'draft',
                    {occurredAt})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> ConfirmAsync(Guid groupId, Guid transactionId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.intercompany_transaction SET status = 'confirmed', updated_at = now()
            WHERE id = {transactionId} AND group_id = {groupId} AND status = 'draft'
            """, ct);
        return n == 0 ? Error.Conflict("intercompany.not_confirmable", "Transaction introuvable ou déjà confirmée.") : Result.Success();
    }

    public async Task<IReadOnlyList<IntercompanyTransactionDto>> ListAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQuery<TxRow>($"""
            SELECT t.id AS "Id", t.source_company_id AS "SourceCompanyId", sc.name AS "SourceCompany",
                   t.target_company_id AS "TargetCompanyId", tc.name AS "TargetCompany",
                   t.transaction_type AS "TransactionType", t.amount AS "Amount", cur.code AS "Currency",
                   t.occurred_at::date AS "OccurredAt", t.status AS "Status", m.status AS "MatchStatus"
            FROM nexora.intercompany_transaction t
            JOIN nexora.company sc ON sc.id = t.source_company_id
            JOIN nexora.company tc ON tc.id = t.target_company_id
            JOIN nexora.currency cur ON cur.id = t.currency_id
            LEFT JOIN nexora.intercompany_match m ON m.source_transaction_id = t.id
            WHERE t.group_id = {groupId} ORDER BY t.occurred_at DESC
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<IntercompanyMatchDto>> ReconcileAsync(Guid groupId, ReconcileIntercompanyRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var source = await db.Database.SqlQuery<CandidateRow>($"""
            SELECT id AS "Id", amount AS "Amount", currency_id AS "CurrencyId", occurred_at::date AS "OccurredAt"
            FROM nexora.intercompany_transaction WHERE id = {request.SourceTransactionId} AND group_id = {groupId}
            """).FirstOrDefaultAsync(ct);
        if (source is null) return Error.NotFound("intercompany.source", "Transaction source introuvable.");

        var tolerance = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE((SELECT intercompany_tolerance FROM nexora.group_policy WHERE group_id = {groupId}), 0) AS "Value"
            """).FirstAsync(ct);

        CandidateRow? counterpart = null;
        if (request.CounterpartTransactionId is { } cid)
        {
            counterpart = await db.Database.SqlQuery<CandidateRow>($"""
                SELECT id AS "Id", amount AS "Amount", currency_id AS "CurrencyId", occurred_at::date AS "OccurredAt"
                FROM nexora.intercompany_transaction
                WHERE id = {cid} AND group_id = {groupId} AND id <> {request.SourceTransactionId}
                """).FirstOrDefaultAsync(ct);
            if (counterpart is null) return Error.NotFound("intercompany.counterpart", "Contrepartie introuvable.");
        }
        else
        {
            // Recherche automatique : sens inversé, montant proche, pas déjà rapproché (§15).
            counterpart = await db.Database.SqlQuery<CandidateRow>($"""
                SELECT t.id AS "Id", t.amount AS "Amount", t.currency_id AS "CurrencyId", t.occurred_at::date AS "OccurredAt"
                FROM nexora.intercompany_transaction t
                JOIN nexora.intercompany_transaction s ON s.id = {request.SourceTransactionId}
                WHERE t.group_id = {groupId} AND t.id <> s.id
                  AND t.source_company_id = s.target_company_id AND t.target_company_id = s.source_company_id
                  AND NOT EXISTS (SELECT 1 FROM nexora.intercompany_match m
                                  WHERE m.source_transaction_id = t.id OR m.counterpart_transaction_id = t.id)
                ORDER BY ABS(t.amount - s.amount), ABS(EXTRACT(EPOCH FROM (t.occurred_at - s.occurred_at)))
                LIMIT 1
                """).FirstOrDefaultAsync(ct);
        }

        string status;
        decimal diff = 0m;
        var currencyMatch = true;
        var dateDiff = 0;
        if (counterpart is null)
        {
            status = "missing_counterpart";
        }
        else
        {
            diff = Math.Round(Math.Abs(source.Amount - counterpart.Amount), 2);
            currencyMatch = source.CurrencyId == counterpart.CurrencyId;
            dateDiff = Math.Abs(source.OccurredAt.DayNumber - counterpart.OccurredAt.DayNumber);
            status = diff <= tolerance && currencyMatch ? "matched" : "asymmetric";
        }

        var matchId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.intercompany_match
                (id, group_id, source_transaction_id, counterpart_transaction_id, status,
                 amount_difference, currency_match, date_difference_days, matched_by, matched_at)
            VALUES ({matchId}, {groupId}, {request.SourceTransactionId}, {counterpart?.Id}, {status},
                    {diff}, {currencyMatch}, {dateDiff}, {tenant.UserId}, now())
            ON CONFLICT (source_transaction_id) DO UPDATE
            SET counterpart_transaction_id = EXCLUDED.counterpart_transaction_id, status = EXCLUDED.status,
                amount_difference = EXCLUDED.amount_difference, currency_match = EXCLUDED.currency_match,
                date_difference_days = EXCLUDED.date_difference_days, matched_by = EXCLUDED.matched_by,
                matched_at = now(), updated_at = now()
            """, ct);

        if (status == "matched")
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.intercompany_transaction SET status = 'reconciled', updated_at = now()
                WHERE group_id = {groupId} AND id IN ({request.SourceTransactionId}, {counterpart!.Id}) AND status <> 'eliminated'
                """, ct);
        }
        await uow.SaveChangesAsync(ct);

        return Result.Success(new IntercompanyMatchDto(matchId, request.SourceTransactionId, counterpart?.Id,
            status, diff, currencyMatch, dateDiff, null));
    }

    public async Task<IReadOnlyList<IntercompanyTransactionDto>> UnreconciledAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQuery<TxRow>($"""
            SELECT t.id AS "Id", t.source_company_id AS "SourceCompanyId", sc.name AS "SourceCompany",
                   t.target_company_id AS "TargetCompanyId", tc.name AS "TargetCompany",
                   t.transaction_type AS "TransactionType", t.amount AS "Amount", cur.code AS "Currency",
                   t.occurred_at::date AS "OccurredAt", t.status AS "Status",
                   COALESCE(m.status, 'unmatched') AS "MatchStatus"
            FROM nexora.intercompany_transaction t
            JOIN nexora.company sc ON sc.id = t.source_company_id
            JOIN nexora.company tc ON tc.id = t.target_company_id
            JOIN nexora.currency cur ON cur.id = t.currency_id
            LEFT JOIN nexora.intercompany_match m ON m.source_transaction_id = t.id
            WHERE t.group_id = {groupId} AND t.status <> 'eliminated'
              AND NOT EXISTS (
                  SELECT 1 FROM nexora.intercompany_match im
                  WHERE (im.source_transaction_id = t.id OR im.counterpart_transaction_id = t.id)
                    AND im.status = 'matched')
            ORDER BY t.occurred_at DESC
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    private static IntercompanyTransactionDto Map(TxRow r) => new(
        r.Id, r.SourceCompanyId, r.SourceCompany, r.TargetCompanyId, r.TargetCompany,
        r.TransactionType, r.Amount, r.Currency, r.OccurredAt, r.Status, r.MatchStatus ?? "unmatched");
}
