using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Contracts.Administration;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Administration;

/// <summary>Versionnement des configurations sensibles (Phase 14 §39).</summary>
public sealed class SqlConfigVersionService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IDateTimeProvider clock)
    : IConfigVersionService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly string[] Domains = ["numbering", "accounting_defaults", "taxes", "approvals", "pos", "stock", "sector_profile"];

    private sealed record Row(Guid Id, string Domain, int VersionNumber, string Status, DateTimeOffset? EffectiveFrom, DateTimeOffset? EffectiveTo, string Snapshot, string Diff, string? Note, DateTimeOffset CreatedAt);
    private sealed record KeyVal(string K, string V);

    public async Task<IReadOnlyList<ConfigVersionDto>> ListAsync(string domain, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", domain AS "Domain", version_number AS "VersionNumber", status AS "Status",
                   effective_from AS "EffectiveFrom", effective_to AS "EffectiveTo", snapshot::text AS "Snapshot",
                   diff::text AS "Diff", note AS "Note", created_at AS "CreatedAt"
            FROM nexora.config_version WHERE company_id = {Company} AND domain = {domain}
            ORDER BY version_number DESC
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<ConfigVersionDto>> SnapshotAsync(string domain, string? note, CancellationToken ct = default)
    {
        var d = domain?.Trim().ToLowerInvariant() ?? "";
        if (!Domains.Contains(d)) return Error.Validation("config_version.domain", $"Domaine inconnu. Attendu : {string.Join(", ", Domains)}.");

        var snapshot = await CaptureAsync(d, ct);
        var current = await db.Database.SqlQuery<string>($"""
            SELECT snapshot::text AS "Value" FROM nexora.config_version
            WHERE company_id = {Company} AND domain = {d} AND status = 'active' ORDER BY version_number DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        var diff = Diff(current, snapshot);

        var nextNumber = await db.Database.SqlQuery<int>($"""
            SELECT COALESCE(MAX(version_number), 0) + 1 AS "Value" FROM nexora.config_version WHERE company_id = {Company} AND domain = {d}
            """).FirstAsync(ct);

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.config_version (id, company_id, domain, version_number, status, snapshot, diff, note, created_by)
            VALUES ({id}, {Company}, {d}, {nextNumber}, 'draft', {snapshot}::jsonb, {diff}::jsonb, {note}, {tenant.UserId})
            """, ct);

        return Result.Success((await ListAsync(d, ct)).First(v => v.Id == id));
    }

    public async Task<Result<ConfigVersionDto>> ActivateAsync(Guid versionId, ActivateVersionRequest request, CancellationToken ct = default)
    {
        var v = (await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", domain AS "Domain", version_number AS "VersionNumber", status AS "Status",
                   effective_from AS "EffectiveFrom", effective_to AS "EffectiveTo", snapshot::text AS "Snapshot",
                   diff::text AS "Diff", note AS "Note", created_at AS "CreatedAt"
            FROM nexora.config_version WHERE id = {versionId} AND company_id = {Company}
            """).ToListAsync(ct)).FirstOrDefault();
        if (v is null) return Error.NotFound("config_version.version", "Version introuvable.");
        if (v.Status != "draft") return Error.Validation("config_version.status", "Seule une version brouillon peut être activée.");

        var effectiveFrom = request.EffectiveFrom ?? clock.UtcNow;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            // La version active précédente est datée de fin — l'historique n'est jamais réécrit (§39).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.config_version
                SET status = 'superseded', effective_to = {effectiveFrom}, updated_at = now()
                WHERE company_id = {Company} AND domain = {v.Domain} AND status = 'active'
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.config_version
                SET status = 'active', effective_from = {effectiveFrom}, effective_to = NULL,
                    approved_by = {tenant.UserId}, approved_at = now(),
                    note = COALESCE({request.Note}, note), updated_at = now()
                WHERE id = {versionId}
                """, token);
            return 0;
        }, ct);

        return Result.Success((await ListAsync(v.Domain, ct)).First(x => x.Id == versionId));
    }

    public async Task<ConfigVersionDto?> ActiveAtAsync(string domain, DateTimeOffset at, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", domain AS "Domain", version_number AS "VersionNumber", status AS "Status",
                   effective_from AS "EffectiveFrom", effective_to AS "EffectiveTo", snapshot::text AS "Snapshot",
                   diff::text AS "Diff", note AS "Note", created_at AS "CreatedAt"
            FROM nexora.config_version
            WHERE company_id = {Company} AND domain = {domain} AND status IN ('active','superseded')
              AND effective_from IS NOT NULL AND effective_from <= {at}
              AND (effective_to IS NULL OR effective_to > {at})
            ORDER BY effective_from DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return row is null ? null : Map(row);
    }

    private async Task<string> CaptureAsync(string domain, CancellationToken ct)
    {
        switch (domain)
        {
            case "numbering":
            {
                var rows = await db.Database.SqlQuery<KeyVal>($"""
                    SELECT (document_type || ':' || COALESCE(year::text, 'tpl')
                            || COALESCE(':' || establishment_id::text, '')) AS "K",
                           (prefix || separator || padding::text || CASE WHEN include_year THEN '+year' ELSE '' END) AS "V"
                    FROM nexora.document_sequence WHERE company_id = {Company} AND status = 'active'
                    ORDER BY 1
                    """).ToListAsync(ct);
                return JsonSerializer.Serialize(rows.GroupBy(r => r.K).ToDictionary(g => g.Key, g => g.First().V));
            }
            case "accounting_defaults":
            {
                var rows = await db.Database.SqlQuery<KeyVal>($"""
                    SELECT m.mapping_key AS "K", a.code AS "V"
                    FROM nexora.account_mapping m JOIN nexora.account a ON a.id = m.account_id
                    WHERE m.company_id = {Company} ORDER BY m.mapping_key
                    """).ToListAsync(ct);
                return JsonSerializer.Serialize(rows.GroupBy(r => r.K).ToDictionary(g => g.Key, g => g.First().V));
            }
            case "taxes":
            {
                var rows = await db.Database.SqlQuery<KeyVal>($"""
                    SELECT code AS "K", (rate::text || ':' || type || CASE WHEN is_inclusive THEN ':incl' ELSE '' END) AS "V"
                    FROM nexora.tax WHERE company_id = {Company} AND status = 'active' ORDER BY code
                    """).ToListAsync(ct);
                return JsonSerializer.Serialize(rows.GroupBy(r => r.K).ToDictionary(g => g.Key, g => g.First().V));
            }
            case "approvals":
            {
                var rows = await db.Database.SqlQuery<KeyVal>($"""
                    SELECT (request_type || ':' || COALESCE(configuration->>'workflow', 'base')) AS "K",
                           (min_amount::text || 'x' || required_levels::text) AS "V"
                    FROM nexora.approval_policy WHERE company_id = {Company} AND status = 'active'
                    ORDER BY 1
                    """).ToListAsync(ct);
                return JsonSerializer.Serialize(rows.GroupBy(r => r.K).ToDictionary(g => g.Key, g => g.First().V));
            }
            default:  // pos / stock : instantané des paramètres de la catégorie
            {
                var rows = await db.Database.SqlQuery<KeyVal>($"""
                    SELECT (s.scope || ':' || s.key) AS "K", s.value::text AS "V"
                    FROM nexora.setting s JOIN nexora.setting_definition d ON d.key = s.key
                    WHERE d.category = {domain} AND (s.company_id = {Company} OR s.company_id IS NULL)
                    ORDER BY 1
                    """).ToListAsync(ct);
                return JsonSerializer.Serialize(rows.GroupBy(r => r.K).ToDictionary(g => g.Key, g => g.First().V));
            }
        }
    }

    private static string Diff(string? before, string after)
    {
        Dictionary<string, string> Parse(string? j)
        {
            try { return JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(j) ? "{}" : j) ?? new(); }
            catch (JsonException) { return new(); }
        }
        var b = Parse(before);
        var a = Parse(after);
        var added = a.Keys.Except(b.Keys).ToDictionary(k => k, k => a[k]);
        var removed = b.Keys.Except(a.Keys).ToArray();
        var changed = a.Keys.Intersect(b.Keys).Where(k => a[k] != b[k]).ToDictionary(k => k, k => new { from = b[k], to = a[k] });
        return JsonSerializer.Serialize(new { added, removed, changed });
    }

    private static ConfigVersionDto Map(Row r) => new(
        r.Id, r.Domain, r.VersionNumber, r.Status, r.EffectiveFrom, r.EffectiveTo,
        SafeJson(r.Snapshot), SafeJson(r.Diff), r.Note, r.CreatedAt);

    private static JsonElement SafeJson(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }
}

/// <summary>Centre de tâches administratives : lacunes de configuration (Phase 14 §44).</summary>
public sealed class SqlAdminTaskService(NexoraDbContext db, ITenantContext tenant, ISettingsService settings) : IAdminTaskService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private static readonly string[] RequiredMappings =
        ["sales_revenue", "sales_tax_output", "accounts_receivable", "accounts_payable", "purchases", "inventory", "cogs", "cash"];

    public async Task<IReadOnlyList<AdminTaskDto>> ListAsync(CancellationToken ct = default)
    {
        var tasks = new List<AdminTaskDto>();

        var noPrice = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.product p
            WHERE p.company_id = {Company} AND p.status = 'active' AND p.is_sellable
              AND NOT EXISTS (SELECT 1 FROM nexora.product_price pp
                              WHERE pp.product_id = p.id AND pp.price_type = 'sale'
                                AND (pp.valid_to IS NULL OR pp.valid_to > now()))
            """).FirstAsync(ct);
        if (noPrice > 0) tasks.Add(new("products_without_price", "produits", "warning",
            "Produits sans prix de vente", "Ces produits ne peuvent pas être vendus au POS.", noPrice));

        var emptyJson = "{}";
        var noTax = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.product p
            WHERE p.company_id = {Company} AND p.status = 'active' AND p.tax_configuration = {emptyJson}::jsonb
            """).FirstAsync(ct);
        if (noTax > 0) tasks.Add(new("products_without_tax", "produits", "info",
            "Produits sans configuration de taxe", "Vérifiez le traitement fiscal de ces produits.", noTax));

        var mappings = await db.Database.SqlQuery<string>($"""
            SELECT mapping_key AS "Value" FROM nexora.account_mapping WHERE company_id = {Company}
            """).ToListAsync(ct);
        var missing = RequiredMappings.Except(mappings).ToArray();
        if (missing.Length > 0) tasks.Add(new("missing_default_accounts", "comptabilité", "critical",
            "Comptes par défaut manquants", $"Clés absentes : {string.Join(", ", missing)}.", missing.Length));

        var noRole = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(DISTINCT sc.user_id)::int AS "Value"
            FROM (SELECT ur.user_id, ur.scope_id FROM nexora.user_role ur) sc
            JOIN nexora.access_scope s ON s.id = sc.scope_id AND s.company_id = {Company}
            WHERE NOT EXISTS (
                SELECT 1 FROM nexora.user_role ur2
                JOIN nexora.role r ON r.id = ur2.role_id
                JOIN nexora.access_scope s2 ON s2.id = ur2.scope_id
                WHERE ur2.user_id = sc.user_id AND s2.company_id = {Company} AND r.status = 'active')
            """).FirstAsync(ct);
        if (noRole > 0) tasks.Add(new("users_without_active_role", "utilisateurs", "warning",
            "Utilisateurs sans rôle actif", "Ces utilisateurs n'ont que des rôles désactivés sur l'entreprise.", noRole));

        var backup = await settings.ResolveAsync("backup.enabled", null, ct);
        if (backup.Value.ValueKind != System.Text.Json.JsonValueKind.True)
            tasks.Add(new("backup_not_configured", "sauvegarde", "warning",
                "Sauvegarde non configurée", "Activez la sauvegarde automatique (paramètre backup.enabled).", 1));

        var connErrors = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.integration_connector WHERE company_id = {Company} AND status = 'error'
            """).FirstAsync(ct);
        if (connErrors > 0) tasks.Add(new("integrations_in_error", "intégrations", "critical",
            "Connecteurs en erreur", "Testez la connexion ou reconfigurez.", connErrors));

        var untested = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.workflow WHERE company_id = {Company} AND status <> 'active' AND is_tested = false
            """).FirstAsync(ct);
        if (untested > 0) tasks.Add(new("workflows_untested", "workflows", "info",
            "Workflows non testés", "Testez-les avant activation (§16).", untested));

        var pendingClose = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.accounting_period
            WHERE company_id = {Company} AND status = 'open' AND end_date < current_date
            """).FirstAsync(ct);
        if (pendingClose > 0) tasks.Add(new("periods_pending_close", "comptabilité", "warning",
            "Périodes comptables à clôturer", "Des périodes échues restent ouvertes.", pendingClose));

        return tasks;
    }
}
