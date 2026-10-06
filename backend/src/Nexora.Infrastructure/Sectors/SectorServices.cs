using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Sectors;
using Nexora.Contracts.Sectors;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sectors;

/// <summary>Configuration sectorielle — activités, modules, terminologie (Phase 8 §3-4, §27-29).</summary>
public sealed class SqlSectorConfigService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : ISectorConfigService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] Activities =
    [
        "supermarket","grocery","pharmacy","restaurant","boutique","hotel","hardware_store",
        "wholesaler","distributor","services","production","ecommerce","agriculture","other",
    ];
    private static readonly string[] Modules =
    [
        "pharmacy","restaurant","retail","wholesale","services","production","boutique","hotel",
        "ecommerce","agriculture","loyalty","delivery","appointments","custom_fields","generic",
    ];

    private sealed record ActivityRow(string ActivityType, bool IsPrimary);
    private sealed record ModuleRow(string ModuleKey, bool Enabled);
    private sealed record TermRow(string TermKey, string Locale, string Label);

    public async Task<SectorConfig> GetAsync(CancellationToken ct = default)
    {
        var activities = await db.Database.SqlQuery<ActivityRow>($"""
            SELECT activity_type AS "ActivityType", is_primary AS "IsPrimary"
            FROM nexora.company_activity WHERE company_id = {Company}
            ORDER BY is_primary DESC, activity_type
            """).ToListAsync(ct);

        var modules = await db.Database.SqlQuery<ModuleRow>($"""
            SELECT module_key AS "ModuleKey", enabled AS "Enabled"
            FROM nexora.company_module WHERE company_id = {Company} ORDER BY module_key
            """).ToListAsync(ct);

        var terms = await db.Database.SqlQuery<TermRow>($"""
            SELECT term_key AS "TermKey", locale AS "Locale", label AS "Label"
            FROM nexora.terminology_override WHERE company_id = {Company} ORDER BY term_key
            """).ToListAsync(ct);

        var primary = activities.FirstOrDefault(a => a.IsPrimary)?.ActivityType
            ?? await db.Companies.AsNoTracking().Where(c => c.Id == Company).Select(c => c.MainActivityType).FirstAsync(ct);

        return new SectorConfig(Company, primary,
            activities.Select(a => a.ActivityType).ToList(),
            modules.Select(m => new ModuleState(m.ModuleKey, m.Enabled)).ToList(),
            terms.Select(t => new TerminologyEntry(t.TermKey, t.Locale, t.Label)).ToList());
    }

    public async Task<Result> SetActivitiesAsync(SetActivitiesRequest request, CancellationToken ct = default)
    {
        var list = request.Activities.Select(a => a.Trim().ToLowerInvariant()).Distinct().ToList();
        if (list.Count == 0) return Error.Validation("sectors.activity_required", "Au moins une activité est requise.");
        if (list.Any(a => !Activities.Contains(a)))
            return Error.Validation("sectors.activity_unknown", "Activité non reconnue.");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            // Ajoute les nouvelles activités, retire celles qui ne sont plus listées (historique métier intact).
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM nexora.company_activity
                WHERE company_id = {Company} AND activity_type <> ALL({list.ToArray()})
                """, token);
            foreach (var a in list)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.company_activity (company_id, activity_type, is_primary)
                    VALUES ({Company}, {a}, {a == list[0]})
                    ON CONFLICT (company_id, activity_type)
                    DO UPDATE SET is_primary = EXCLUDED.is_primary, updated_at = now()
                    """, token);

            // L'activité principale se reflète dans company.main_activity_type (compatibilité Core).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.company SET main_activity_type = {list[0]}, updated_at = now() WHERE id = {Company}
                """, token);

            // Active les modules recommandés pour les nouvelles activités (sans rien désactiver).
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.company_module (company_id, module_key)
                SELECT {Company}, p.module_key FROM nexora.sector_module_preset p
                WHERE p.activity_type = ANY({list.ToArray()})
                ON CONFLICT (company_id, module_key) DO NOTHING
                """, token);
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<Result> SetModuleAsync(SetModuleRequest request, CancellationToken ct = default)
    {
        var key = request.ModuleKey.Trim().ToLowerInvariant();
        if (!Modules.Contains(key)) return Error.Validation("sectors.module_unknown", $"Module « {key} » inconnu.");

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.company_module (company_id, module_key, enabled, enabled_at, disabled_at)
            VALUES ({Company}, {key}, {request.Enabled}, now(),
                    CASE WHEN {request.Enabled} THEN NULL ELSE now() END)
            ON CONFLICT (company_id, module_key) DO UPDATE
            SET enabled = EXCLUDED.enabled,
                enabled_at  = CASE WHEN EXCLUDED.enabled THEN now() ELSE nexora.company_module.enabled_at END,
                disabled_at = CASE WHEN EXCLUDED.enabled THEN NULL ELSE now() END,
                updated_at  = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<bool> IsModuleEnabledAsync(string moduleKey, CancellationToken ct = default)
        => await db.Database.SqlQuery<bool>($"""
            SELECT COALESCE((SELECT enabled FROM nexora.company_module
                             WHERE company_id = {Company} AND module_key = {moduleKey}), false) AS "Value"
            """).FirstAsync(ct);

    public async Task<Result> SetTerminologyAsync(SetTerminologyRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.TermKey) || string.IsNullOrWhiteSpace(request.Label))
            return Error.Validation("sectors.terminology_invalid", "Clé et libellé requis.");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.terminology_override (company_id, term_key, locale, label)
            VALUES ({Company}, {request.TermKey.Trim()}, {request.Locale}, {request.Label.Trim()})
            ON CONFLICT (company_id, term_key, locale) DO UPDATE SET label = EXCLUDED.label, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

/// <summary>Moteur de champs personnalisés (Phase 8 §16). Valeurs typées et validées.</summary>
public sealed class SqlCustomFieldService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : ICustomFieldService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private static readonly string[] Entities =
        ["product","customer","supplier","sale","purchase_order","establishment","employee","service","recipe","generic"];
    private static readonly string[] Types = ["text","number","date","boolean","list","reference","file"];

    private sealed record FieldRow(
        Guid Id, string EntityType, string FieldKey, string Label, string DataType,
        bool IsRequired, string Options, string? ReferenceEntity, int Position, string? ViewPermission, string Status);

    public async Task<Result<Guid>> DefineAsync(DefineCustomFieldRequest request, CancellationToken ct = default)
    {
        var entity = request.EntityType.Trim().ToLowerInvariant();
        var type = request.DataType.Trim().ToLowerInvariant();
        var key = request.FieldKey.Trim().ToLowerInvariant();
        if (!Entities.Contains(entity)) return Error.Validation("sectors.cf_entity", $"Entité « {entity} » non supportée.");
        if (!Types.Contains(type)) return Error.Validation("sectors.cf_type", $"Type « {type} » non supporté.");
        if (string.IsNullOrWhiteSpace(key)) return Error.Validation("sectors.cf_key", "Clé de champ requise.");
        if (type == "list" && (request.Options is null || request.Options.Count == 0))
            return Error.Validation("sectors.cf_options", "Un champ liste doit déclarer ses options.");

        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.custom_field
                          WHERE company_id = {Company} AND entity_type = {entity} AND field_key = {key}) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("sectors.cf_exists", $"Le champ « {key} » existe déjà pour {entity}.");

        var id = Guid.NewGuid();
        var optionsJson = System.Text.Json.JsonSerializer.Serialize(request.Options ?? []);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.custom_field
                (id, company_id, entity_type, field_key, label, data_type, is_required, options, reference_entity, view_permission, position)
            VALUES ({id}, {Company}, {entity}, {key}, {request.Label.Trim()}, {type}, {request.IsRequired},
                    {optionsJson}::jsonb, {request.ReferenceEntity}, {request.ViewPermission}, {request.Position})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<CustomFieldDto>> ListAsync(string entityType, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<FieldRow>($"""
            SELECT id AS "Id", entity_type AS "EntityType", field_key AS "FieldKey", label AS "Label",
                   data_type AS "DataType", is_required AS "IsRequired", options::text AS "Options",
                   reference_entity AS "ReferenceEntity", position AS "Position",
                   view_permission AS "ViewPermission", status AS "Status"
            FROM nexora.custom_field
            WHERE company_id = {Company} AND entity_type = {entityType} AND status = 'active'
            ORDER BY position, field_key
            """).ToListAsync(ct);

        return rows.Select(r => new CustomFieldDto(
            r.Id, r.EntityType, r.FieldKey, r.Label, r.DataType, r.IsRequired,
            System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.Options) ?? [],
            r.ReferenceEntity, r.Position, r.ViewPermission, r.Status)).ToList();
    }

    public async Task<Result> SetValuesAsync(SetCustomFieldValuesRequest request, CancellationToken ct = default)
    {
        var defs = await ListAsync(request.EntityType, ct);
        var byKey = defs.ToDictionary(d => d.FieldKey);
        var provided = request.Values.ToDictionary(v => v.FieldKey.Trim().ToLowerInvariant(), v => v.Value);

        // Obligatoires manquants (§16).
        var missing = defs.Where(d => d.IsRequired &&
            (!provided.TryGetValue(d.FieldKey, out var val) || string.IsNullOrWhiteSpace(val))).ToList();
        if (missing.Count > 0)
            return Error.Validation("sectors.cf_required",
                $"Champ(s) obligatoire(s) manquant(s) : {string.Join(", ", missing.Select(m => m.Label))}.");

        var writes = new List<(CustomFieldDto Def, Coerced C)>();
        foreach (var (fk, raw) in provided)
        {
            if (!byKey.TryGetValue(fk, out var def))
                return Error.Validation("sectors.cf_unknown", $"Champ « {fk} » non défini pour {request.EntityType}.");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!TryCoerce(def, raw!, out var coerced, out var why))
                return Error.Validation("sectors.cf_type_mismatch", $"« {def.Label} » : {why}");
            writes.Add((def, coerced));
        }

        await uow.ExecuteInTransactionAsync(async token =>
        {
            foreach (var (def, c) in writes)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.custom_field_value
                        (company_id, custom_field_id, entity_type, entity_id,
                         value_text, value_number, value_date, value_boolean, value_reference)
                    VALUES ({Company}, {def.Id}, {request.EntityType}, {request.EntityId},
                            {c.Text}, {c.Number}, {c.Date}, {c.Boolean}, {c.Reference})
                    ON CONFLICT (custom_field_id, entity_id) DO UPDATE
                    SET value_text = EXCLUDED.value_text, value_number = EXCLUDED.value_number,
                        value_date = EXCLUDED.value_date, value_boolean = EXCLUDED.value_boolean,
                        value_reference = EXCLUDED.value_reference, updated_at = now()
                    """, token);
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<IReadOnlyList<CustomFieldValueDto>> GetValuesAsync(string entityType, Guid entityId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<ValueRow>($"""
            SELECT f.field_key AS "FieldKey", f.label AS "Label", f.data_type AS "DataType",
                   v.value_text AS "Text", v.value_number AS "Number", v.value_date AS "Date",
                   v.value_boolean AS "Boolean", v.value_reference AS "Reference"
            FROM nexora.custom_field_value v
            JOIN nexora.custom_field f ON f.id = v.custom_field_id
            WHERE v.company_id = {Company} AND v.entity_type = {entityType} AND v.entity_id = {entityId}
            ORDER BY f.position, f.field_key
            """).ToListAsync(ct);
        return rows.Select(r => new CustomFieldValueDto(
            r.FieldKey, r.Label, r.DataType, r.Text, r.Number, r.Date, r.Boolean, r.Reference)).ToList();
    }

    private sealed record ValueRow(
        string FieldKey, string Label, string DataType,
        string? Text, decimal? Number, DateOnly? Date, bool? Boolean, Guid? Reference);

    private readonly record struct Coerced(string? Text, decimal? Number, DateOnly? Date, bool? Boolean, Guid? Reference);

    private static bool TryCoerce(CustomFieldDto def, string raw, out Coerced value, out string why)
    {
        value = default; why = "";
        switch (def.DataType)
        {
            case "number":
                if (!decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n))
                { why = "nombre attendu."; return false; }
                value = new Coerced(null, n, null, null, null); return true;
            case "date":
                if (!DateOnly.TryParse(raw, CultureInfo.InvariantCulture, out var d))
                { why = "date attendue (AAAA-MM-JJ)."; return false; }
                value = new Coerced(null, null, d, null, null); return true;
            case "boolean":
                if (!bool.TryParse(raw, out var b)) { why = "booléen attendu (true/false)."; return false; }
                value = new Coerced(null, null, null, b, null); return true;
            case "reference":
                if (!Guid.TryParse(raw, out var g)) { why = "identifiant d'entité attendu."; return false; }
                value = new Coerced(null, null, null, null, g); return true;
            case "list":
                if (!def.Options.Contains(raw)) { why = $"valeur hors liste ({string.Join(", ", def.Options)})."; return false; }
                value = new Coerced(raw, null, null, null, null); return true;
            default: // text, file
                value = new Coerced(raw, null, null, null, null); return true;
        }
    }
}
