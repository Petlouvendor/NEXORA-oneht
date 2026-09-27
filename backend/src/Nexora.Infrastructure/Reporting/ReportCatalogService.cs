using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Catalogue de rapports & rapports personnalisés (Phase 42 §48-49) — documente les rapports déjà
/// livrés (Phases 7/9/28/29) et active <c>saved_report</c> (V026). <see cref="RunByKeyAsync"/> ne
/// réexécute que les rapports fondés sur <see cref="ReportFilter"/> (§55) ; les autres restent
/// documentés avec leur point d'entrée dédié — jamais un aiguillage silencieusement incomplet.
/// </summary>
public sealed class SqlReportCatalogService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow,
    ISalesReports sales, IStockReports stock, IAgingReports aging, IPermissionService permissions) : IReportCatalogService
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly string[] GenericKeys =
        ["sales_journal", "stock_valuation", "stock_ruptures", "stock_dormant", "top_products", "aging_receivables", "aging_payables"];

    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    public IReadOnlyList<ReportCatalogEntryDto> StandardReports() =>
    [
        new("sales_journal", "sales", "Journal des ventes", "Détail des ventes de la période", "Phase 7", ["reporting.view", "reporting.view_sales"], true, "/reports/sales/journal"),
        new("top_products", "sales", "Top produits", "Classement des produits par ventes", "Phase 7", ["reporting.view", "reporting.view_sales"], true, "/reports/sales/top-products"),
        new("stock_valuation", "stock", "Valorisation du stock", "Quantité et valeur par produit", "Phase 7", ["reporting.view_financial"], true, "/reports/stock/valuation"),
        new("stock_ruptures", "stock", "Ruptures de stock", "Produits sous le point de commande", "Phase 7", ["reporting.view"], true, "/reports/stock/ruptures"),
        new("stock_dormant", "stock", "Stock dormant", "Produits sans mouvement récent", "Phase 7", ["reporting.view"], true, "/reports/stock/dormant"),
        new("aging_receivables", "finance", "Balance âgée clients", "Créances par tranche d'ancienneté", "Phase 7", ["reporting.view_financial"], true, "/reports/aging/receivables"),
        new("aging_payables", "finance", "Balance âgée fournisseurs", "Dettes par tranche d'ancienneté", "Phase 7", ["reporting.view_financial"], true, "/reports/aging/payables"),
        new("budget_vs_actual", "finance", "Budget vs réalisé", "Écart budget/réel par compte", "Phase 7", ["reporting.view_financial"], false, "/reports/budget-vs-actual?fiscalYear="),
        new("trial_balance", "accounting", "Balance générale", "Balance des comptes de la période", "Phase 29", ["accounting.view"], false, "/accounting/reports/trial-balance"),
        new("general_ledger", "accounting", "Grand livre", "Écritures détaillées par compte", "Phase 29", ["accounting.view"], false, "/accounting/reports/general-ledger"),
        new("income_statement", "accounting", "Compte de résultat", "Produits, charges et résultat", "Phase 29", ["accounting.view"], false, "/accounting/reports/income-statement"),
        new("balance_sheet", "accounting", "Bilan", "Actif, passif et capitaux propres", "Phase 29", ["accounting.view"], false, "/accounting/reports/balance-sheet"),
        new("treasury_position", "finance", "Position de trésorerie", "Soldes convertis en devise fonctionnelle", "Phase 28", ["finance.view"], false, "/treasury/position"),
        new("treasury_liquidity", "finance", "Liquidité & gap", "Écarts d'échéance et alertes", "Phase 28", ["finance.view"], false, "/treasury/liquidity"),
        new("cashflow_forecast", "finance", "Prévision de trésorerie par échéance", "Encaissements/décaissements attendus par tranche", "Phase 28", ["finance.view"], false, "/cashflow/forecast"),
        new("consolidated_statements", "group", "États consolidés groupe", "Compte de résultat et bilan consolidés", "Phase 9", ["reporting.view_group"], false, "/groups/{groupId}/consolidation/runs/{runId}/statements"),
    ];

    // ------------------------------------------------------------- Rapports enregistrés (§49)

    private sealed record Row(Guid Id, string Name, string ReportKey, string Filter, bool IsShared, Guid? UserId, DateTimeOffset UpdatedAt);

    private const string Select = """
        SELECT id AS "Id", name AS "Name", report_key AS "ReportKey", filter::text AS "Filter",
               is_shared AS "IsShared", user_id AS "UserId", updated_at AS "UpdatedAt"
        FROM nexora.saved_report
        """;

    public async Task<IReadOnlyList<SavedReportDto>> ListSavedAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} AND (user_id = {1} OR is_shared) ORDER BY updated_at DESC",
            Company, (object?)tenant.UserId ?? DBNull.Value)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<SavedReportDto>> SaveAsync(SaveSavedReportRequest request, CancellationToken ct = default)
    {
        var key = request.ReportKey.Trim().ToLowerInvariant();
        if (!GenericKeys.Contains(key))
            return Error.Validation("saved_report.report_key",
                $"« {key} » n'est pas réexécutable génériquement — consultez le catalogue pour son point d'entrée dédié.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("saved_report.name", "Nom requis.");
        if (await ForbiddenReportAsync(key, ct) is { } forbidden) return forbidden;

        var id = request.Id ?? Guid.NewGuid();
        var filterJson = request.Filter.GetRawText();
        if (request.Id is { } existingId)
        {
            var updated = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.saved_report SET name = {request.Name}, report_key = {key}, filter = {filterJson}::jsonb,
                       is_shared = {request.IsShared}, updated_at = now()
                WHERE id = {existingId} AND company_id = {Company} AND user_id = {tenant.UserId}
                """, ct);
            if (updated == 0) return Error.NotFound("saved_report.not_found", "Rapport enregistré introuvable.");
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.saved_report (id, company_id, user_id, name, report_key, filter, is_shared)
                VALUES ({id}, {Company}, {tenant.UserId}, {request.Name}, {key}, {filterJson}::jsonb, {request.IsShared})
                """, ct);
        }
        await uow.SaveChangesAsync(ct);

        var saved = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstAsync(ct);
        return Result.Success(ToDto(saved));
    }

    public async Task<Result> DeleteSavedAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.saved_report WHERE id = {id} AND company_id = {Company} AND user_id = {tenant.UserId}
            """, ct);
        if (rows == 0) return Error.NotFound("saved_report.not_found", "Rapport enregistré introuvable ou non-propriétaire.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<ReportRunResultDto>> RunAsync(Guid savedReportId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE id = {0} AND company_id = {1} AND (user_id = {2} OR is_shared)",
            savedReportId, Company, (object?)tenant.UserId ?? DBNull.Value)
            .FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("saved_report.not_found", "Rapport enregistré introuvable.");
        // Un rapport enregistré (ou partagé) ne contourne jamais les permissions du rapport lui-même :
        // « reporting.view » ne suffit pas pour une balance âgée ou un journal des ventes (guide §17, §18).
        if (await ForbiddenReportAsync(row.ReportKey, ct) is { } forbidden) return forbidden;

        var result = await RunByKeyAsync(row.ReportKey, row.Filter, ct);
        return result.IsSuccess
            ? Result.Success(new ReportRunResultDto(row.ReportKey, DateTimeOffset.UtcNow, result.Value))
            : Result.Failure<ReportRunResultDto>(result.Error!);
    }

    private async Task<Error?> ForbiddenReportAsync(string reportKey, CancellationToken ct)
    {
        var entry = StandardReports().FirstOrDefault(r => r.Key == reportKey.Trim().ToLowerInvariant());
        if (entry is null) return null;
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var missing = entry.RolesAllowed.FirstOrDefault(p => !granted.Contains(p));
        return missing is null ? null : Error.Forbidden("report.forbidden", $"Ce rapport exige la permission {missing}.");
    }

    public async Task<Result<JsonElement>> RunByKeyAsync(string reportKey, string filterJson, CancellationToken ct = default)
    {
        var key = reportKey.Trim().ToLowerInvariant();
        if (!GenericKeys.Contains(key))
            return Error.Validation("report.not_runnable",
                $"« {key} » n'est pas réexécutable génériquement — consultez le catalogue pour son point d'entrée dédié.");

        var filter = string.IsNullOrWhiteSpace(filterJson) || filterJson == "{}"
            ? new ReportFilter()
            : JsonSerializer.Deserialize<ReportFilter>(filterJson, Web) ?? new ReportFilter();
        var extra = string.IsNullOrWhiteSpace(filterJson) ? default : JsonDocument.Parse(filterJson).RootElement;
        int IntOr(string prop, int fallback) =>
            extra.ValueKind == JsonValueKind.Object && extra.TryGetProperty(prop, out var v) && v.TryGetInt32(out var n) ? n : fallback;

        object payload = key switch
        {
            "sales_journal" => await sales.JournalAsync(filter, ct),
            "top_products" => await sales.TopProductsAsync(filter, IntOr("take", 20), ct),
            "stock_valuation" => await stock.ValuationAsync(filter, ct),
            "stock_ruptures" => await stock.RupturesAsync(filter, ct),
            "stock_dormant" => await stock.DormantAsync(filter, IntOr("days", 90), ct),
            "aging_receivables" => await aging.ReceivablesAsync(ct: ct),
            "aging_payables" => await aging.PayablesAsync(ct: ct),
            _ => throw new InvalidOperationException($"Rapport « {key} » non branché."),
        };
        return Result.Success(JsonSerializer.SerializeToElement(payload, Web));
    }

    private SavedReportDto ToDto(Row r) => new(r.Id, r.Name, r.ReportKey,
        JsonDocument.Parse(r.Filter).RootElement, r.IsShared, r.UserId == tenant.UserId, r.UpdatedAt);
}
