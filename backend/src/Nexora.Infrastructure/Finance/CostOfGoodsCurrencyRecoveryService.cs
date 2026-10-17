using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Abstractions;
using Nexora.Domain.Accounting;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

/// <inheritdoc cref="ICostOfGoodsCurrencyRecovery"/>
public sealed class CostOfGoodsCurrencyRecoveryService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IAccountingPeriods periods,
    IDocumentNumberGenerator numbers, IBusinessCalendar calendar) : ICostOfGoodsCurrencyRecovery
{
    /// <summary>Type de pièce des écritures correctives : leur <c>source_id</c> est l'écriture corrigée.</summary>
    public const string SourceType = "cogs_currency_correction";

    private sealed record Row(
        Guid CompanyId, string CompanyName, Guid? GroupId, Guid EntryId, string EntryNumber, DateOnly EntryDate,
        string EntrySourceType, Guid AccountId, Guid FunctionalCurrencyId, string FunctionalCurrency,
        decimal Debit, decimal Credit, decimal BaseDebit, decimal BaseCredit);

    public async Task<IReadOnlyList<CostOfGoodsCorrectionLine>> RunAsync(bool apply, CancellationToken ct = default)
    {
        List<Row> rows;
        using (tenant.BeginSystemScope())
            // Lignes de coût des ventes et de stock d'une écriture de vente ou de retour, tenues dans une autre devise que
            // la devise fonctionnelle et dont la base diffère du montant : le montant (en vérité déjà en devise
            // fonctionnelle) a été converti. Seules les écritures validées non extournées : une écriture extournée et son
            // miroir s'annulent déjà.
            rows = await db.Database.SqlQuery<Row>($"""
                SELECT co.id AS "CompanyId", co.name AS "CompanyName", co.group_id AS "GroupId",
                       e.id AS "EntryId", e.entry_number AS "EntryNumber", e.entry_date AS "EntryDate",
                       e.source_type AS "EntrySourceType", l.account_id AS "AccountId",
                       co.default_currency_id AS "FunctionalCurrencyId", fc.code AS "FunctionalCurrency",
                       l.debit AS "Debit", l.credit AS "Credit", l.base_debit AS "BaseDebit", l.base_credit AS "BaseCredit"
                FROM nexora.journal_entry e
                JOIN nexora.company co ON co.id = e.company_id
                JOIN nexora.currency fc ON fc.id = co.default_currency_id
                JOIN nexora.journal_entry_line l ON l.journal_entry_id = e.id
                JOIN nexora.account_mapping m ON m.company_id = e.company_id AND m.account_id = l.account_id
                                             AND m.mapping_key IN ({AccountKeys.Cogs}, {AccountKeys.Inventory})
                WHERE e.status = 'posted' AND e.origin = 'system' AND e.source_type IN ('sale', 'sale_return')
                  AND l.currency_id <> co.default_currency_id
                  AND (l.base_debit <> l.debit OR l.base_credit <> l.credit)
                  AND NOT EXISTS (SELECT 1 FROM nexora.journal_entry c
                                  WHERE c.source_type = {SourceType} AND c.source_id = e.id)
                ORDER BY co.name, e.entry_date, e.entry_number
                """).ToListAsync(ct);

        var lines = new List<CostOfGoodsCorrectionLine>();
        foreach (var entry in rows.GroupBy(r => r.EntryId))
        {
            var first = entry.First();
            // Excédent porté par chaque ligne : base − montant (le montant était le vrai coût en devise fonctionnelle).
            var fixes = entry.Select(r => (r.AccountId, Debit: r.BaseCredit - r.Credit, Credit: r.BaseDebit - r.Debit)).ToList();
            // Une vente débite le coût des ventes (excédent retiré) ; un retour le crédite (excédent rendu) : signe négatif.
            var excess = entry.Where(r => r.BaseDebit > 0m).Sum(r => r.BaseDebit - r.Debit);
            var line = new CostOfGoodsCorrectionLine(first.CompanyId, first.CompanyName, first.EntryId, first.EntryNumber,
                first.EntryDate, first.EntrySourceType, first.EntrySourceType == "sale" ? excess : -excess,
                first.FunctionalCurrency, "a_corriger");
            if (!apply) { lines.Add(line); continue; }

            tenant.SetScope([first.CompanyId], first.GroupId is { } g ? [g] : [], first.CompanyId);
            try
            {
                var number = await uow.ExecuteInTransactionAsync(async token =>
                {
                    var (date, periodId) = await DateAndPeriodAsync(first.EntryDate, token);
                    var journalId = await db.Database.SqlQuery<Guid>($"""
                        SELECT id AS "Value" FROM nexora.journal WHERE company_id = {first.CompanyId} AND code = 'OD'
                        """).FirstAsync(token);
                    var num = await numbers.NextAsync("journal:OD", null, token);
                    var correction = new JournalEntry
                    {
                        Id = Guid.NewGuid(), CompanyId = first.CompanyId, JournalId = journalId, AccountingPeriodId = periodId,
                        EntryNumber = num, EntryDate = date, Origin = "adjustment", SourceType = SourceType,
                        SourceId = first.EntryId, SourceOperationUuid = OperationFor(first.EntryId),
                        Description = $"Correction du coût des ventes en devise de l'écriture {first.EntryNumber} (audit A-052)",
                        Status = JournalEntryStatus.Posted, PostedAt = DateTimeOffset.UtcNow, CreatedBy = tenant.UserId,
                    };
                    var n = 1;
                    foreach (var f in fixes)
                        correction.Lines.Add(new JournalEntryLine
                        {
                            Id = Guid.NewGuid(), CompanyId = first.CompanyId, AccountId = f.AccountId, LineNumber = n++,
                            Description = "Excédent de conversion du coût des ventes",
                            Debit = Math.Max(f.Debit, 0m), Credit = Math.Max(f.Credit, 0m),
                            BaseDebit = Math.Max(f.Debit, 0m), BaseCredit = Math.Max(f.Credit, 0m),
                            CurrencyId = first.FunctionalCurrencyId,
                        });
                    await db.JournalEntries.AddAsync(correction, token);
                    await uow.SaveChangesAsync(token);
                    return num;
                }, ct);
                lines.Add(line with { Status = "corrige", CorrectionNumber = number });
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or Npgsql.PostgresException)
            {
                db.ChangeTracker.Clear();
                lines.Add(line with { Status = "refuse", Detail = ex.Message });
            }
        }
        return lines;
    }

    /// <summary>Date de l'écriture d'origine si sa période est ouverte ; sinon le jour même (période fermée).</summary>
    private async Task<(DateOnly Date, Guid PeriodId)> DateAndPeriodAsync(DateOnly original, CancellationToken ct)
    {
        try { return (original, (await periods.ResolveOpenPeriodAsync(original, ct)).Id); }
        catch (InvalidOperationException)
        {
            var today = await calendar.TodayAsync(ct);
            return (today, (await periods.ResolveOpenPeriodAsync(today, ct)).Id);
        }
    }

    /// <summary>Identifiant d'opération stable par écriture corrigée : une correction relancée ne double rien.</summary>
    private static Guid OperationFor(Guid entryId)
        => new(MD5.HashData(Encoding.UTF8.GetBytes($"nexora.cogs-currency-correction:{entryId}")));
}
