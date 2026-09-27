using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Integrations;
using Nexora.Contracts.Integrations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Integrations;

/// <summary>
/// Import et rapprochement des relevés bancaires (§16-17). Réutilise bank_statement /
/// bank_statement_line (V011). Import → normalisation → détection des doublons →
/// correspondance montant/date/référence → exceptions → validation.
/// </summary>
public sealed class SqlBankReconciliationService(NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant)
    : IBankReconciliationService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private const int MatchDayWindow = 3;

    private sealed record StatementRow(
        Guid Id, Guid FinancialAccountId, string? StatementNumber, DateOnly StatementDate,
        decimal OpeningBalance, decimal ClosingBalance, string Status, int LineCount, int MatchedCount);
    private sealed record LineRow(
        Guid Id, DateOnly ValueDate, decimal Amount, string? Description, string? Reference, string Status, Guid? MatchedTransactionId);
    private sealed record TxnCandidate(Guid Id, decimal Amount, DateOnly OccurredOn);

    public async Task<Result<ImportStatementResult>> ImportStatementAsync(ImportStatementRequest request, CancellationToken ct = default)
    {
        if (request.Lines.Count == 0) return Error.Validation("integrations.statement_empty", "Le relevé ne contient aucune ligne.");

        var accountOk = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.financial_account
            WHERE id = {request.FinancialAccountId} AND company_id = {Company}
            """).FirstAsync(ct);
        if (accountOk == 0) return Error.NotFound("integrations.financial_account", "Compte financier introuvable.");

        // Doublons déjà présents sur ce compte (n'importe quel relevé) : (date, montant, référence).
        var existing = await db.Database.SqlQuery<string>($"""
            SELECT (l.value_date::text || '|' || l.amount::text || '|' || COALESCE(l.reference,'')) AS "Value"
            FROM nexora.bank_statement_line l
            JOIN nexora.bank_statement s ON s.id = l.bank_statement_id
            WHERE s.company_id = {Company} AND s.financial_account_id = {request.FinancialAccountId}
            """).ToListAsync(ct);
        var seen = new HashSet<string>(existing);

        var toInsert = new List<StatementLineInput>();
        var duplicates = 0;
        foreach (var line in request.Lines)
        {
            var key = $"{line.ValueDate:yyyy-MM-dd}|{line.Amount}|{line.Reference ?? ""}";
            if (!seen.Add(key)) { duplicates++; continue; }
            toInsert.Add(line);
        }
        if (toInsert.Count == 0)
            return Error.Conflict("integrations.statement_all_duplicates", "Toutes les lignes du relevé sont des doublons — rien importé.");

        var statementId = Guid.NewGuid();
        var result = await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.bank_statement
                    (id, company_id, financial_account_id, statement_number, statement_date, opening_balance, closing_balance, status)
                VALUES ({statementId}, {Company}, {request.FinancialAccountId}, {request.StatementNumber},
                        {request.StatementDate}, {request.OpeningBalance}, {request.ClosingBalance}, 'imported')
                """, token);

            foreach (var line in toInsert)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.bank_statement_line
                        (id, bank_statement_id, company_id, value_date, amount, description, reference, status)
                    VALUES ({Guid.NewGuid()}, {statementId}, {Company}, {line.ValueDate}, {line.Amount},
                            {line.Description}, {line.Reference}, 'unmatched')
                    """, token);
            }
            return toInsert.Count;
        }, ct);

        var matched = (await AutoMatchAsync(statementId, ct)).Value;
        return Result.Success(new ImportStatementResult(statementId, result, duplicates, matched, result - matched));
    }

    public async Task<IReadOnlyList<BankStatementDto>> ListStatementsAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<StatementRow>($"""
            SELECT s.id AS "Id", s.financial_account_id AS "FinancialAccountId", s.statement_number AS "StatementNumber",
                   s.statement_date AS "StatementDate", s.opening_balance AS "OpeningBalance",
                   s.closing_balance AS "ClosingBalance", s.status AS "Status",
                   (SELECT COUNT(*)::int FROM nexora.bank_statement_line l WHERE l.bank_statement_id = s.id) AS "LineCount",
                   (SELECT COUNT(*)::int FROM nexora.bank_statement_line l WHERE l.bank_statement_id = s.id AND l.status = 'matched') AS "MatchedCount"
            FROM nexora.bank_statement s WHERE s.company_id = {Company}
            ORDER BY s.statement_date DESC, s.created_at DESC LIMIT 100
            """).ToListAsync(ct);
        return rows.Select(r => new BankStatementDto(
            r.Id, r.FinancialAccountId, r.StatementNumber, r.StatementDate, r.OpeningBalance, r.ClosingBalance,
            r.Status, r.LineCount, r.MatchedCount)).ToList();
    }

    public async Task<Result<IReadOnlyList<StatementLineDto>>> LinesAsync(Guid statementId, CancellationToken ct = default)
    {
        var owned = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.bank_statement WHERE id = {statementId} AND company_id = {Company}
            """).FirstAsync(ct);
        if (owned == 0) return Error.NotFound("integrations.statement", "Relevé introuvable.");

        var rows = await db.Database.SqlQuery<LineRow>($"""
            SELECT id AS "Id", value_date AS "ValueDate", amount AS "Amount", description AS "Description",
                   reference AS "Reference", status AS "Status", matched_transaction_id AS "MatchedTransactionId"
            FROM nexora.bank_statement_line WHERE bank_statement_id = {statementId} AND company_id = {Company}
            ORDER BY value_date, amount
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<StatementLineDto>>(rows.Select(r => new StatementLineDto(
            r.Id, r.ValueDate, r.Amount, r.Description, r.Reference, r.Status, r.MatchedTransactionId)).ToList());
    }

    public async Task<Result<int>> AutoMatchAsync(Guid statementId, CancellationToken ct = default)
    {
        var owned = await db.Database.SqlQuery<Guid>($"""
            SELECT financial_account_id AS "Value" FROM nexora.bank_statement WHERE id = {statementId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (owned == Guid.Empty) return Error.NotFound("integrations.statement", "Relevé introuvable.");

        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT id AS "Id", value_date AS "ValueDate", amount AS "Amount", description AS "Description",
                   reference AS "Reference", status AS "Status", matched_transaction_id AS "MatchedTransactionId"
            FROM nexora.bank_statement_line WHERE bank_statement_id = {statementId} AND company_id = {Company} AND status = 'unmatched'
            """).ToListAsync(ct);
        if (lines.Count == 0) return Result.Success(0);

        var candidates = await db.Database.SqlQuery<TxnCandidate>($"""
            SELECT t.id AS "Id", t.amount AS "Amount", (t.occurred_at::date) AS "OccurredOn"
            FROM nexora.financial_transaction t
            WHERE t.company_id = {Company} AND t.financial_account_id = {owned} AND t.status = 'cleared'
              AND NOT EXISTS (SELECT 1 FROM nexora.bank_statement_line bl WHERE bl.matched_transaction_id = t.id)
            """).ToListAsync(ct);

        var used = new HashSet<Guid>();
        var matched = 0;
        foreach (var line in lines)
        {
            var best = candidates
                .Where(c => !used.Contains(c.Id) && c.Amount == line.Amount
                            && Math.Abs(c.OccurredOn.DayNumber - line.ValueDate.DayNumber) <= MatchDayWindow)
                .OrderBy(c => Math.Abs(c.OccurredOn.DayNumber - line.ValueDate.DayNumber))
                .FirstOrDefault();
            if (best is null) continue;

            used.Add(best.Id);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.bank_statement_line SET matched_transaction_id = {best.Id}, status = 'matched', updated_at = now()
                WHERE id = {line.Id}
                """, ct);
            matched++;
        }

        await RefreshStatementStatusAsync(statementId, ct);
        return Result.Success(matched);
    }

    public async Task<Result> MatchLineAsync(Guid lineId, MatchLineRequest request, CancellationToken ct = default)
    {
        var line = await db.Database.SqlQuery<Guid>($"""
            SELECT bank_statement_id AS "Value" FROM nexora.bank_statement_line WHERE id = {lineId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (line == Guid.Empty) return Error.NotFound("integrations.statement_line", "Ligne de relevé introuvable.");

        var txnOk = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.financial_transaction WHERE id = {request.TransactionId} AND company_id = {Company}
            """).FirstAsync(ct);
        if (txnOk == 0) return Error.NotFound("integrations.transaction", "Transaction financière introuvable.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.bank_statement_line SET matched_transaction_id = {request.TransactionId}, status = 'matched', updated_at = now()
            WHERE id = {lineId}
            """, ct);
        await RefreshStatementStatusAsync(line, ct);
        return Result.Success();
    }

    public async Task<Result> ValidateAsync(Guid statementId, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.bank_statement SET status = 'reconciled', updated_at = now()
            WHERE id = {statementId} AND company_id = {Company}
            """, ct);
        return n == 0 ? Error.NotFound("integrations.statement", "Relevé introuvable.") : Result.Success();
    }

    private Task RefreshStatementStatusAsync(Guid statementId, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.bank_statement s SET status = CASE
                WHEN NOT EXISTS (SELECT 1 FROM nexora.bank_statement_line l WHERE l.bank_statement_id = s.id AND l.status = 'unmatched')
                    THEN 'reconciled' ELSE 'reconciling' END,
                updated_at = now()
            WHERE s.id = {statementId} AND s.status <> 'reconciled'
            """, ct);
}

/// <summary>Centre des intégrations — observabilité transverse (§39-40).</summary>
public sealed class SqlIntegrationCenter(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock)
    : IIntegrationCenter
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record CountRow(string Key, int Count);
    private sealed record HealthRow(
        Guid Id, string Name, string ConnectorType, string Provider, string Status,
        DateTimeOffset? LastSyncAt, string? LastError, int PendingPayments, DateTimeOffset? LastWebhookAt);

    public async Task<IntegrationCenterDto> OverviewAsync(CancellationToken ct = default)
    {
        var connectorCounts = await db.Database.SqlQuery<CountRow>($"""
            SELECT status AS "Key", COUNT(*)::int AS "Count" FROM nexora.integration_connector
            WHERE company_id = {Company} GROUP BY status
            """).ToListAsync(ct);
        int Conn(string k) => connectorCounts.FirstOrDefault(c => c.Key == k)?.Count ?? 0;

        var webhookCounts = await db.Database.SqlQuery<CountRow>($"""
            SELECT status AS "Key", COUNT(*)::int AS "Count" FROM nexora.webhook_event
            WHERE company_id = {Company} AND received_at > now() - interval '30 days' GROUP BY status
            """).ToListAsync(ct);
        int Wh(string k) => webhookCounts.FirstOrDefault(c => c.Key == k)?.Count ?? 0;

        var deliveryCounts = await db.Database.SqlQuery<CountRow>($"""
            SELECT status AS "Key", COUNT(*)::int AS "Count" FROM nexora.outbound_webhook_delivery
            WHERE company_id = {Company} GROUP BY status
            """).ToListAsync(ct);
        int Del(string k) => deliveryCounts.FirstOrDefault(c => c.Key == k)?.Count ?? 0;

        var pendingPayments = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.payment_intent
            WHERE company_id = {Company} AND status IN ('pending','authorized')
            """).FirstAsync(ct);

        var bankExceptions = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.bank_statement_line l
            JOIN nexora.bank_statement s ON s.id = l.bank_statement_id
            WHERE s.company_id = {Company} AND s.status <> 'reconciled' AND l.status = 'unmatched'
            """).FirstAsync(ct);

        var connectors = (await db.Database.SqlQuery<HealthRow>($"""
            SELECT c.id AS "Id", c.name AS "Name", c.connector_type AS "ConnectorType", c.provider AS "Provider",
                   c.status AS "Status", c.last_sync_at AS "LastSyncAt", c.last_error AS "LastError",
                   (SELECT COUNT(*)::int FROM nexora.payment_intent pi
                     WHERE pi.connector_id = c.id AND pi.status IN ('pending','authorized')) AS "PendingPayments",
                   (SELECT MAX(we.received_at) FROM nexora.webhook_event we WHERE we.connector_id = c.id) AS "LastWebhookAt"
            FROM nexora.integration_connector c WHERE c.company_id = {Company}
            ORDER BY c.connector_type, c.name
            """).ToListAsync(ct))
            .Select(r => new ConnectorHealthDto(r.Id, r.Name, r.ConnectorType, r.Provider, r.Status,
                r.LastSyncAt, r.LastError, r.PendingPayments, r.LastWebhookAt))
            .ToList();

        var actions = new List<string>();
        if (Conn("error") > 0) actions.Add($"{Conn("error")} connecteur(s) en erreur — tester la connexion / reconfigurer.");
        if (Wh("failed") > 0) actions.Add($"{Wh("failed")} webhook(s) entrant(s) en échec — inspecter le journal technique.");
        if (pendingPayments > 0) actions.Add($"{pendingPayments} paiement(s) en attente — vérifier auprès du fournisseur, lancer l'expiration si dépassé.");
        if (Del("exhausted") > 0) actions.Add($"{Del("exhausted")} livraison(s) sortante(s) épuisée(s) — corriger l'URL cible puis rejouer.");
        if (bankExceptions > 0) actions.Add($"{bankExceptions} ligne(s) de relevé non rapprochée(s) — rapprochement manuel requis.");

        return new IntegrationCenterDto(
            clock.UtcNow, Conn("active"), Conn("error"),
            pendingPayments, Wh("failed"), Wh("duplicate"),
            Del("pending") + Del("failed"), Del("exhausted"), bankExceptions,
            connectors, actions);
    }
}
