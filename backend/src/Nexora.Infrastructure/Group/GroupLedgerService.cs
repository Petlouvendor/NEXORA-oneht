using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Group;
using Nexora.Contracts.Group;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Group;

internal static class GroupGuard
{
    public static void Require(ITenantContext tenant, Guid groupId)
    {
        if (!tenant.BypassRls && !tenant.GroupIds.Contains(groupId))
            throw new DomainException(Error.Forbidden("group.scope",
                "Groupe hors du périmètre autorisé. Activez la vue groupe (X-Group-Scope) avec un périmètre habilité (§21-22)."));
    }
}

/// <summary>Plan comptable groupe + mappings versionnés (Phase 9 §12).</summary>
public sealed class SqlGroupLedgerService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IGroupLedgerService
{
    private static readonly string[] Types = ["asset", "liability", "equity", "revenue", "expense"];
    private static readonly string[] FxMethods = ["closing_rate", "average_rate", "historical"];

    private sealed record AccountRow(Guid Id, string Code, string Name, string AccountType, bool IsEliminable, int SortOrder, string Status);
    private sealed record CoverageRow(Guid CompanyId, string CompanyName, int Mapped, int UnmappedWithActivity, string? UnmappedCodes);
    private sealed record PolicyRow(string ConsolidationCurrency, string FxMethod, decimal IntercompanyTolerance);

    public async Task<IReadOnlyList<GroupAccountDto>> ListAccountsAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var rows = await db.Database.SqlQuery<AccountRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", account_type AS "AccountType",
                   is_eliminable AS "IsEliminable", sort_order AS "SortOrder", status AS "Status"
            FROM nexora.group_account WHERE group_id = {groupId} ORDER BY sort_order, code
            """).ToListAsync(ct);
        return rows.Select(r => new GroupAccountDto(r.Id, r.Code, r.Name, r.AccountType, r.IsEliminable, r.SortOrder, r.Status)).ToList();
    }

    public async Task<Result<Guid>> UpsertAccountAsync(Guid groupId, UpsertGroupAccountRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var type = request.AccountType.Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return Error.Validation("group.account_type", $"Type de compte « {type} » invalide.");
        if (string.IsNullOrWhiteSpace(request.Code)) return Error.Validation("group.account_code", "Code requis.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.group_account (id, group_id, code, name, account_type, is_eliminable, sort_order)
            VALUES ({id}, {groupId}, {request.Code.Trim()}, {request.Name.Trim()}, {type}, {request.IsEliminable}, {request.SortOrder})
            ON CONFLICT (group_id, code) DO UPDATE SET name = EXCLUDED.name, account_type = EXCLUDED.account_type,
                is_eliminable = EXCLUDED.is_eliminable, sort_order = EXCLUDED.sort_order, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var actual = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.group_account WHERE group_id = {groupId} AND code = {request.Code.Trim()}
            """).FirstAsync(ct);
        return Result.Success(actual);
    }

    public async Task<Result> SetMappingAsync(Guid groupId, SetMappingRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var inGroup = await db.Companies.AsNoTracking().AnyAsync(c => c.Id == request.CompanyId && c.GroupId == groupId, ct);
        if (!inGroup) return Error.NotFound("group.company", "Entreprise hors du groupe.");

        var accountId = await db.Database.SqlQuery<Guid?>($"""
            SELECT a.id AS "Value" FROM nexora.account a
            JOIN nexora.chart_of_accounts coa ON coa.id = a.chart_of_accounts_id
            WHERE a.company_id = {request.CompanyId} AND a.code = {request.LocalAccountCode}
            ORDER BY (coa.status = 'active') DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (accountId is null) return Error.NotFound("group.local_account", $"Compte local « {request.LocalAccountCode} » introuvable.");

        var groupAccountId = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.group_account WHERE group_id = {groupId} AND code = {request.GroupAccountCode}
            """).FirstOrDefaultAsync(ct);
        if (groupAccountId is null) return Error.NotFound("group.group_account", $"Compte groupe « {request.GroupAccountCode} » introuvable.");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            // Versionnement : l'ancienne cartographie active est superseded et datée (§12).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.account_group_mapping
                SET status = 'superseded', effective_to = current_date, updated_at = now()
                WHERE account_id = {accountId} AND status = 'active'
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.account_group_mapping
                    (group_id, company_id, account_id, group_account_id, effective_from, status, created_by)
                VALUES ({groupId}, {request.CompanyId}, {accountId}, {groupAccountId}, current_date, 'active', {tenant.UserId})
                """, token);
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<MappingCoverageRow>> MappingCoverageAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var rows = await db.Database.SqlQuery<CoverageRow>($"""
            SELECT c.id AS "CompanyId", c.name AS "CompanyName",
                   COUNT(*) FILTER (WHERE m.id IS NOT NULL)::int AS "Mapped",
                   COUNT(*) FILTER (WHERE m.id IS NULL AND act.has_activity)::int AS "UnmappedWithActivity",
                   string_agg(a.code, ',') FILTER (WHERE m.id IS NULL AND act.has_activity) AS "UnmappedCodes"
            FROM nexora.company c
            JOIN nexora.account a ON a.company_id = c.id
            LEFT JOIN nexora.account_group_mapping m ON m.account_id = a.id AND m.status = 'active'
            LEFT JOIN LATERAL (
                SELECT EXISTS(
                    SELECT 1 FROM nexora.journal_entry_line l
                    JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                    WHERE l.account_id = a.id AND e.status = 'posted') AS has_activity
            ) act ON true
            WHERE c.group_id = {groupId} AND a.status = 'active'
            GROUP BY c.id, c.name ORDER BY c.name
            """).ToListAsync(ct);

        return rows.Select(r => new MappingCoverageRow(r.CompanyId, r.CompanyName, r.Mapped, r.UnmappedWithActivity,
            (r.UnmappedCodes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))).ToList();
    }

    public async Task<GroupPolicyDto> GetPolicyAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var p = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT cur.code AS "ConsolidationCurrency", gp.fx_method AS "FxMethod",
                   gp.intercompany_tolerance AS "IntercompanyTolerance"
            FROM nexora.group_policy gp JOIN nexora.currency cur ON cur.id = gp.consolidation_currency_id
            WHERE gp.group_id = {groupId}
            """).FirstOrDefaultAsync(ct);
        if (p is null)
        {
            var code = await db.Database.SqlQuery<string>($"""
                SELECT cur.code AS "Value" FROM nexora.business_group g
                JOIN nexora.currency cur ON cur.id = g.default_currency_id WHERE g.id = {groupId}
                """).FirstAsync(ct);
            return new GroupPolicyDto(groupId, code, "closing_rate", 0m);
        }
        return new GroupPolicyDto(groupId, p.ConsolidationCurrency, p.FxMethod, p.IntercompanyTolerance);
    }

    public async Task<Result> SetPolicyAsync(Guid groupId, SetGroupPolicyRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        Guid? currencyId = null;
        if (request.ConsolidationCurrency is { } code)
        {
            currencyId = await db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            if (currencyId is null) return Error.Validation("group.currency", $"Devise « {code} » inconnue.");
        }
        var method = request.FxMethod?.Trim().ToLowerInvariant();
        if (method is not null && !FxMethods.Contains(method))
            return Error.Validation("group.fx_method", "Méthode de conversion invalide.");
        if (request.IntercompanyTolerance is < 0m)
            return Error.Validation("group.tolerance", "La tolérance ne peut être négative.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.group_policy SET
                consolidation_currency_id = COALESCE({currencyId}, consolidation_currency_id),
                fx_method = COALESCE({method}, fx_method),
                intercompany_tolerance = COALESCE({request.IntercompanyTolerance}, intercompany_tolerance),
                updated_at = now()
            WHERE group_id = {groupId}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
