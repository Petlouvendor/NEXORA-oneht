using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Notifications;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>Actions de recouvrement (§13) : appel, SMS, visite, note… jamais modifiées ni supprimées.</summary>
public sealed class SqlCollectionActionService(NexoraDbContext db, ITenantContext tenant, IBusinessCalendar calendar) : ICollectionActionService
{
    private Guid Company => CustomerAccountRules.Company(tenant);

    public async Task<Result<CollectionActionDto>> LogAsync(Guid customerId, CreateCollectionActionRequest r, CancellationToken ct = default)
    {
        var type = r.ActionType?.Trim().ToLowerInvariant() ?? "";
        if (!CustomerAccountRules.ActionTypes.Contains(type))
            return Error.Validation("collection.action_type", $"Type d'action « {r.ActionType} » inconnu.");
        var outcome = string.IsNullOrWhiteSpace(r.Outcome) ? null : r.Outcome.Trim().ToLowerInvariant();
        if (outcome is not null && !CustomerAccountRules.ActionOutcomes.Contains(outcome))
            return Error.Validation("collection.outcome", $"Résultat « {r.Outcome} » inconnu.");
        if (string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("collection.note", "La note de suivi est obligatoire.");
        if (r.NextActionDate is { } next && next < calendar.Today)
            return Error.Validation("collection.next_date", "La prochaine action ne peut pas être dans le passé.");
        if (!await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.NotFound("customer.not_found", "Client introuvable.");
        if (r.ReceivableId is { } rid && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.customer_receivable WHERE id = {rid} AND customer_id = {customerId}
                              AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("collection.credit", "Ce crédit n'appartient pas au client.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.collection_action (id, company_id, customer_id, receivable_id, action_type, outcome, note, next_action_date, created_by)
            VALUES ({id}, {Company}, {customerId}, {r.ReceivableId}, {type}, {outcome}, {r.Note.Trim()}, {r.NextActionDate},
                    {CustomerAccountRules.User(tenant)})
            """, ct);
        return Result.Success((await Query(null, id, false).FirstAsync(ct)));
    }

    public async Task<IReadOnlyList<CollectionActionDto>> ListAsync(Guid? customerId, bool onlyDue, CancellationToken ct = default)
        => await Query(customerId, null, onlyDue).ToListAsync(ct);

    private IQueryable<CollectionActionDto> Query(Guid? customerId, Guid? id, bool onlyDue)
    {
        var today = calendar.Today;
        return db.Database.SqlQuery<CollectionActionDto>($"""
            SELECT a.id AS "Id", a.customer_id AS "CustomerId", c.name AS "CustomerName", a.receivable_id AS "ReceivableId",
                   r.credit_number AS "CreditNumber", a.action_type AS "ActionType", a.outcome AS "Outcome", a.note AS "Note",
                   a.next_action_date AS "NextActionDate", u.full_name AS "CreatedByName", a.created_at AS "CreatedAt"
            FROM nexora.collection_action a
            JOIN nexora.customer c ON c.id = a.customer_id
            LEFT JOIN nexora.customer_receivable r ON r.id = a.receivable_id
            LEFT JOIN nexora.app_user u ON u.id = a.created_by
            WHERE a.company_id = {Company}
              AND ({customerId}::uuid IS NULL OR a.customer_id = {customerId}::uuid)
              AND ({id}::uuid IS NULL OR a.id = {id}::uuid)
              AND (NOT {onlyDue} OR (a.next_action_date IS NOT NULL AND a.next_action_date <= {today}))
            ORDER BY a.created_at DESC
            LIMIT 500
            """);
    }
}

/// <summary>
/// Rapports clients (§26). Chaque rapport rend un tableau (colonnes + lignes) directement exportable ;
/// les montants sont lus dans les tables sources, jamais recalculés côté navigateur.
/// </summary>
public sealed class SqlCustomerReports(NexoraDbContext db, ITenantContext tenant, IBusinessCalendar calendar) : ICustomerReports
{
    private Guid Company => CustomerAccountRules.Company(tenant);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static readonly CustomerReportDefinitionDto[] Definitions =
    [
        new("balances", "Clients avec solde", "Clients dont le solde dû ou l'avoir disponible n'est pas nul, avec l'échu."),
        new("open_credits", "Crédits ouverts", "Chaque crédit encore dû : montant, réglé, reste, échéance, retard."),
        new("aging", "Balance âgée des créances", "Reste dû par client réparti en à échoir / 1-30 / 31-60 / 61-90 / > 90 jours."),
        new("payments", "Versements par période et moyen", "Versements validés de la période regroupés par moyen de paiement."),
        new("payment_journal", "Journal des versements", "Chaque versement de la période, annulés compris, avec auteur et motif."),
        new("expenses", "Dépenses par client", "Dépenses imputées de la période par client et catégorie, refacturées ou non."),
        new("over_limit", "Clients au-delà de leur limite", "Clients dont l'encours dépasse la limite de crédit accordée."),
        new("credit_memos", "Avoirs et remboursements", "Avoirs émis, utilisés, disponibles et remboursements de la période."),
        new("collections", "Actions de recouvrement", "Relances, appels, visites et promesses de la période."),
        new("ledger", "Journal financier clients", "Tous les mouvements du journal client de la période (historique complet)."),
    ];

    public IReadOnlyList<CustomerReportDefinitionDto> Catalog() => Definitions;

    private sealed record Line(string?[] Cells, decimal A, decimal B);

    public async Task<Result<CustomerReportDto>> RunAsync(string key, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var def = Definitions.FirstOrDefault(d => d.Key == key);
        if (def is null) return Error.NotFound("customer_report.unknown", $"Rapport « {key} » inconnu.");
        var today = calendar.Today;
        var start = from ?? today.AddDays(-30);
        var end = to ?? today;
        if (end < start) return Error.Validation("customer_report.period", "La fin de période précède son début.");
        var periodic = key is "payments" or "payment_journal" or "expenses" or "credit_memos" or "collections" or "ledger";

        (string[] Columns, List<string?[]> Rows, Dictionary<string, decimal> Totals) table = key switch
        {
            "balances" => await BalancesAsync(today, ct),
            "open_credits" => await OpenCreditsAsync(today, ct),
            "aging" => await AgingAsync(today, ct),
            "payments" => await PaymentsByMethodAsync(start, end, ct),
            "payment_journal" => await PaymentJournalAsync(start, end, ct),
            "expenses" => await ExpensesAsync(start, end, ct),
            "over_limit" => await OverLimitAsync(ct),
            "credit_memos" => await CreditMemosAsync(start, end, ct),
            "collections" => await CollectionsAsync(start, end, ct),
            _ => await LedgerAsync(start, end, ct),
        };
        return Result.Success(new CustomerReportDto(def.Key, def.Title, periodic ? start : null, periodic ? end : null,
            table.Columns, table.Rows.Select(r => (IReadOnlyList<string?>)r).ToList(), table.Totals));
    }

    private static string M(decimal v) => v.ToString("0.00", Inv);

    /// <summary>§21 : un total par devise (« Solde dû (HTG) »), jamais une somme de deux devises.</summary>
    private static Dictionary<string, decimal> ByCurrency<T>(IEnumerable<T> rows, Func<T, string> currency,
        params (string Label, Func<T, decimal> Value)[] columns)
    {
        var totals = new Dictionary<string, decimal>();
        foreach (var g in rows.GroupBy(currency).OrderBy(g => g.Key))
            foreach (var (label, value) in columns)
                totals[$"{label} ({g.Key})"] = g.Sum(value);
        return totals;
    }
    private static string? D(DateOnly? d) => d?.ToString("yyyy-MM-dd", Inv);
    private static string T(DateTimeOffset d) => d.UtcDateTime.ToString("yyyy-MM-dd HH:mm", Inv);

    private sealed record BalanceRow(string CustomerNumber, string Name, string Status, string Currency, decimal? CreditLimit,
        decimal Due, decimal Overdue, decimal StoreCredit);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> BalancesAsync(DateOnly today, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<BalanceRow>($"""
            WITH x AS (
                SELECT r.customer_id, r.currency_id, SUM(r.remaining_amount) AS due,
                       COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date < {today}), 0) AS overdue, 0::numeric AS store
                FROM nexora.customer_receivable r
                WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
                GROUP BY r.customer_id, r.currency_id
                UNION ALL
                SELECT m.customer_id, m.currency_id, 0, 0, SUM(m.remaining_amount)
                FROM nexora.customer_credit_memo m
                WHERE m.company_id = {Company} AND m.status IN ('available','partially_used')
                GROUP BY m.customer_id, m.currency_id)
            SELECT c.customer_number AS "CustomerNumber", c.name AS "Name", c.status AS "Status", cur.code AS "Currency",
                   CASE WHEN x.currency_id = c.default_currency_id THEN c.credit_limit END AS "CreditLimit",
                   SUM(x.due) AS "Due", SUM(x.overdue) AS "Overdue", SUM(x.store) AS "StoreCredit"
            FROM x JOIN nexora.customer c ON c.id = x.customer_id JOIN nexora.currency cur ON cur.id = x.currency_id
            GROUP BY c.id, c.customer_number, c.name, c.status, cur.code, x.currency_id, c.default_currency_id, c.credit_limit
            HAVING SUM(x.due) <> 0 OR SUM(x.store) <> 0
            ORDER BY cur.code, SUM(x.due) DESC
            """).ToListAsync(ct);
        return (["Code client", "Client", "Statut", "Devise", "Limite", "Solde dû", "Dont échu", "Avoir disponible", "Solde net"],
            rows.Select(r => new string?[] { r.CustomerNumber, r.Name, r.Status, r.Currency, r.CreditLimit is { } l ? M(l) : null,
                M(r.Due), M(r.Overdue), M(r.StoreCredit), M(r.Due - r.StoreCredit) }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Solde dû", r => r.Due), ("Dont échu", r => r.Overdue), ("Avoir disponible", r => r.StoreCredit)));
    }

    private sealed record CreditRow(string CreditNumber, string CustomerNumber, string Name, string? Origin, string Currency,
        decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount, DateOnly IssueDate, DateOnly? DueDate, string Status);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> OpenCreditsAsync(DateOnly today, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<CreditRow>($"""
            SELECT r.credit_number AS "CreditNumber", c.customer_number AS "CustomerNumber", c.name AS "Name",
                   coalesce(s.document_number, e.expense_number) AS "Origin", cur.code AS "Currency",
                   r.original_amount AS "OriginalAmount", r.paid_amount AS "PaidAmount", r.remaining_amount AS "RemainingAmount",
                   r.issue_date AS "IssueDate", r.due_date AS "DueDate", r.status AS "Status"
            FROM nexora.customer_receivable r
            JOIN nexora.customer c ON c.id = r.customer_id
            JOIN nexora.currency cur ON cur.id = r.currency_id
            LEFT JOIN nexora.sale s ON s.id = r.sale_id
            LEFT JOIN nexora.customer_expense e ON e.id = r.source_id AND r.source_type = 'customer_expense'
            WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND r.remaining_amount > 0
            ORDER BY r.due_date NULLS LAST, r.issue_date
            """).ToListAsync(ct);
        return (["Crédit", "Code client", "Client", "Origine", "Devise", "Montant", "Réglé", "Reste dû", "Émis le", "Échéance", "Statut", "Retard (j)"],
            rows.Select(r => new string?[] { r.CreditNumber, r.CustomerNumber, r.Name, r.Origin, r.Currency, M(r.OriginalAmount),
                M(r.PaidAmount), M(r.RemainingAmount), D(r.IssueDate), D(r.DueDate),
                SqlCustomerAccountQueries.DisplayStatus(r.Status, r.RemainingAmount, r.DueDate, today),
                (r.DueDate is { } d && d < today ? today.DayNumber - d.DayNumber : 0).ToString(Inv) }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Montant", r => r.OriginalAmount), ("Reste dû", r => r.RemainingAmount)));
    }

    private sealed record AgingRow(string CustomerNumber, string Name, string Currency, decimal Current, decimal B1, decimal B2, decimal B3, decimal B4);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> AgingAsync(DateOnly today, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<AgingRow>($"""
            SELECT c.customer_number AS "CustomerNumber", c.name AS "Name", cur.code AS "Currency",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date IS NULL OR r.due_date >= {today}), 0) AS "Current",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date BETWEEN {today.AddDays(-30)} AND {today.AddDays(-1)}), 0) AS "B1",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date BETWEEN {today.AddDays(-60)} AND {today.AddDays(-31)}), 0) AS "B2",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date BETWEEN {today.AddDays(-90)} AND {today.AddDays(-61)}), 0) AS "B3",
                   COALESCE(SUM(r.remaining_amount) FILTER (WHERE r.due_date < {today.AddDays(-90)}), 0) AS "B4"
            FROM nexora.customer_receivable r
            JOIN nexora.customer c ON c.id = r.customer_id
            JOIN nexora.currency cur ON cur.id = r.currency_id
            WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND r.remaining_amount > 0
            GROUP BY c.customer_number, c.name, cur.code
            ORDER BY SUM(r.remaining_amount) DESC
            """).ToListAsync(ct);
        return (["Code client", "Client", "Devise", "À échoir", "1-30 j", "31-60 j", "61-90 j", "> 90 j", "Total"],
            rows.Select(r => new string?[] { r.CustomerNumber, r.Name, r.Currency, M(r.Current), M(r.B1), M(r.B2), M(r.B3), M(r.B4),
                M(r.Current + r.B1 + r.B2 + r.B3 + r.B4) }).ToList(),
            ByCurrency(rows, r => r.Currency, ("À échoir", r => r.Current), ("1-30 j", r => r.B1), ("31-60 j", r => r.B2),
                ("61-90 j", r => r.B3), ("> 90 j", r => r.B4)));
    }

    private sealed record MethodRow(string Method, string Currency, int Count, decimal Amount, int Voided, decimal VoidedAmount);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> PaymentsByMethodAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<MethodRow>($"""
            SELECT coalesce(pm.name, fa.name, 'Non précisé') AS "Method", cur.code AS "Currency",
                   COUNT(*) FILTER (WHERE p.status = 'completed')::int AS "Count",
                   COALESCE(SUM(p.amount) FILTER (WHERE p.status = 'completed'), 0) AS "Amount",
                   COUNT(*) FILTER (WHERE p.status = 'void')::int AS "Voided",
                   COALESCE(SUM(p.amount) FILTER (WHERE p.status = 'void'), 0) AS "VoidedAmount"
            FROM nexora.payment p
            JOIN nexora.currency cur ON cur.id = p.currency_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            LEFT JOIN nexora.financial_account fa ON fa.id = p.financial_account_id
            WHERE p.company_id = {Company} AND p.direction = 'inbound' AND p.customer_id IS NOT NULL
              AND NOT EXISTS(SELECT 1 FROM nexora.sale_payment sp WHERE sp.payment_id = p.id)
              AND p.received_at::date BETWEEN {start} AND {end}
            GROUP BY 1, 2
            ORDER BY 4 DESC
            """).ToListAsync(ct);
        return (["Moyen", "Devise", "Versements", "Montant", "Annulés", "Montant annulé"],
            rows.Select(r => new string?[] { r.Method, r.Currency, r.Count.ToString(Inv), M(r.Amount), r.Voided.ToString(Inv), M(r.VoidedAmount) }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Montant", r => r.Amount), ("Montant annulé", r => r.VoidedAmount)));
    }

    private sealed record JournalRow(DateTimeOffset ReceivedAt, string PaymentNumber, string CustomerNumber, string Name, string Currency,
        decimal Amount, string Status, string? Method, string? Credits, string? ReceivedBy, string? Reference, string? VoidReason);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> PaymentJournalAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<JournalRow>($"""
            SELECT p.received_at AS "ReceivedAt", p.payment_number AS "PaymentNumber", c.customer_number AS "CustomerNumber",
                   c.name AS "Name", cur.code AS "Currency", p.amount AS "Amount", p.status AS "Status",
                   coalesce(pm.name, fa.name) AS "Method",
                   (SELECT string_agg(r.credit_number, ', ' ORDER BY r.credit_number) FROM nexora.payment_allocation a
                      JOIN nexora.customer_receivable r ON r.id = a.receivable_id WHERE a.payment_id = p.id) AS "Credits",
                   u.full_name AS "ReceivedBy", p.reference AS "Reference", p.void_reason AS "VoidReason"
            FROM nexora.payment p
            JOIN nexora.customer c ON c.id = p.customer_id
            JOIN nexora.currency cur ON cur.id = p.currency_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            LEFT JOIN nexora.financial_account fa ON fa.id = p.financial_account_id
            LEFT JOIN nexora.app_user u ON u.id = p.created_by
            WHERE p.company_id = {Company} AND p.direction = 'inbound'
              AND NOT EXISTS(SELECT 1 FROM nexora.sale_payment sp WHERE sp.payment_id = p.id)
              AND p.received_at::date BETWEEN {start} AND {end}
            ORDER BY p.received_at
            """).ToListAsync(ct);
        return (["Date", "Versement", "Code client", "Client", "Devise", "Montant", "Statut", "Moyen", "Crédits réglés", "Reçu par", "Référence", "Motif d'annulation"],
            rows.Select(r => new string?[] { T(r.ReceivedAt), r.PaymentNumber, r.CustomerNumber, r.Name, r.Currency, M(r.Amount),
                r.Status == "void" ? "annulé" : "validé", r.Method, r.Credits, r.ReceivedBy, r.Reference, r.VoidReason }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Validés", r => r.Status != "void" ? r.Amount : 0m), ("Annulés", r => r.Status == "void" ? r.Amount : 0m)));
    }

    private sealed record ExpenseRow(string CustomerNumber, string Name, string Category, string Currency, int Count,
        decimal Billed, decimal NotBilled, decimal Pending);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> ExpensesAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ExpenseRow>($"""
            SELECT c.customer_number AS "CustomerNumber", c.name AS "Name", e.category AS "Category", cur.code AS "Currency",
                   COUNT(*)::int AS "Count",
                   COALESCE(SUM(e.amount) FILTER (WHERE e.status = 'validated' AND e.billable), 0) AS "Billed",
                   COALESCE(SUM(e.amount) FILTER (WHERE e.status = 'validated' AND NOT e.billable), 0) AS "NotBilled",
                   COALESCE(SUM(e.amount) FILTER (WHERE e.status IN ('draft','pending')), 0) AS "Pending"
            FROM nexora.customer_expense e
            JOIN nexora.customer c ON c.id = e.customer_id
            JOIN nexora.currency cur ON cur.id = e.currency_id
            WHERE e.company_id = {Company} AND e.expense_date BETWEEN {start} AND {end}
            GROUP BY c.customer_number, c.name, e.category, cur.code
            ORDER BY c.name, e.category
            """).ToListAsync(ct);
        return (["Code client", "Client", "Catégorie", "Devise", "Dépenses", "Refacturées validées", "Non refacturées validées", "En attente"],
            rows.Select(r => new string?[] { r.CustomerNumber, r.Name, r.Category, r.Currency, r.Count.ToString(Inv), M(r.Billed),
                M(r.NotBilled), M(r.Pending) }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Refacturées", r => r.Billed), ("Non refacturées", r => r.NotBilled), ("En attente", r => r.Pending)));
    }

    private sealed record LimitRow(string CustomerNumber, string Name, string Currency, decimal CreditLimit, decimal Due);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> OverLimitAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<LimitRow>($"""
            SELECT * FROM (
                SELECT c.customer_number AS "CustomerNumber", c.name AS "Name", cur.code AS "Currency", c.credit_limit AS "CreditLimit",
                       COALESCE((SELECT SUM(nexora.convert_amount(r.remaining_amount, r.currency_id, c.default_currency_id))
                                 FROM nexora.customer_receivable r WHERE r.customer_id = c.id
                                 AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS "Due"
                FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id
                WHERE c.company_id = {Company}) t
            WHERE "Due" > "CreditLimit"
            ORDER BY "Due" - "CreditLimit" DESC
            """).ToListAsync(ct);
        return (["Code client", "Client", "Devise", "Limite", "Encours", "Dépassement"],
            rows.Select(r => new string?[] { r.CustomerNumber, r.Name, r.Currency, M(r.CreditLimit), M(r.Due), M(r.Due - r.CreditLimit) }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Dépassement", r => r.Due - r.CreditLimit)));
    }

    private sealed record MemoRow(DateTimeOffset At, string Kind, string Number, string CustomerNumber, string Name, string Currency,
        decimal Amount, decimal Remaining, string Status, string? Detail);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> CreditMemosAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<MemoRow>($"""
            SELECT * FROM (
                SELECT m.created_at AS "At", 'avoir' AS "Kind", m.memo_number AS "Number", c.customer_number AS "CustomerNumber",
                       c.name AS "Name", cur.code AS "Currency", m.amount AS "Amount", m.remaining_amount AS "Remaining",
                       m.status AS "Status", m.reason AS "Detail"
                FROM nexora.customer_credit_memo m JOIN nexora.customer c ON c.id = m.customer_id
                JOIN nexora.currency cur ON cur.id = m.currency_id
                WHERE m.company_id = {Company} AND m.created_at::date BETWEEN {start} AND {end}
                UNION ALL
                SELECT x.requested_at, 'remboursement', x.refund_number, c.customer_number, c.name, cur.code, x.amount, 0::numeric,
                       x.status, x.reason
                FROM nexora.customer_refund x JOIN nexora.customer c ON c.id = x.customer_id
                JOIN nexora.currency cur ON cur.id = x.currency_id
                WHERE x.company_id = {Company} AND x.requested_at::date BETWEEN {start} AND {end}) t
            ORDER BY "At"
            """).ToListAsync(ct);
        return (["Date", "Type", "Numéro", "Code client", "Client", "Devise", "Montant", "Disponible", "Statut", "Motif"],
            rows.Select(r => new string?[] { T(r.At), r.Kind, r.Number, r.CustomerNumber, r.Name, r.Currency, M(r.Amount),
                r.Kind == "avoir" ? M(r.Remaining) : null, r.Status, r.Detail }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Avoirs émis", r => r.Kind == "avoir" ? r.Amount : 0m),
                ("Remboursés", r => r.Kind == "remboursement" && r.Status == "executed" ? r.Amount : 0m)));
    }

    private sealed record ActionRow(DateTimeOffset At, string CustomerNumber, string Name, string Kind, string? Outcome, string? Detail,
        decimal? Amount, string? Actor);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> CollectionsAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<ActionRow>($"""
            SELECT * FROM (
                SELECT a.created_at AS "At", c.customer_number AS "CustomerNumber", c.name AS "Name", a.action_type AS "Kind",
                       a.outcome AS "Outcome", a.note AS "Detail", NULL::numeric AS "Amount", u.full_name AS "Actor"
                FROM nexora.collection_action a JOIN nexora.customer c ON c.id = a.customer_id
                LEFT JOIN nexora.app_user u ON u.id = a.created_by
                WHERE a.company_id = {Company} AND a.created_at::date BETWEEN {start} AND {end}
                UNION ALL
                SELECT p.created_at, c.customer_number, c.name, 'promesse (' || cur.code || ')', p.status, p.note, p.amount, u.full_name
                FROM nexora.customer_promise p JOIN nexora.customer c ON c.id = p.customer_id
                JOIN nexora.currency cur ON cur.id = c.default_currency_id
                LEFT JOIN nexora.app_user u ON u.id = p.created_by
                WHERE p.company_id = {Company} AND p.created_at::date BETWEEN {start} AND {end}) t
            ORDER BY "At"
            """).ToListAsync(ct);
        return (["Date", "Code client", "Client", "Action", "Résultat", "Note", "Montant promis", "Par"],
            rows.Select(r => new string?[] { T(r.At), r.CustomerNumber, r.Name, r.Kind, r.Outcome, r.Detail,
                r.Amount is { } a ? M(a) : null, r.Actor }).ToList(),
            new());
    }

    private sealed record LedgerRow(DateTimeOffset At, string CustomerNumber, string Name, string EntryType, string? Reference,
        string? Description, string Currency, decimal Debit, decimal Credit, string? Actor);

    private async Task<(string[], List<string?[]>, Dictionary<string, decimal>)> LedgerAsync(DateOnly start, DateOnly end, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<LedgerRow>($"""
            SELECT l.occurred_at AS "At", c.customer_number AS "CustomerNumber", c.name AS "Name", l.entry_type AS "EntryType",
                   l.reference AS "Reference", l.description AS "Description", cur.code AS "Currency", l.debit AS "Debit",
                   l.credit AS "Credit", u.full_name AS "Actor"
            FROM nexora.customer_ledger_entry l
            JOIN nexora.customer c ON c.id = l.customer_id
            JOIN nexora.currency cur ON cur.id = l.currency_id
            LEFT JOIN nexora.app_user u ON u.id = l.created_by
            WHERE l.company_id = {Company} AND l.occurred_at::date BETWEEN {start} AND {end}
            ORDER BY l.occurred_at, l.created_at
            """).ToListAsync(ct);
        return (["Date", "Code client", "Client", "Mouvement", "Référence", "Libellé", "Devise", "Débit (dette +)", "Crédit (dette −)", "Par"],
            rows.Select(r => new string?[] { T(r.At), r.CustomerNumber, r.Name,
                SqlCustomerAccountQueries.EntryLabels.GetValueOrDefault(r.EntryType, r.EntryType), r.Reference, r.Description,
                r.Currency, M(r.Debit), M(r.Credit), r.Actor }).ToList(),
            ByCurrency(rows, r => r.Currency, ("Débit", r => r.Debit), ("Crédit", r => r.Credit)));
    }
}

/// <summary>
/// Alertes crédit (§25), une fois par objet : crédit échu, crédit proche ou au-delà de la limite (une
/// fois par mois), promesse de paiement échue non tenue. Destinataires : détenteurs de <c>credit.collect</c>
/// + responsable du client.
/// </summary>
public sealed class SqlCreditAlertScanner(
    NexoraDbContext db, ITenantContext tenant, INotificationService notifications,
    Application.Administration.ISettingsService settings, ILogger<SqlCreditAlertScanner> logger, IBusinessCalendar calendar) : ICreditAlertScanner
{
    private sealed record OverdueRow(Guid Id, Guid CustomerId, string Name, Guid? ManagerId, string CreditNumber, decimal Remaining,
        string Currency, DateOnly DueDate);
    private sealed record LimitRow(Guid CustomerId, string Name, Guid? ManagerId, decimal CreditLimit, decimal Due, string Currency);
    private sealed record PromiseRow(Guid Id, Guid CustomerId, string Name, Guid? ManagerId, decimal Amount, DateOnly PromisedDate);

    public async Task<int> ScanAsync(CancellationToken ct = default)
    {
        var company = CustomerAccountRules.Company(tenant);
        var today = calendar.Today;
        var collectors = await CustomerAccountRules.HoldersAsync(db, tenant, Permissions.Credit.Collect, null, ct);
        var sent = 0;

        async Task SendAsync(string title, string detail, Guid customerId, string name, Guid? manager, string dedup, int priority, string severity)
        {
            var recipients = new HashSet<Guid>(collectors);
            if (manager is { } m) recipients.Add(m);
            if (recipients.Count == 0) return;
            var r = await notifications.NotifyAsync(new NotifyRequest(
                Category: "credit.alert", Recipients: recipients.ToList(), TemplateKey: "credit.alert",
                Variables: new Dictionary<string, string?> { ["alert.title"] = title, ["customer.name"] = name, ["alert.detail"] = detail },
                Priority: priority, Severity: severity, Source: "automation", ActionUrl: $"/customers/{customerId}",
                ReferenceType: "customer", ReferenceId: customerId, DedupKey: dedup), ct);
            sent += r.NotificationsCreated;
        }

        var overdue = await db.Database.SqlQuery<OverdueRow>($"""
            SELECT r.id AS "Id", r.customer_id AS "CustomerId", c.name AS "Name", c.account_manager_id AS "ManagerId",
                   r.credit_number AS "CreditNumber", r.remaining_amount AS "Remaining", cur.code AS "Currency", r.due_date AS "DueDate"
            FROM nexora.customer_receivable r
            JOIN nexora.customer c ON c.id = r.customer_id
            JOIN nexora.currency cur ON cur.id = r.currency_id
            WHERE r.company_id = {company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
              AND r.remaining_amount > 0 AND r.due_date < {today} AND r.due_date >= {today.AddDays(-7)}
            LIMIT 200
            """).ToListAsync(ct);
        foreach (var o in overdue)
            await SendAsync("Créance échue", $"{o.CreditNumber} : {o.Remaining:N2} {o.Currency} restant dû, échéance du {o.DueDate:dd/MM/yyyy} dépassée.",
                o.CustomerId, o.Name, o.ManagerId, $"credit.overdue:{o.Id}:{o.DueDate:yyyy-MM-dd}", 2, "warning");

        var nearPercent = 90m;
        try
        {
            var s = await settings.ResolveAsync("credit.near_limit_percent", null, ct);
            if (s.Value.ValueKind == System.Text.Json.JsonValueKind.Number) nearPercent = s.Value.GetDecimal();
        }
        catch (InvalidOperationException) { /* réglage absent : 90 % */ }

        var limits = await db.Database.SqlQuery<LimitRow>($"""
            SELECT * FROM (
                SELECT c.id AS "CustomerId", c.name AS "Name", c.account_manager_id AS "ManagerId", c.credit_limit AS "CreditLimit",
                       COALESCE((SELECT SUM(nexora.convert_amount(r.remaining_amount, r.currency_id, c.default_currency_id))
                                 FROM nexora.customer_receivable r WHERE r.customer_id = c.id
                                 AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS "Due",
                       cur.code AS "Currency"
                FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id
                WHERE c.company_id = {company} AND c.credit_limit > 0 AND c.status <> 'archived') t
            WHERE "Due" * 100 >= "CreditLimit" * {nearPercent}
            LIMIT 200
            """).ToListAsync(ct);
        var month = today.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        foreach (var l in limits)
        {
            var over = l.Due > l.CreditLimit;
            await SendAsync(over ? "Limite de crédit dépassée" : "Crédit proche de la limite",
                $"Encours {l.Due:N2} {l.Currency} pour une limite de {l.CreditLimit:N2} {l.Currency} ({Math.Round(l.Due / l.CreditLimit * 100m, 0)} %).",
                l.CustomerId, l.Name, l.ManagerId, $"credit.{(over ? "over" : "near")}_limit:{l.CustomerId}:{month}", over ? 2 : 3,
                over ? "error" : "warning");
        }

        var promises = await db.Database.SqlQuery<PromiseRow>($"""
            SELECT p.id AS "Id", p.customer_id AS "CustomerId", c.name AS "Name", c.account_manager_id AS "ManagerId",
                   p.amount AS "Amount", p.promised_date AS "PromisedDate"
            FROM nexora.customer_promise p JOIN nexora.customer c ON c.id = p.customer_id
            WHERE p.company_id = {company} AND p.status = 'open' AND p.promised_date < {today}
            LIMIT 200
            """).ToListAsync(ct);
        foreach (var p in promises)
            await SendAsync("Promesse de paiement échue", $"Promesse de {p.Amount:N2} au {p.PromisedDate:dd/MM/yyyy} non soldée : à vérifier et clôturer.",
                p.CustomerId, p.Name, p.ManagerId, $"credit.promise_due:{p.Id}", 2, "warning");

        if (sent > 0) logger.LogInformation("Alertes crédit : {Count} notification(s) pour l'entreprise {Company}", sent, company);
        return sent;
    }
}
