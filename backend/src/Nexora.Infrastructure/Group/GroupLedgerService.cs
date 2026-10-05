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
    /// <summary>Seul le taux de clôture est appliqué par la consolidation : les autres méthodes sont refusées
    /// plutôt qu'enregistrées sans effet (finalisation phase 14).</summary>
    internal static readonly string[] SupportedFxMethods = ["closing_rate"];

    private sealed record AccountRow(Guid Id, string Code, string Name, string AccountType, bool IsEliminable, int SortOrder, string Status);
    private sealed record CoverageRow(Guid CompanyId, string CompanyName, int Mapped, int UnmappedWithActivity, string? UnmappedCodes);
    private sealed record PolicyRow(string ConsolidationCurrency, string FxMethod, decimal IntercompanyTolerance);
    private sealed record MappingRow(Guid AccountId, string Code, string Name, string AccountType, bool HasActivity,
        string? GroupAccountCode, string? GroupAccountName, DateOnly? EffectiveFrom, int Versions);
    private sealed record CompanyOptionRow(Guid Id, string Name, string Status, string Currency, string? Sector, DateTimeOffset? ArchivedAt, string? ArchiveReason);
    private sealed record UserOptionRow(Guid Id, string Name, string Email);
    private sealed record TypedRow(Guid Id, string AccountType);

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
        var type = (request.AccountType ?? "").Trim().ToLowerInvariant();
        if (!Types.Contains(type)) return Error.Validation("group.account_type", $"Type de compte « {type} » invalide.");
        var code = (request.Code ?? "").Trim();
        if (code.Length is 0 or > 20) return Error.Validation("group.account_code", "Code requis (20 caractères au plus).");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 120)
            return Error.Validation("group.account_name", "Libellé requis (120 caractères au plus).");
        if (request.SortOrder is < 0 or > 9999) return Error.Validation("group.sort_order", "Ordre entre 0 et 9999.");

        // Changer le type d'un compte groupe déjà alimenté fausserait les états : les comptes locaux
        // qui y correspondent sont d'un autre type (finalisation phase 14).
        using (tenant.BeginSystemScope())
        {
            var conflicting = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM nexora.account_group_mapping m
                JOIN nexora.group_account ga ON ga.id = m.group_account_id
                JOIN nexora.account a ON a.id = m.account_id
                WHERE ga.group_id = {groupId} AND ga.code = {code} AND m.status = 'active' AND a.account_type <> {type}
                """).FirstAsync(ct);
            if (conflicting > 0)
                return Error.Conflict("group.account_type_in_use",
                    $"{conflicting} compte(s) local(aux) d'un autre type correspondent à ce compte : changez d'abord leurs correspondances.");
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.group_account (id, group_id, code, name, account_type, is_eliminable, sort_order)
            VALUES ({id}, {groupId}, {code}, {request.Name.Trim()}, {type}, {request.IsEliminable}, {request.SortOrder})
            ON CONFLICT (group_id, code) DO UPDATE SET name = EXCLUDED.name, account_type = EXCLUDED.account_type,
                is_eliminable = EXCLUDED.is_eliminable, sort_order = EXCLUDED.sort_order, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var actual = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.group_account WHERE group_id = {groupId} AND code = {code}
            """).FirstAsync(ct);
        return Result.Success(actual);
    }

    public async Task<Result> SetMappingAsync(Guid groupId, SetMappingRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var inGroup = await db.Companies.AsNoTracking().AnyAsync(c => c.Id == request.CompanyId && c.GroupId == groupId, ct);
        if (!inGroup) return Error.NotFound("group.company", "Entreprise hors du groupe.");

        var local = await db.Database.SqlQuery<TypedRow>($"""
            SELECT a.id AS "Id", a.account_type AS "AccountType" FROM nexora.account a
            JOIN nexora.chart_of_accounts coa ON coa.id = a.chart_of_accounts_id
            WHERE a.company_id = {request.CompanyId} AND a.code = {request.LocalAccountCode} AND a.status = 'active'
            ORDER BY (coa.status = 'active') DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (local is null) return Error.NotFound("group.local_account", $"Compte local « {request.LocalAccountCode} » introuvable ou inactif.");
        var accountId = local.Id;

        var target = await db.Database.SqlQuery<TypedRow>($"""
            SELECT id AS "Id", account_type AS "AccountType" FROM nexora.group_account
            WHERE group_id = {groupId} AND code = {request.GroupAccountCode} AND status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (target is null) return Error.NotFound("group.group_account", $"Compte groupe « {request.GroupAccountCode} » introuvable.");
        var groupAccountId = target.Id;

        // Un compte de charge rattaché à un compte d'actif groupe fausse bilan et résultat consolidés.
        if (local.AccountType != target.AccountType)
            return Error.Validation("group.mapping_type",
                $"Types incompatibles : le compte local est « {TypeLabel(local.AccountType)} », le compte groupe « {TypeLabel(target.AccountType)} ».");

        var current = await db.Database.SqlQuery<Guid?>($"""
            SELECT group_account_id AS "Value" FROM nexora.account_group_mapping WHERE account_id = {accountId} AND status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (current == groupAccountId) return Result.Success();   // déjà en place : pas de nouvelle version

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

    internal static string TypeLabel(string type) => type switch
    {
        "asset" => "actif", "liability" => "passif", "equity" => "capitaux propres",
        "revenue" => "produit", "expense" => "charge", _ => type,
    };

    public async Task<Result<IReadOnlyList<LocalAccountMappingDto>>> ListMappingsAsync(Guid groupId, Guid companyId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();
        if (!await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && c.GroupId == groupId, ct))
            return Error.NotFound("group.company", "Entreprise hors du groupe.");
        var rows = await db.Database.SqlQuery<MappingRow>($"""
            SELECT a.id AS "AccountId", a.code AS "Code", a.name AS "Name", a.account_type AS "AccountType",
                   EXISTS(SELECT 1 FROM nexora.journal_entry_line l JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                          WHERE l.account_id = a.id AND e.status = 'posted') AS "HasActivity",
                   ga.code AS "GroupAccountCode", ga.name AS "GroupAccountName", m.effective_from AS "EffectiveFrom",
                   (SELECT COUNT(*)::int FROM nexora.account_group_mapping h WHERE h.account_id = a.id) AS "Versions"
            FROM nexora.account a
            JOIN nexora.chart_of_accounts coa ON coa.id = a.chart_of_accounts_id AND coa.status = 'active'
            LEFT JOIN nexora.account_group_mapping m ON m.account_id = a.id AND m.status = 'active'
            LEFT JOIN nexora.group_account ga ON ga.id = m.group_account_id
            WHERE a.company_id = {companyId} AND a.status = 'active'
            ORDER BY a.code
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<LocalAccountMappingDto>>(rows.Select(r => new LocalAccountMappingDto(
            r.AccountId, r.Code, r.Name, r.AccountType, r.HasActivity, r.GroupAccountCode, r.GroupAccountName, r.EffectiveFrom, r.Versions)).ToList());
    }

    public async Task<GroupOptionsDto> OptionsAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();
        var name = await db.Database.SqlQuery<string>($"""
            SELECT name AS "Value" FROM nexora.business_group WHERE id = {groupId}
            """).FirstAsync(ct);
        var companies = await db.Database.SqlQuery<CompanyOptionRow>($"""
            SELECT c.id AS "Id", c.name AS "Name", c.status AS "Status", COALESCE(cur.code, '—') AS "Currency",
                   c.main_activity_type AS "Sector", c.archived_at AS "ArchivedAt", c.archive_reason AS "ArchiveReason"
            FROM nexora.company c LEFT JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.group_id = {groupId} ORDER BY c.name
            """).ToListAsync(ct);
        var currencies = await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.currency WHERE status = 'active' ORDER BY code
            """).ToListAsync(ct);
        // Personnes du groupe : rôle actif sur le groupe ou sur l'une de ses sociétés.
        var users = await db.Database.SqlQuery<UserOptionRow>($"""
            SELECT DISTINCT u.id AS "Id", COALESCE(NULLIF(u.full_name, ''), u.email::text) AS "Name", u.email::text AS "Email"
            FROM nexora.app_user u
            JOIN nexora.user_role ur ON ur.user_id = u.id AND ur.revoked_at IS NULL AND (ur.valid_until IS NULL OR ur.valid_until > now())
            JOIN nexora.access_scope sc ON sc.id = ur.scope_id
            LEFT JOIN nexora.company c ON c.id = sc.company_id
            WHERE u.status = 'active' AND (sc.group_id = {groupId} OR c.group_id = {groupId})
            ORDER BY 2
            """).ToListAsync(ct);
        return new GroupOptionsDto(groupId, name,
            companies.Select(c => new GroupCompanyOption(c.Id, c.Name, c.Status, c.Currency, c.Sector, c.ArchivedAt, c.ArchiveReason)).ToList(),
            currencies, SqlCompanyProvisioningService.Activities,
            users.Select(u => new GroupUserOption(u.Id, u.Name, u.Email)).ToList(), SupportedFxMethods);
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
        if (method is not null && !SupportedFxMethods.Contains(method))
            return Error.Validation("group.fx_method_unsupported",
                "Seul le taux de clôture est appliqué par la consolidation ; le taux moyen et le taux historique ne le sont pas encore.");
        if (request.IntercompanyTolerance is < 0m)
            return Error.Validation("group.tolerance", "La tolérance ne peut être négative.");

        // La politique n'existait pas toujours : la mise à jour seule ne sauvait alors rien, en renvoyant un succès.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.group_policy (group_id, consolidation_currency_id, fx_method, intercompany_tolerance)
            SELECT {groupId}, COALESCE({currencyId}, g.default_currency_id), COALESCE({method}, 'closing_rate'),
                   COALESCE({request.IntercompanyTolerance}, 0)
            FROM nexora.business_group g WHERE g.id = {groupId}
            ON CONFLICT (group_id) DO UPDATE SET
                consolidation_currency_id = COALESCE({currencyId}, nexora.group_policy.consolidation_currency_id),
                fx_method = COALESCE({method}, nexora.group_policy.fx_method),
                intercompany_tolerance = COALESCE({request.IntercompanyTolerance}, nexora.group_policy.intercompany_tolerance),
                updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
