using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

// --- Résolution de comptes (mapping + code) ------------------------------------

public sealed class EfAccountResolver(NexoraDbContext db, ITenantContext tenant) : IAccountResolver
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<Guid> ResolveAsync(string mappingKey, CancellationToken ct = default)
    {
        var id = await db.AccountMappings.AsNoTracking()
            .Where(m => m.CompanyId == Company && m.MappingKey == mappingKey)
            .Select(m => (Guid?)m.AccountId)
            .FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException(
            $"Aucun compte mappé pour « {mappingKey} » (entreprise {Company}). Configurer nexora.account_mapping.");
    }

    public async Task<Guid> ResolveByCodeAsync(string accountCode, CancellationToken ct = default)
    {
        var id = await db.Accounts.AsNoTracking()
            .Where(a => a.CompanyId == Company && a.Code == accountCode && a.IsPostable)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException($"Compte {accountCode} introuvable ou non postable.");
    }

    public async Task<Account> GetAsync(Guid accountId, CancellationToken ct = default)
        => await db.Accounts.AsNoTracking().FirstAsync(a => a.Id == accountId, ct);

    public async Task<Guid> ResolveTenderAccountAsync(Guid financialAccountId, CancellationToken ct = default)
    {
        var fa = await db.FinancialAccounts.AsNoTracking().FirstOrDefaultAsync(f => f.Id == financialAccountId, ct)
            ?? throw new InvalidOperationException($"Compte financier {financialAccountId} introuvable.");
        if (fa.LedgerAccountId is { } gl) return gl;
        return await ResolveAsync(fa.AccountType == "bank" ? AccountKeys.Bank : AccountKeys.Cash, ct);
    }
}

// --- Devises ------------------------------------------------------------------

public sealed class EfCompanyCurrencyContext(NexoraDbContext db, ITenantContext tenant) : ICompanyCurrencyContext
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<string> FunctionalCurrencyAsync(CancellationToken ct = default)
    {
        var code = await (from c in db.Companies.AsNoTracking()
                          join cur in db.Currencies.AsNoTracking() on c.DefaultCurrencyId equals cur.Id
                          where c.Id == Company
                          select cur.Code).FirstOrDefaultAsync(ct);
        return code ?? throw new InvalidOperationException("Devise fonctionnelle introuvable.");
    }

    public async Task<int> DecimalPlacesAsync(string currency, CancellationToken ct = default)
        => await db.Currencies.AsNoTracking().Where(c => c.Code == currency)
            .Select(c => (int)c.DecimalPlaces).FirstOrDefaultAsync(ct) is var d and > 0 ? d : 2;

    public async Task<Guid> ResolveCurrencyIdAsync(string currency, CancellationToken ct = default)
        => await db.Currencies.AsNoTracking().Where(c => c.Code == currency)
            .Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct)
           ?? throw new InvalidOperationException($"Devise {currency} inconnue.");
}

// --- Taux de change (historisés, §22) ---------------------------------------

public sealed class EfExchangeRateProvider(NexoraDbContext db) : IExchangeRateProvider
{
    public async Task<(decimal Rate, Guid? RateId)> GetRateAsync(
        string fromCurrency, string toCurrency, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
            return (1m, null);

        var from = await CurrencyId(fromCurrency, ct);
        var to = await CurrencyId(toCurrency, ct);

        var direct = await db.ExchangeRates.AsNoTracking()
            .Where(r => r.BaseCurrencyId == from && r.QuoteCurrencyId == to && r.EffectiveAt <= at)
            .OrderByDescending(r => r.EffectiveAt)
            .Select(r => new { r.Id, r.Rate })
            .FirstOrDefaultAsync(ct);
        if (direct is not null) return (direct.Rate, direct.Id);

        var inverse = await db.ExchangeRates.AsNoTracking()
            .Where(r => r.BaseCurrencyId == to && r.QuoteCurrencyId == from && r.EffectiveAt <= at)
            .OrderByDescending(r => r.EffectiveAt)
            .Select(r => new { r.Id, r.Rate })
            .FirstOrDefaultAsync(ct);
        if (inverse is { Rate: > 0 }) return (1m / inverse.Rate, inverse.Id);

        throw new InvalidOperationException(
            $"Aucun taux de change {fromCurrency}→{toCurrency} au {at:yyyy-MM-dd} (§22 : pas de recalcul rétroactif).");
    }

    public async Task<(Money Converted, Guid? RateId)> ConvertAsync(
        Money amount, string toCurrency, DateTimeOffset at, CancellationToken ct = default)
    {
        var (rate, rateId) = await GetRateAsync(amount.Currency, toCurrency, at, ct);
        return (new Money(amount.Amount * rate, toCurrency), rateId);
    }

    private Task<Guid> CurrencyId(string code, CancellationToken ct) =>
        db.Currencies.AsNoTracking().Where(c => c.Code == code).Select(c => c.Id).FirstAsync(ct);
}

// --- Périodes comptables (§27) ---------------------------------------------

public sealed class EfAccountingPeriods(
    NexoraDbContext db, ITenantContext tenant, IBusinessCalendar calendar,
    ILogger<EfAccountingPeriods> logger) : IAccountingPeriods
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<AccountingPeriod> ResolveOpenPeriodAsync(DateOnly date, CancellationToken ct = default)
    {
        var period = await CoveringAsync(date, ct)
            ?? await OpenFollowingYearsUpToAsync(date, ct)
            ?? throw new InvalidOperationException($"Aucune période comptable ne couvre le {date:yyyy-MM-dd}.");

        if (period.Status != PeriodStatus.Open)
            throw new InvalidOperationException(
                $"Période « {period.Name} » {period.Status} — écriture interdite (§27).");
        return period;
    }

    /// <summary>
    /// Filet de sécurité (audit 2026-10 éd. 2, A-042) : seul l'amorçage créait un exercice, si bien qu'au premier jour
    /// de l'année suivante toute écriture — donc toute vente — était refusée. Une opération du jour postérieure au
    /// dernier exercice ouvre les exercices suivants, d'un an chacun et sans trou. Jamais pour une date future (une
    /// saisie erronée en 2062 n'ouvre rien), ni avant le premier exercice, ni dans un trou entre deux exercices.
    /// </summary>
    private async Task<AccountingPeriod?> OpenFollowingYearsUpToAsync(DateOnly date, CancellationToken ct)
    {
        var last = await LastAsync(ct);
        if (last is null) return null;
        // Une opération simultanée vient peut-être d'ouvrir l'exercice (entre la première lecture et celle-ci) : relire.
        if (date <= last.EndDate) return await CoveringAsync(date, ct);
        if (date > (await calendar.TodayAsync(ct)).AddDays(1)) return null;

        while (last.EndDate < date)
        {
            last = await CreateFollowingAsync(last, ct);
            logger.LogWarning("Exercice {Name} ouvert automatiquement pour une opération du {Date} (entreprise {Company})",
                last.Name, date, Company);
        }
        return await CoveringAsync(date, ct);
    }

    public async Task<AccountingPeriod?> LastAsync(CancellationToken ct = default)
        => await db.AccountingPeriods.AsNoTracking()
            .Where(p => p.CompanyId == Company).OrderByDescending(p => p.EndDate).FirstOrDefaultAsync(ct);

    public async Task<AccountingPeriod> OpenNextYearAsync(CancellationToken ct = default)
    {
        var last = await LastAsync(ct)
            ?? throw new InvalidOperationException("Aucun exercice comptable : l'entreprise n'a pas été initialisée.");
        return await CreateFollowingAsync(last, ct);
    }

    /// <summary>
    /// Exercice d'un an qui suit immédiatement <paramref name="previous"/>. Deux créations simultanées (deux ventes du
    /// 1er janvier) se heurtent à la contrainte d'exclusion des chevauchements : la seconde ne fait rien et relit
    /// l'exercice créé par la première.
    /// </summary>
    private async Task<AccountingPeriod> CreateFollowingAsync(AccountingPeriod previous, CancellationToken ct)
    {
        var start = previous.EndDate.AddDays(1);
        var end = start.AddYears(1).AddDays(-1);
        var name = start is { Month: 1, Day: 1 } ? $"Exercice {start.Year}" : $"Exercice {start.Year}-{end.Year}";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.accounting_period (company_id, name, start_date, end_date, status)
            VALUES ({Company}, {name}, {start}, {end}, 'open')
            ON CONFLICT DO NOTHING
            """, ct);
        return await CoveringAsync(start, ct)
            ?? throw new InvalidOperationException($"L'exercice commençant le {start:yyyy-MM-dd} n'a pas pu être créé.");
    }

    private Task<AccountingPeriod?> CoveringAsync(DateOnly date, CancellationToken ct)
        => db.AccountingPeriods.AsNoTracking()
            .FirstOrDefaultAsync(p => p.CompanyId == Company && p.StartDate <= date && p.EndDate >= date, ct);

    public Task<AccountingPeriod?> GetAsync(Guid periodId, CancellationToken ct = default)
        => db.AccountingPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.Id == periodId, ct);

    public async Task<IReadOnlyList<AccountingPeriod>> ListAsync(CancellationToken ct = default)
        => await db.AccountingPeriods.AsNoTracking()
            .Where(p => p.CompanyId == Company).OrderBy(p => p.StartDate).ToListAsync(ct);

    public async Task CloseAsync(Guid periodId, Guid? closedBy, CancellationToken ct = default)
    {
        await db.AccountingPeriods.Where(p => p.Id == periodId && p.CompanyId == Company)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PeriodStatus.Closed)
                .SetProperty(p => p.ClosedAt, DateTimeOffset.UtcNow)
                .SetProperty(p => p.ClosedBy, closedBy), ct);
    }

    public async Task ReopenAsync(Guid periodId, Guid? reopenedBy, CancellationToken ct = default)
    {
        await db.AccountingPeriods.Where(p => p.Id == periodId && p.CompanyId == Company)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Status, PeriodStatus.Open)
                .SetProperty(p => p.ClosedAt, (DateTimeOffset?)null)
                .SetProperty(p => p.ClosedBy, (Guid?)null), ct);
    }
}

// --- Numérotation des documents (§7, réutilise la fonction SQL) -------------

public sealed class EfDocumentNumberGenerator(NexoraDbContext db, ITenantContext tenant) : IDocumentNumberGenerator
{
    public async Task<string> NextAsync(string documentType, Guid? establishmentId = null, CancellationToken ct = default)
    {
        var company = tenant.ActiveCompanyId
            ?? throw TenantErrors.ActiveCompanyRequired();
        // Année locale de l'entreprise : next_document_number prend current_date (session au fuseau de l'entreprise).
        int? year = null;
        var rows = await db.Database
            .SqlQuery<string>($"""SELECT nexora.next_document_number({company}, {documentType}, {establishmentId}, {year}) AS "Value" """)
            .ToListAsync(ct);
        return rows[0];
    }
}
