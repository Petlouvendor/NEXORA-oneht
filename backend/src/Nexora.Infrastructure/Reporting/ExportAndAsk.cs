using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Application.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Printing;
using Nexora.Contracts.Reporting;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Export des rapports de pilotage conforme aux filtres et permissions, journalisé (§30, §36) : vrai
/// classeur Excel (.xlsx), PDF ou CSV, rendus par le moteur d'impression commun (cellules typées).
/// </summary>
public sealed class ReportExportService(
    NexoraDbContext db, ITenantContext tenant, IPrintService print,
    ISalesReports sales, IStockReports stock, IAgingReports aging) : IExportService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken ct = default)
    {
        var format = PrintFormats.Normalize(request.Format);
        if (!PrintFormats.IsValid(format))
            throw new InvalidOperationException($"Format « {request.Format} » inconnu (csv, xlsx, pdf).");
        var (doc, sensitivity) = request.ReportKey switch
        {
            "sales_journal" => await SalesJournal(request.Filter, ct),
            "stock_valuation" => await StockValuation(request.Filter, ct),
            "aging_receivables" => await Aging("receivable", ct),
            "aging_payables" => await Aging("payable", ct),
            _ => throw new InvalidOperationException($"Rapport « {request.ReportKey} » non exportable."),
        };
        var file = await print.RenderAsync(doc, format, request.ReportKey, request.ReportKey, sensitivity, ct: ct);
        var rows = doc.Tables?.Sum(t => t.Rows.Count) ?? 0;
        return new ExportResult(file.FileName, file.ContentType, file.Content, rows, sensitivity);
    }

    public async Task<IReadOnlyList<ExportHistoryEntryDto>> HistoryAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<ExportHistoryRow>($"""
            SELECT id AS "Id", report_key AS "ReportKey", format AS "Format", row_count AS "RowCount",
                   sensitivity AS "Sensitivity", created_at AS "CreatedAt"
            FROM nexora.export_log WHERE company_id = {Company} ORDER BY created_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(r => new ExportHistoryEntryDto(r.Id, r.ReportKey, r.Format, r.RowCount, r.Sensitivity, r.CreatedAt)).ToList();
    }

    private sealed record ExportHistoryRow(Guid Id, string ReportKey, string Format, int? RowCount, string Sensitivity, DateTimeOffset CreatedAt);

    private async Task<(PrintDocument, string)> SalesJournal(ReportFilter f, CancellationToken ct)
    {
        var j = await sales.JournalAsync(f with { PageSize = 5000 }, ct);
        var rows = j.Rows.Select(r => (IReadOnlyList<object?>)
            [r.Date, r.DocumentNumber, r.Customer, r.Salesperson, r.SaleType, r.Subtotal, r.Discount, r.Tax, r.Total, r.Paid]).ToList();
        var table = new PrintTable(null,
            [new("Date", PrintKinds.Date), new("Document"), new("Client"), new("Vendeur"), new("Type"),
             new("Sous-total", PrintKinds.Money), new("Remise", PrintKinds.Money), new("Taxe", PrintKinds.Money),
             new("Total", PrintKinds.Money), new("Payé", PrintKinds.Money)],
            rows,
            [null, "Totaux", null, null, null, j.Rows.Sum(r => r.Subtotal), j.Rows.Sum(r => r.Discount), j.Rows.Sum(r => r.Tax),
             j.Rows.Sum(r => r.Total), j.Rows.Sum(r => r.Paid)]);
        return (new PrintDocument("Journal des ventes", Subtitle: $"Du {j.From:dd/MM/yyyy} au {j.To:dd/MM/yyyy} — {j.Currency}",
            Tables: [table],
            Summary:
            [
                new("Nombre de ventes", j.Count, PrintKinds.Integer),
                new("Taxes", j.TotalTax, PrintKinds.Money, j.Currency),
                new("Chiffre d'affaires", j.TotalRevenue, PrintKinds.Money, j.Currency, Emphasis: true),
            ], Landscape: true), "financial");
    }

    private async Task<(PrintDocument, string)> StockValuation(ReportFilter f, CancellationToken ct)
    {
        var v = await stock.ValuationAsync(f, ct);
        var table = new PrintTable(null,
            [new("SKU"), new("Produit"), new("Quantité", PrintKinds.Number), new("Coût moyen", PrintKinds.Money, v.Currency),
             new("Valeur", PrintKinds.Money, v.Currency)],
            v.Rows.Select(r => (IReadOnlyList<object?>)[r.Sku, r.Name, r.OnHand, r.AverageCost, r.Value]).ToList(),
            [null, "Total", v.Rows.Sum(r => r.OnHand), null, v.TotalValue]);
        return (new PrintDocument("Valorisation du stock", Subtitle: $"Au {v.AsOf:dd/MM/yyyy}", Tables: [table],
            Summary: [new("Valeur totale du stock", v.TotalValue, PrintKinds.Money, v.Currency, Emphasis: true)]), "normal");
    }

    private async Task<(PrintDocument, string)> Aging(string kind, CancellationToken ct)
    {
        var a = kind == "receivable" ? await aging.ReceivablesAsync(ct: ct) : await aging.PayablesAsync(ct: ct);
        var columns = new List<PrintColumn> { new(kind == "receivable" ? "Client" : "Fournisseur"), new("Total", PrintKinds.Money, a.Currency) };
        columns.AddRange(a.BucketLabels.Select(l => new PrintColumn(l, PrintKinds.Money, a.Currency)));
        var rows = a.Rows.Select(r => (IReadOnlyList<object?>)new object?[] { r.Party, r.Total }.Concat(r.Buckets.Cast<object?>()).ToList()).ToList();
        var totals = new List<object?> { "Total", a.GrandTotal };
        totals.AddRange(Enumerable.Range(0, a.BucketLabels.Count).Select(i => (object?)a.Rows.Sum(r => i < r.Buckets.Count ? r.Buckets[i] : 0m)));
        return (new PrintDocument(kind == "receivable" ? "Balance âgée clients" : "Balance âgée fournisseurs",
            Subtitle: $"Au {a.AsOf:dd/MM/yyyy}", Tables: [new PrintTable(null, columns, rows, totals)],
            Summary: [new("Total dû", a.GrandTotal, PrintKinds.Money, a.Currency, Emphasis: true)]), "financial");
    }
}
