using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Preferences;
using Nexora.Contracts.Preferences;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Preferences;

internal static class UxJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    public static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();

    public static JsonElement Parse(string? json, JsonElement fallback)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return fallback; }
    }
}

// =============================================================================
// Dispositions de tableau de bord (Phase 40 §13, §37).
// =============================================================================
public sealed class SqlDashboardLayoutService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IPermissionService permissions)
    : IDashboardLayoutService
{
    private Guid User => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, Guid CompanyId, Guid? UserId, string? Role, string Name, string Widgets, bool IsDefault, DateTimeOffset UpdatedAt);

    private const string Select = """
        SELECT id AS "Id", company_id AS "CompanyId", user_id AS "UserId", role AS "Role", name AS "Name",
               widgets::text AS "Widgets", is_default AS "IsDefault", updated_at AS "UpdatedAt"
        FROM nexora.dashboard_layout
        """;

    public async Task<DashboardLayoutDto?> GetEffectiveAsync(CancellationToken ct = default)
    {
        var mine = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND user_id = {1} ORDER BY is_default DESC, updated_at DESC LIMIT 1",
            Company, User).FirstOrDefaultAsync(ct);
        if (mine is not null) return ToDto(mine);

        var roles = await MyRolesAsync(ct);
        if (roles.Count > 0)
        {
            var byRole = await db.Database.SqlQueryRaw<Row>(
                Select + " WHERE company_id = {0} AND user_id IS NULL AND is_default AND role = ANY({1}::text[]) ORDER BY updated_at DESC LIMIT 1",
                Company, roles.ToArray()).FirstOrDefaultAsync(ct);
            if (byRole is not null) return ToDto(byRole);
        }

        var companyDefault = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND user_id IS NULL AND role IS NULL AND is_default LIMIT 1", Company).FirstOrDefaultAsync(ct);
        return companyDefault is null ? null : ToDto(companyDefault);
    }

    public async Task<IReadOnlyList<DashboardLayoutDto>> ListAsync(CancellationToken ct = default)
    {
        // Explicite : mes dispositions + les défauts d'entreprise/rôle (user_id NULL). RLS = seconde barrière.
        var rows = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND (user_id = {1} OR user_id IS NULL) ORDER BY (user_id IS NULL), updated_at DESC",
            Company, User).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public Task<Result<DashboardLayoutDto>> SaveAsync(SaveDashboardLayoutRequest request, CancellationToken ct = default)
        => SaveInternalAsync(request, companyDefault: false, ct);

    public async Task<Result<DashboardLayoutDto>> SaveCompanyDefaultAsync(SaveDashboardLayoutRequest request, CancellationToken ct = default)
        => await SaveInternalAsync(request, companyDefault: true, ct);

    private async Task<Result<DashboardLayoutDto>> SaveInternalAsync(SaveDashboardLayoutRequest r, bool companyDefault, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 80) return Error.Validation("layout.name", "Nom invalide (1–80 caractères).");
        if (r.Widgets.ValueKind != JsonValueKind.Array) return Error.Validation("layout.widgets", "Les widgets doivent être une liste.");
        if (r.Widgets.GetArrayLength() > 40) return Error.Validation("layout.widgets", "Trop de widgets (max 40).");

        var userId = companyDefault ? (Guid?)null : User;
        var role = companyDefault ? (string.IsNullOrWhiteSpace(r.Role) ? null : r.Role.Trim()) : null;
        var isDefault = companyDefault ? true : r.IsDefault;
        var widgets = r.Widgets.GetRawText();
        var id = r.Id ?? Guid.NewGuid();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            if (isDefault)
            {
                if (companyDefault && role is not null)
                    await db.Database.ExecuteSqlAsync($"UPDATE nexora.dashboard_layout SET is_default = false WHERE company_id = {Company} AND user_id IS NULL AND role = {role} AND id <> {id}", token);
                else if (companyDefault)
                    await db.Database.ExecuteSqlAsync($"UPDATE nexora.dashboard_layout SET is_default = false WHERE company_id = {Company} AND user_id IS NULL AND role IS NULL AND id <> {id}", token);
                else
                    await db.Database.ExecuteSqlAsync($"UPDATE nexora.dashboard_layout SET is_default = false WHERE company_id = {Company} AND user_id = {User} AND id <> {id}", token);
            }

            var updated = r.Id is { } existing
                ? await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.dashboard_layout SET name = {r.Name.Trim()}, widgets = {widgets}::jsonb, is_default = {isDefault},
                        role = {role}, updated_at = now()
                    WHERE id = {existing} AND company_id = {Company}
                      AND (user_id = {User} OR ({companyDefault} AND user_id IS NULL))
                    """, token)
                : 0;
            if (updated == 0)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.dashboard_layout (id, company_id, user_id, role, name, widgets, is_default, created_by)
                    VALUES ({id}, {Company}, {userId}, {role}, {r.Name.Trim()}, {widgets}::jsonb, {isDefault}, {User})
                    """, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);

        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0} AND company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("layout", "Disposition introuvable.") : Result.Success(ToDto(row));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.dashboard_layout WHERE id = {id} AND company_id = {Company}
              AND (user_id = {User} OR user_id IS NULL)
            """, ct);
        await uow.SaveChangesAsync(ct);
        return n == 0 ? Error.NotFound("layout", "Disposition introuvable.") : Result.Success();
    }

    private async Task<List<string>> MyRolesAsync(CancellationToken ct)
    {
        var profile = await permissions.LoadProfileAsync(User, ct);
        return profile.Memberships.Where(m => m.CompanyId == Company).SelectMany(m => m.Roles).Distinct().ToList();
    }

    private static DashboardLayoutDto ToDto(Row r) => new(r.Id, r.CompanyId, r.UserId, r.Role, r.Name,
        UxJson.Parse(r.Widgets, UxJson.EmptyArray), r.IsDefault, r.UpdatedAt);
}

// =============================================================================
// Filtres de tableaux sauvegardés (Phase 40 §21-22, §44).
// =============================================================================
public sealed class SqlSavedFilterService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : ISavedFilterService
{
    private Guid User => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, Guid CompanyId, Guid UserId, string ViewKey, string Name, string Filter, bool IsDefault, bool Shared, DateTimeOffset UpdatedAt);

    public async Task<IReadOnlyList<SavedFilterDto>> ListAsync(string? viewKey, CancellationToken ct = default)
    {
        // Filtre explicite (les miens + les partagés de l'entreprise) — RLS = seconde barrière (§56).
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", company_id AS "CompanyId", user_id AS "UserId", view_key AS "ViewKey", name AS "Name",
                   filter::text AS "Filter", is_default AS "IsDefault", shared AS "Shared", updated_at AS "UpdatedAt"
            FROM nexora.saved_filter
            WHERE company_id = {Company} AND (user_id = {User} OR shared)
              AND ({viewKey}::text IS NULL OR view_key = {viewKey}::text)
            ORDER BY (user_id = {User}) DESC, name
            """).ToListAsync(ct);
        return rows.Select(r => new SavedFilterDto(r.Id, r.CompanyId, r.UserId, r.ViewKey, r.Name,
            UxJson.Parse(r.Filter, UxJson.EmptyObject), r.IsDefault, r.Shared, r.UserId == User, r.UpdatedAt)).ToList();
    }

    public async Task<Result<SavedFilterDto>> SaveAsync(SaveFilterRequest r, CancellationToken ct = default)
    {
        var view = (r.ViewKey ?? "").Trim();
        if (view.Length is < 2 or > 60 || !view.Contains('.')) return Error.Validation("filter.view_key", "Clé de vue invalide (ex. « sales.list »).");
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > 60) return Error.Validation("filter.name", "Nom invalide (1–60 caractères).");
        var filter = r.Filter.ValueKind == JsonValueKind.Object ? r.Filter.GetRawText() : "{}";
        var id = r.Id ?? Guid.NewGuid();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            if (r.IsDefault)
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.saved_filter SET is_default = false WHERE user_id = {User} AND view_key = {view} AND id <> {id}", token);

            var updated = r.Id is { } existing
                ? await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.saved_filter SET name = {r.Name.Trim()}, filter = {filter}::jsonb, is_default = {r.IsDefault}, shared = {r.Shared}, updated_at = now()
                    WHERE id = {existing} AND user_id = {User}
                    """, token)
                : 0;
            if (updated == 0)
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.saved_filter (id, company_id, user_id, view_key, name, filter, is_default, shared)
                    VALUES ({id}, {Company}, {User}, {view}, {r.Name.Trim()}, {filter}::jsonb, {r.IsDefault}, {r.Shared})
                    ON CONFLICT ON CONSTRAINT saved_filter_uk DO UPDATE SET
                        filter = EXCLUDED.filter, is_default = EXCLUDED.is_default, shared = EXCLUDED.shared, updated_at = now()
                    """, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);

        var saved = (await ListAsync(view, ct)).FirstOrDefault(f => f.Id == id)
            ?? (await ListAsync(view, ct)).First(f => f.Name == r.Name.Trim() && f.Mine);
        return Result.Success(saved);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.saved_filter WHERE id = {id} AND user_id = {User}", ct);
        await uow.SaveChangesAsync(ct);
        return n == 0 ? Error.NotFound("filter", "Filtre introuvable ou non modifiable.") : Result.Success();
    }
}

// =============================================================================
// Raccourcis utilisateur (Phase 40 §11, §20, §37-38).
// =============================================================================
public sealed class SqlShortcutService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IShortcutService
{
    private Guid User => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    public async Task<IReadOnlyList<ShortcutDto>> ListAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<ShortcutDto>($"""
            SELECT id AS "Id", chord AS "Chord", action AS "Action", label AS "Label", sort_order AS "SortOrder"
            FROM nexora.ui_shortcut WHERE user_id = {User} ORDER BY sort_order, label
            """).ToListAsync(ct);

    public async Task<Result<ShortcutDto>> SaveAsync(SaveShortcutRequest r, CancellationToken ct = default)
    {
        var chord = (r.Chord ?? "").Trim().ToLowerInvariant();
        if (chord.Length is < 1 or > 12) return Error.Validation("shortcut.chord", "Raccourci invalide (1–12 caractères).");
        if (string.IsNullOrWhiteSpace(r.Action) || r.Action.Trim().Length > 120) return Error.Validation("shortcut.action", "Action invalide.");
        if (string.IsNullOrWhiteSpace(r.Label) || r.Label.Trim().Length > 60) return Error.Validation("shortcut.label", "Libellé invalide.");
        var id = r.Id ?? Guid.NewGuid();

        var updated = r.Id is { } existing
            ? await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.ui_shortcut SET chord = {chord}, action = {r.Action.Trim()}, label = {r.Label.Trim()}, sort_order = {r.SortOrder}, updated_at = now()
                WHERE id = {existing} AND user_id = {User}
                """, ct)
            : 0;
        if (updated == 0)
        {
            var conflict = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.ui_shortcut WHERE user_id = {User} AND chord = {chord}) AS "Value"
                """).FirstAsync(ct);
            if (conflict) return Error.Conflict("shortcut.chord_taken", "Ce raccourci est déjà utilisé.");
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.ui_shortcut (id, user_id, company_id, chord, action, label, sort_order)
                VALUES ({id}, {User}, {tenant.ActiveCompanyId}, {chord}, {r.Action.Trim()}, {r.Label.Trim()}, {r.SortOrder})
                """, ct);
        }
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListAsync(ct)).First(s => s.Id == id));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.ui_shortcut WHERE id = {id} AND user_id = {User}", ct);
        await uow.SaveChangesAsync(ct);
        return n == 0 ? Error.NotFound("shortcut", "Raccourci introuvable.") : Result.Success();
    }
}

// =============================================================================
// Version du design system (Phase 40 §50, §57) — 2.0.0 = NEXORA ONE (palette v2, profondeur/verre,
// composants d'intelligence : docs/nexora-one-experience-intelligente.md §3-4).
// =============================================================================
public sealed class DesignSystemService : IDesignSystemService
{
    public DesignSystemVersionDto GetVersion() => new(
        Version: "2.0.0",
        WcagTarget: "WCAG 2.2 AA",
        ComponentCount: 30,
        Themes: ["system", "light", "dark", "high-contrast"],
        Densities: ["comfortable", "compact"],
        TokenGroups:
        [
            new("color", 27, ["--nx-brand", "--nx-brand-soft", "--nx-bg", "--nx-surface", "--nx-text", "--nx-success", "--nx-error", "--nx-ai-gradient"]),
            new("typography", 9, ["--nx-font", "--nx-font-num", "--nx-fs-xs", "--nx-fs-md", "--nx-fs-2xl"]),
            new("space", 6, ["--nx-space", "--nx-gap", "--nx-radius", "--nx-radius-sm", "--nx-radius-lg", "--nx-radius-xl"]),
            new("elevation", 7, ["--nx-shadow", "--nx-ring", "--nx-elev-1", "--nx-elev-2", "--nx-elev-3", "--nx-glow", "--nx-glass-blur"]),
            new("control", 3, ["--nx-control-h", "--nx-control-pad", "--nx-tap"]),
            new("motion", 2, ["--nx-dur", "--nx-ease"]),
        ],
        Components:
        [
            "Button", "Input", "Select", "Combobox", "DatePicker", "Table", "Card", "Modal", "Drawer",
            "Toast", "Alert", "Tabs", "Badge", "Tooltip", "Dropdown", "Stepper", "Pagination", "DataGrid",
            "KpiCard", "Chart", "CommandPalette",
            // NEXORA ONE — composants d'intelligence (src/shared/intel.tsx, src/ai/).
            "HeroKpi", "Sparkline", "SignalCount", "RecommendationCard", "ActivityTimeline", "BarList",
            "ForecastChart", "SectionHeader", "NexoraAiPanel",
        ],
        ReleasedOn: new DateOnly(2026, 9, 27));
}
