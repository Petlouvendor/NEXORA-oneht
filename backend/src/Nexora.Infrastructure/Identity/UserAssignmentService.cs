using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Contracts.Identity;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Identity;

/// <summary>Administration des utilisateurs et affectations (Phase 22 §9, §12-13, §32, §40).</summary>
public sealed class SqlUserAssignmentService(
    NexoraDbContext db, ITenantContext tenant, IPermissionService permissions, IDateTimeProvider clock,
    IPasswordHasher hasher, IUnitOfWork uow, ISessionCacheInvalidator sessionCache)
    : IUserAssignmentService
{
    private Guid Caller => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private static readonly string[] ProtectedRoles = ["super_administrator", "group_administrator"];
    private static readonly string[] Statuses = ["active", "suspended", "locked"];

    private sealed record UserRow(Guid Id, string Email, string? FullName, string Status, DateTimeOffset? LastLoginAt, bool MfaEnabled);
    private sealed record AssignmentRow(
        Guid Id, Guid UserId, string RoleName, string ScopeType, Guid? GroupId, Guid? CompanyId, Guid? EstablishmentId,
        DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, string? Reason, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

    public async Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        var authorized = await AuthorizedCompaniesAsync(ct);
        var rows = await db.Database.SqlQuery<UserRow>($"""
            SELECT u.id AS "Id", u.email::text AS "Email", u.full_name AS "FullName", u.status AS "Status",
                   u.last_login_at AS "LastLoginAt", u.mfa_enabled AS "MfaEnabled"
            FROM nexora.app_user u
            WHERE EXISTS (
                SELECT 1 FROM nexora.user_role ur
                JOIN nexora.access_scope s ON s.id = ur.scope_id
                LEFT JOIN nexora.company gc ON gc.group_id = s.group_id
                WHERE ur.user_id = u.id AND (s.company_id = ANY({authorized}) OR gc.id = ANY({authorized})))
            ORDER BY u.email::text
            """).ToListAsync(ct);
        return rows.Select(r => new UserSummaryDto(r.Id, r.Email, r.FullName, r.Status, r.LastLoginAt, r.MfaEnabled)).ToList();
    }

    public async Task<Result<IReadOnlyList<AssignmentDto>>> ListAssignmentsAsync(Guid userId, CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        if (!await SharesCompanyAsync(userId, ct))
            return Error.NotFound("identity.user", "Utilisateur introuvable dans votre périmètre.");

        var now = clock.UtcNow;
        var rows = await LoadAssignmentsAsync(ur => ur.UserId == userId, ct);
        return Result.Success<IReadOnlyList<AssignmentDto>>(rows.Select(r => Map(r, now)).ToList());
    }

    public async Task<Result<Guid>> GrantAsync(Guid userId, GrantAssignmentRequest request, CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        if (!await SharesCompanyAsync(userId, ct, includeSelfCandidate: true))
            return Error.NotFound("identity.user", "Utilisateur introuvable dans votre périmètre.");

        var scopeType = request.ScopeType?.Trim().ToUpperInvariant() ?? "COMPANY";
        if (scopeType is not ("COMPANY" or "ESTABLISHMENT" or "GROUP"))
            return Error.Validation("identity.scope_type", "Périmètre autorisé : COMPANY, ESTABLISHMENT ou GROUP.");

        var companyId = request.CompanyId ?? Company;
        var authorized = await AuthorizedCompaniesAsync(ct);
        if (!authorized.Contains(companyId))
            return Error.Forbidden("identity.scope", "Vous ne pouvez affecter que dans votre périmètre (§18).");

        // Périmètre groupe (finalisation phase 14) : le groupe de la société indiquée — toutes ses sociétés.
        Guid? groupId = null;
        if (scopeType == "GROUP")
        {
            groupId = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => c.GroupId).FirstOrDefaultAsync(ct);
            if (groupId is null) return Error.Validation("identity.no_group", "Cette entreprise n'appartient à aucun groupe.");
        }

        Guid? establishmentId = null;
        if (scopeType == "ESTABLISHMENT")
        {
            if (request.EstablishmentId is not { } eid) return Error.Validation("identity.establishment", "Établissement requis.");
            var ok = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.establishment WHERE id = {eid} AND company_id = {companyId}
                """).FirstAsync(ct);
            if (ok == 0) return Error.NotFound("identity.establishment", "Établissement introuvable.");
            establishmentId = eid;
        }

        var roleName = request.RoleName?.Trim() ?? "";
        if (ProtectedRoles.Contains(roleName.ToLowerInvariant()))
            return Error.Forbidden("identity.role_protected", "Ce rôle ne s'attribue pas par ce canal (§27).");

        var role = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.role
            WHERE name = {roleName} AND status = 'active' AND (company_id = {companyId} OR system_role)
            ORDER BY system_role LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (role == Guid.Empty) return Error.NotFound("identity.role", $"Rôle « {roleName} » introuvable.");

        // Moindre privilège : on ne peut accorder que des permissions que l'on possède soi-même (§12, §18).
        var rolePerms = await db.Database.SqlQuery<string>($"""
            SELECT p.code AS "Value" FROM nexora.role_permission rp
            JOIN nexora.permission p ON p.id = rp.permission_id WHERE rp.role_id = {role}
            """).ToListAsync(ct);
        var callerProfile = await permissions.LoadProfileAsync(Caller, ct);
        // Au niveau groupe, on compare aux droits de groupe de l'appelant (toutes les sociétés du groupe) ;
        // au niveau société, les droits de groupe du rôle sont inertes et n'entrent pas en compte.
        var callerPerms = (groupId is { } g
            ? callerProfile.GroupPermissionsFor(g).Concat(await CommonCompanyPermissionsAsync(callerProfile, g, ct))
            : callerProfile.PermissionsFor(companyId)).ToHashSet();
        var escalation = rolePerms
            .Where(p => groupId is not null || !Application.Security.Permissions.IsGroupLevel(p))
            .Where(p => !callerPerms.Contains(p)).ToList();
        if (escalation.Count > 0)
            return Error.Forbidden("identity.escalation",
                $"Élévation de privilège refusée — permission(s) que vous ne détenez pas : {string.Join(", ", escalation.Take(5))}.");

        if (request.ValidUntil is { } until && request.ValidFrom is { } from && until <= from)
            return Error.Validation("identity.validity", "La fin de validité doit suivre le début.");

        var scopeId = groupId is { } gid
            ? await FindOrCreateGroupScopeAsync(gid, ct)
            : await FindOrCreateScopeAsync(scopeType, companyId, establishmentId, ct);
        // Une affectation retirée puis rendue est réactivée : on renvoie SON identifiant (le nouvel identifiant
        // généré n'existait pas, et le retrait par l'identifiant renvoyé échouait — finalisation phase 14).
        var id = (await db.Database.SqlQuery<Guid>($"""
            INSERT INTO nexora.user_role (id, user_id, role_id, scope_id, granted_by, valid_from, valid_until, reason)
            VALUES ({Guid.NewGuid()}, {userId}, {role}, {scopeId}, {Caller}, {request.ValidFrom}, {request.ValidUntil}, {request.Reason})
            ON CONFLICT (user_id, role_id, scope_id) DO UPDATE SET
                valid_from = EXCLUDED.valid_from, valid_until = EXCLUDED.valid_until,
                reason = EXCLUDED.reason, revoked_at = NULL, revoked_by = NULL, granted_by = EXCLUDED.granted_by
            RETURNING id AS "Value"
            """).ToListAsync(ct)).Single();
        return Result.Success(id);
    }

    public async Task<Result> RevokeAsync(Guid assignmentId, RevokeAssignmentRequest request, CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Error.Validation("identity.reason", "Un motif de révocation est requis (§44).");

        var rows = await LoadAssignmentsAsync(ur => ur.Id == assignmentId, ct);
        var row = rows.FirstOrDefault();
        if (row is null) return Error.NotFound("identity.assignment", "Affectation introuvable.");

        var authorized = await AuthorizedCompaniesAsync(ct);
        var scopeCompanies = await CompaniesForScopeAsync(row, ct);
        if (!scopeCompanies.Any(authorized.Contains))
            return Error.Forbidden("identity.scope", "Cette affectation est hors de votre périmètre.");
        // Un rôle de groupe ne se retire que depuis le niveau groupe : un administrateur d'une seule
        // société ne peut pas retirer l'accès de la direction du groupe (finalisation phase 14).
        if (row.ScopeType == "GROUP" && row.GroupId is { } rg && !await ManagesUsersGroupWideAsync(rg, ct))
            return Error.Forbidden("identity.group_scope", "Ce rôle est attribué au niveau du groupe : seul un administrateur du groupe peut le retirer.");
        if (row.UserId != Caller && await OutranksCallerAsync(row.UserId, ct) is { } outranked) return outranked;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.user_role
            SET revoked_at = now(), revoked_by = {Caller},
                reason = COALESCE(reason || ' — ', '') || {"révoquée : " + request.Reason.Trim()}
            WHERE id = {assignmentId} AND revoked_at IS NULL
            """, ct);
        sessionCache.InvalidateUser(row.UserId);   // droits relus dès la requête suivante (audit 2026-10, A-010)
        return Result.Success();
    }

    public async Task<Result> SetStatusAsync(Guid userId, SetUserStatusRequest request, CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        var status = request.Status?.Trim().ToLowerInvariant() ?? "";
        if (!Statuses.Contains(status)) return Error.Validation("identity.status", $"Statut invalide ({string.Join('/', Statuses)}).");
        if (userId == Caller) return Error.Forbidden("identity.self", "Vous ne pouvez pas changer votre propre statut.");
        if (!await SharesCompanyAsync(userId, ct))
            return Error.NotFound("identity.user", "Utilisateur introuvable dans votre périmètre.");
        if (await OutranksCallerAsync(userId, ct) is { } outranked) return outranked;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.app_user
            SET status = {status},
                security_epoch = CASE WHEN {status} <> 'active' THEN now() ELSE security_epoch END,
                updated_at = now()
            WHERE id = {userId}
            """, ct);
        sessionCache.InvalidateUser(userId);   // statut relu en base dès la requête suivante
        return Result.Success();
    }

    // --- Helpers ---------------------------------------------------------

    /// <summary>
    /// Anti-élévation sur un compte existant (audit 2026-10, A-010) : on ne suspend pas, et on ne retire pas les
    /// rôles d'un compte qui détient — dans une entreprise, un groupe ou la plateforme qu'il partage avec l'appelant —
    /// une permission que l'appelant n'a pas. Avant, seule l'attribution était gardée : un administrateur des
    /// utilisateurs d'une société pouvait suspendre la direction ou lui retirer son rôle.
    /// </summary>
    private async Task<Error?> OutranksCallerAsync(Guid targetUserId, CancellationToken ct)
    {
        var caller = await permissions.LoadProfileAsync(Caller, ct);
        var target = await permissions.LoadProfileAsync(targetUserId, ct);
        var callerPlatform = (caller.PlatformPermissions ?? []).ToHashSet();
        var missing = new HashSet<string>((target.PlatformPermissions ?? []).Where(p => !callerPlatform.Contains(p)));
        foreach (var company in target.CompanyIds.Intersect(caller.CompanyIds))
        {
            var held = caller.PermissionsFor(company).Concat(callerPlatform).ToHashSet();
            missing.UnionWith(target.PermissionsFor(company).Where(p => !held.Contains(p)));
        }
        foreach (var group in target.GroupIds.Intersect(caller.GroupIds))
        {
            var held = caller.GroupPermissionsFor(group).ToHashSet();
            missing.UnionWith((target.GroupPermissions is { } gp && gp.TryGetValue(group, out var tp) ? tp : [])
                .Where(p => !held.Contains(p)));
        }
        return missing.Count == 0
            ? null
            : Error.Forbidden("identity.escalation",
                $"Ce compte détient des droits que vous n'avez pas ({string.Join(", ", missing.Order().Take(5))}) : seul un administrateur au moins aussi habilité peut agir sur lui.");
    }

    /// <summary>
    /// Entreprises où l'appelant gère les utilisateurs (audit 2026-10 éd. 2, A-033) : la permission était vérifiée sur
    /// l'entreprise active, mais le périmètre comprenait toute entreprise d'appartenance — un administrateur de A,
    /// simple caissier dans B, listait et affectait les comptes de B.
    /// </summary>
    private async Task<Guid[]> AuthorizedCompaniesAsync(CancellationToken ct)
    {
        var profile = await permissions.LoadProfileAsync(Caller, ct);
        return profile.Memberships
            .Where(m => profile.HasPermission(Nexora.Application.Security.Permissions.Admin.ManageUsers, m.CompanyId))
            .Select(m => m.CompanyId).Distinct().ToArray();
    }

    private sealed class GrantRejected(Error error) : Exception(error.Message) { public Error Error { get; } = error; }

    public async Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        if (email.Length is < 3 or > 254 || !email.Contains('@'))
            return Error.Validation("identity.email", "Adresse email invalide.");
        if (string.IsNullOrWhiteSpace(request.FullName))
            return Error.Validation("identity.full_name", "Nom complet requis.");
        if (PasswordPolicy.Validate(request.Password, email) is { } weak) return weak;
        if (request.InitialAssignment is null)
            return Error.Validation("identity.assignment", "Une première affectation (rôle + périmètre) est requise.");

        using var _sys = tenant.BeginSystemScope();   // app_user est une identité transverse (Phase 3 §12)
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.app_user WHERE email = {email}::citext) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("identity.email_exists", "Un compte existe déjà avec cette adresse.");

        var id = Guid.NewGuid();
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.app_user (id, email, full_name, password_hash, status)
                    VALUES ({id}, {email}::citext, {request.FullName.Trim()}, {hasher.Hash(request.Password)}, 'active')
                    """, token);
                // Même garde anti-élévation que POST /users/{id}/assignments : on ne crée jamais un
                // compte plus puissant que soi. Un refus annule aussi la création du compte.
                var granted = await GrantAsync(id, request.InitialAssignment, token);
                if (!granted.IsSuccess) throw new GrantRejected(granted.Error!);
                return 0;
            }, ct);
        }
        catch (GrantRejected rejected)
        {
            return rejected.Error;
        }
        return Result.Success(id);
    }

    private async Task<bool> SharesCompanyAsync(Guid userId, CancellationToken ct, bool includeSelfCandidate = false)
    {
        var authorized = await AuthorizedCompaniesAsync(ct);
        var n = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM nexora.user_role ur
            JOIN nexora.access_scope s ON s.id = ur.scope_id
            LEFT JOIN nexora.company gc ON gc.group_id = s.group_id
            WHERE ur.user_id = {userId} AND (s.company_id = ANY({authorized}) OR gc.id = ANY({authorized}))
            """).FirstAsync(ct);
        if (n > 0) return true;
        // Un utilisateur nouvellement créé n'a encore aucune affectation : on autorise un premier grant.
        if (includeSelfCandidate)
        {
            var exists = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.app_user WHERE id = {userId}
                """).FirstAsync(ct);
            var noAssignments = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.user_role WHERE user_id = {userId}
                """).FirstAsync(ct);
            return exists > 0 && noAssignments == 0;
        }
        return false;
    }

    private async Task<bool> ManagesUsersGroupWideAsync(Guid groupId, CancellationToken ct)
    {
        var profile = await permissions.LoadProfileAsync(Caller, ct);
        const string manage = Application.Security.Permissions.Admin.ManageUsers;
        return profile.GroupPermissionsFor(groupId).Contains(manage)
            || (await CommonCompanyPermissionsAsync(profile, groupId, ct)).Contains(manage);
    }

    /// <summary>Permissions détenues dans CHACUNE des sociétés actives du groupe (rôle de groupe ou rôles société partout).</summary>
    private async Task<IReadOnlyCollection<string>> CommonCompanyPermissionsAsync(UserSecurityProfile profile, Guid groupId, CancellationToken ct)
    {
        var companies = await db.Companies.AsNoTracking()
            .Where(c => c.GroupId == groupId && c.Status != "archived").Select(c => c.Id).ToListAsync(ct);
        if (companies.Count == 0) return [];
        IEnumerable<string>? common = null;
        foreach (var c in companies)
        {
            var perms = profile.PermissionsFor(c);
            if (!profile.CompanyIds.Contains(c)) return [];
            common = common is null ? perms : common.Intersect(perms);
        }
        return (common ?? []).ToArray();
    }

    private async Task<Guid> FindOrCreateGroupScopeAsync(Guid groupId, CancellationToken ct)
    {
        var existing = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.access_scope
            WHERE scope_type = 'GROUP' AND group_id = {groupId} AND company_id IS NULL LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty) return existing;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.access_scope (id, scope_type, group_id) VALUES ({id}, 'GROUP', {groupId})
            """, ct);
        return id;
    }

    private async Task<Guid> FindOrCreateScopeAsync(string scopeType, Guid companyId, Guid? establishmentId, CancellationToken ct)
    {
        var existing = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.access_scope
            WHERE scope_type = {scopeType} AND company_id = {companyId}
              AND establishment_id IS NOT DISTINCT FROM {establishmentId}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (existing != Guid.Empty) return existing;

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.access_scope (id, scope_type, company_id, establishment_id)
            VALUES ({id}, {scopeType}, {companyId}, {establishmentId})
            """, ct);
        return id;
    }

    private async Task<List<AssignmentRow>> LoadAssignmentsAsync(
        System.Linq.Expressions.Expression<Func<Domain.Identity.UserRole, bool>> predicate, CancellationToken ct)
        => await db.UserRoles.AsNoTracking().Where(predicate)
            .Select(ur => new AssignmentRow(
                ur.Id, ur.UserId, ur.Role!.Name, ur.Scope!.ScopeTypeValue,
                ur.Scope!.GroupId, ur.Scope!.CompanyId, ur.Scope!.EstablishmentId,
                ur.ValidFrom, ur.ValidUntil, ur.Reason, ur.GrantedAt, ur.RevokedAt))
            .ToListAsync(ct);

    private async Task<Guid[]> CompaniesForScopeAsync(AssignmentRow row, CancellationToken ct)
    {
        if (row.CompanyId is { } c) return [c];
        if (row.GroupId is { } g)
            return (await db.Database.SqlQuery<Guid>($"""SELECT id AS "Value" FROM nexora.company WHERE group_id = {g}""").ToListAsync(ct)).ToArray();
        return [];
    }

    private static AssignmentDto Map(AssignmentRow r, DateTimeOffset now) => new(
        r.Id, r.UserId, r.RoleName, r.ScopeType, r.GroupId, r.CompanyId, r.EstablishmentId,
        r.ValidFrom, r.ValidUntil, r.Reason,
        r.RevokedAt is null && (r.ValidFrom is null || r.ValidFrom <= now) && (r.ValidUntil is null || r.ValidUntil > now),
        r.GrantedAt, r.RevokedAt);
}

/// <summary>Périmètre effectif de l'utilisateur courant (Phase 22 §14-16, §39).</summary>
public sealed class SqlScopeQueryService(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions)
    : IScopeQueryService
{
    private Guid User => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private sealed record GroupRow(Guid Id, string Name);

    public async Task<IReadOnlyList<string>> MyPermissionsAsync(CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        var profile = await permissions.LoadProfileAsync(User, ct);
        return profile.PermissionsFor(tenant.ActiveCompanyId).OrderBy(p => p, StringComparer.Ordinal).ToArray();
    }

    public async Task<ScopeTreeDto> MyScopesAsync(CancellationToken ct = default)
    {
        using var _sys = tenant.BeginSystemScope();   // recherche d'identité (Phase 3 §12)
        var profile = await permissions.LoadProfileAsync(User, ct);

        var companies = profile.Memberships
            .Select(m => new CompanyScopeDto(m.CompanyId, m.CompanyName, m.GroupId,
                m.Roles.OrderBy(r => r, StringComparer.Ordinal).ToArray(), IsReadOnly(m.Permissions)))
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();

        var groupIds = companies.Where(c => c.GroupId is not null).Select(c => c.GroupId!.Value).Distinct().ToArray();
        var groups = groupIds.Length == 0
            ? new List<GroupScopeDto>()
            : (await db.Database.SqlQuery<GroupRow>($"""
                SELECT id AS "Id", name AS "Name" FROM nexora.business_group WHERE id = ANY({groupIds})
                """).ToListAsync(ct))
                .Select(g => new GroupScopeDto(g.Id, g.Name)).ToList();

        return new ScopeTreeDto(groups, companies);
    }

    /// <summary>Périmètre en lecture seule : aucune permission d'écriture effective (§16, §31).</summary>
    private static bool IsReadOnly(IReadOnlyList<string> perms) =>
        perms.All(p => p.Contains("view", StringComparison.Ordinal)
            || p.Contains("read", StringComparison.Ordinal)
            || p.EndsWith(".export", StringComparison.Ordinal));
}
