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
        string TransactionType, decimal Amount, string Currency, DateOnly OccurredAt, string Status, string? MatchStatus,
        string? SourceDocument, string? TargetDocument, Guid? CounterpartId, decimal? MatchDifference);
    private sealed record CandidateRow(
        Guid Id, decimal Amount, Guid CurrencyId, DateOnly OccurredAt, string Status, Guid SourceCompanyId, Guid TargetCompanyId);


    public async Task<Result<Guid>> RecordAsync(Guid groupId, RecordIntercompanyRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var type = request.TransactionType.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return Error.Validation("intercompany.type", $"Type « {type} » invalide.");
        if (request.SourceCompanyId == request.TargetCompanyId)
            return Error.Validation("intercompany.same_company", "Source et destination doivent différer.");
        if (request.Amount <= 0m) return Error.Validation("intercompany.amount", "Le montant doit être positif.");
        if (request.SourceReference is { Length: > 60 } || request.TargetReference is { Length: > 60 })
            return Error.Validation("intercompany.reference", "Référence de pièce : 60 caractères au plus.");

        using var _ = tenant.BeginSystemScope();
        var bothInGroup = await db.Companies.AsNoTracking()
            .CountAsync(c => c.GroupId == groupId && (c.Id == request.SourceCompanyId || c.Id == request.TargetCompanyId), ct);
        if (bothInGroup != 2) return Error.NotFound("intercompany.company", "Les deux entreprises doivent appartenir au groupe.");

        var archived = await db.Companies.AsNoTracking()
            .AnyAsync(c => (c.Id == request.SourceCompanyId || c.Id == request.TargetCompanyId) && c.Status == "archived", ct);
        if (archived) return Error.Conflict("intercompany.archived", "Une société archivée n'enregistre plus d'opérations.");

        var currencyId = await db.Currencies.AsNoTracking().Where(c => c.Code == request.Currency).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("intercompany.currency", $"Devise « {request.Currency} » inconnue.");

        var id = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(request.OccurredAt.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.intercompany_transaction
                (id, group_id, source_company_id, target_company_id, transaction_type,
                 source_document, target_document, amount, currency_id, status, occurred_at, created_by)
            VALUES ({id}, {groupId}, {request.SourceCompanyId}, {request.TargetCompanyId}, {type},
                    {Clean(request.SourceReference)}, {Clean(request.TargetReference)}, {request.Amount}, {currencyId}, 'draft',
                    {occurredAt}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> ConfirmAsync(Guid groupId, Guid transactionId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.intercompany_transaction
            SET status = 'confirmed', confirmed_by = {tenant.UserId}, confirmed_at = now(), updated_at = now()
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
            (t.occurred_at AT TIME ZONE 'UTC')::date AS "OccurredAt", t.status AS "Status", m.status AS "MatchStatus",
            t.source_document AS "SourceDocument", t.target_document AS "TargetDocument",
            CASE WHEN m.source_transaction_id = t.id THEN m.counterpart_transaction_id ELSE m.source_transaction_id END AS "CounterpartId",
            m.amount_difference AS "MatchDifference"
            FROM nexora.intercompany_transaction t
            JOIN nexora.company sc ON sc.id = t.source_company_id
            JOIN nexora.company tc ON tc.id = t.target_company_id
            JOIN nexora.currency cur ON cur.id = t.currency_id
            LEFT JOIN LATERAL (SELECT * FROM nexora.intercompany_match mm
                               WHERE mm.source_transaction_id = t.id OR mm.counterpart_transaction_id = t.id
                               ORDER BY (mm.status = 'matched') DESC, mm.matched_at DESC LIMIT 1) m ON true
            WHERE t.group_id = {groupId} ORDER BY t.occurred_at DESC, t.created_at DESC
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<IntercompanyMatchDto>> ReconcileAsync(Guid groupId, ReconcileIntercompanyRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var source = await LoadCandidateAsync(groupId, request.SourceTransactionId, ct);
        if (source is null) return Error.NotFound("intercompany.source", "Transaction source introuvable.");
        if (Unusable(source, "L'opération") is { } srcErr) return srcErr;
        // Une transaction déjà rapprochée (comme source ou comme contrepartie) serait éliminée deux fois.
        if (await IsMatchedAsync(source.Id, ct))
            return Error.Conflict("intercompany.already_matched", "Cette opération est déjà rapprochée.");

        var tolerance = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE((SELECT intercompany_tolerance FROM nexora.group_policy WHERE group_id = {groupId}), 0) AS "Value"
            """).FirstAsync(ct);

        CandidateRow? counterpart = null;
        if (request.CounterpartTransactionId is { } cid)
        {
            counterpart = cid == source.Id ? null : await LoadCandidateAsync(groupId, cid, ct);
            if (counterpart is null) return Error.NotFound("intercompany.counterpart", "Contrepartie introuvable.");
            if (Unusable(counterpart, "La contrepartie") is { } cpErr) return cpErr;
            // La contrepartie est l'écriture de l'autre société : sens inverse (B → A pour A → B).
            if (counterpart.SourceCompanyId != source.TargetCompanyId || counterpart.TargetCompanyId != source.SourceCompanyId)
                return Error.Validation("intercompany.direction",
                    "La contrepartie doit être l'opération inverse (de la société destinataire vers la société d'origine).");
            if (await IsMatchedAsync(counterpart.Id, ct))
                return Error.Conflict("intercompany.already_matched", "La contrepartie est déjà rapprochée avec une autre opération.");
        }
        else
        {
            // Recherche automatique : sens inversé, montant proche, pas déjà rapproché (§15).
            counterpart = await db.Database.SqlQuery<CandidateRow>($"""
                SELECT t.id AS "Id", t.amount AS "Amount", t.currency_id AS "CurrencyId", (t.occurred_at AT TIME ZONE 'UTC')::date AS "OccurredAt",
                       t.status AS "Status", t.source_company_id AS "SourceCompanyId", t.target_company_id AS "TargetCompanyId"
                FROM nexora.intercompany_transaction t
                JOIN nexora.intercompany_transaction s ON s.id = {request.SourceTransactionId}
                WHERE t.group_id = {groupId} AND t.id <> s.id AND t.status = 'confirmed'
                  AND t.source_company_id = s.target_company_id AND t.target_company_id = s.source_company_id
                  AND NOT EXISTS (SELECT 1 FROM nexora.intercompany_match m
                                  WHERE (m.source_transaction_id = t.id OR m.counterpart_transaction_id = t.id)
                                    AND m.status = 'matched')
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
            (t.occurred_at AT TIME ZONE 'UTC')::date AS "OccurredAt", t.status AS "Status", m.status AS "MatchStatus",
            t.source_document AS "SourceDocument", t.target_document AS "TargetDocument",
            CASE WHEN m.source_transaction_id = t.id THEN m.counterpart_transaction_id ELSE m.source_transaction_id END AS "CounterpartId",
            m.amount_difference AS "MatchDifference"
            FROM nexora.intercompany_transaction t
            JOIN nexora.company sc ON sc.id = t.source_company_id
            JOIN nexora.company tc ON tc.id = t.target_company_id
            JOIN nexora.currency cur ON cur.id = t.currency_id
            LEFT JOIN LATERAL (SELECT * FROM nexora.intercompany_match mm
                               WHERE mm.source_transaction_id = t.id OR mm.counterpart_transaction_id = t.id
                               ORDER BY mm.matched_at DESC LIMIT 1) m ON true
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
        r.TransactionType, r.Amount, r.Currency, r.OccurredAt, r.Status, r.MatchStatus ?? "unmatched",
        r.SourceDocument, r.TargetDocument, r.CounterpartId, r.MatchDifference);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private Task<CandidateRow?> LoadCandidateAsync(Guid groupId, Guid id, CancellationToken ct)
        => db.Database.SqlQuery<CandidateRow>($"""
            SELECT id AS "Id", amount AS "Amount", currency_id AS "CurrencyId", (occurred_at AT TIME ZONE 'UTC')::date AS "OccurredAt",
                   status AS "Status", source_company_id AS "SourceCompanyId", target_company_id AS "TargetCompanyId"
            FROM nexora.intercompany_transaction WHERE id = {id} AND group_id = {groupId}
            """).FirstOrDefaultAsync(ct)!;

    /// <summary>Cycle prévu au schéma : brouillon → confirmée → rapprochée → éliminée. Seule une opération
    /// confirmée se rapproche ; une opération éliminée appartient à une consolidation publiée.</summary>
    private static Error? Unusable(CandidateRow t, string what) => t.Status switch
    {
        "draft" => Error.Conflict("intercompany.not_confirmed", $"{what} est encore en brouillon : confirmez-la d'abord."),
        "eliminated" => Error.Conflict("intercompany.eliminated", $"{what} est déjà éliminée dans une consolidation publiée."),
        _ => null,
    };

    private async Task<bool> IsMatchedAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.intercompany_match
                          WHERE (source_transaction_id = {id} OR counterpart_transaction_id = {id}) AND status = 'matched') AS "Value"
            """).FirstAsync(ct);
}
