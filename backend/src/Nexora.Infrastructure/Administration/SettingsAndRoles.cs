using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Contracts.Administration;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Administration;

/// <summary>Paramètres résolus par périmètre (Phase 14 §2-3).</summary>
public sealed class SqlSettingsService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : ISettingsService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly string[] Scopes = ["system", "group", "company", "establishment"];
    private static int Rank(string s) => Array.IndexOf(Scopes, s);

    private sealed record DefRow(string Key, string Category, string ValueType, string DefaultValue, string AllowedValues, string MaxScope, bool IsSensitive, bool AffectsFuture, string Description);
    private sealed record ValueRow(string Scope, string Value);

    public async Task<IReadOnlyList<SettingDefinitionDto>> DefinitionsAsync(string? category, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<DefRow>($"""
            SELECT key AS "Key", category AS "Category", value_type AS "ValueType", default_value::text AS "DefaultValue",
                   allowed_values::text AS "AllowedValues", max_scope AS "MaxScope", is_sensitive AS "IsSensitive",
                   affects_future AS "AffectsFuture", description AS "Description"
            FROM nexora.setting_definition WHERE ({category}::text IS NULL OR category = {category})
            ORDER BY category, key
            """).ToListAsync(ct);
        return rows.Select(r => new SettingDefinitionDto(r.Key, r.Category, r.ValueType, Parse(r.DefaultValue),
            Parse(r.AllowedValues), r.MaxScope, r.IsSensitive, r.AffectsFuture, r.Description)).ToList();
    }

    public async Task<IReadOnlyList<EffectiveSettingDto>> EffectiveAsync(Guid? establishmentId, CancellationToken ct = default)
    {
        var defs = await DefinitionsAsync(null, ct);
        var list = new List<EffectiveSettingDto>();
        foreach (var d in defs)
            list.Add(await ResolveAsync(d.Key, establishmentId, ct));
        return list;
    }

    public async Task<EffectiveSettingDto> ResolveAsync(string key, Guid? establishmentId = null, CancellationToken ct = default)
    {
        var def = (await db.Database.SqlQuery<DefRow>($"""
            SELECT key AS "Key", category AS "Category", value_type AS "ValueType", default_value::text AS "DefaultValue",
                   allowed_values::text AS "AllowedValues", max_scope AS "MaxScope", is_sensitive AS "IsSensitive",
                   affects_future AS "AffectsFuture", description AS "Description"
            FROM nexora.setting_definition WHERE key = {key}
            """).ToListAsync(ct)).FirstOrDefault()
            ?? throw new InvalidOperationException($"Paramètre inconnu : {key}");

        var groupId = await db.Database.SqlQuery<Guid?>($"""
            SELECT group_id AS "Value" FROM nexora.company WHERE id = {Company}
            """).FirstOrDefaultAsync(ct);

        var rows = await db.Database.SqlQuery<ValueRow>($"""
            SELECT scope AS "Scope", value::text AS "Value" FROM nexora.setting
            WHERE key = {key} AND (
                  (scope = 'system')
               OR (scope = 'group'         AND scope_id = {groupId})
               OR (scope = 'company'       AND scope_id = {Company})
               OR (scope = 'establishment' AND scope_id = {establishmentId}))
            """).ToListAsync(ct);

        // Le périmètre le plus fin gagne (§3).
        var best = rows.OrderByDescending(r => Rank(r.Scope)).FirstOrDefault();
        return best is not null
            ? new EffectiveSettingDto(key, def.Category, Parse(best.Value), best.Scope)
            : new EffectiveSettingDto(key, def.Category, Parse(def.DefaultValue), "definition");
    }

    public async Task<Result<SetSettingResult>> SetAsync(SetSettingRequest request, CancellationToken ct = default)
    {
        var scope = request.Scope?.Trim().ToLowerInvariant() ?? "";
        if (Rank(scope) < 0) return Error.Validation("settings.scope", "Périmètre invalide (system/group/company/establishment).");
        if (scope == "system") return Error.Forbidden("settings.scope_system", "Les paramètres système ne se modifient pas par ce canal.");

        var def = (await db.Database.SqlQuery<DefRow>($"""
            SELECT key AS "Key", category AS "Category", value_type AS "ValueType", default_value::text AS "DefaultValue",
                   allowed_values::text AS "AllowedValues", max_scope AS "MaxScope", is_sensitive AS "IsSensitive",
                   affects_future AS "AffectsFuture", description AS "Description"
            FROM nexora.setting_definition WHERE key = {request.Key}
            """).ToListAsync(ct)).FirstOrDefault();
        if (def is null) return Error.NotFound("settings.key", $"Paramètre « {request.Key} » inconnu.");
        if (Rank(scope) > Rank(def.MaxScope))
            return Error.Validation("settings.scope_too_fine", $"« {request.Key} » ne peut être posé qu'au niveau « {def.MaxScope} » au plus fin.");
        if (def.IsSensitive && string.IsNullOrWhiteSpace(request.Note))
            return Error.Validation("settings.reason_required", "Un motif est requis pour un paramètre sensible (§2, §38).");

        if (!TypeMatches(def.ValueType, request.Value, Parse(def.AllowedValues)))
            return Error.Validation("settings.value", $"Valeur incompatible avec le type « {def.ValueType} ».");

        // Cible de périmètre + company_id pour l'isolation RLS.
        Guid? scopeId = request.ScopeId;
        Guid? companyId;
        switch (scope)
        {
            case "company":
                scopeId = Company; companyId = Company; break;
            case "establishment":
                if (scopeId is null) return Error.Validation("settings.scope_id", "Établissement requis.");
                var ok = await db.Database.SqlQuery<int>($"""
                    SELECT COUNT(*)::int AS "Value" FROM nexora.establishment WHERE id = {scopeId} AND company_id = {Company}
                    """).FirstAsync(ct);
                if (ok == 0) return Error.NotFound("settings.establishment", "Établissement introuvable.");
                companyId = Company; break;
            case "group":
                var gid = await db.Database.SqlQuery<Guid?>($"""SELECT group_id AS "Value" FROM nexora.company WHERE id = {Company}""").FirstOrDefaultAsync(ct);
                if (gid is null) return Error.Validation("settings.group", "L'entreprise n'appartient à aucun groupe.");
                scopeId = gid; companyId = null; break;
            default:
                return Error.Validation("settings.scope", "Périmètre non pris en charge.");
        }

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.setting (key, scope, scope_id, company_id, value, note, updated_by)
            VALUES ({request.Key}, {scope}, {scopeId}, {companyId}, {request.Value.GetRawText()}::jsonb, {request.Note}, {tenant.UserId})
            ON CONFLICT (key, scope, scope_id) DO UPDATE SET
                value = EXCLUDED.value, note = EXCLUDED.note, updated_by = EXCLUDED.updated_by, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var warnings = new List<string>();
        if (def.AffectsFuture)
            warnings.Add("Ce paramètre influe sur les transactions futures — vérifiez la cohérence avec l'historique (§45).");
        if (def.IsSensitive)
            warnings.Add("Paramètre sensible : changement audité, envisagez un instantané de configuration (§39).");

        var effective = await ResolveAsync(request.Key, scope == "establishment" ? scopeId : null, ct);
        return Result.Success(new SetSettingResult(effective, warnings));
    }

    private static bool TypeMatches(string type, JsonElement v, JsonElement allowed) => type switch
    {
        "string" => v.ValueKind == JsonValueKind.String,
        "number" => v.ValueKind == JsonValueKind.Number,
        "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "json" => true,
        "enum" => v.ValueKind == JsonValueKind.String && allowed.ValueKind == JsonValueKind.Array
                  && allowed.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.String && a.GetString() == v.GetString()),
        _ => false,
    };

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "null" : json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("null").RootElement.Clone(); }
    }
}

/// <summary>Rôles personnalisés d'entreprise (Phase 14 §10-11).</summary>
public sealed class SqlCustomRoleService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : ICustomRoleService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record RoleRow(Guid Id, string Name, string? Description, bool SystemRole, string Status);

    public async Task<IReadOnlyList<RoleDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<RoleRow>($"""
            SELECT id AS "Id", name AS "Name", description AS "Description", system_role AS "SystemRole", status AS "Status"
            FROM nexora.role WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);
        var result = new List<RoleDto>();
        foreach (var r in rows) result.Add(await HydrateAsync(r, ct));
        return result;
    }

    public async Task<Result<Guid>> CreateAsync(CreateRoleRequest request, CancellationToken ct = default)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 2 or > 60) return Error.Validation("roles.name", "Nom de rôle invalide (2 à 60 caractères).");

        var exists = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.role WHERE company_id = {Company} AND lower(name) = {name.ToLowerInvariant()}
            """).FirstAsync(ct);
        if (exists > 0) return Error.Conflict("roles.duplicate", "Un rôle porte déjà ce nom.");

        var codes = new HashSet<string>(request.Permissions?.Select(p => p.Trim()).Where(p => p.Length > 0) ?? []);
        if (request.BaseRoleId is { } baseId)
        {
            var baseCodes = await db.Database.SqlQuery<string>($"""
                SELECT p.code AS "Value" FROM nexora.role_permission rp
                JOIN nexora.permission p ON p.id = rp.permission_id
                JOIN nexora.role r ON r.id = rp.role_id
                WHERE rp.role_id = {baseId} AND (r.company_id = {Company} OR r.system_role)
                """).ToListAsync(ct);
            foreach (var c in baseCodes) codes.Add(c);
        }

        var codeArray = codes.ToArray();
        var validCodes = await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.permission WHERE code = ANY({codeArray})
            """).ToListAsync(ct);
        var unknown = codes.Except(validCodes).ToList();
        if (unknown.Count > 0) return Error.Validation("roles.permission", $"Permission(s) inconnue(s) : {string.Join(", ", unknown)}.");

        var id = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.role (id, company_id, name, description, system_role, status)
                VALUES ({id}, {Company}, {name}, {request.Description}, false, 'active')
                """, token);
            foreach (var code in validCodes)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.role_permission (role_id, permission_id)
                    SELECT {id}, id FROM nexora.permission WHERE code = {code}
                    ON CONFLICT DO NOTHING
                    """, token);
            return 0;
        }, ct);
        return Result.Success(id);
    }

    public async Task<Result> SetPermissionsAsync(Guid roleId, SetRolePermissionsRequest request, CancellationToken ct = default)
    {
        var role = await LoadOwnedAsync(roleId, ct);
        if (role is null) return Error.NotFound("roles.role", "Rôle introuvable ou non modifiable.");
        if (role.SystemRole) return Error.Forbidden("roles.system", "Un rôle système ne se modifie pas.");

        var codes = (request.Permissions ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct().ToArray();
        var valid = await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.permission WHERE code = ANY({codes})
            """).ToListAsync(ct);
        var unknown = codes.Except(valid).ToList();
        if (unknown.Count > 0) return Error.Validation("roles.permission", $"Permission(s) inconnue(s) : {string.Join(", ", unknown)}.");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.role_permission WHERE role_id = {roleId}", token);
            foreach (var code in valid)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.role_permission (role_id, permission_id)
                    SELECT {roleId}, id FROM nexora.permission WHERE code = {code}
                    """, token);
            await db.Database.ExecuteSqlAsync($"UPDATE nexora.role SET updated_at = now() WHERE id = {roleId}", token);
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<Result<RoleDto>> PreviewAsync(Guid roleId, CancellationToken ct = default)
    {
        var role = await LoadOwnedAsync(roleId, ct);
        return role is null ? Error.NotFound("roles.role", "Rôle introuvable.") : Result.Success(await HydrateAsync(role, ct));
    }

    public async Task<Result> DisableAsync(Guid roleId, bool force, CancellationToken ct = default)
    {
        var role = await LoadOwnedAsync(roleId, ct);
        if (role is null) return Error.NotFound("roles.role", "Rôle introuvable.");
        if (role.SystemRole) return Error.Forbidden("roles.system", "Un rôle système ne se désactive pas.");

        var assigned = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.user_role WHERE role_id = {roleId}
            """).FirstAsync(ct);
        if (assigned > 0 && !force)
            return Error.Conflict("roles.assigned",
                $"Rôle assigné à {assigned} utilisateur(s) — retirez les affectations ou forcez la désactivation (§45).");

        await db.Database.ExecuteSqlAsync($"UPDATE nexora.role SET status = 'inactive', updated_at = now() WHERE id = {roleId}", ct);
        return Result.Success();
    }

    private Task<RoleRow?> LoadOwnedAsync(Guid roleId, CancellationToken ct) => db.Database.SqlQuery<RoleRow>($"""
        SELECT id AS "Id", name AS "Name", description AS "Description", system_role AS "SystemRole", status AS "Status"
        FROM nexora.role WHERE id = {roleId} AND company_id = {Company}
        """).FirstOrDefaultAsync(ct);

    private async Task<RoleDto> HydrateAsync(RoleRow r, CancellationToken ct)
    {
        var perms = await db.Database.SqlQuery<string>($"""
            SELECT p.code AS "Value" FROM nexora.role_permission rp
            JOIN nexora.permission p ON p.id = rp.permission_id
            WHERE rp.role_id = {r.Id} ORDER BY p.code
            """).ToListAsync(ct);
        var count = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.user_role WHERE role_id = {r.Id}
            """).FirstAsync(ct);
        return new RoleDto(r.Id, r.Name, r.Description, r.SystemRole, r.Status, count, perms);
    }
}
