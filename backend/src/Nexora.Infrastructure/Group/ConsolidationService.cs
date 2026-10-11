using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Group;
using Nexora.Contracts.Group;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Group;

/// <summary>
/// Processus de consolidation (Phase 9 §17-20). N'écrit jamais dans les comptabilités
/// sources : agrège des copies converties et mappées, applique les éliminations,
/// contrôle la cohérence.
/// </summary>
public sealed class SqlConsolidationService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant) : IConsolidationService
{
    private const decimal Cent = 0.01m;

    private sealed record RunRow(
        Guid Id, string PeriodLabel, DateOnly FromDate, DateOnly ToDate, Guid CurrencyId, string Currency,
        string Status, bool CoherenceOk, decimal TotalDebit, decimal TotalCredit, decimal EliminationTotal,
        string Diagnostics, DateTimeOffset? GeneratedAt, DateTimeOffset? ValidatedAt, DateTimeOffset? PublishedAt);
    private sealed record CompanyRow(
        Guid CompanyId, string CompanyName, bool Included, Guid? FunctionalCurrencyId, string FunctionalCurrency,
        decimal FxRate, bool PeriodClosed, bool MappingComplete, decimal ContributedDebit, decimal ContributedCredit);
    private sealed record AggRow(Guid GroupAccountId, string AccountType, decimal Debit, decimal Credit);
    private sealed record LineRow(
        string Code, string Name, string AccountType, decimal AggDebit, decimal AggCredit,
        decimal ElimDebit, decimal ElimCredit, decimal ConDebit, decimal ConCredit);

    public async Task<Result<ConsolidationRunDto>> CreateRunAsync(Guid groupId, CreateConsolidationRunRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        if (string.IsNullOrWhiteSpace(request.PeriodLabel) || request.PeriodLabel.Trim().Length > 60)
            return Error.Validation("consolidation.label", "Libellé de période requis (60 caractères au plus).");
        if (request.CompanyIds is null || request.CompanyIds.Count == 0) return Error.Validation("consolidation.no_company", "Aucune entreprise sélectionnée.");
        if (request.ToDate < request.FromDate) return Error.Validation("consolidation.period", "Période invalide.");
        if (request.ToDate.DayNumber - request.FromDate.DayNumber > 366)
            return Error.Validation("consolidation.period_length", "Une consolidation couvre au plus un exercice (366 jours).");

        using var _ = tenant.BeginSystemScope();
        var groupCompanies = await db.Companies.AsNoTracking().Where(c => c.GroupId == groupId)
            .Select(c => new { c.Id, c.Status }).ToListAsync(ct);
        if (request.CompanyIds.Any(id => groupCompanies.All(c => c.Id != id)))
            return Error.Validation("consolidation.company", "Une entreprise sélectionnée n'appartient pas au groupe.");
        if (request.CompanyIds.Any(id => groupCompanies.Any(c => c.Id == id && c.Status == "archived")))
            return Error.Validation("consolidation.archived_company", "Une société archivée ne peut plus être consolidée.");
        if (request.ProfileId is { } pid && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.consolidation_profile WHERE id = {pid} AND group_id = {groupId}) AS "Value"
                """).FirstAsync(ct))
            return Error.NotFound("consolidation.profile", "Profil de consolidation introuvable dans ce groupe.");

        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT COALESCE(
                (SELECT consolidation_currency_id FROM nexora.group_policy WHERE group_id = {groupId}),
                (SELECT default_currency_id FROM nexora.business_group WHERE id = {groupId})) AS "Value"
            """).FirstAsync(ct);

        var runId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.consolidation_run
                    (id, group_id, consolidation_profile_id, period_label, from_date, to_date, currency_id, status, created_by)
                VALUES ({runId}, {groupId}, {request.ProfileId}, {request.PeriodLabel.Trim()},
                        {request.FromDate}, {request.ToDate}, {currencyId}, 'draft', {tenant.UserId})
                """, token);
            foreach (var cid in request.CompanyIds.Distinct())
            {
                var funcCur = await db.Companies.AsNoTracking().Where(c => c.Id == cid).Select(c => c.DefaultCurrencyId).FirstAsync(token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.consolidation_run_company
                        (consolidation_run_id, group_id, company_id, included, functional_currency_id)
                    VALUES ({runId}, {groupId}, {cid}, true, {funcCur})
                    """, token);
            }
            return 0;
        }, ct);

        return await GetRunAsync(groupId, runId, ct);
    }

    public async Task<Result<ConsolidationRunDto>> ExecuteAsync(Guid groupId, Guid runId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        using var _ = tenant.BeginSystemScope();

        var run = await LoadRunRowAsync(groupId, runId, ct);
        if (run is null) return Error.NotFound("consolidation.run", "Consolidation introuvable.");
        if (run.Status is "validated" or "published")
            return Error.Conflict("consolidation.locked", "Une consolidation validée ou publiée doit d'abord être rouverte.");

        var companies = await LoadCompanyRowsAsync(runId, ct);
        var included = companies.Where(c => c.Included).ToList();
        if (included.Count == 0) return Error.Validation("consolidation.no_company", "Aucune entreprise incluse.");

        var diagnostics = new List<string>();
        var fxMissing = false;
        var aggByAccount = new Dictionary<Guid, (string Type, decimal Debit, decimal Credit)>();

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.consolidation_run_line WHERE consolidation_run_id = {runId}", token);

            foreach (var c in included)
            {
                // Taux de conversion vers la devise de consolidation (§13).
                decimal fx = 1m;
                if (c.FunctionalCurrencyId is { } fc && fc != run.CurrencyId)
                {
                    var rate = await db.Database.SqlQuery<decimal?>($"""
                        SELECT rate AS "Value" FROM nexora.exchange_rate
                        WHERE base_currency_id = {fc} AND quote_currency_id = {run.CurrencyId}
                          AND effective_at::date <= {run.ToDate}   -- taux de clôture (une date sans fuseau faisait planter l'exécution)
                        ORDER BY effective_at DESC LIMIT 1
                        """).FirstOrDefaultAsync(token);
                    if (rate is { } r && r > 0m) fx = r;
                    else
                    {
                        // Convertir à 1 une société en HTG dans une consolidation en USD donnerait des états faux :
                        // le calcul reste visible, mais la consolidation n'est pas validable (finalisation phase 14).
                        fxMissing = true;
                        diagnostics.Add($"Taux de change {c.FunctionalCurrency}→{run.Currency} absent au {run.ToDate:dd/MM/yyyy} pour {c.CompanyName} — saisissez-le : consolidation non validable.");
                    }
                }

                // Clôture de période (§17 étape 3 : vérifier, ne pas bloquer).
                var periodClosed = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS(SELECT 1 FROM nexora.accounting_period
                                  WHERE company_id = {c.CompanyId} AND status IN ('closed','locked')
                                    AND start_date <= {run.FromDate} AND end_date >= {run.ToDate}) AS "Value"
                    """).FirstAsync(token);
                if (!periodClosed) diagnostics.Add($"Période non clôturée pour {c.CompanyName}.");

                // Complétude du mapping (§17 étape 4).
                var unmapped = await db.Database.SqlQuery<int>($"""
                    SELECT COUNT(DISTINCT l.account_id)::int AS "Value"
                    FROM nexora.journal_entry_line l
                    JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                    LEFT JOIN nexora.account_group_mapping m ON m.account_id = l.account_id AND m.status = 'active'
                    WHERE e.company_id = {c.CompanyId} AND e.status = 'posted'
                      AND e.entry_date BETWEEN {run.FromDate} AND {run.ToDate} AND m.id IS NULL
                    """).FirstAsync(token);
                var mappingComplete = unmapped == 0;
                if (!mappingComplete) diagnostics.Add($"{unmapped} compte(s) non mappé(s) avec mouvements pour {c.CompanyName}.");

                // Agrégation : balance de la période mappée sur le plan groupe, convertie (§17 étapes 5-6).
                var agg = await db.Database.SqlQuery<AggRow>($"""
                    SELECT m.group_account_id AS "GroupAccountId", ga.account_type AS "AccountType",
                           COALESCE(SUM(l.base_debit), 0) AS "Debit", COALESCE(SUM(l.base_credit), 0) AS "Credit"
                    FROM nexora.journal_entry_line l
                    JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                    JOIN nexora.account_group_mapping m ON m.account_id = l.account_id AND m.status = 'active'
                    JOIN nexora.group_account ga ON ga.id = m.group_account_id
                    WHERE e.company_id = {c.CompanyId} AND e.status = 'posted'
                      AND e.entry_date BETWEEN {run.FromDate} AND {run.ToDate}
                    GROUP BY m.group_account_id, ga.account_type
                    """).ToListAsync(token);

                decimal contribD = 0m, contribC = 0m;
                foreach (var a in agg)
                {
                    var d = Math.Round(a.Debit * fx, 2);
                    var cr = Math.Round(a.Credit * fx, 2);
                    contribD += d; contribC += cr;
                    var cur = aggByAccount.GetValueOrDefault(a.GroupAccountId, (a.AccountType, 0m, 0m));
                    aggByAccount[a.GroupAccountId] = (a.AccountType, cur.Item2 + d, cur.Item3 + cr);
                }

                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.consolidation_run_company
                    SET fx_rate = {fx}, period_closed = {periodClosed}, mapping_complete = {mappingComplete},
                        contributed_debit = {contribD}, contributed_credit = {contribC}, updated_at = now()
                    WHERE consolidation_run_id = {runId} AND company_id = {c.CompanyId}
                    """, token);
            }

            // Éliminations intercompany rapprochées (§16) : réduites sur les comptes groupe éliminables.
            var matchedTotal = await db.Database.SqlQuery<decimal>($"""
                SELECT COALESCE(SUM(t.amount), 0) AS "Value"
                FROM nexora.intercompany_match im
                JOIN nexora.intercompany_transaction t ON t.id = im.source_transaction_id
                WHERE im.group_id = {groupId} AND im.status = 'matched'
                  AND (t.occurred_at AT TIME ZONE 'UTC')::date BETWEEN {run.FromDate} AND {run.ToDate}
                """).FirstAsync(token);

            var unreconciled = await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*)::int AS "Value"
                FROM nexora.intercompany_transaction t
                WHERE t.group_id = {groupId} AND t.status <> 'eliminated'
                  AND (t.occurred_at AT TIME ZONE 'UTC')::date BETWEEN {run.FromDate} AND {run.ToDate}
                  AND NOT EXISTS (
                      SELECT 1 FROM nexora.intercompany_match im
                      WHERE (im.source_transaction_id = t.id OR im.counterpart_transaction_id = t.id)
                        AND im.status = 'matched')
                """).FirstAsync(token);
            if (unreconciled > 0) diagnostics.Add($"{unreconciled} transaction(s) intercompany non rapprochée(s) sur la période.");

            var eliminableAssets = aggByAccount.Where(kv => kv.Value.Type == "asset").ToList();
            var eliminableLiabilities = aggByAccount.Where(kv => kv.Value.Type == "liability").ToList();
            var elimApplied = 0m;
            var elimByAccount = new Dictionary<Guid, (decimal Debit, decimal Credit)>();
            if (matchedTotal > 0m)
            {
                var elimAssetIds = (await db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM nexora.group_account
                    WHERE group_id = {groupId} AND is_eliminable AND account_type = 'asset'
                    """).ToListAsync(token)).ToHashSet();
                var elimLiabIds = (await db.Database.SqlQuery<Guid>($"""
                    SELECT id AS "Value" FROM nexora.group_account
                    WHERE group_id = {groupId} AND is_eliminable AND account_type = 'liability'
                    """).ToListAsync(token)).ToHashSet();

                var assetTarget = eliminableAssets.FirstOrDefault(kv => elimAssetIds.Contains(kv.Key));
                var liabTarget = eliminableLiabilities.FirstOrDefault(kv => elimLiabIds.Contains(kv.Key));
                if (assetTarget.Key != Guid.Empty && liabTarget.Key != Guid.Empty)
                {
                    var e = Math.Round(Math.Min(matchedTotal, Math.Min(assetTarget.Value.Debit, liabTarget.Value.Credit)), 2);
                    elimByAccount[assetTarget.Key] = (e, 0m);   // retire la créance intragroupe du débit
                    elimByAccount[liabTarget.Key] = (0m, e);    // retire la dette intragroupe du crédit
                    elimApplied = e;
                }
                else
                {
                    diagnostics.Add("Comptes groupe éliminables (actif + passif) manquants — éliminations non appliquées.");
                }
            }

            decimal totalConD = 0m, totalConC = 0m;
            foreach (var (accountId, val) in aggByAccount)
            {
                var (elimD, elimC) = elimByAccount.GetValueOrDefault(accountId, (0m, 0m));
                var conD = Math.Round(val.Debit - elimD, 2);
                var conC = Math.Round(val.Credit - elimC, 2);
                totalConD += conD; totalConC += conC;
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.consolidation_run_line
                        (consolidation_run_id, group_id, group_account_id, account_type,
                         aggregated_debit, aggregated_credit, elimination_debit, elimination_credit,
                         consolidated_debit, consolidated_credit)
                    VALUES ({runId}, {groupId}, {accountId}, {val.Type},
                            {val.Debit}, {val.Credit}, {elimD}, {elimC}, {conD}, {conC})
                    """, token);
            }

            var coherenceOk = Math.Abs(totalConD - totalConC) <= Cent && !fxMissing
                && diagnostics.All(d => !d.StartsWith("Comptes groupe éliminables"));
            var status = unreconciled > 0 ? "reconciling" : "ready";
            var diagJson = System.Text.Json.JsonSerializer.Serialize(diagnostics);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.consolidation_run
                SET status = {status}, coherence_ok = {coherenceOk},
                    total_debit = {Math.Round(totalConD, 2)}, total_credit = {Math.Round(totalConC, 2)},
                    elimination_total = {Math.Round(elimApplied, 2)}, diagnostics = {diagJson}::jsonb,
                    generated_at = now(), updated_at = now()
                WHERE id = {runId}
                """, token);
            return 0;
        }, ct);

        return await GetRunAsync(groupId, runId, ct);
    }

    public async Task<Result<ConsolidationRunDto>> ValidateAsync(Guid groupId, Guid runId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var run = await LoadRunRowAsync(groupId, runId, ct);
        if (run is null) return Error.NotFound("consolidation.run", "Consolidation introuvable.");
        if (run.Status != "ready") return Error.Conflict("consolidation.not_ready", "La consolidation doit être « prête » (rapprochements et cohérence).");
        if (!run.CoherenceOk) return Error.Conflict("consolidation.incoherent", "Contrôle de cohérence non satisfait (§20).");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.consolidation_run SET status = 'validated', validated_by = {tenant.UserId},
                validated_at = now(), updated_at = now() WHERE id = {runId} AND group_id = {groupId}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetRunAsync(groupId, runId, ct);
    }

    public async Task<Result<ConsolidationRunDto>> PublishAsync(Guid groupId, Guid runId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var run = await LoadRunRowAsync(groupId, runId, ct);
        if (run is null) return Error.NotFound("consolidation.run", "Consolidation introuvable.");
        if (run.Status != "validated") return Error.Conflict("consolidation.not_validated", "Seule une consolidation validée peut être publiée.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.consolidation_run SET status = 'published', published_by = {tenant.UserId},
                published_at = now(), updated_at = now() WHERE id = {runId} AND group_id = {groupId}
            """, ct);
        // Les transactions intercompany rapprochées de la période sont marquées éliminées (§16).
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.intercompany_transaction t SET status = 'eliminated', updated_at = now()
            FROM nexora.intercompany_match m
            JOIN nexora.intercompany_transaction src ON src.id = m.source_transaction_id
            WHERE (m.source_transaction_id = t.id OR m.counterpart_transaction_id = t.id)
              AND m.status = 'matched' AND t.group_id = {groupId}
              AND (src.occurred_at AT TIME ZONE 'UTC')::date BETWEEN {run.FromDate} AND {run.ToDate}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetRunAsync(groupId, runId, ct);
    }

    public async Task<Result<ConsolidationRunDto>> ReopenAsync(Guid groupId, Guid runId, ReopenRunRequest request, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Error.Validation("consolidation.reason", "Un motif de réouverture est obligatoire (§18).");
        var run = await LoadRunRowAsync(groupId, runId, ct);
        if (run is null) return Error.NotFound("consolidation.run", "Consolidation introuvable.");
        if (run.Status is not ("validated" or "published"))
            return Error.Conflict("consolidation.not_reopenable", "Seule une consolidation validée ou publiée se rouvre.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.consolidation_run SET status = 'reopened', reopen_reason = {request.Reason.Trim()},
                validated_by = NULL, validated_at = NULL, published_by = NULL, published_at = NULL, updated_at = now()
            WHERE id = {runId} AND group_id = {groupId}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetRunAsync(groupId, runId, ct);
    }

    public async Task<Result<ConsolidationRunDto>> GetRunAsync(Guid groupId, Guid runId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var run = await LoadRunRowAsync(groupId, runId, ct);
        if (run is null) return Error.NotFound("consolidation.run", "Consolidation introuvable.");
        var companies = await LoadCompanyRowsAsync(runId, ct);
        return Result.Success(MapRun(groupId, run, companies));
    }

    public async Task<IReadOnlyList<ConsolidationRunDto>> ListRunsAsync(Guid groupId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.consolidation_run WHERE group_id = {groupId} ORDER BY to_date DESC, created_at DESC
            """).ToListAsync(ct);
        var list = new List<ConsolidationRunDto>();
        foreach (var id in ids)
        {
            var run = await LoadRunRowAsync(groupId, id, ct);
            if (run is not null) list.Add(MapRun(groupId, run, await LoadCompanyRowsAsync(id, ct)));
        }
        return list;
    }

    public async Task<Result<ConsolidatedStatementsDto>> GetStatementsAsync(Guid groupId, Guid runId, CancellationToken ct = default)
    {
        GroupGuard.Require(tenant, groupId);
        var run = await LoadRunRowAsync(groupId, runId, ct);
        if (run is null) return Error.NotFound("consolidation.run", "Consolidation introuvable.");

        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT ga.code AS "Code", ga.name AS "Name", rl.account_type AS "AccountType",
                   rl.aggregated_debit AS "AggDebit", rl.aggregated_credit AS "AggCredit",
                   rl.elimination_debit AS "ElimDebit", rl.elimination_credit AS "ElimCredit",
                   rl.consolidated_debit AS "ConDebit", rl.consolidated_credit AS "ConCredit"
            FROM nexora.consolidation_run_line rl
            JOIN nexora.group_account ga ON ga.id = rl.group_account_id
            WHERE rl.consolidation_run_id = {runId} ORDER BY ga.sort_order, ga.code
            """).ToListAsync(ct);

        decimal Net(string type, bool debitPositive) => lines.Where(l => l.AccountType == type)
            .Sum(l => debitPositive ? l.ConDebit - l.ConCredit : l.ConCredit - l.ConDebit);

        var assets = Math.Round(Net("asset", true), 2);
        var liabilities = Math.Round(Net("liability", false), 2);
        var equity = Math.Round(Net("equity", false), 2);
        var revenue = Math.Round(Net("revenue", false), 2);
        var expense = Math.Round(Net("expense", true), 2);
        var netResult = Math.Round(revenue - expense, 2);

        var companies = await LoadCompanyRowsAsync(runId, ct);

        var dto = new ConsolidatedStatementsDto(
            runId, run.PeriodLabel, run.Currency, run.Status,
            lines.Select(l => new ConsolidationLineDto(l.Code, l.Name, l.AccountType,
                l.AggDebit, l.AggCredit, l.ElimDebit, l.ElimCredit, l.ConDebit, l.ConCredit)).ToList(),
            assets, liabilities, equity, revenue, expense, netResult,
            Math.Abs(assets - (liabilities + equity + netResult)) <= Cent,
            Math.Abs(lines.Sum(l => l.ConDebit) - lines.Sum(l => l.ConCredit)) <= Cent,
            companies.Select(MapCompany).ToList());
        return Result.Success(dto);
    }

    // --- helpers ---------------------------------------------------------

    private Task<RunRow?> LoadRunRowAsync(Guid groupId, Guid runId, CancellationToken ct)
        => db.Database.SqlQuery<RunRow>($"""
            SELECT r.id AS "Id", r.period_label AS "PeriodLabel", r.from_date AS "FromDate", r.to_date AS "ToDate",
                   r.currency_id AS "CurrencyId", cur.code AS "Currency", r.status AS "Status",
                   r.coherence_ok AS "CoherenceOk", r.total_debit AS "TotalDebit", r.total_credit AS "TotalCredit",
                   r.elimination_total AS "EliminationTotal", r.diagnostics::text AS "Diagnostics",
                   r.generated_at AS "GeneratedAt", r.validated_at AS "ValidatedAt", r.published_at AS "PublishedAt"
            FROM nexora.consolidation_run r JOIN nexora.currency cur ON cur.id = r.currency_id
            WHERE r.id = {runId} AND r.group_id = {groupId}
            """).FirstOrDefaultAsync(ct)!;

    private Task<List<CompanyRow>> LoadCompanyRowsAsync(Guid runId, CancellationToken ct)
        => db.Database.SqlQuery<CompanyRow>($"""
            SELECT rc.company_id AS "CompanyId", c.name AS "CompanyName", rc.included AS "Included",
                   rc.functional_currency_id AS "FunctionalCurrencyId", COALESCE(fc.code, '—') AS "FunctionalCurrency",
                   rc.fx_rate AS "FxRate", rc.period_closed AS "PeriodClosed", rc.mapping_complete AS "MappingComplete",
                   rc.contributed_debit AS "ContributedDebit", rc.contributed_credit AS "ContributedCredit"
            FROM nexora.consolidation_run_company rc
            JOIN nexora.company c ON c.id = rc.company_id
            LEFT JOIN nexora.currency fc ON fc.id = rc.functional_currency_id
            WHERE rc.consolidation_run_id = {runId} ORDER BY c.name
            """).ToListAsync(ct);

    private static ConsolidationCompanyDto MapCompany(CompanyRow r) => new(
        r.CompanyId, r.CompanyName, r.Included, r.FunctionalCurrency, r.FxRate,
        r.PeriodClosed, r.MappingComplete, r.ContributedDebit, r.ContributedCredit);

    private static ConsolidationRunDto MapRun(Guid groupId, RunRow r, List<CompanyRow> companies) => new(
        r.Id, groupId, r.PeriodLabel, r.FromDate, r.ToDate, r.Currency, r.Status, r.CoherenceOk,
        r.TotalDebit, r.TotalCredit, r.EliminationTotal,
        System.Text.Json.JsonSerializer.Deserialize<List<string>>(r.Diagnostics) ?? [],
        companies.Select(MapCompany).ToList(),
        r.GeneratedAt, r.ValidatedAt, r.PublishedAt);
}
