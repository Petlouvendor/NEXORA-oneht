using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Finance.Reversals;
using Nexora.Contracts.Finance;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Reporting;

namespace Nexora.Infrastructure.Finance;

// =============================================================================
// Comptabilité générale — workflow manuel & clôture d'exercice (Phase 29 §9-19, §47).
// S'appuie sur le socle Phase 4 (journal_entry, account, PostingRuleSet, ReversalService).
// =============================================================================

/// <summary>Lectures du plan comptable & des journaux (§4-8, §51).</summary>
public sealed class SqlChartQueries(NexoraDbContext db, ITenantContext tenant) : IChartQueries
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    public async Task<IReadOnlyList<ChartOfAccountsDto>> ChartsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<ChartOfAccountsDto>($"""
            SELECT c.id AS "Id", c.name AS "Name", c.status AS "Status",
                   (SELECT COUNT(*)::int FROM nexora.account a WHERE a.chart_of_accounts_id = c.id) AS "AccountCount"
            FROM nexora.chart_of_accounts c WHERE c.company_id = {Company}
            ORDER BY c.name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<AccountDto>> AccountsAsync(string? accountType, bool onlyPostable, CancellationToken ct = default)
        => await db.Database.SqlQuery<AccountDto>($"""
            SELECT a.id AS "Id", a.code AS "Code", a.name AS "Name", a.account_type AS "AccountType",
                   a.is_postable AS "IsPostable", a.status AS "Status", a.parent_account_id AS "ParentAccountId",
                   cur.code AS "Currency"
            FROM nexora.account a
            LEFT JOIN nexora.currency cur ON cur.id = a.currency_id
            WHERE a.company_id = {Company}
              AND ({accountType}::text IS NULL OR a.account_type = {accountType})
              AND (NOT {onlyPostable} OR a.is_postable)
            ORDER BY a.code
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<JournalDto>> JournalsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<JournalDto>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", journal_type AS "JournalType", status AS "Status"
            FROM nexora.journal WHERE company_id = {Company} ORDER BY code
            """).ToListAsync(ct);
}

/// <summary>Batterie de contrôles comptables §47.</summary>
public sealed class SqlAccountingControls(NexoraDbContext db, ITenantContext tenant, IAccountingPeriods periods)
    : IAccountingControls
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private static readonly string[] ThirdPartyTypes = ["customer", "supplier", "employee", "other"];

    private sealed record AccountRow(string Code, string AccountType, bool IsPostable, string Status);

    public async Task<IReadOnlyList<AccountingControlDto>> EvaluateAsync(CreateJournalEntryRequest r, CancellationToken ct = default)
    {
        var c = new List<AccountingControlDto>();
        static AccountingControlDto Ok(string k, string l, string? d = null) => new(k, l, "pass", d);
        static AccountingControlDto Fail(string k, string l, string d) => new(k, l, "fail", d);
        static AccountingControlDto Warn(string k, string l, string d) => new(k, l, "warn", d);

        var lines = r.Lines ?? [];

        // §10 équilibre + nombre de lignes + signe
        var debit = lines.Sum(l => l.Debit);
        var credit = lines.Sum(l => l.Credit);
        c.Add(lines.Count >= 2 ? Ok("min_lines", "Au moins deux lignes") : Fail("min_lines", "Au moins deux lignes", $"{lines.Count} ligne(s)"));
        c.Add(Math.Round(debit, 4) == Math.Round(credit, 4) && debit > 0m
            ? Ok("balance", "Équilibre débit / crédit", $"{debit:0.##}")
            : Fail("balance", "Équilibre débit / crédit", $"débit {debit:0.##} ≠ crédit {credit:0.##}"));
        c.Add(lines.All(l => l.Debit >= 0m && l.Credit >= 0m && (l.Debit == 0m) != (l.Credit == 0m))
            ? Ok("line_amounts", "Une ligne = un débit ou un crédit, positif")
            : Fail("line_amounts", "Une ligne = un débit ou un crédit, positif", "ligne invalide"));

        // §47 journal
        var journalOk = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.journal
            WHERE company_id = {Company} AND code = {r.JournalCode} AND status = 'active'
            """).FirstAsync(ct);
        c.Add(journalOk > 0 ? Ok("journal", "Journal actif") : Fail("journal", "Journal actif", $"« {r.JournalCode} » introuvable"));

        // §47 devise
        var currencyOk = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.currency WHERE code = {r.Currency}
            """).FirstAsync(ct);
        c.Add(currencyOk > 0 ? Ok("currency", "Devise valide") : Fail("currency", "Devise valide", $"« {r.Currency} » inconnue"));

        // §15/§47 période ouverte + date dans la période
        try
        {
            var p = await periods.ResolveOpenPeriodAsync(r.EntryDate, ct);
            c.Add(Ok("period_open", "Période ouverte", p.Name));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            c.Add(Fail("period_open", "Période ouverte", ex.Message));
        }

        // §20/§47 comptes postables & actifs
        var codes = lines.Select(l => l.AccountCode).Distinct().ToArray();
        var accounts = codes.Length == 0 ? [] : await db.Database.SqlQuery<AccountRow>($"""
            SELECT code AS "Code", account_type AS "AccountType", is_postable AS "IsPostable", status AS "Status"
            FROM nexora.account WHERE company_id = {Company} AND code = ANY({codes})
            """).ToListAsync(ct);
        var missing = codes.Where(x => accounts.All(a => a.Code != x)).ToArray();
        var notPostable = accounts.Where(a => !a.IsPostable).Select(a => a.Code).ToArray();
        var inactive = accounts.Where(a => a.Status != "active").Select(a => a.Code).ToArray();
        if (missing.Length > 0) c.Add(Fail("accounts_exist", "Comptes du plan comptable", $"inconnu(s) : {string.Join(", ", missing)}"));
        else c.Add(Ok("accounts_exist", "Comptes du plan comptable"));
        c.Add(notPostable.Length == 0 ? Ok("accounts_postable", "Comptes postables")
            : Fail("accounts_postable", "Comptes postables", $"non postable(s) : {string.Join(", ", notPostable)}"));
        c.Add(inactive.Length == 0 ? Ok("accounts_active", "Comptes actifs")
            : Fail("accounts_active", "Comptes actifs", $"inactif(s) : {string.Join(", ", inactive)}"));

        // §47 cohérence tiers
        var badTp = lines.Where(l =>
            (l.ThirdPartyType is not null && (l.ThirdPartyId is null || !ThirdPartyTypes.Contains(l.ThirdPartyType)))
            || (l.ThirdPartyId is not null && l.ThirdPartyType is null)).ToArray();
        c.Add(badTp.Length == 0 ? Ok("third_party", "Tiers cohérent")
            : Fail("third_party", "Tiers cohérent", $"{badTp.Length} ligne(s) tiers incohérente(s)"));

        // §47 doublon (warn)
        var dup = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value"
            FROM nexora.journal_entry e
            JOIN nexora.journal j ON j.id = e.journal_id
            WHERE e.company_id = {Company} AND e.status = 'posted' AND j.code = {r.JournalCode}
              AND e.entry_date = {r.EntryDate}
              AND COALESCE(e.description,'') = COALESCE({r.Description}, '')
              AND (SELECT COALESCE(SUM(base_debit),0) FROM nexora.journal_entry_line l WHERE l.journal_entry_id = e.id) = {debit}
            """).FirstAsync(ct);
        c.Add(dup == 0 ? Ok("no_duplicate", "Pas de doublon apparent")
            : Warn("no_duplicate", "Pas de doublon apparent", $"{dup} écriture(s) similaire(s) déjà validée(s)"));

        return c;
    }
}

/// <summary>Workflow comptable manuel : brouillon → contrôles → validation → extourne (§9-14).</summary>
public sealed class SqlManualJournalService(
    NexoraDbContext db,
    IUnitOfWork uow,
    ITenantContext tenant,
    IAccountingControls controls,
    IReversalService reversal,
    IAccountingPeriods periods,
    IDocumentNumberGenerator numbers,
    ICompanyCurrencyContext currency,
    IExchangeRateProvider fx) : IManualJournalService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record EntryHead(
        Guid Id, string JournalCode, string EntryNumber, DateOnly EntryDate, string Status, string Origin,
        string? Description, string Currency, Guid? ReversalOfEntryId, DateTimeOffset? PostedAt, DateTimeOffset CreatedAt);
    private sealed record LineRow(
        int LineNumber, string AccountCode, string AccountName, string? Description,
        decimal Debit, decimal Credit, decimal BaseDebit, decimal BaseCredit,
        string? ThirdPartyType, Guid? ThirdPartyId, Guid? CostCenterId);

    public async Task<IReadOnlyList<JournalEntryDto>> ListAsync(string? status, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var heads = await db.Database.SqlQuery<EntryHead>($"""
            SELECT e.id AS "Id", j.code AS "JournalCode", e.entry_number AS "EntryNumber", e.entry_date AS "EntryDate",
                   e.status AS "Status", e.origin AS "Origin", e.description AS "Description",
                   COALESCE((SELECT cur.code FROM nexora.journal_entry_line jl
                             JOIN nexora.currency cur ON cur.id = jl.currency_id
                             WHERE jl.journal_entry_id = e.id LIMIT 1), 'HTG') AS "Currency",
                   e.reversal_of_entry_id AS "ReversalOfEntryId",
                   e.posted_at AS "PostedAt", e.created_at AS "CreatedAt"
            FROM nexora.journal_entry e JOIN nexora.journal j ON j.id = e.journal_id
            WHERE e.company_id = {Company}
              AND ({status}::text IS NULL OR e.status = {status})
              AND ({from}::date IS NULL OR e.entry_date >= {from})
              AND ({to}::date IS NULL OR e.entry_date <= {to})
            ORDER BY e.entry_date DESC, e.created_at DESC
            LIMIT 200
            """).ToListAsync(ct);

        var result = new List<JournalEntryDto>();
        foreach (var h in heads) result.Add(await HydrateAsync(h, ct));
        return result;
    }

    public async Task<Result<JournalEntryDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var h = await HeadAsync(id, ct);
        return h is null ? Error.NotFound("accounting.entry_not_found", "Écriture introuvable.") : Result.Success(await HydrateAsync(h, ct));
    }

    public async Task<Result<Guid>> CreateDraftAsync(CreateJournalEntryRequest r, CancellationToken ct = default)
    {
        var guard = await ValidateShapeAsync(r, ct);
        if (guard is not null) return guard;   // Error → implicit Result<Guid>

        try
        {
            var id = await uow.ExecuteInTransactionAsync(async token =>
            {
                var (entry, _) = await BuildEntryAsync(r, JournalEntryStatus.Draft, "manual", token);
                await db.JournalEntries.AddAsync(entry, token);
                await uow.SaveChangesAsync(token);
                return entry.Id;
            }, ct);
            return Result.Success(id);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("accounting.draft_failed", ex.Message);
        }
    }

    public async Task<Result> UpdateDraftAsync(Guid id, CreateJournalEntryRequest r, CancellationToken ct = default)
    {
        var head = await HeadAsync(id, ct);
        if (head is null) return Error.NotFound("accounting.entry_not_found", "Écriture introuvable.");
        if (head.Status != "draft") return Error.Conflict("accounting.entry_not_draft", "Seul un brouillon est modifiable (§11).");
        var guard = await ValidateShapeAsync(r, ct);
        if (guard is not null) return guard;

        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var period = await periods.ResolveOpenPeriodAsync(r.EntryDate, token);
                var journalId = await ResolveJournalIdAsync(r.JournalCode, token);
                await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.journal_entry_line WHERE journal_entry_id = {id}", token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.journal_entry
                       SET journal_id = {journalId}, accounting_period_id = {period.Id},
                           entry_date = {r.EntryDate}, description = {r.Description}, updated_at = now()
                     WHERE id = {id} AND company_id = {Company}
                    """, token);
                var n = 1;
                foreach (var l in r.Lines)
                {
                    var line = await BuildLineAsync(l, r.Currency, r.EntryDate, n++, token);
                    await InsertLineAsync(id, line, token);
                }
                return 0;
            }, ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("accounting.draft_failed", ex.Message);
        }
    }

    public async Task<Result> DeleteDraftAsync(Guid id, CancellationToken ct = default)
    {
        var head = await HeadAsync(id, ct);
        if (head is null) return Error.NotFound("accounting.entry_not_found", "Écriture introuvable.");
        if (head.Status != "draft") return Error.Conflict("accounting.entry_not_draft", "Une écriture validée ne se supprime pas (extourne — §2, §13).");

        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.journal_entry_line WHERE journal_entry_id = {id}", token);
                await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.journal_entry WHERE id = {id} AND company_id = {Company}", token);
                return 0;
            }, ct);
            return Result.Success();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("accounting.delete_failed", ex.Message);
        }
    }

    private Task InsertLineAsync(Guid entryId, JournalEntryLine line, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.journal_entry_line
              (id, journal_entry_id, company_id, account_id, line_number, description, debit, credit,
               currency_id, exchange_rate_id, base_debit, base_credit, third_party_type, third_party_id, cost_center_id)
            VALUES ({line.Id}, {entryId}, {Company}, {line.AccountId}, {line.LineNumber}, {line.Description},
                    {line.Debit}, {line.Credit}, {line.CurrencyId}, {line.ExchangeRateId}, {line.BaseDebit}, {line.BaseCredit},
                    {line.ThirdPartyType}, {line.ThirdPartyId}, {line.CostCenterId})
            """, ct);

    public async Task<Result<PostJournalEntryResult>> PostAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await db.JournalEntries.Include(e => e.Lines).AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == id && e.CompanyId == Company, ct);
        if (entry is null) return Error.NotFound("accounting.entry_not_found", "Écriture introuvable.");
        if (entry.Status != JournalEntryStatus.Draft) return Error.Conflict("accounting.entry_not_draft", "Seul un brouillon peut être validé.");

        var journalCode = await db.Database.SqlQuery<string>($"""
            SELECT code AS "Value" FROM nexora.journal WHERE id = {entry.JournalId}
            """).FirstAsync(ct);
        var codeById = await AccountCodesAsync(entry.Lines.Select(l => l.AccountId).ToArray(), ct);

        var req = new CreateJournalEntryRequest(journalCode, entry.EntryDate, "HTG", entry.Description,
            entry.Lines.OrderBy(l => l.LineNumber).Select(l => new JournalEntryLineInput(
                codeById.GetValueOrDefault(l.AccountId, "?"), l.Debit, l.Credit, l.Description,
                l.ThirdPartyType, l.ThirdPartyId, l.CostCenterId)).ToList());

        var checks = await controls.EvaluateAsync(req, ct);
        var failed = checks.Where(x => x.Status == "fail").ToList();
        if (failed.Count > 0)
            return Error.Validation("accounting.controls_failed",
                "Contrôles §47 non passés : " + string.Join(" ; ", failed.Select(f => $"{f.Label} ({f.Detail})")));

        try
        {
            var number = await uow.ExecuteInTransactionAsync(async token =>
            {
                var tracked = await db.JournalEntries.FirstAsync(e => e.Id == id, token);
                var num = await numbers.NextAsync($"journal:{journalCode}", null, token);
                tracked.EntryNumber = num;
                tracked.Status = JournalEntryStatus.Posted;
                tracked.PostedAt = DateTimeOffset.UtcNow;
                tracked.PostedBy = tenant.UserId;
                await uow.SaveChangesAsync(token);
                return num;
            }, ct);
            return Result.Success(new PostJournalEntryResult(id, number, checks));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("accounting.post_failed", ex.Message);
        }
    }

    public async Task<Result<Guid>> ReverseAsync(Guid id, ReverseJournalEntryRequest r, CancellationToken ct = default)
        => await reversal.ReverseAsync(id, r.Reason, r.ReversalDate, ct);

    // --- helpers -------------------------------------------------------

    private async Task<Error?> ValidateShapeAsync(CreateJournalEntryRequest r, CancellationToken ct)
    {
        if (r.Lines is null || r.Lines.Count < 1)
            return Error.Validation("accounting.no_lines", "Au moins une ligne est requise.");
        var journalId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.journal WHERE company_id = {Company} AND code = {r.JournalCode} AND status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (journalId == Guid.Empty) return Error.Validation("accounting.journal_unknown", $"Journal « {r.JournalCode} » introuvable.");
        var missing = new List<string>();
        foreach (var code in r.Lines.Select(l => l.AccountCode).Distinct())
        {
            var ok = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value" FROM nexora.account
                WHERE company_id = {Company} AND code = {code} AND is_postable AND status = 'active'
                """).FirstAsync(ct);
            if (ok == 0) missing.Add(code);
        }
        if (missing.Count > 0)
            return Error.Validation("accounting.account_unknown", $"Compte(s) inconnu(s) / non postable(s) : {string.Join(", ", missing)}.");
        return null;
    }

    private async Task<(JournalEntry Entry, AccountingPeriod Period)> BuildEntryAsync(
        CreateJournalEntryRequest r, JournalEntryStatus status, string origin, CancellationToken ct)
    {
        var period = await periods.ResolveOpenPeriodAsync(r.EntryDate, ct);
        var journalId = await ResolveJournalIdAsync(r.JournalCode, ct);
        var number = status == JournalEntryStatus.Draft
            ? "BROUILLON-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant()
            : await numbers.NextAsync($"journal:{r.JournalCode}", null, ct);

        var entry = new JournalEntry
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            JournalId = journalId,
            AccountingPeriodId = period.Id,
            EntryNumber = number,
            EntryDate = r.EntryDate,
            Origin = origin,
            SourceType = origin == "manual" ? "manual" : origin,
            Description = r.Description,
            Status = status,
            PostedAt = status == JournalEntryStatus.Posted ? DateTimeOffset.UtcNow : null,
            PostedBy = status == JournalEntryStatus.Posted ? tenant.UserId : null,
            CreatedBy = tenant.UserId,
        };
        var n = 1;
        foreach (var l in r.Lines) entry.Lines.Add(await BuildLineAsync(l, r.Currency, r.EntryDate, n++, ct));
        return (entry, period);
    }

    private async Task<JournalEntryLine> BuildLineAsync(JournalEntryLineInput l, string ccy, DateOnly date, int lineNumber, CancellationToken ct)
    {
        var func = await currency.FunctionalCurrencyAsync(ct);
        var accountId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.account WHERE company_id = {Company} AND code = {l.AccountCode}
            """).FirstAsync(ct);
        var currencyId = await currency.ResolveCurrencyIdAsync(ccy, ct);

        decimal baseDebit = l.Debit, baseCredit = l.Credit;
        Guid? rateId = null;
        if (!string.Equals(ccy, func, StringComparison.OrdinalIgnoreCase))
        {
            var at = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            if (l.Debit > 0m) { var (m, rid) = await fx.ConvertAsync(new Money(l.Debit, ccy), func, at, ct); baseDebit = m.Amount; rateId = rid; }
            if (l.Credit > 0m) { var (m, rid) = await fx.ConvertAsync(new Money(l.Credit, ccy), func, at, ct); baseCredit = m.Amount; rateId = rid; }
        }

        return new JournalEntryLine
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            AccountId = accountId,
            LineNumber = lineNumber,
            Description = l.Description,
            Debit = l.Debit,
            Credit = l.Credit,
            CurrencyId = currencyId,
            ExchangeRateId = rateId,
            BaseDebit = Math.Round(baseDebit, 2),
            BaseCredit = Math.Round(baseCredit, 2),
            ThirdPartyType = l.ThirdPartyType,
            ThirdPartyId = l.ThirdPartyId,
            CostCenterId = l.CostCenterId,
        };
    }

    private Task<Guid> ResolveJournalIdAsync(string code, CancellationToken ct)
        => db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.journal WHERE company_id = {Company} AND code = {code}
            """).FirstAsync(ct);

    private Task<EntryHead?> HeadAsync(Guid id, CancellationToken ct)
        => db.Database.SqlQuery<EntryHead>($"""
            SELECT e.id AS "Id", j.code AS "JournalCode", e.entry_number AS "EntryNumber", e.entry_date AS "EntryDate",
                   e.status AS "Status", e.origin AS "Origin", e.description AS "Description",
                   COALESCE((SELECT cur.code FROM nexora.journal_entry_line jl
                             JOIN nexora.currency cur ON cur.id = jl.currency_id
                             WHERE jl.journal_entry_id = e.id LIMIT 1), 'HTG') AS "Currency",
                   e.reversal_of_entry_id AS "ReversalOfEntryId",
                   e.posted_at AS "PostedAt", e.created_at AS "CreatedAt"
            FROM nexora.journal_entry e JOIN nexora.journal j ON j.id = e.journal_id
            WHERE e.id = {id} AND e.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private async Task<JournalEntryDto> HydrateAsync(EntryHead h, CancellationToken ct)
    {
        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT l.line_number AS "LineNumber", a.code AS "AccountCode", a.name AS "AccountName", l.description AS "Description",
                   l.debit AS "Debit", l.credit AS "Credit", l.base_debit AS "BaseDebit", l.base_credit AS "BaseCredit",
                   l.third_party_type AS "ThirdPartyType", l.third_party_id AS "ThirdPartyId", l.cost_center_id AS "CostCenterId"
            FROM nexora.journal_entry_line l JOIN nexora.account a ON a.id = l.account_id
            WHERE l.journal_entry_id = {h.Id}
            ORDER BY l.line_number
            """).ToListAsync(ct);

        var td = lines.Sum(l => l.BaseDebit);
        var tc = lines.Sum(l => l.BaseCredit);
        return new JournalEntryDto(
            h.Id, h.JournalCode, h.EntryNumber, h.EntryDate, h.Status, h.Origin, h.Description, h.Currency,
            Math.Round(td, 2), Math.Round(tc, 2), lines.Count >= 2 && Math.Round(td, 2) == Math.Round(tc, 2),
            h.ReversalOfEntryId, h.PostedAt, h.CreatedAt,
            lines.Select(l => new JournalEntryLineDto(
                l.LineNumber, l.AccountCode, l.AccountName, l.Description, l.Debit, l.Credit,
                l.BaseDebit, l.BaseCredit, l.ThirdPartyType, l.ThirdPartyId, l.CostCenterId)).ToList());
    }

    private async Task<Dictionary<Guid, string>> AccountCodesAsync(Guid[] ids, CancellationToken ct)
    {
        if (ids.Length == 0) return [];
        var rows = await db.Database.SqlQuery<AccountCode>($"""
            SELECT id AS "Id", code AS "Code" FROM nexora.account WHERE id = ANY({ids})
            """).ToListAsync(ct);
        return rows.ToDictionary(x => x.Id, x => x.Code);
    }
    private sealed record AccountCode(Guid Id, string Code);
}

/// <summary>Clôture d'exercice & à-nouveaux (§16, §18-19).</summary>
public sealed class SqlFiscalYearService(
    NexoraDbContext db,
    IUnitOfWork uow,
    ITenantContext tenant,
    IAccountResolver accounts,
    IAccountingPeriods periods,
    IDocumentNumberGenerator numbers,
    ICompanyCurrencyContext currency) : IFiscalYearService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record PeriodRow(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate, string Status);
    private sealed record AccBalance(Guid AccountId, string Code, string AccountType, decimal Balance);

    public async Task<Result<YearEndCloseReportDto>> PreviewCloseAsync(Guid periodId, CancellationToken ct = default)
    {
        var report = await BuildReportAsync(periodId, ct);
        return report is null
            ? Error.NotFound("accounting.period_not_found", "Période comptable introuvable.")
            : Result.Success(report);
    }

    public async Task<Result<CloseYearResultDto>> CloseYearAsync(Guid periodId, CloseYearRequest r, CancellationToken ct = default)
    {
        var report = await BuildReportAsync(periodId, ct);
        if (report is null) return Error.NotFound("accounting.period_not_found", "Période comptable introuvable.");
        if (report.Status == "completed")
            return Error.Conflict("accounting.year_already_closed", "L'exercice est déjà clôturé.");
        if (!report.CanClose)
        {
            var blockers = string.Join(" ; ", report.Controls.Where(x => x.Blocking && x.Status == "fail").Select(x => x.Label));
            if (!r.Force) return Error.Conflict("accounting.year_close_blocked", $"Clôture bloquée : {blockers}.");
            if (string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("accounting.force_reason", "Un motif est obligatoire pour forcer.");
        }

        var pnl = await PnlBalancesAsync(periodId, report.EndDate, ct);
        if (pnl.Count == 0)
            return Error.Validation("accounting.no_result", "Aucun compte de résultat mouvementé — rien à solder.");

        var func = await currency.FunctionalCurrencyAsync(ct);
        var currencyId = await currency.ResolveCurrencyIdAsync(func, ct);
        var resultAccount = await accounts.ResolveAsync(AccountKeys.CurrentYearEarnings, ct);
        var forced = !report.CanClose && r.Force;

        try
        {
            var (closeId, entryId, entryNumber) = await uow.ExecuteInTransactionAsync(async token =>
            {
                var journalId = await JournalIdAsync("CL", token);
                var number = await numbers.NextAsync("journal:CL", null, token);
                var entry = new JournalEntry
                {
                    Id = Guid.NewGuid(), CompanyId = Company, JournalId = journalId, AccountingPeriodId = periodId,
                    EntryNumber = number, EntryDate = report.EndDate, Origin = "closing", SourceType = "year_end_close",
                    Description = $"Clôture de l'exercice — {report.PeriodName}",
                    Status = JournalEntryStatus.Posted, PostedAt = DateTimeOffset.UtcNow, PostedBy = tenant.UserId,
                    CreatedBy = tenant.UserId,
                };
                var n = 1;
                decimal totalRev = 0m, totalExp = 0m;
                foreach (var a in pnl)
                {
                    // solde le compte : contre-passe son solde courant
                    if (a.Balance == 0m) continue;
                    var debit = a.AccountType == "revenue" ? a.Balance : 0m;   // revenu créditeur → on le débite
                    var credit = a.AccountType == "expense" ? a.Balance : 0m;  // charge débitrice → on la crédite
                    if (debit < 0m) { credit = -debit; debit = 0m; }
                    if (credit < 0m) { debit = -credit; credit = 0m; }
                    entry.Lines.Add(Line(a.AccountId, n++, currencyId, Math.Round(debit, 2), Math.Round(credit, 2), "Solde de clôture"));
                    if (a.AccountType == "revenue") totalRev += a.Balance; else totalExp += a.Balance;
                }
                var net = Math.Round(totalRev - totalExp, 2);
                if (net > 0m) entry.Lines.Add(Line(resultAccount, n++, currencyId, 0m, net, "Résultat de l'exercice (bénéfice)"));
                else if (net < 0m) entry.Lines.Add(Line(resultAccount, n++, currencyId, -net, 0m, "Résultat de l'exercice (perte)"));

                await db.JournalEntries.AddAsync(entry, token);
                await uow.SaveChangesAsync(token);

                await periods.CloseAsync(periodId, tenant.UserId, token);

                var next = await db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM nexora.accounting_period
                    WHERE company_id = {Company} AND start_date = {report.EndDate.AddDays(1)}
                    """).FirstOrDefaultAsync(token);

                var cid = Guid.NewGuid();
                var controlsJson = JsonSerializer.Serialize(report.Controls);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.accounting_year_close
                      (id, company_id, accounting_period_id, net_result, result_entry_id, next_period_id, controls, forced, note, closed_by)
                    VALUES ({cid}, {Company}, {periodId}, {net}, {entry.Id},
                            {(next == Guid.Empty ? (Guid?)null : next)}, {controlsJson}::jsonb, {forced}, {r.Note}, {tenant.UserId})
                    """, token);

                return (cid, entry.Id, number);
            }, ct);

            return Result.Success(new CloseYearResultDto(closeId, periodId, report.NetResult, entryId, entryNumber, forced));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("accounting.year_close_failed", ex.Message);
        }
    }

    public async Task<Result<OpeningBalancesResultDto>> GenerateOpeningBalancesAsync(
        Guid targetPeriodId, GenerateOpeningBalancesRequest r, CancellationToken ct = default)
    {
        var target = await PeriodAsync(targetPeriodId, ct);
        if (target is null) return Error.NotFound("accounting.period_not_found", "Période cible introuvable.");
        if (target.Status != "open") return Error.Conflict("accounting.period_not_open", "La période cible doit être ouverte.");
        var from = await PeriodAsync(r.FromPeriodId, ct);
        if (from is null) return Error.NotFound("accounting.period_not_found", "Période source introuvable.");

        var exists = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.journal_entry
            WHERE company_id = {Company} AND accounting_period_id = {targetPeriodId} AND origin = 'opening'
            """).FirstAsync(ct);
        if (exists > 0) return Error.Conflict("accounting.opening_exists", "Une écriture d'à-nouveaux existe déjà pour cette période.");

        var balances = await BalanceSheetBalancesAsync(from.EndDate, ct);
        if (balances.Count == 0) return Error.Validation("accounting.no_balances", "Aucun solde de bilan à reporter.");

        var func = await currency.FunctionalCurrencyAsync(ct);
        var currencyId = await currency.ResolveCurrencyIdAsync(func, ct);
        var carryAccount = await accounts.ResolveAsync(AccountKeys.RetainedEarnings, ct);

        try
        {
            var (entryId, number, lineCount, total) = await uow.ExecuteInTransactionAsync(async token =>
            {
                var journalId = await JournalIdAsync("AN", token);
                var num = await numbers.NextAsync("journal:AN", null, token);
                var entry = new JournalEntry
                {
                    Id = Guid.NewGuid(), CompanyId = Company, JournalId = journalId, AccountingPeriodId = targetPeriodId,
                    EntryNumber = num, EntryDate = target.StartDate, Origin = "opening", SourceType = "opening_balances",
                    Description = $"À-nouveaux — reprise des soldes de {from.Name}",
                    Status = JournalEntryStatus.Posted, PostedAt = DateTimeOffset.UtcNow, PostedBy = tenant.UserId,
                    CreatedBy = tenant.UserId,
                };
                var n = 1;
                decimal dr = 0m, cr = 0m;
                foreach (var b in balances)
                {
                    if (b.Balance == 0m) continue;
                    var debit = b.Balance > 0m ? b.Balance : 0m;
                    var credit = b.Balance < 0m ? -b.Balance : 0m;
                    entry.Lines.Add(Line(b.AccountId, n++, currencyId, Math.Round(debit, 2), Math.Round(credit, 2), "Report à nouveau"));
                    dr += debit; cr += credit;
                }
                var residual = Math.Round(dr - cr, 2);
                if (residual > 0m) entry.Lines.Add(Line(carryAccount, n++, currencyId, 0m, residual, "Équilibrage report à nouveau"));
                else if (residual < 0m) entry.Lines.Add(Line(carryAccount, n++, currencyId, -residual, 0m, "Équilibrage report à nouveau"));

                await db.JournalEntries.AddAsync(entry, token);
                await uow.SaveChangesAsync(token);
                return (entry.Id, num, entry.Lines.Count, entry.Lines.Sum(x => x.BaseDebit));
            }, ct);

            return Result.Success(new OpeningBalancesResultDto(entryId, number, targetPeriodId, target.StartDate, lineCount, Math.Round(total, 2)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.PostgresException or DbUpdateException)
        {
            return Error.Conflict("accounting.opening_failed", ex.Message);
        }
    }

    // --- helpers -----------------------------------------------------

    private JournalEntryLine Line(Guid accountId, int n, Guid currencyId, decimal debit, decimal credit, string desc) => new()
    {
        Id = Guid.NewGuid(), CompanyId = Company, AccountId = accountId, LineNumber = n, Description = desc,
        Debit = debit, Credit = credit, CurrencyId = currencyId, BaseDebit = debit, BaseCredit = credit,
    };

    private Task<PeriodRow?> PeriodAsync(Guid id, CancellationToken ct)
        => db.Database.SqlQuery<PeriodRow>($"""
            SELECT id AS "Id", name AS "Name", start_date AS "StartDate", end_date AS "EndDate", status AS "Status"
            FROM nexora.accounting_period WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private Task<Guid> JournalIdAsync(string code, CancellationToken ct)
        => db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.journal WHERE company_id = {Company} AND code = {code}
            """).FirstAsync(ct);

    private Task<List<AccBalance>> PnlBalancesAsync(Guid periodId, DateOnly endDate, CancellationToken ct)
        => db.Database.SqlQuery<AccBalance>($"""
            SELECT a.id AS "AccountId", a.code AS "Code", a.account_type AS "AccountType",
                   CASE WHEN a.account_type = 'revenue'
                        THEN COALESCE(SUM(l.base_credit - l.base_debit),0)
                        ELSE COALESCE(SUM(l.base_debit - l.base_credit),0) END AS "Balance"
            FROM nexora.account a
            JOIN nexora.journal_entry_line l ON l.account_id = a.id
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            WHERE a.company_id = {Company} AND a.account_type IN ('revenue','expense')
              AND e.status = 'posted' AND e.origin <> 'closing' AND e.entry_date <= {endDate}
            GROUP BY a.id, a.code, a.account_type
            HAVING CASE WHEN a.account_type = 'revenue'
                        THEN COALESCE(SUM(l.base_credit - l.base_debit),0)
                        ELSE COALESCE(SUM(l.base_debit - l.base_credit),0) END <> 0
            ORDER BY a.code
            """).ToListAsync(ct);

    private Task<List<AccBalance>> BalanceSheetBalancesAsync(DateOnly asOf, CancellationToken ct)
        => db.Database.SqlQuery<AccBalance>($"""
            SELECT a.id AS "AccountId", a.code AS "Code", a.account_type AS "AccountType",
                   COALESCE(SUM(l.base_debit - l.base_credit),0) AS "Balance"
            FROM nexora.account a
            JOIN nexora.journal_entry_line l ON l.account_id = a.id
            JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
            WHERE a.company_id = {Company} AND a.account_type IN ('asset','liability','equity')
              AND e.status = 'posted' AND e.entry_date <= {asOf}
            GROUP BY a.id, a.code, a.account_type
            HAVING COALESCE(SUM(l.base_debit - l.base_credit),0) <> 0
            ORDER BY a.code
            """).ToListAsync(ct);

    private async Task<YearEndCloseReportDto?> BuildReportAsync(Guid periodId, CancellationToken ct)
    {
        var period = await PeriodAsync(periodId, ct);
        if (period is null) return null;

        var today = await ReportingClock.TodayAsync(db, ct);
        var func = await currency.FunctionalCurrencyAsync(ct);

        var drafts = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.journal_entry
            WHERE company_id = {Company} AND accounting_period_id = {periodId} AND status = 'draft'
            """).FirstAsync(ct);
        var suspense = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.treasury_suspense_item
            WHERE company_id = {Company} AND status = 'open'
            """).FirstAsync(ct);
        var financeClosed = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.financial_close
            WHERE company_id = {Company} AND accounting_period_id = {periodId} AND status = 'completed'
            """).FirstAsync(ct);
        var alreadyClosed = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.accounting_year_close
            WHERE company_id = {Company} AND accounting_period_id = {periodId} AND status = 'completed'
            """).FirstAsync(ct);

        var pnl = await PnlBalancesAsync(periodId, period.EndDate, ct);
        var revenue = Math.Round(pnl.Where(x => x.AccountType == "revenue").Sum(x => x.Balance), 2);
        var expenses = Math.Round(pnl.Where(x => x.AccountType == "expense").Sum(x => x.Balance), 2);

        static string V(bool fail, bool warn) => fail ? "fail" : warn ? "warn" : "pass";
        var controls = new List<YearEndControlDto>
        {
            new("period_open", "Période ouverte", V(period.Status != "open", false),
                period.Status != "open" ? $"statut {period.Status}" : "ouverte", 0, true),
            new("draft_entries", "Écritures en brouillon sur la période", V(drafts > 0, false),
                drafts > 0 ? $"{drafts} brouillon(s) à valider ou supprimer" : "aucun", drafts, true),
            new("finance_close", "Clôture financière préalable (§17)", V(false, financeClosed == 0),
                financeClosed == 0 ? "clôture financière non effectuée" : "effectuée", 0, false),
            new("suspense_open", "Comptes de suspense ouverts", V(false, suspense > 0),
                suspense > 0 ? $"{suspense} suspens à résoudre" : "aucun", suspense, false),
        };

        var canClose = controls.Where(x => x.Blocking).All(x => x.Status != "fail");
        return new YearEndCloseReportDto(
            period.Id, period.Name, period.EndDate, alreadyClosed > 0 ? "completed" : "draft", canClose,
            revenue, expenses, Math.Round(revenue - expenses, 2), func, controls, DateTimeOffset.UtcNow);
    }
}
