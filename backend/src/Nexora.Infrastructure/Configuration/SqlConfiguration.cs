using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Configuration;
using Nexora.Contracts.Configuration;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Configuration;

// =============================================================================
// Configuration & adaptabilité (Phase 32 §6, §46-48, §52). S'appuie sur le socle
// Phases 8 (company_module / custom_field / terminology_override) et 14
// (setting, config_version) — aucune donnée métier dupliquée.
// =============================================================================

/// <summary>Applique un bundle de configuration à une entreprise cible — upserts idempotents.</summary>
internal static class ConfigApplier
{
    private static readonly string[] CustomFieldEntities =
        ["product", "customer", "supplier", "sale", "purchase_order", "establishment", "employee", "service", "recipe", "generic"];
    private static readonly string[] CustomFieldTypes = ["text", "number", "date", "boolean", "list", "reference", "file"];

    public static async Task<(int Modules, int Fields, int Terms, int Settings, List<string> Warnings)> ApplyAsync(
        NexoraDbContext db, Guid company, Guid? userId,
        IReadOnlyList<ProfileModuleSpec> modules,
        IReadOnlyList<ProfileFieldSpec> fields,
        IReadOnlyList<ProfileTermSpec> terms,
        IReadOnlyList<ProfileSettingSpec>? settings,
        IReadOnlyList<ProfileNumberingSpec>? numbering,
        bool dryRun, CancellationToken ct)
    {
        var warnings = new List<string>();
        int m = 0, f = 0, t = 0, s = 0;

        foreach (var mod in modules ?? [])
        {
            var known = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.company_module WHERE company_id = {company} AND module_key = {mod.Key} AND enabled = {mod.Enabled}
                """).FirstAsync(ct);
            if (known > 0) continue;
            if (!dryRun)
            {
                var n = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.company_module (company_id, module_key, enabled, enabled_at)
                    VALUES ({company}, {mod.Key}, {mod.Enabled}, now())
                    ON CONFLICT ON CONSTRAINT company_module_uk
                    DO UPDATE SET enabled = {mod.Enabled}, disabled_at = CASE WHEN {mod.Enabled} THEN NULL ELSE now() END, updated_at = now()
                    """, ct);
                if (n == 0) { warnings.Add($"Module « {mod.Key} » ignoré (clé inconnue)."); continue; }
            }
            m++;
        }

        foreach (var fld in fields ?? [])
        {
            if (!CustomFieldEntities.Contains(fld.EntityType) || !CustomFieldTypes.Contains(fld.DataType))
            {
                warnings.Add($"Champ « {fld.FieldKey} » ignoré (entité/type invalide).");
                continue;
            }
            var exists = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.custom_field
                WHERE company_id = {company} AND entity_type = {fld.EntityType} AND field_key = {fld.FieldKey}
                """).FirstAsync(ct);
            if (exists > 0) continue;
            if (!dryRun)
            {
                var options = JsonSerializer.Serialize(fld.Options ?? []);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.custom_field (company_id, entity_type, field_key, label, data_type, options, status)
                    VALUES ({company}, {fld.EntityType}, {fld.FieldKey}, {fld.Label}, {fld.DataType}, {options}::jsonb, 'active')
                    """, ct);
            }
            f++;
        }

        foreach (var term in terms ?? [])
        {
            var same = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.terminology_override
                WHERE company_id = {company} AND term_key = {term.TermKey} AND locale = {term.Locale} AND label = {term.Label}
                """).FirstAsync(ct);
            if (same > 0) continue;
            if (!dryRun)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.terminology_override (company_id, term_key, locale, label)
                    VALUES ({company}, {term.TermKey}, {term.Locale}, {term.Label})
                    ON CONFLICT ON CONSTRAINT terminology_override_uk DO UPDATE SET label = {term.Label}, updated_at = now()
                    """, ct);
            t++;
        }

        foreach (var set in settings ?? [])
        {
            var defExists = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.setting_definition WHERE key = {set.Key}
                """).FirstAsync(ct);
            if (defExists == 0) { warnings.Add($"Réglage « {set.Key} » ignoré (aucune définition)."); continue; }
            if (!dryRun)
            {
                var json = set.Value.GetRawText();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.setting (key, scope, scope_id, company_id, value, updated_by)
                    VALUES ({set.Key}, 'company', {company}, {company}, {json}::jsonb, {userId})
                    ON CONFLICT ON CONSTRAINT setting_uk DO UPDATE SET value = {json}::jsonb, updated_by = {userId}, updated_at = now()
                    """, ct);
            }
            s++;
        }

        foreach (var num in numbering ?? [])
        {
            if (dryRun) continue;
            // Année locale de l'entreprise (session au fuseau de l'entreprise), comme next_document_number.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.document_sequence (company_id, document_type, year, prefix, include_year)
                VALUES ({company}, {num.DocumentType}, extract(year FROM current_date)::int, {num.Prefix}, true)
                ON CONFLICT ON CONSTRAINT document_sequence_uk DO UPDATE SET prefix = {num.Prefix}, updated_at = now()
                """, ct);
        }

        return (m, f, t, s, warnings);
    }
}

// -----------------------------------------------------------------------------

public sealed class SqlSectorProfileService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow,
    IConfigVersionService configVersions, IConfigGuardrails guardrails) : ISectorProfileService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record ProfileRow(Guid Id, string Key, string Name, string SectorType, int Version, string Status,
        bool IsSystem, string? Description, Guid? CompanyId, string Definition);

    public async Task<IReadOnlyList<SectorProfileDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<ProfileRow>($"""
            SELECT id AS "Id", key AS "Key", name AS "Name", sector_type AS "SectorType", version AS "Version",
                   status AS "Status", is_system AS "IsSystem", description AS "Description", company_id AS "CompanyId",
                   definition::text AS "Definition"
            FROM nexora.sector_profile
            WHERE (company_id IS NULL OR company_id = {Company}) AND status = 'published'
            ORDER BY is_system DESC, name
            """).ToListAsync(ct);
        return rows.Select(r => new SectorProfileDto(r.Id, r.Key, r.Name, r.SectorType, r.Version, r.Status,
            r.IsSystem, r.Description, r.CompanyId == Company)).ToList();
    }

    private async Task<ProfileRow?> LoadAsync(string key, CancellationToken ct)
        => await db.Database.SqlQuery<ProfileRow>($"""
            SELECT id AS "Id", key AS "Key", name AS "Name", sector_type AS "SectorType", version AS "Version",
                   status AS "Status", is_system AS "IsSystem", description AS "Description", company_id AS "CompanyId",
                   definition::text AS "Definition"
            FROM nexora.sector_profile
            WHERE key = {key} AND (company_id IS NULL OR company_id = {Company})
            ORDER BY (company_id = {Company}) DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);

    private static SectorProfileDefinitionDto Parse(string json)
    {
        var d = JsonSerializer.Deserialize<SectorProfileDefinitionDto>(json, Web);
        return d ?? new SectorProfileDefinitionDto([], [], [], [], []);
    }
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public async Task<Result<SectorProfileDetailDto>> GetAsync(string key, CancellationToken ct = default)
    {
        var r = await LoadAsync(key, ct);
        if (r is null) return Error.NotFound("config.profile_not_found", "Profil sectoriel introuvable.");
        return Result.Success(new SectorProfileDetailDto(
            new SectorProfileDto(r.Id, r.Key, r.Name, r.SectorType, r.Version, r.Status, r.IsSystem, r.Description, r.CompanyId == Company),
            Parse(r.Definition)));
    }

    public async Task<Result<ProfilePreviewDto>> PreviewAsync(string key, CancellationToken ct = default)
    {
        var r = await LoadAsync(key, ct);
        if (r is null) return Error.NotFound("config.profile_not_found", "Profil sectoriel introuvable.");
        var def = Parse(r.Definition);
        var items = new List<ProfilePreviewItemDto>();

        foreach (var m in def.Modules)
        {
            var on = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.company_module WHERE company_id = {Company} AND module_key = {m.Key} AND enabled
                """).FirstAsync(ct);
            items.Add(new ProfilePreviewItemDto("module", m.Key, m.Key, on > 0));
        }
        foreach (var f in def.CustomFields)
        {
            var ex = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.custom_field
                WHERE company_id = {Company} AND entity_type = {f.EntityType} AND field_key = {f.FieldKey}
                """).FirstAsync(ct);
            items.Add(new ProfilePreviewItemDto("custom_field", $"{f.EntityType}.{f.FieldKey}", f.Label, ex > 0));
        }
        foreach (var term in def.Terminology)
        {
            var ex = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.terminology_override
                WHERE company_id = {Company} AND term_key = {term.TermKey} AND locale = {term.Locale}
                """).FirstAsync(ct);
            items.Add(new ProfilePreviewItemDto("terminology", term.TermKey, term.Label, ex > 0));
        }
        foreach (var set in def.Settings)
            items.Add(new ProfilePreviewItemDto("setting", set.Key, set.Value.ToString(), false));

        var report = await guardrails.ValidateAsync(ct);
        return Result.Success(new ProfilePreviewDto(r.Key, r.Name, items,
            report.Checks.Where(c => c.Status != "pass").Select(c => $"{c.Label} : {c.Detail}").ToList()));
    }

    public async Task<Result<ApplyProfileResultDto>> ApplyAsync(string key, ApplyProfileRequest r, CancellationToken ct = default)
    {
        var row = await LoadAsync(key, ct);
        if (row is null) return Error.NotFound("config.profile_not_found", "Profil sectoriel introuvable.");
        var def = Parse(row.Definition);

        var gr = await guardrails.ValidateAsync(ct);
        if (!gr.CanPublish && !r.DryRun)
        {
            var blockers = string.Join(" ; ", gr.Checks.Where(c => c.Status == "fail").Select(c => c.Label));
            return Error.Conflict("config.guardrails_failed", $"Garde-fous non passés : {blockers} (§52).");
        }

        Guid? versionId = null;
        try
        {
            var result = await uow.ExecuteInTransactionAsync(async token =>
            {
                if (!r.DryRun)
                {
                    var snap = await configVersions.SnapshotAsync("sector_profile", $"Avant application du profil « {row.Name} »", token);
                    if (snap.IsSuccess) versionId = snap.Value!.Id;
                }

                var (m, f, t, s, warnings) = await ConfigApplier.ApplyAsync(db, Company, tenant.UserId,
                    def.Modules, def.CustomFields, def.Terminology, def.Settings, def.Numbering, r.DryRun, token);

                var summary = JsonSerializer.Serialize(new { modulesApplied = m, fieldsCreated = f, terminologySet = t, settingsSet = s, warnings }, Web);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.config_transfer (company_id, kind, source_profile_key, dry_run, config_version_id, summary, created_by)
                    VALUES ({Company}, 'apply_profile', {row.Key}, {r.DryRun}, {versionId}, {summary}::jsonb, {tenant.UserId})
                    """, token);

                return new ApplyProfileResultDto(row.Key, r.DryRun, versionId, m, f, t, s, warnings);
            }, ct);
            return Result.Success(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("config.apply_failed", ex.Message);
        }
    }
}

// -----------------------------------------------------------------------------

public sealed class SqlConfigTransferService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IConfigGuardrails guardrails,
    IPermissionService permissions) : IConfigTransferService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record ModRow(string Key, bool Enabled);
    private sealed record FieldRow(string EntityType, string FieldKey, string Label, string DataType, string Options);
    private sealed record TermRow(string TermKey, string Locale, string Label);
    private sealed record SetRow(string Key, string Scope, string Value);
    private sealed record NumRow(string DocumentType, string Prefix);
    private sealed record RoleRow(string Name, string? Description);

    public async Task<ConfigBundleDto> ExportAsync(CancellationToken ct = default)
    {
        var modules = (await db.Database.SqlQuery<ModRow>($"""
            SELECT module_key AS "Key", enabled AS "Enabled" FROM nexora.company_module WHERE company_id = {Company}
            """).ToListAsync(ct)).Select(x => new ProfileModuleSpec(x.Key, x.Enabled)).ToList();

        var fields = (await db.Database.SqlQuery<FieldRow>($"""
            SELECT entity_type AS "EntityType", field_key AS "FieldKey", label AS "Label", data_type AS "DataType", options::text AS "Options"
            FROM nexora.custom_field WHERE company_id = {Company} AND status = 'active'
            """).ToListAsync(ct))
            .Select(x => new ProfileFieldSpec(x.EntityType, x.FieldKey, x.Label, x.DataType,
                JsonSerializer.Deserialize<List<string>>(x.Options) ?? [])).ToList();

        var terms = (await db.Database.SqlQuery<TermRow>($"""
            SELECT term_key AS "TermKey", locale AS "Locale", label AS "Label" FROM nexora.terminology_override WHERE company_id = {Company}
            """).ToListAsync(ct)).Select(x => new ProfileTermSpec(x.TermKey, x.Locale, x.Label)).ToList();

        // §48 : jamais de réglage sensible.
        var settings = (await db.Database.SqlQuery<SetRow>($"""
            SELECT s.key AS "Key", s.scope AS "Scope", s.value::text AS "Value"
            FROM nexora.setting s JOIN nexora.setting_definition d ON d.key = s.key
            WHERE s.company_id = {Company} AND s.scope IN ('company','establishment') AND d.is_sensitive = false
            """).ToListAsync(ct))
            .Select(x => new ExportedSettingDto(x.Key, x.Scope, JsonDocument.Parse(x.Value).RootElement.Clone())).ToList();

        var numbering = (await db.Database.SqlQuery<NumRow>($"""
            SELECT document_type AS "DocumentType", prefix AS "Prefix" FROM nexora.document_sequence
            WHERE company_id = {Company} AND prefix <> ''
            """).ToListAsync(ct)).Select(x => new ProfileNumberingSpec(x.DocumentType, x.Prefix)).DistinctBy(x => x.DocumentType).ToList();

        var roles = new List<ExportedRoleDto>();
        foreach (var role in await db.Database.SqlQuery<RoleRow>($"""
            SELECT name AS "Name", description AS "Description" FROM nexora.role
            WHERE company_id = {Company} AND NOT system_role AND status = 'active'
            """).ToListAsync(ct))
        {
            var perms = await db.Database.SqlQuery<string>($"""
                SELECT p.code AS "Value" FROM nexora.role_permission rp
                JOIN nexora.permission p ON p.id = rp.permission_id
                JOIN nexora.role r ON r.id = rp.role_id
                WHERE r.company_id = {Company} AND r.name = {role.Name}
                """).ToListAsync(ct);
            roles.Add(new ExportedRoleDto(role.Name, role.Description, perms));
        }

        return new ConfigBundleDto("1.0", Company, DateTimeOffset.UtcNow, modules, fields, terms, settings, numbering, roles);
    }

    public async Task<Result<ApplyProfileResultDto>> ImportAsync(ImportConfigRequest r, CancellationToken ct = default)
    {
        if (r.Bundle is null) return Error.Validation("config.bundle", "Bundle de configuration manquant.");
        var gr = await guardrails.ValidateAsync(ct);
        if (!gr.CanPublish && !r.DryRun)
            return Error.Conflict("config.guardrails_failed", "Garde-fous non passés (§52).");
        return await ApplyBundleAsync(Company, r.Bundle, "import", null, r.DryRun, ct);
    }

    public async Task<Result<ApplyProfileResultDto>> CloneAsync(CloneConfigRequest r, CancellationToken ct = default)
    {
        if (r.TargetCompanyId == Company)
            return Error.Validation("config.clone_self", "L'entreprise cible doit être différente de l'entreprise source.");
        if (!tenant.CompanyIds.Contains(r.TargetCompanyId))
            return Error.Forbidden("config.clone_scope", "Entreprise cible hors de votre périmètre.");
        // A-034 : config.transfer n'était vérifiée que sur l'entreprise source ; la configuration d'une entreprise où
        // l'appelant n'est que caissier pouvait être écrasée. La permission est exigée aussi sur la cible.
        if (tenant.UserId is not { } caller
            || !(await permissions.LoadProfileAsync(caller, ct)).HasPermission(Nexora.Application.Security.Permissions.Config.Transfer, r.TargetCompanyId))
            return Error.Forbidden("config.clone_scope", "Vous n'avez pas le droit de modifier la configuration de l'entreprise cible.");

        var bundle = await ExportAsync(ct);
        return await ApplyBundleAsync(r.TargetCompanyId, bundle, "clone", Company, false, ct);
    }

    public async Task<IReadOnlyList<ConfigTransferDto>> HistoryAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<TransferRow>($"""
            SELECT id AS "Id", kind AS "Kind", source_company_id AS "SourceCompanyId", source_profile_key AS "SourceProfileKey",
                   dry_run AS "DryRun", config_version_id AS "ConfigVersionId", summary::text AS "Summary", created_at AS "CreatedAt"
            FROM nexora.config_transfer WHERE company_id = {Company} ORDER BY created_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(x => new ConfigTransferDto(x.Id, x.Kind, x.SourceCompanyId, x.SourceProfileKey, x.DryRun,
            x.ConfigVersionId, JsonDocument.Parse(x.Summary).RootElement.Clone(), x.CreatedAt)).ToList();
    }
    private sealed record TransferRow(Guid Id, string Kind, Guid? SourceCompanyId, string? SourceProfileKey, bool DryRun,
        Guid? ConfigVersionId, string Summary, DateTimeOffset CreatedAt);

    private async Task<Result<ApplyProfileResultDto>> ApplyBundleAsync(
        Guid target, ConfigBundleDto b, string kind, Guid? sourceCompany, bool dryRun, CancellationToken ct)
    {
        try
        {
            var result = await uow.ExecuteInTransactionAsync(async token =>
            {
                var settings = b.Settings.Select(x => new ProfileSettingSpec(x.Key, x.Value)).ToList();
                var (m, f, t, s, warnings) = await ConfigApplier.ApplyAsync(db, target, tenant.UserId,
                    b.Modules, b.CustomFields, b.Terminology, settings, b.Numbering, dryRun, token);

                var summary = JsonSerializer.Serialize(new { modulesApplied = m, fieldsCreated = f, terminologySet = t, settingsSet = s, warnings }, Web);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.config_transfer (company_id, kind, source_company_id, dry_run, summary, created_by)
                    VALUES ({target}, {kind}, {sourceCompany}, {dryRun}, {summary}::jsonb, {tenant.UserId})
                    """, token);
                return new ApplyProfileResultDto(kind, dryRun, null, m, f, t, s, warnings);
            }, ct);
            return Result.Success(result);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("config.apply_failed", ex.Message);
        }
    }
}

// -----------------------------------------------------------------------------

public sealed class SqlConfigGuardrails(NexoraDbContext db, ITenantContext tenant) : IConfigGuardrails
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] CoreMappings =
        ["sales_revenue", "sales_tax_output", "accounts_receivable", "accounts_payable", "cogs", "inventory", "cash", "bank"];

    public async Task<GuardrailReportDto> ValidateAsync(CancellationToken ct = default)
    {
        var checks = new List<GuardrailDto>();
        static GuardrailDto Ok(string k, string l, string? d = null) => new(k, l, "pass", d);
        static GuardrailDto Fail(string k, string l, string d) => new(k, l, "fail", d);
        static GuardrailDto Warn(string k, string l, string d) => new(k, l, "warn", d);

        // §18, §52 : devise fonctionnelle valide
        var currencyOk = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.company co
            JOIN nexora.currency cur ON cur.id = co.default_currency_id
            WHERE co.id = {Company} AND cur.status = 'active'
            """).FirstAsync(ct);
        checks.Add(currencyOk > 0 ? Ok("currency_valid", "Devise fonctionnelle valide")
            : Fail("currency_valid", "Devise fonctionnelle valide", "Devise de l'entreprise inconnue ou inactive."));

        // §32, §52 : mappings comptables essentiels présents
        var mapped = await db.Database.SqlQuery<string>($"""
            SELECT mapping_key AS "Value" FROM nexora.account_mapping WHERE company_id = {Company}
            """).ToListAsync(ct);
        var missing = CoreMappings.Where(k => !mapped.Contains(k)).ToArray();
        checks.Add(missing.Length == 0 ? Ok("required_mappings", "Mappings comptables essentiels")
            : Fail("required_mappings", "Mappings comptables essentiels", $"Clés absentes : {string.Join(", ", missing)}."));

        // §19, §52 : pas de collision de préfixe de numérotation entre types
        var collision = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM (
              SELECT prefix, year, COUNT(DISTINCT document_type) c
              FROM nexora.document_sequence WHERE company_id = {Company} AND prefix <> ''
              GROUP BY prefix, year HAVING COUNT(DISTINCT document_type) > 1
            ) q
            """).FirstAsync(ct);
        checks.Add(collision == 0 ? Ok("numbering_unique", "Numérotation sans collision de préfixe")
            : Warn("numbering_unique", "Numérotation sans collision de préfixe", $"{collision} préfixe(s) partagé(s) par plusieurs types de document."));

        // §16, §52 : champs « liste » sans options
        var badLists = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.custom_field
            WHERE company_id = {Company} AND status = 'active' AND data_type = 'list' AND jsonb_array_length(options) = 0
            """).FirstAsync(ct);
        checks.Add(badLists == 0 ? Ok("custom_fields_valid", "Champs personnalisés cohérents")
            : Warn("custom_fields_valid", "Champs personnalisés cohérents", $"{badLists} champ(s) « liste » sans option."));

        // §32, §52 : au moins un exercice comptable ouvert
        var openPeriod = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.accounting_period WHERE company_id = {Company} AND status = 'open'
            """).FirstAsync(ct);
        checks.Add(openPeriod > 0 ? Ok("open_accounting_period", "Exercice comptable ouvert")
            : Warn("open_accounting_period", "Exercice comptable ouvert", "Aucune période comptable ouverte."));

        return new GuardrailReportDto(
            checks.All(c => c.Status != "fail"),
            checks.Count(c => c.Status == "pass"),
            checks.Count(c => c.Status == "fail"),
            checks.Count(c => c.Status == "warn"),
            checks, DateTimeOffset.UtcNow);
    }
}
