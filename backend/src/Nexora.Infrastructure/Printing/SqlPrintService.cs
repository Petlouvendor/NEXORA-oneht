using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Printing;
using Nexora.Contracts.Printing;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Printing;

/// <summary>
/// Rendu d'un document pour l'entreprise active : en-tête (nom, raison sociale, NIF, adresse et
/// téléphone de l'établissement, pied de page et couleur de la marque), « Imprimé le … par … », puis
/// journalisation dans <c>export_log</c> — chaque impression ou export est tracé (qui, quoi, format).
/// </summary>
public sealed class SqlPrintService(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, IDocumentRenderer renderer) : IPrintService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    public async Task<RenderedFile> RenderAsync(
        PrintDocument document, string format, string logKey, string fileBaseName,
        string sensitivity = "normal", Guid? sourceId = null, Guid? establishmentId = null, CancellationToken ct = default)
    {
        var issuer = await IssuerAsync(establishmentId, ct);
        var printedBy = tenant.UserId is { } uid
            ? await db.Database.SqlQuery<string?>($"""
                SELECT full_name AS "Value" FROM nexora.app_user WHERE id = {uid}
                """).FirstOrDefaultAsync(ct)
            : null;
        var normalized = PrintFormats.Normalize(format);
        var file = renderer.Render(document, issuer, new PrintMeta(clock.UtcNow, printedBy), normalized, fileBaseName);

        var rows = document.Tables?.Sum(t => t.Rows.Count) ?? 0;
        var filter = JsonSerializer.Serialize(new { sourceId, reference = document.Reference, title = document.Title });
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.export_log (company_id, user_id, report_key, filter, format, row_count, sensitivity)
            VALUES ({Company}, {tenant.UserId}, {logKey}, {filter}::jsonb, {normalized}, {rows}, {sensitivity})
            """, ct);
        return file;
    }

    public async Task<int> PreviousPrintsAsync(string logKey, Guid sourceId, CancellationToken ct = default)
    {
        var id = sourceId.ToString();
        return await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.export_log
            WHERE company_id = {Company} AND report_key = {logKey} AND filter ->> 'sourceId' = {id}
            """).FirstAsync(ct);
    }

    private async Task<PrintIssuer> IssuerAsync(Guid? establishmentId, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<IssuerRow>($"""
            SELECT coalesce(nullif(btrim(b.display_name), ''), co.name) AS "Name", co.legal_name AS "LegalName",
                   co.tax_identifier AS "TaxId", e.address AS "Address", e.phone AS "Phone",
                   b.document_footer AS "Footer", b.accent_color AS "AccentColor", co.timezone AS "TimeZone"
            FROM nexora.company co
            LEFT JOIN nexora.company_branding b ON b.company_id = co.id
            LEFT JOIN LATERAL (
                SELECT es.address, es.phone FROM nexora.establishment es
                WHERE es.company_id = co.id
                ORDER BY (es.id = {establishmentId}) DESC, (es.status = 'active') DESC, es.code
                LIMIT 1) e ON true
            WHERE co.id = {Company}
            """).FirstAsync(ct);
        return new PrintIssuer(row.Name, row.LegalName, row.TaxId, row.Address, row.Phone, row.Footer, row.AccentColor, row.TimeZone);
    }

    private sealed record IssuerRow(
        string Name, string? LegalName, string? TaxId, string? Address, string? Phone, string? Footer,
        string? AccentColor, string TimeZone);
}
