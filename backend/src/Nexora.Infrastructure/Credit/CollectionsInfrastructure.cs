using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Credit;
using Nexora.Contracts.Credit;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Credit;

public sealed class SqlCollectionsStore(NexoraDbContext db, ITenantContext tenant) : ICollectionsStore
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public Task<string?> CustomerNameAsync(Guid customerId, CancellationToken ct = default)
        => db.Database.SqlQuery<string?>($"""
            SELECT name AS "Value" FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public Task<string> CustomerCurrencyAsync(Guid customerId, CancellationToken ct = default)
        => db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.id = {customerId}
            """).FirstAsync(ct);

    public async Task<Guid> CreatePromiseAsync(Guid customerId, decimal amount, decimal snapshotRemaining, DateOnly date, string channel,
        IReadOnlyList<Guid> receivableIds, string? note, Guid? userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_promise
              (id, company_id, customer_id, amount, snapshot_remaining, promised_date, channel, receivable_ids, note, created_by)
            VALUES ({id}, {Company}, {customerId}, {amount}, {snapshotRemaining}, {date}, {channel},
                    {receivableIds.ToArray()}, {note}, {userId})
            """, ct);
        return id;
    }

    private sealed record PromiseRow(Guid CustomerId, decimal Amount, decimal SnapshotRemaining, Guid[] ReceivableIds, string Status);

    public async Task<PromiseSettleData?> GetPromiseAsync(Guid promiseId, CancellationToken ct = default)
    {
        var r = await db.Database.SqlQuery<PromiseRow>($"""
            SELECT customer_id AS "CustomerId", amount AS "Amount", snapshot_remaining AS "SnapshotRemaining",
                   receivable_ids AS "ReceivableIds", status AS "Status"
            FROM nexora.customer_promise WHERE id = {promiseId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        return r is null ? null : new PromiseSettleData(r.CustomerId, r.Amount, r.SnapshotRemaining, r.ReceivableIds, r.Status);
    }

    public async Task SettlePromiseAsync(Guid promiseId, string status, decimal keptAmount, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_promise SET status = {status}, kept_amount = {keptAmount}, settled_at = now(), updated_at = now()
            WHERE id = {promiseId} AND company_id = {Company}
            """, ct);

    public async Task<Guid> OpenDisputeAsync(Guid customerId, Guid? receivableId, decimal amount, string reason, Guid? userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_dispute
              (id, company_id, customer_id, receivable_id, disputed_amount, reason, created_by)
            VALUES ({id}, {Company}, {customerId}, {receivableId}, {amount}, {reason}, {userId})
            """, ct);
        return id;
    }

    public Task<DisputeCore?> GetDisputeAsync(Guid disputeId, CancellationToken ct = default)
        => db.Database.SqlQuery<DisputeCore>($"""
            SELECT customer_id AS "CustomerId", receivable_id AS "ReceivableId", status AS "Status"
            FROM nexora.customer_dispute WHERE id = {disputeId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public async Task ResolveDisputeAsync(Guid disputeId, string status, string resolution, Guid? userId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_dispute
               SET status = {status}, resolution = {resolution}, resolved_at = now(), resolved_by = {userId}, updated_at = now()
             WHERE id = {disputeId} AND company_id = {Company}
            """, ct);

    public async Task SetReceivableStatusAsync(Guid receivableId, string status, CancellationToken ct = default)
        => await db.Receivables.Where(r => r.Id == receivableId)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, status), ct);

    public Task<ReceivableCore?> GetReceivableAsync(Guid receivableId, CancellationToken ct = default)
        => db.Database.SqlQuery<ReceivableCore>($"""
            SELECT r.customer_id AS "CustomerId", cur.code AS "Currency", r.original_amount AS "OriginalAmount",
                   r.paid_amount AS "PaidAmount", r.remaining_amount AS "RemainingAmount", r.status AS "Status"
            FROM nexora.customer_receivable r JOIN nexora.currency cur ON cur.id = r.currency_id
            WHERE r.id = {receivableId} AND r.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public Task<bool> ActiveBlockExistsAsync(Guid customerId, string blockType, CancellationToken ct = default)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.customer_block
              WHERE company_id = {Company} AND customer_id = {customerId} AND block_type = {blockType} AND released_at IS NULL) AS "Value"
            """).FirstAsync(ct);

    public async Task<Guid> CreateBlockAsync(Guid customerId, string blockType, string reason, Guid? userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_block (id, company_id, customer_id, block_type, reason, blocked_by)
            VALUES ({id}, {Company}, {customerId}, {blockType}, {reason}, {userId})
            """, ct);
        return id;
    }

    public Task<BlockCore?> GetBlockAsync(Guid blockId, CancellationToken ct = default)
        => db.Database.SqlQuery<BlockCore>($"""
            SELECT customer_id AS "CustomerId", block_type AS "BlockType", is_active AS "IsActive"
            FROM nexora.customer_block WHERE id = {blockId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public async Task ReleaseBlockAsync(Guid blockId, string reason, Guid? userId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_block
               SET released_at = now(), released_by = {userId}, release_reason = {reason}, updated_at = now()
             WHERE id = {blockId} AND company_id = {Company}
            """, ct);

    public Task<int> ActiveBlockCountAsync(Guid customerId, CancellationToken ct = default)
        => db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.customer_block
            WHERE company_id = {Company} AND customer_id = {customerId} AND released_at IS NULL
              AND block_type IN ('credit','sales','account')
            """).FirstAsync(ct);

    public async Task SetCustomerStatusAsync(Guid customerId, string status, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer SET status = {status}, updated_at = now()
            WHERE id = {customerId} AND company_id = {Company}
            """, ct);

    public async Task<Guid> CreateWriteOffAsync(Guid receivableId, Guid customerId, decimal amount, string reason,
        Guid journalEntryId, Guid? userId, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.receivable_write_off
              (id, company_id, receivable_id, customer_id, amount, reason, journal_entry_id, approved_by, created_by)
            VALUES ({id}, {Company}, {receivableId}, {customerId}, {amount}, {reason}, {journalEntryId}, {userId}, {userId})
            """, ct);
        return id;
    }

    public async Task ApplyWriteOffToReceivableAsync(Guid receivableId, decimal amount, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_receivable
               SET paid_amount = paid_amount + {amount}, status = 'WRITTEN_OFF', updated_at = now()
             WHERE id = {receivableId} AND company_id = {Company}
            """, ct);
}

/// <summary>Lectures crédit & recouvrement (Phase 27 §15, §29, §35).</summary>
public sealed class SqlCreditPortfolioQueries(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock) : ICreditPortfolioQueries
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<IReadOnlyList<CollectionsRowDto>> CollectionsAsync(bool onlyOverdue, CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        return await db.Database.SqlQuery<CollectionsRowDto>($"""
            SELECT c.id AS "CustomerId", c.name AS "CustomerName", cur.code AS "Currency",
                   COALESCE(SUM(r.remaining_amount), 0) AS "TotalOutstanding",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date IS NULL OR r.due_date >= {today}), 0) AS "Current",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date BETWEEN {today.AddDays(-30)} AND {today.AddDays(-1)}), 0) AS "Bucket1To30",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date BETWEEN {today.AddDays(-60)} AND {today.AddDays(-31)}), 0) AS "Bucket31To60",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date BETWEEN {today.AddDays(-90)} AND {today.AddDays(-61)}), 0) AS "Bucket61To90",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date < {today.AddDays(-90)}), 0) AS "Bucket90Plus",
                   COALESCE((SELECT SUM(d.disputed_amount) FROM nexora.customer_dispute d
                             WHERE d.customer_id = c.id AND d.status IN ('open','under_review')), 0) AS "DisputedAmount",
                   COALESCE((SELECT COUNT(*)::int FROM nexora.customer_promise p
                             WHERE p.customer_id = c.id AND p.status = 'open'), 0) AS "OpenPromises",
                   COALESCE((SELECT COUNT(*)::int FROM nexora.customer_dispute d
                             WHERE d.customer_id = c.id AND d.status IN ('open','under_review')), 0) AS "OpenDisputes",
                   (c.status = 'blocked') AS "Blocked",
                   COALESCE(MAX(({today} - r.due_date))::int, 0) AS "OldestOverdueDays"
            FROM nexora.customer c
            JOIN nexora.currency cur ON cur.id = c.default_currency_id
            LEFT JOIN nexora.customer_receivable r ON r.customer_id = c.id
                 AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
            WHERE c.company_id = {Company}
            GROUP BY c.id, c.name, cur.code, c.status
            HAVING (NOT {onlyOverdue} OR COALESCE(MAX(({today} - r.due_date)), -1) > 0)
                   AND COALESCE(SUM(r.remaining_amount), 0) > 0
            ORDER BY "OldestOverdueDays" DESC, "TotalOutstanding" DESC
            """).ToListAsync(ct);
    }

    public async Task<AgingSummaryDto> AgingSummaryAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var currency = await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.currency cur
            JOIN nexora.company co ON co.default_currency_id = cur.id WHERE co.id = {Company}
            """).FirstOrDefaultAsync(ct) ?? "HTG";

        var a = await db.Database.SqlQuery<AgingRow>($"""
            SELECT COALESCE(SUM(remaining_amount), 0) AS "Total",
                   COALESCE(SUM(remaining_amount) FILTER (WHERE due_date IS NULL OR due_date >= {today}), 0) AS "Current",
                   COALESCE(SUM(remaining_amount) FILTER (WHERE due_date BETWEEN {today.AddDays(-30)} AND {today.AddDays(-1)}), 0) AS "B1",
                   COALESCE(SUM(remaining_amount) FILTER (WHERE due_date BETWEEN {today.AddDays(-60)} AND {today.AddDays(-31)}), 0) AS "B2",
                   COALESCE(SUM(remaining_amount) FILTER (WHERE due_date BETWEEN {today.AddDays(-90)} AND {today.AddDays(-61)}), 0) AS "B3",
                   COALESCE(SUM(remaining_amount) FILTER (WHERE due_date < {today.AddDays(-90)}), 0) AS "B4"
            FROM nexora.customer_receivable
            WHERE company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
            """).FirstAsync(ct);

        // DSO ≈ créances / (ventes des 90 derniers jours / 90) — si les ventes sont suffisantes (§30).
        var sales90 = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(total), 0) AS "Value" FROM nexora.sale
            WHERE company_id = {Company} AND status = 'completed' AND sold_at >= now() - interval '90 days'
            """).FirstAsync(ct);
        decimal? dso = sales90 > 0m ? Math.Round(a.Total / (sales90 / 90m), 1) : null;

        return new AgingSummaryDto(currency, a.Total, a.Current, a.B1, a.B2, a.B3, a.B4,
            a.B1 + a.B2 + a.B3 + a.B4, dso);
    }

    private sealed record AgingRow(decimal Total, decimal Current, decimal B1, decimal B2, decimal B3, decimal B4);
    private sealed record StmtRow(DateOnly Date, string DocumentType, string DocumentNumber, string? Reference, decimal Charge, decimal Payment);

    public async Task<CustomerStatementDto?> CustomerStatementAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var name = await db.Database.SqlQuery<string>($"""
            SELECT name AS "Value" FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (name is null) return null;
        var currency = await db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id WHERE c.id = {customerId}
            """).FirstAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        // Module clients §7 : le relevé lit le journal financier client (crédits, versements, annulations,
        // retours/avoirs, radiations, remboursements). Avant, il sommait toutes les ventes « crédit/mixte »
        // au total ET tous les paiements du client, comptant compris : un client payant comptant
        // apparaissait créditeur, et un retour sur crédit n'y figurait pas.
        var rows = await db.Database.SqlQuery<StmtRow>($"""
            SELECT l.occurred_at::date AS "Date", l.entry_type AS "DocumentType", coalesce(l.reference, '—') AS "DocumentNumber",
                   l.description AS "Reference", l.debit AS "Charge", l.credit AS "Payment"
            FROM nexora.customer_ledger_entry l
            WHERE l.company_id = {Company} AND l.customer_id = {customerId}
            ORDER BY l.occurred_at, l.created_at
            """).ToListAsync(ct);

        decimal opening = 0m, running = 0m;
        var lines = new List<CustomerStatementLineDto>();
        foreach (var r in rows)
        {
            if (from is { } f && r.Date < f) { opening += r.Charge - r.Payment; running = opening; continue; }
            if (to is { } t && r.Date > t) continue;
            running += r.Charge - r.Payment;
            lines.Add(new CustomerStatementLineDto(r.Date, r.DocumentType, r.DocumentNumber, r.Reference,
                r.Charge, r.Payment, Math.Round(running, 2)));
        }

        var overdue = await db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(remaining_amount), 0) AS "Value" FROM nexora.customer_receivable
            WHERE company_id = {Company} AND customer_id = {customerId}
              AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE') AND due_date < {today}
            """).FirstAsync(ct);

        return new CustomerStatementDto(customerId, name, currency, Math.Round(opening, 2), Math.Round(running, 2),
            Math.Round(overdue, 2), lines);
    }

    public async Task<IReadOnlyList<PromiseDto>> PromisesAsync(Guid? customerId, string? status, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<PromiseListRow>($"""
            SELECT p.id AS "Id", p.customer_id AS "CustomerId", c.name AS "CustomerName", p.amount AS "Amount",
                   p.kept_amount AS "KeptAmount", p.promised_date AS "PromisedDate", p.channel AS "Channel",
                   p.status AS "Status", p.note AS "Note", p.receivable_ids AS "ReceivableIds", p.created_at AS "CreatedAt"
            FROM nexora.customer_promise p JOIN nexora.customer c ON c.id = p.customer_id
            WHERE p.company_id = {Company}
              AND ({customerId}::uuid IS NULL OR p.customer_id = {customerId})
              AND ({status}::text IS NULL OR p.status = {status})
            ORDER BY p.promised_date
            """).ToListAsync(ct);
        return rows.Select(r => new PromiseDto(r.Id, r.CustomerId, r.CustomerName, r.Amount, r.KeptAmount,
            r.PromisedDate, r.Channel, r.Status, r.Note, r.ReceivableIds, r.CreatedAt)).ToList();
    }

    public async Task<IReadOnlyList<DisputeDto>> DisputesAsync(Guid? customerId, string? status, CancellationToken ct = default)
        => await db.Database.SqlQuery<DisputeDto>($"""
            SELECT d.id AS "Id", d.customer_id AS "CustomerId", c.name AS "CustomerName", d.receivable_id AS "ReceivableId",
                   d.disputed_amount AS "DisputedAmount", d.reason AS "Reason", d.status AS "Status",
                   d.resolution AS "Resolution", d.created_at AS "CreatedAt", d.resolved_at AS "ResolvedAt"
            FROM nexora.customer_dispute d JOIN nexora.customer c ON c.id = d.customer_id
            WHERE d.company_id = {Company}
              AND ({customerId}::uuid IS NULL OR d.customer_id = {customerId})
              AND ({status}::text IS NULL OR d.status = {status})
            ORDER BY d.created_at DESC
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<CustomerBlockDto>> BlocksAsync(Guid? customerId, bool onlyActive, CancellationToken ct = default)
        => await db.Database.SqlQuery<CustomerBlockDto>($"""
            SELECT b.id AS "Id", b.customer_id AS "CustomerId", c.name AS "CustomerName", b.block_type AS "BlockType",
                   b.reason AS "Reason", b.is_active AS "IsActive", b.blocked_at AS "BlockedAt",
                   b.released_at AS "ReleasedAt", b.release_reason AS "ReleaseReason"
            FROM nexora.customer_block b JOIN nexora.customer c ON c.id = b.customer_id
            WHERE b.company_id = {Company}
              AND ({customerId}::uuid IS NULL OR b.customer_id = {customerId})
              AND (NOT {onlyActive} OR b.released_at IS NULL)
            ORDER BY b.blocked_at DESC
            """).ToListAsync(ct);

    private sealed record PromiseListRow(
        Guid Id, Guid CustomerId, string CustomerName, decimal Amount, decimal KeptAmount,
        DateOnly PromisedDate, string Channel, string Status, string? Note, Guid[] ReceivableIds, DateTimeOffset CreatedAt);
}
