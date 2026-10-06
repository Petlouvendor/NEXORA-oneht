using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Migration;
using Nexora.Contracts.Migration;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Migration;

// =============================================================================
// Checklist d'onboarding + score de migration (Phase 39 §31-34). La plupart des
// items sont dérivés en direct de l'état des données ; `onboarding_task` porte les
// acquittements manuels (formation, notifications, tests…).
// =============================================================================
public sealed class SqlOnboardingService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IDateTimeProvider clock)
    : IOnboardingService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Item(string Key, string Label, string Category, bool Auto, Func<Counts, bool>? Derived);

    private static readonly Item[] Definition =
    [
        new("company",        "Entreprise créée",          "base",       true,  _ => true),
        new("currency",       "Devise par défaut",         "base",       true,  c => c.HasCurrency),
        new("logo",           "Logo / identité visuelle",  "base",       true,  c => c.HasLogo),
        new("taxes",          "Taxes paramétrées",         "base",       true,  c => c.Taxes > 0),
        new("establishments", "Établissement(s)",          "base",       true,  c => c.Establishments > 0),
        new("warehouses",     "Entrepôt(s)",               "stock",      true,  c => c.Warehouses > 0),
        new("registers",      "Caisse(s)",                 "finance",    true,  c => c.Registers > 0),
        new("users",          "Utilisateurs & rôles",      "base",       true,  c => c.Users > 0),
        new("products",       "Produits importés",         "data",       true,  c => c.Products > 0),
        new("customers",      "Clients importés",          "data",       true,  c => c.Customers > 0),
        new("suppliers",      "Fournisseurs importés",     "data",       true,  c => c.Suppliers > 0),
        new("stock_initial",  "Stock d'ouverture",         "stock",      true,  c => c.OpeningStock > 0),
        new("accounts",       "Plan comptable",            "accounting", true,  c => c.Accounts > 0),
        new("payments",       "Comptes financiers",        "finance",    true,  c => c.FinancialAccounts > 0),
        new("documents",      "Séquences de documents",    "base",       true,  c => c.DocumentSequences > 0),
        new("backup",         "Politique de sauvegarde",   "base",       true,  c => c.HasBackupPolicy),
        new("notifications",  "Notifications configurées", "base",       false, null),
        new("tests",          "Tests de bout en bout",     "base",       false, null),
    ];

    private sealed record Counts(
        bool HasCurrency, bool HasLogo, int Taxes, int Establishments, int Warehouses, int Registers, int Users,
        int Products, int Customers, int Suppliers, int OpeningStock, int Accounts, int FinancialAccounts,
        int DocumentSequences, bool HasBackupPolicy, int ImportsCompleted, int ImportsMapped,
        int ImportErrorRows, int ImportTotalRows);

    public async Task<OnboardingChecklistDto> GetChecklistAsync(CancellationToken ct = default)
    {
        var company = Company;
        var c = await db.Database.SqlQuery<Counts>($"""
            SELECT
              (SELECT default_currency_id IS NOT NULL FROM nexora.company WHERE id = {company}) AS "HasCurrency",
              EXISTS(SELECT 1 FROM nexora.company_branding WHERE company_id = {company} AND logo_url IS NOT NULL) AS "HasLogo",
              (SELECT COUNT(*)::int FROM nexora.tax WHERE company_id = {company}) AS "Taxes",
              (SELECT COUNT(*)::int FROM nexora.establishment WHERE company_id = {company}) AS "Establishments",
              (SELECT COUNT(*)::int FROM nexora.warehouse WHERE company_id = {company}) AS "Warehouses",
              (SELECT COUNT(*)::int FROM nexora.cash_register WHERE company_id = {company}) AS "Registers",
              (SELECT COUNT(DISTINCT ur.user_id)::int FROM nexora.user_role ur
                 JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                 WHERE ur.revoked_at IS NULL AND sc.company_id = {company}) AS "Users",
              (SELECT COUNT(*)::int FROM nexora.product WHERE company_id = {company}) AS "Products",
              (SELECT COUNT(*)::int FROM nexora.customer WHERE company_id = {company}) AS "Customers",
              (SELECT COUNT(*)::int FROM nexora.supplier WHERE company_id = {company}) AS "Suppliers",
              (SELECT COUNT(*)::int FROM nexora.stock_movement WHERE company_id = {company} AND movement_type = 'opening') AS "OpeningStock",
              (SELECT COUNT(*)::int FROM nexora.account WHERE company_id = {company}) AS "Accounts",
              (SELECT COUNT(*)::int FROM nexora.financial_account WHERE company_id = {company}) AS "FinancialAccounts",
              (SELECT COUNT(*)::int FROM nexora.document_sequence WHERE company_id = {company}) AS "DocumentSequences",
              EXISTS(SELECT 1 FROM nexora.backup_policy WHERE company_id = {company}) AS "HasBackupPolicy",
              (SELECT COUNT(*)::int FROM nexora.import_job WHERE company_id = {company} AND status = 'completed') AS "ImportsCompleted",
              (SELECT COUNT(*)::int FROM nexora.import_job WHERE company_id = {company} AND status IN ('mapped','validated','simulated','completed')) AS "ImportsMapped",
              (SELECT COALESCE(SUM(error_rows), 0)::int FROM nexora.import_job WHERE company_id = {company}) AS "ImportErrorRows",
              (SELECT COALESCE(SUM(total_rows), 0)::int FROM nexora.import_job WHERE company_id = {company}) AS "ImportTotalRows"
            """).FirstAsync(ct);

        var manual = (await db.Database.SqlQuery<ManualTask>($"""
            SELECT task_key AS "Key", status AS "Status" FROM nexora.onboarding_task WHERE company_id = {company}
            """).ToListAsync(ct)).ToDictionary(x => x.Key, x => x.Status);

        var items = new List<OnboardingItemDto>();
        foreach (var def in Definition)
        {
            string status, source, detail;
            if (def.Auto && def.Derived is not null)
            {
                var isDone = def.Derived(c);
                var over = manual.GetValueOrDefault(def.Key);
                status = isDone ? "done" : over == "skipped" ? "skipped" : over == "done" ? "done" : "pending";
                source = over is null ? "auto" : "manual";
                detail = isDone ? "OK" : "À compléter dans le module concerné.";
            }
            else
            {
                var over = manual.GetValueOrDefault(def.Key, "pending");
                status = over;
                source = "manual";
                detail = over == "done" ? "Marqué comme fait." : over == "skipped" ? "Ignoré." : "À cocher manuellement.";
            }
            items.Add(new OnboardingItemDto(def.Key, def.Label, def.Category, status, source, detail));
        }

        var done = items.Count(i => i.Status is "done" or "skipped");
        var progress = items.Count == 0 ? 0m : Math.Round(100m * done / items.Count, 1);

        // Score de migration (§34).
        var dataItems = items.Where(i => i.Category == "data" || i.Key == "stock_initial").ToList();
        var dataScore = dataItems.Count == 0 ? 0 : (int)Math.Round(100.0 * dataItems.Count(i => i.Status == "done") / dataItems.Count);
        var mappingScore = c.ImportsCompleted > 0 ? 100 : c.ImportsMapped > 0 ? 60 : 0;
        var qualityScore = c.ImportTotalRows == 0 ? 100 : (int)Math.Round(100.0 * (c.ImportTotalRows - c.ImportErrorRows) / c.ImportTotalRows);
        var accountingItems = items.Where(i => i.Category == "accounting").ToList();
        var accountingScore = accountingItems.Count == 0 ? 0 : (int)Math.Round(100.0 * accountingItems.Count(i => i.Status == "done") / accountingItems.Count);
        var ready = items.Where(i => i.Source == "auto").All(i => i.Status is "done" or "skipped") && dataScore >= 60;
        var score = new MigrationScoreDto(dataScore, mappingScore, qualityScore, accountingScore, ready);

        return new OnboardingChecklistDto(done, items.Count, progress, score, items, clock.UtcNow);
    }
    private sealed record ManualTask(string Key, string Status);

    public async Task<Result<OnboardingChecklistDto>> CompleteTaskAsync(CompleteOnboardingTaskRequest r, CancellationToken ct = default)
    {
        if (Definition.All(d => d.Key != r.Key)) return Error.Validation("onboarding.key", "Étape d'onboarding inconnue.");
        var status = r.Status is "done" or "skipped" or "pending" ? r.Status : "done";

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.onboarding_task (company_id, task_key, status, note, completed_by, completed_at)
            VALUES ({Company}, {r.Key}, {status}, {r.Note}, {tenant.UserId}, {(status == "done" ? clock.UtcNow : (DateTimeOffset?)null)})
            ON CONFLICT ON CONSTRAINT onboarding_task_uk DO UPDATE SET
                status = EXCLUDED.status, note = EXCLUDED.note, completed_by = EXCLUDED.completed_by,
                completed_at = EXCLUDED.completed_at, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(await GetChecklistAsync(ct));
    }
}
