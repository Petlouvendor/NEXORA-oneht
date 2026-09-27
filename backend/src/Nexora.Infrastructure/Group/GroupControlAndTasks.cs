using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Group;
using Nexora.Contracts.Group;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Group;

/// <summary>Centre de contrôle du comptable en chef / cabinet (Phase 9 §23-24).</summary>
public sealed class SqlGroupControlCenter(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock) : IGroupControlCenter
{
    private sealed record CompanyStatusRow(
        Guid CompanyId, string CompanyName, string? Sector, string Status,
        string? LatestPeriod, string? LatestPeriodStatus, int UnmappedAccounts,
        int UnreconciledIntercompany, decimal Receivables, decimal Payables);

    public async Task<GroupControlCenterDto> SummaryAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var currency = await db.Database.SqlQuery<string>($"""
            SELECT COALESCE(
                (SELECT cur.code FROM nexora.group_policy gp JOIN nexora.currency cur ON cur.id = gp.consolidation_currency_id WHERE gp.group_id = {groupId}),
                (SELECT cur.code FROM nexora.business_group g JOIN nexora.currency cur ON cur.id = g.default_currency_id WHERE g.id = {groupId})) AS "Value"
            """).FirstAsync(ct);

        var rows = await db.Database.SqlQuery<CompanyStatusRow>($"""
            SELECT c.id AS "CompanyId", c.name AS "CompanyName", c.main_activity_type AS "Sector", c.status AS "Status",
                   p.name AS "LatestPeriod", p.status AS "LatestPeriodStatus",
                   COALESCE(um.cnt, 0) AS "UnmappedAccounts",
                   COALESCE(ic.cnt, 0) AS "UnreconciledIntercompany",
                   COALESCE(ar.total, 0) AS "Receivables",
                   COALESCE(ap.total, 0) AS "Payables"
            FROM nexora.company c
            LEFT JOIN LATERAL (
                SELECT name, status FROM nexora.accounting_period
                WHERE company_id = c.id ORDER BY end_date DESC LIMIT 1) p ON true
            LEFT JOIN LATERAL (
                SELECT COUNT(DISTINCT l.account_id)::int cnt
                FROM nexora.journal_entry_line l
                JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                LEFT JOIN nexora.account_group_mapping m ON m.account_id = l.account_id AND m.status = 'active'
                WHERE e.company_id = c.id AND e.status = 'posted' AND m.id IS NULL) um ON true
            LEFT JOIN LATERAL (
                SELECT COUNT(*)::int cnt FROM nexora.intercompany_transaction t
                WHERE t.group_id = {groupId} AND (t.source_company_id = c.id OR t.target_company_id = c.id)
                  AND t.status <> 'eliminated'
                  AND NOT EXISTS (SELECT 1 FROM nexora.intercompany_match mm
                                  WHERE (mm.source_transaction_id = t.id OR mm.counterpart_transaction_id = t.id)
                                    AND mm.status = 'matched')) ic ON true
            LEFT JOIN LATERAL (
                SELECT COALESCE(SUM(original_amount - paid_amount), 0) total FROM nexora.customer_receivable
                WHERE company_id = c.id AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')) ar ON true
            LEFT JOIN LATERAL (
                SELECT COALESCE(SUM(original_amount - paid_amount), 0) total FROM nexora.supplier_payable
                WHERE company_id = c.id AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE')) ap ON true
            WHERE c.group_id = {groupId}
            ORDER BY c.name
            """).ToListAsync(ct);

        var runsReady = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.consolidation_run
            WHERE group_id = {groupId} AND status = 'ready' AND coherence_ok
            """).FirstAsync(ct);
        var openTasks = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.group_task
            WHERE group_id = {groupId} AND status IN ('open','in_progress','blocked')
            """).FirstAsync(ct);
        var unreconciled = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.intercompany_transaction t
            WHERE t.group_id = {groupId} AND t.status <> 'eliminated'
              AND NOT EXISTS (SELECT 1 FROM nexora.intercompany_match m
                              WHERE (m.source_transaction_id = t.id OR m.counterpart_transaction_id = t.id)
                                AND m.status = 'matched')
            """).FirstAsync(ct);

        return new GroupControlCenterDto(groupId, currency, clock.UtcNow,
            rows.Select(r => new GroupCompanyStatusDto(
                r.CompanyId, r.CompanyName, r.Sector, r.Status,
                r.LatestPeriod ?? "—", r.LatestPeriodStatus ?? "—",
                r.UnmappedAccounts, r.UnreconciledIntercompany,
                Math.Round(r.Receivables, 2), Math.Round(r.Payables, 2))).ToList(),
            unreconciled, runsReady, openTasks,
            Math.Round(rows.Sum(r => r.Receivables), 2), Math.Round(rows.Sum(r => r.Payables), 2));
    }
}

/// <summary>Tâches de contrôle groupe (Phase 9 §25).</summary>
public sealed class SqlGroupTaskService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IGroupTaskService
{
    private static readonly string[] Priorities = ["low", "normal", "high", "urgent"];
    private static readonly string[] Statuses = ["open", "in_progress", "blocked", "done", "cancelled"];
    private sealed record TaskRow(
        Guid Id, Guid? CompanyId, string? CompanyName, string Title, string? Description,
        Guid? AssigneeUserId, string Priority, DateOnly? DueDate, string Status, string? Notes);

    public async Task<Result<Guid>> CreateAsync(Guid groupId, CreateGroupTaskRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        if (string.IsNullOrWhiteSpace(request.Title)) return Error.Validation("group.task_title", "Titre requis.");
        var priority = request.Priority.Trim().ToLowerInvariant();
        if (!Priorities.Contains(priority)) return Error.Validation("group.task_priority", "Priorité invalide.");
        if (request.CompanyId is { } cid)
        {
            using var _ = tenant.BeginSystemScope();
            if (!await db.Companies.AsNoTracking().AnyAsync(c => c.Id == cid && c.GroupId == groupId, ct))
                return Error.NotFound("group.task_company", "Entreprise hors du groupe.");
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.group_task
                (id, group_id, company_id, title, description, assignee_user_id, priority, due_date, status, created_by)
            VALUES ({id}, {groupId}, {request.CompanyId}, {request.Title.Trim()}, {request.Description},
                    {request.AssigneeUserId}, {priority}, {request.DueDate}, 'open', {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<GroupTaskDto>> ListAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();
        var rows = await db.Database.SqlQuery<TaskRow>($"""
            SELECT t.id AS "Id", t.company_id AS "CompanyId", c.name AS "CompanyName", t.title AS "Title",
                   t.description AS "Description", t.assignee_user_id AS "AssigneeUserId", t.priority AS "Priority",
                   t.due_date AS "DueDate", t.status AS "Status", t.notes AS "Notes"
            FROM nexora.group_task t LEFT JOIN nexora.company c ON c.id = t.company_id
            WHERE t.group_id = {groupId}
            ORDER BY CASE t.priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'normal' THEN 2 ELSE 3 END, t.due_date NULLS LAST
            """).ToListAsync(ct);
        return rows.Select(r => new GroupTaskDto(r.Id, r.CompanyId, r.CompanyName, r.Title, r.Description,
            r.AssigneeUserId, r.Priority, r.DueDate, r.Status, r.Notes)).ToList();
    }

    public async Task<Result> UpdateAsync(Guid groupId, Guid taskId, UpdateGroupTaskRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var status = request.Status.Trim().ToLowerInvariant();
        if (!Statuses.Contains(status)) return Error.Validation("group.task_status", "Statut invalide.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.group_task SET status = {status}, notes = COALESCE({request.Notes}, notes), updated_at = now()
            WHERE id = {taskId} AND group_id = {groupId}
            """, ct);
        return n == 0 ? Error.NotFound("group.task", "Tâche introuvable.") : Result.Success();
    }
}

/// <summary>Ajout / archivage d'entreprise sans nouvelle installation (Phase 9 §28-29).</summary>
public sealed class SqlCompanyProvisioningService(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : ICompanyProvisioningService
{
    internal static readonly string[] Activities =
    [
        "supermarket","grocery","pharmacy","restaurant","boutique","hotel","hardware_store",
        "wholesaler","distributor","services","production","ecommerce","agriculture","other",
    ];

    public async Task<Result<Guid>> ProvisionAsync(ProvisionCompanyRequest request, CancellationToken ct = default)
    {
        var activity = request.ActivityType.Trim().ToLowerInvariant();
        if (!Activities.Contains(activity)) return Error.Validation("company.activity", $"Activité « {activity} » inconnue.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("company.name", "Nom requis.");
        if (request.GroupId is { } gid) GroupGuard.Require(tenant, gid);

        var currencyId = await db.Currencies.AsNoTracking().Where(c => c.Code == request.Currency).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("company.currency", $"Devise « {request.Currency} » inconnue.");

        var companyId = Guid.NewGuid();
        var year = DateOnly.FromDateTime(DateTime.UtcNow).Year;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            using var scope = tenant.BeginSystemScope();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.company (id, group_id, name, main_activity_type, default_currency_id, complexity_level, deployment_mode)
                VALUES ({companyId}, {request.GroupId}, {request.Name.Trim()}, {activity}, {currencyId}, 'STANDARD', 'HYBRID')
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.company_activity (company_id, activity_type, is_primary) VALUES ({companyId}, {activity}, true)
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.company_module (company_id, module_key)
                SELECT {companyId}, module_key FROM nexora.sector_module_preset WHERE activity_type = {activity}
                ON CONFLICT DO NOTHING
                """, token);

            // Référentiel prêt à l'emploi (résumé fonctionnel §20, V072) : plan comptable + correspondances,
            // journaux, exercice, caisse/banque, moyens de paiement, succursale, point de vente, caisse.
            await db.Database.ExecuteSqlAsync($"""
                SELECT nexora.bootstrap_company_defaults({companyId}, {currencyId}, {year})
                """, token);
            return 0;
        }, ct);

        return Result.Success(companyId);
    }

    public async Task<Result> ArchiveAsync(Guid companyId, ArchiveCompanyRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Error.Validation("company.archive_reason", "Un motif d'archivage est obligatoire (§29).");

        using var _ = tenant.BeginSystemScope();
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == companyId, ct);
        if (company is null) return Error.NotFound("company.not_found", "Entreprise introuvable.");
        if (company.GroupId is { } gid && !tenant.BypassRls) GroupGuard.Require(tenant, gid);

        // Archivage : désactive les nouvelles opérations, conserve tout l'historique (§29).
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.company SET status = 'archived', archived_at = now(), archived_by = {tenant.UserId}, updated_at = now()
            WHERE id = {companyId}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.company_module SET enabled = false, disabled_at = now(), updated_at = now()
            WHERE company_id = {companyId} AND enabled
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
