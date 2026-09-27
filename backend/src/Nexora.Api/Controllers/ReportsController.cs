using System.Text;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Application.Security;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Rapports opérationnels et financiers (Phase 7 §13-24). Tous les rapports partent
/// des données sources ; aucun chiffre n'est stocké en double. Les exports sont
/// journalisés et respectent filtres et permissions (§30, §36).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/reports")]
public sealed class ReportsController(
    ISalesReports sales,
    IStockReports stock,
    IAgingReports aging,
    IBudgetReports budget,
    IExportService export,
    IPermissionService permissions,
    ITenantContext tenant) : ApiControllerBase
{
    // --- Ventes (§13-14) — chiffre d'affaires : reporting.view_sales en plus (guide §18) ---------

    [HttpGet("sales/journal")]
    [RequirePermission(Permissions.Reporting.View)]
    [RequirePermission(Permissions.Reporting.ViewSales)]
    public async Task<IActionResult> SalesJournal([FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await sales.JournalAsync(filter, ct));

    [HttpGet("sales/breakdown/{dimension}")]
    [RequirePermission(Permissions.Reporting.View)]
    [RequirePermission(Permissions.Reporting.ViewSales)]
    public async Task<IActionResult> SalesBreakdown(string dimension, [FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await sales.BreakdownAsync(dimension, filter, ct));

    [HttpGet("sales/top-products")]
    [RequirePermission(Permissions.Reporting.View)]
    [RequirePermission(Permissions.Reporting.ViewSales)]
    public async Task<IActionResult> TopProducts([FromQuery] ReportFilter filter, [FromQuery] int take = 20, CancellationToken ct = default)
        => Ok(await sales.TopProductsAsync(filter, take, ct));

    // --- Stock (§15) --------------------------------------------------

    [HttpGet("stock/valuation")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> StockValuation([FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await stock.ValuationAsync(filter, ct));

    [HttpGet("stock/ruptures")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> StockRuptures([FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await stock.RupturesAsync(filter, ct));

    [HttpGet("stock/dormant")]
    [RequirePermission(Permissions.Reporting.View)]
    public async Task<IActionResult> StockDormant([FromQuery] ReportFilter filter, [FromQuery] int days = 90, CancellationToken ct = default)
        => Ok(await stock.DormantAsync(filter, days, ct));

    // --- Antériorité des soldes (§17-19) -----------------------------

    [HttpGet("aging/receivables")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> AgingReceivables([FromQuery] DateOnly? asOf, CancellationToken ct)
        => Ok(await aging.ReceivablesAsync(asOf, ct));

    [HttpGet("aging/payables")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> AgingPayables([FromQuery] DateOnly? asOf, CancellationToken ct)
        => Ok(await aging.PayablesAsync(asOf, ct));

    // --- Budget vs réalisé (§24) -----------------------------------

    [HttpGet("budget-vs-actual")]
    [RequirePermission(Permissions.Reporting.ViewFinancial)]
    public async Task<IActionResult> BudgetVsActual([FromQuery] int fiscalYear, [FromQuery] ReportFilter filter, CancellationToken ct)
        => Ok(await budget.BudgetVsActualAsync(fiscalYear, filter, ct));

    // --- Export (§30, §36) ----------------------------------------

    [HttpGet("~/api/v{version:apiVersion}/reporting/exports")]
    [RequirePermission(Permissions.Reporting.Export)]
    public async Task<IActionResult> ExportHistory(CancellationToken ct) => Ok(await export.HistoryAsync(ct));

    [HttpPost("export")]
    [RequirePermission(Permissions.Reporting.Export)]
    public async Task<IActionResult> Export([FromBody] ExportRequest request, CancellationToken ct)
    {
        // Deny-by-default : un rapport financier exige la permission dédiée (§36).
        if (ReportCatalog.IsFinancial(request.ReportKey) && tenant.UserId is { } uid)
        {
            var profile = await permissions.LoadProfileAsync(uid, ct);
            if (!profile.HasPermission(Permissions.Reporting.ExportFinancial, tenant.ActiveCompanyId))
                throw new DomainException(Error.Forbidden("reporting.export_financial",
                    "L'export de ce rapport financier requiert la permission reporting.export_financial."));
        }

        var result = await export.ExportAsync(request, ct);
        return Download(result.Content, result.ContentType, result.FileName);
    }
}
