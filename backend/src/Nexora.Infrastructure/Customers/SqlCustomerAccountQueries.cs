using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Customers;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>
/// Lectures du compte client (§7-§9, §14, §15, §17). Aucun solde n'est lu dans un champ figé : tout
/// est recalculé depuis les crédits, leurs règlements actifs et le journal financier client.
/// </summary>
public sealed class SqlCustomerAccountQueries(
    NexoraDbContext db, ITenantContext tenant, IPermissionService permissions, ISettingsService settings,
    Nexora.Application.Sales.ICreditControl creditControl, IBusinessCalendar calendar)
    : ICustomerAccountQueries
{
    private Guid Company => CustomerAccountRules.Company(tenant);
    private DateOnly Today => calendar.Today;

    internal static readonly IReadOnlyDictionary<string, string> EntryLabels = new Dictionary<string, string>
    {
        ["credit_sale"] = "Vente à crédit",
        ["customer_expense"] = "Dépense imputée",
        ["opening"] = "Reprise de solde",
        ["manual_debit"] = "Crédit",
        ["payment"] = "Versement",
        ["payment_void"] = "Annulation de versement",
        ["sale_return"] = "Retour / avoir",
        ["overpayment"] = "Excédent converti en avoir",
        ["write_off"] = "Radiation",
        ["refund"] = "Remboursement d'avoir",
        ["credit_cancelled"] = "Annulation de crédit",
        ["credit_memo_cancelled"] = "Annulation d'avoir",
    };

    // ------------------------------------------------------------------ Recherche POS (§17)

    private sealed record LookupRow(Guid Id, string CustomerNumber, string DisplayName, string? PhoneHint, string Status,
        decimal CreditLimit, decimal OpenReceivables, bool Blocked, string Currency, decimal StoreCredit);

    public async Task<IReadOnlyList<CustomerLookupDto>> LookupAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 2) return [];
        limit = Math.Clamp(limit, 1, 20);
        var digits = new string(q.Where(char.IsDigit).ToArray());
        var phone = digits.Length >= 3 ? digits : null;

        var rows = await db.Database.SqlQuery<LookupRow>($"""
            SELECT c.id AS "Id", c.customer_number AS "CustomerNumber",
                   btrim(coalesce(c.first_name || ' ', '') || c.name) AS "DisplayName",
                   CASE WHEN c.phone IS NULL THEN NULL ELSE right(regexp_replace(c.phone, '\D', '', 'g'), 4) END AS "PhoneHint",
                   c.status AS "Status", c.credit_limit AS "CreditLimit",
                   COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r
                             WHERE r.customer_id = c.id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS "OpenReceivables",
                   EXISTS(SELECT 1 FROM nexora.customer_block b WHERE b.customer_id = c.id AND b.released_at IS NULL
                          AND b.block_type IN ('credit','sales','account')) AS "Blocked",
                   cur.code AS "Currency",
                   COALESCE((SELECT SUM(m.remaining_amount) FROM nexora.customer_credit_memo m WHERE m.customer_id = c.id
                             AND m.currency_id = c.default_currency_id AND m.status IN ('available','partially_used')), 0) AS "StoreCredit"
            FROM nexora.customer c
            JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.company_id = {Company} AND c.status <> 'archived'
              AND (nexora.search_norm(coalesce(c.first_name, '') || ' ' || c.name) LIKE '%' || nexora.search_norm({q}) || '%'
                   OR c.customer_number ILIKE {q} || '%'
                   OR c.email::text ILIKE {q} || '%'
                   OR ({phone}::text IS NOT NULL AND regexp_replace(coalesce(c.phone, ''), '\D', '', 'g') LIKE '%' || {phone}::text || '%')
                   OR EXISTS(SELECT 1 FROM nexora.customer_receivable r WHERE r.customer_id = c.id AND r.credit_number = upper({q}))
                   OR EXISTS(SELECT 1 FROM nexora.sale s WHERE s.customer_id = c.id AND s.company_id = c.company_id AND s.document_number = {q}))
            ORDER BY (c.customer_number ILIKE {q}) DESC, c.name
            LIMIT {limit}
            """).ToListAsync(ct);

        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var showBalance = granted.Contains(Permissions.Pos.ViewCustomerBalance) || granted.Contains(Permissions.Credit.ViewPortfolio);
        var list = new List<CustomerLookupDto>(rows.Count);
        foreach (var r in rows)
        {
            var enabled = r.Status == "active" && !r.Blocked && r.CreditLimit > 0m;
            decimal? available = null;
            if (showBalance)
            {
                // Même calcul que la décision de vente : encours toutes devises converti dans celle du client.
                var decision = enabled ? await creditControl.EvaluateAsync(r.Id, r.Currency, 0m, ct) : null;
                available = decision is null ? 0m : Math.Max(0m, decision.AvailableCredit);
            }
            list.Add(new CustomerLookupDto(r.Id, r.CustomerNumber, r.DisplayName, r.PhoneHint, r.Blocked ? "blocked" : r.Status,
                enabled, available, r.Currency, showBalance ? r.StoreCredit : null));
        }
        return list;
    }

    // ------------------------------------------------------------------ Tableau de bord (§15)

    private sealed record SummaryRow(
        string CustomerNumber, string Name, string Status, string Currency, decimal CreditLimit,
        decimal TotalPurchases, int PurchaseCount, decimal CreditPurchases, decimal TotalPaid, decimal BalanceDue,
        decimal ExpensesBilled, decimal ExpensesNotBilled, decimal ReturnsAmount, decimal ReturnsQuantity,
        decimal StoreCredit, decimal Overdue, int OverdueCredits, int OldestOverdueDays,
        DateTimeOffset? LastPaymentAt, decimal? LastPaymentAmount, decimal LedgerBalance,
        int OpenPromises, int OpenDisputes, bool Blocked, int PendingExpenses, int PendingRefunds);

    public async Task<CustomerAccountSummaryDto?> SummaryAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var today = Today;
        var s = await db.Database.SqlQuery<SummaryRow>($"""
            SELECT c.customer_number AS "CustomerNumber", c.name AS "Name", c.status AS "Status", cur.code AS "Currency",
                   c.credit_limit AS "CreditLimit",
                   COALESCE((SELECT SUM(s.total) FROM nexora.sale s WHERE s.customer_id = c.id AND s.currency_id = c.default_currency_id
                             AND s.status IN ('completed','partially_refunded','refunded')
                             AND ({from}::date IS NULL OR s.sold_at::date >= {from}::date)
                             AND ({to}::date IS NULL OR s.sold_at::date <= {to}::date)), 0) AS "TotalPurchases",
                   COALESCE((SELECT COUNT(*) FROM nexora.sale s WHERE s.customer_id = c.id AND s.currency_id = c.default_currency_id
                             AND s.status IN ('completed','partially_refunded','refunded')
                             AND ({from}::date IS NULL OR s.sold_at::date >= {from}::date)
                             AND ({to}::date IS NULL OR s.sold_at::date <= {to}::date)), 0)::int AS "PurchaseCount",
                   COALESCE((SELECT SUM(r.original_amount) FROM nexora.customer_receivable r JOIN nexora.sale s ON s.id = r.sale_id
                             WHERE r.customer_id = c.id AND r.currency_id = c.default_currency_id
                             AND ({from}::date IS NULL OR s.sold_at::date >= {from}::date)
                             AND ({to}::date IS NULL OR s.sold_at::date <= {to}::date)), 0) AS "CreditPurchases",
                   COALESCE((SELECT SUM(p.amount) FROM nexora.payment p WHERE p.customer_id = c.id AND p.currency_id = c.default_currency_id AND p.direction = 'inbound'
                             AND p.status = 'completed'
                             AND ({from}::date IS NULL OR p.received_at::date >= {from}::date)
                             AND ({to}::date IS NULL OR p.received_at::date <= {to}::date)), 0) AS "TotalPaid",
                   COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r WHERE r.customer_id = c.id AND r.currency_id = c.default_currency_id
                             AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS "BalanceDue",
                   COALESCE((SELECT SUM(e.amount) FROM nexora.customer_expense e WHERE e.customer_id = c.id AND e.currency_id = c.default_currency_id
                             AND e.status = 'validated' AND e.billable
                             AND ({from}::date IS NULL OR e.expense_date >= {from}::date)
                             AND ({to}::date IS NULL OR e.expense_date <= {to}::date)), 0) AS "ExpensesBilled",
                   COALESCE((SELECT SUM(e.amount) FROM nexora.customer_expense e WHERE e.customer_id = c.id AND e.currency_id = c.default_currency_id
                             AND e.status = 'validated' AND NOT e.billable
                             AND ({from}::date IS NULL OR e.expense_date >= {from}::date)
                             AND ({to}::date IS NULL OR e.expense_date <= {to}::date)), 0) AS "ExpensesNotBilled",
                   COALESCE((SELECT SUM(sr.refund_total) FROM nexora.sale_return sr JOIN nexora.sale s ON s.id = sr.sale_id
                             WHERE s.customer_id = c.id AND sr.currency_id = c.default_currency_id
                             AND ({from}::date IS NULL OR sr.returned_at::date >= {from}::date)
                             AND ({to}::date IS NULL OR sr.returned_at::date <= {to}::date)), 0) AS "ReturnsAmount",
                   COALESCE((SELECT SUM(l.quantity) FROM nexora.sale_return_line l
                             JOIN nexora.sale_return sr ON sr.id = l.sale_return_id JOIN nexora.sale s ON s.id = sr.sale_id
                             WHERE s.customer_id = c.id AND sr.currency_id = c.default_currency_id
                             AND ({from}::date IS NULL OR sr.returned_at::date >= {from}::date)
                             AND ({to}::date IS NULL OR sr.returned_at::date <= {to}::date)), 0) AS "ReturnsQuantity",
                   COALESCE((SELECT SUM(m.remaining_amount) FROM nexora.customer_credit_memo m WHERE m.customer_id = c.id AND m.currency_id = c.default_currency_id
                             AND m.status IN ('available','partially_used')), 0) AS "StoreCredit",
                   COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r WHERE r.customer_id = c.id AND r.currency_id = c.default_currency_id
                             AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND r.due_date < {today}), 0) AS "Overdue",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_receivable r WHERE r.customer_id = c.id AND r.currency_id = c.default_currency_id
                             AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND r.due_date < {today}), 0)::int AS "OverdueCredits",
                   COALESCE((SELECT MAX({today} - r.due_date) FROM nexora.customer_receivable r WHERE r.customer_id = c.id AND r.currency_id = c.default_currency_id
                             AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND r.due_date < {today}), 0)::int AS "OldestOverdueDays",
                   (SELECT p.received_at FROM nexora.payment p WHERE p.customer_id = c.id AND p.currency_id = c.default_currency_id AND p.direction = 'inbound'
                     AND p.status = 'completed' ORDER BY p.received_at DESC LIMIT 1) AS "LastPaymentAt",
                   (SELECT p.amount FROM nexora.payment p WHERE p.customer_id = c.id AND p.currency_id = c.default_currency_id AND p.direction = 'inbound'
                     AND p.status = 'completed' ORDER BY p.received_at DESC LIMIT 1) AS "LastPaymentAmount",
                   COALESCE((SELECT SUM(l.debit - l.credit) FROM nexora.customer_ledger_entry l WHERE l.customer_id = c.id AND l.currency_id = c.default_currency_id), 0) AS "LedgerBalance",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_promise p WHERE p.customer_id = c.id AND p.status = 'open'), 0)::int AS "OpenPromises",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_dispute d WHERE d.customer_id = c.id
                             AND d.status IN ('open','under_review')), 0)::int AS "OpenDisputes",
                   (c.status = 'blocked' OR EXISTS(SELECT 1 FROM nexora.customer_block b WHERE b.customer_id = c.id
                     AND b.released_at IS NULL AND b.block_type IN ('credit','sales','account'))) AS "Blocked",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_expense e WHERE e.customer_id = c.id AND e.status = 'pending'), 0)::int AS "PendingExpenses",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_refund x WHERE x.customer_id = c.id
                             AND x.status IN ('requested','approved')), 0)::int AS "PendingRefunds"
            FROM nexora.customer c
            JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.id = {customerId} AND c.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (s is null) return null;

        var net = s.BalanceDue - s.StoreCredit;
        var balances = await BalancesAsync(customerId, today, ct);
        var exposure = balances.Sum(b => b.ExposureInCustomerCurrency ?? 0m);
        var rateMissing = balances.Any(b => b.ExposureInCustomerCurrency is null && b.BalanceDue > 0m);
        // Crédit disponible = limite − encours de TOUTES les devises converti (même règle que la vente).
        var available = s.Status == "active" && !s.Blocked && !rateMissing ? Math.Max(0m, s.CreditLimit - exposure) : 0m;
        var signals = await SignalsAsync(customerId, s with { BalanceDue = exposure }, ct);
        return new CustomerAccountSummaryDto(customerId, s.CustomerNumber, s.Name, s.Blocked && s.Status == "active" ? "blocked" : s.Status,
            s.Currency, from, to, s.TotalPurchases, s.PurchaseCount, s.CreditPurchases, s.TotalPaid, s.BalanceDue, s.CreditLimit,
            available, s.ExpensesBilled, s.ExpensesNotBilled, s.ReturnsAmount, s.ReturnsQuantity, s.StoreCredit, net,
            s.Overdue, s.OverdueCredits, s.OldestOverdueDays, s.LastPaymentAt, s.LastPaymentAmount,
            s.LedgerBalance, balances.All(b => b.LedgerConsistent) && Math.Round(s.LedgerBalance, 2) == Math.Round(net, 2),
            s.OpenPromises, s.OpenDisputes, s.Blocked, s.PendingExpenses, s.PendingRefunds, signals,
            balances, Math.Round(exposure, 2), rateMissing);
    }

    private sealed record BalanceRow(string Currency, decimal BalanceDue, decimal OverdueAmount, decimal StoreCredit,
        decimal LedgerBalance, decimal? Exposure);

    /// <summary>§21 : un solde par devise — jamais deux devises additionnées ; conversion affichée à part.</summary>
    private async Task<List<CurrencyBalanceDto>> BalancesAsync(Guid customerId, DateOnly today, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<BalanceRow>($"""
            WITH cur AS (
                SELECT currency_id FROM nexora.customer_receivable WHERE customer_id = {customerId} AND company_id = {Company}
                UNION SELECT currency_id FROM nexora.customer_credit_memo WHERE customer_id = {customerId} AND company_id = {Company}
                UNION SELECT currency_id FROM nexora.customer_ledger_entry WHERE customer_id = {customerId} AND company_id = {Company}),
            v AS (
                SELECT cur.currency_id,
                       COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r WHERE r.customer_id = {customerId}
                                 AND r.currency_id = cur.currency_id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0) AS due,
                       COALESCE((SELECT SUM(r.remaining_amount) FROM nexora.customer_receivable r WHERE r.customer_id = {customerId}
                                 AND r.currency_id = cur.currency_id AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
                                 AND r.due_date < {today}), 0) AS overdue,
                       COALESCE((SELECT SUM(m.remaining_amount) FROM nexora.customer_credit_memo m WHERE m.customer_id = {customerId}
                                 AND m.currency_id = cur.currency_id AND m.status IN ('available','partially_used')), 0) AS store,
                       COALESCE((SELECT SUM(l.debit - l.credit) FROM nexora.customer_ledger_entry l WHERE l.customer_id = {customerId}
                                 AND l.currency_id = cur.currency_id), 0) AS ledger
                FROM cur)
            SELECT c.code AS "Currency", v.due AS "BalanceDue", v.overdue AS "OverdueAmount", v.store AS "StoreCredit",
                   v.ledger AS "LedgerBalance",
                   nexora.convert_amount(v.due, v.currency_id, (SELECT default_currency_id FROM nexora.customer WHERE id = {customerId})) AS "Exposure"
            FROM v JOIN nexora.currency c ON c.id = v.currency_id
            ORDER BY c.code
            """).ToListAsync(ct);
        return rows.Select(b => new CurrencyBalanceDto(b.Currency, b.BalanceDue, b.OverdueAmount, b.StoreCredit, b.BalanceDue - b.StoreCredit,
            b.LedgerBalance, Math.Round(b.LedgerBalance, 2) == Math.Round(b.BalanceDue - b.StoreCredit, 2),
            b.Exposure is null ? null : Math.Round(b.Exposure.Value, 2))).ToList();
    }

    private sealed record LateRow(int Total, int Late, decimal AvgDaysLate);
    private sealed record TrendRow(decimal CreditLast30, decimal CreditPrev90, decimal DueSoonAmount, int DueSoonCount,
        int VoidedPayments90, decimal PurchasesLast90, decimal PurchasesPrev90);

    /// <summary>
    /// Signaux explicables (§27) : chaque signal expose la règle appliquée et les chiffres lus. Ils
    /// éclairent la décision — ils ne changent aucun solde, ne bloquent rien et n'exécutent rien.
    /// </summary>
    private async Task<IReadOnlyList<CustomerSignalDto>> SignalsAsync(Guid customerId, SummaryRow s, CancellationToken ct)
    {
        var today = Today;
        var inv = CultureInfo.InvariantCulture;
        var list = new List<CustomerSignalDto>();

        var late = await db.Database.SqlQuery<LateRow>($"""
            WITH r AS (
                SELECT r.due_date, r.status,
                       (SELECT MAX(a.allocated_at)::date FROM nexora.payment_allocation a
                        WHERE a.receivable_id = r.id AND a.voided_at IS NULL) AS last_paid
                FROM nexora.customer_receivable r
                WHERE r.customer_id = {customerId} AND r.company_id = {Company} AND r.due_date IS NOT NULL
                  AND r.due_date >= {today.AddDays(-365)} AND r.due_date < {today} AND r.status NOT IN ('CANCELLED'))
            SELECT COUNT(*)::int AS "Total",
                   COUNT(*) FILTER (WHERE (status = 'PAID' AND last_paid > due_date)
                                       OR status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE'))::int AS "Late",
                   COALESCE(AVG((COALESCE(CASE WHEN status = 'PAID' THEN last_paid END, {today}) - due_date))
                            FILTER (WHERE (status = 'PAID' AND last_paid > due_date)
                                       OR status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')), 0)::numeric AS "AvgDaysLate"
            FROM r
            """).FirstAsync(ct);
        if (late.Late >= 3)
            list.Add(new CustomerSignalDto("late_payer", "warning", "Retards de paiement récurrents",
                "Règle : au moins 3 crédits échus sur 12 mois réglés après l'échéance ou encore impayés.",
                new Dictionary<string, string>
                {
                    ["crédits échus (12 mois)"] = late.Total.ToString(inv), ["en retard"] = late.Late.ToString(inv),
                    ["retard moyen (jours)"] = Math.Round(late.AvgDaysLate, 1).ToString(inv),
                }));

        if (s.CreditLimit > 0m)
        {
            var nearPercent = 90m;
            try
            {
                var setting = await settings.ResolveAsync("credit.near_limit_percent", null, ct);
                if (setting.Value.ValueKind == JsonValueKind.Number) nearPercent = setting.Value.GetDecimal();
            }
            catch (InvalidOperationException) { /* réglage absent : 90 % */ }
            var usage = Math.Round(s.BalanceDue / s.CreditLimit * 100m, 1);
            var data = new Dictionary<string, string>
            {
                ["encours"] = s.BalanceDue.ToString("0.00", inv), ["limite"] = s.CreditLimit.ToString("0.00", inv),
                ["utilisation (%)"] = usage.ToString(inv),
            };
            if (s.BalanceDue > s.CreditLimit)
                list.Add(new CustomerSignalDto("over_limit", "danger", "Limite de crédit dépassée",
                    "Règle : encours dû supérieur à la limite accordée.", data));
            else if (usage >= nearPercent)
                list.Add(new CustomerSignalDto("near_limit", "warning", "Crédit proche de la limite",
                    $"Règle : encours ≥ {nearPercent.ToString(inv)} % de la limite (réglage credit.near_limit_percent).", data));
        }

        // Montants convertis dans la devise du client, comme la décision de crédit (audit 2026-10 éd. 2 : les signaux
        // additionnaient les montants de toutes les devises tels quels).
        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT default_currency_id AS "Value" FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstAsync(ct);
        var t = await db.Database.SqlQuery<TrendRow>($"""
            SELECT COALESCE((SELECT SUM(nexora.convert_amount(original_amount, currency_id, {currencyId}, issue_date::timestamptz)) FROM nexora.customer_receivable WHERE customer_id = {customerId}
                             AND company_id = {Company} AND status <> 'CANCELLED' AND issue_date > {today.AddDays(-30)}), 0) AS "CreditLast30",
                   COALESCE((SELECT SUM(nexora.convert_amount(original_amount, currency_id, {currencyId}, issue_date::timestamptz)) FROM nexora.customer_receivable WHERE customer_id = {customerId}
                             AND company_id = {Company} AND status <> 'CANCELLED'
                             AND issue_date <= {today.AddDays(-30)} AND issue_date > {today.AddDays(-120)}), 0) AS "CreditPrev90",
                   COALESCE((SELECT SUM(nexora.convert_amount(remaining_amount, currency_id, {currencyId})) FROM nexora.customer_receivable WHERE customer_id = {customerId}
                             AND company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
                             AND due_date BETWEEN {today} AND {today.AddDays(7)}), 0) AS "DueSoonAmount",
                   COALESCE((SELECT COUNT(*) FROM nexora.customer_receivable WHERE customer_id = {customerId}
                             AND company_id = {Company} AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')
                             AND due_date BETWEEN {today} AND {today.AddDays(7)}), 0)::int AS "DueSoonCount",
                   COALESCE((SELECT COUNT(*) FROM nexora.payment WHERE customer_id = {customerId} AND company_id = {Company}
                             AND status = 'void' AND voided_at >= now() - interval '90 days'), 0)::int AS "VoidedPayments90",
                   COALESCE((SELECT SUM(nexora.convert_amount(total, currency_id, {currencyId}, sold_at)) FROM nexora.sale WHERE customer_id = {customerId} AND company_id = {Company}
                             AND status IN ('completed','partially_refunded','refunded') AND sold_at >= now() - interval '90 days'), 0) AS "PurchasesLast90",
                   COALESCE((SELECT SUM(nexora.convert_amount(total, currency_id, {currencyId}, sold_at)) FROM nexora.sale WHERE customer_id = {customerId} AND company_id = {Company}
                             AND status IN ('completed','partially_refunded','refunded')
                             AND sold_at < now() - interval '90 days' AND sold_at >= now() - interval '180 days'), 0) AS "PurchasesPrev90"
            """).FirstAsync(ct);

        var monthlyBefore = t.CreditPrev90 / 3m;
        if (monthlyBefore > 0m && t.CreditLast30 >= 2m * monthlyBefore)
            list.Add(new CustomerSignalDto("credit_spike", "warning", "Hausse inhabituelle du crédit",
                "Règle : crédits des 30 derniers jours ≥ 2 × la moyenne mensuelle des 90 jours précédents.",
                new Dictionary<string, string>
                {
                    ["crédit 30 j"] = t.CreditLast30.ToString("0.00", inv),
                    ["moyenne mensuelle antérieure"] = Math.Round(monthlyBefore, 2).ToString("0.00", inv),
                }));
        if (t.DueSoonCount > 0)
            list.Add(new CustomerSignalDto("due_soon", "info", "Échéances proches",
                "Règle : crédits encore dus arrivant à échéance dans les 7 prochains jours.",
                new Dictionary<string, string>
                {
                    ["crédits"] = t.DueSoonCount.ToString(inv), ["montant"] = t.DueSoonAmount.ToString("0.00", inv),
                }));
        if (t.VoidedPayments90 >= 2)
            list.Add(new CustomerSignalDto("payment_voids", "warning", "Annulations de versements répétées",
                "Règle : au moins 2 versements annulés sur 90 jours — à vérifier (erreurs de saisie ou manipulation).",
                new Dictionary<string, string> { ["versements annulés (90 j)"] = t.VoidedPayments90.ToString(inv) }));
        if (t.PurchasesPrev90 > 0m && t.PurchasesLast90 < 0.5m * t.PurchasesPrev90)
            list.Add(new CustomerSignalDto("purchase_decline", "info", "Baisse d'activité",
                "Règle : achats des 90 derniers jours < 50 % des 90 jours précédents.",
                new Dictionary<string, string>
                {
                    ["achats 90 j"] = t.PurchasesLast90.ToString("0.00", inv), ["90 j précédents"] = t.PurchasesPrev90.ToString("0.00", inv),
                }));
        else if (t.PurchasesPrev90 > 0m && t.PurchasesLast90 > 2m * t.PurchasesPrev90)
            list.Add(new CustomerSignalDto("purchase_growth", "info", "Forte hausse des achats",
                "Règle : achats des 90 derniers jours > 2 × les 90 jours précédents.",
                new Dictionary<string, string>
                {
                    ["achats 90 j"] = t.PurchasesLast90.ToString("0.00", inv), ["90 j précédents"] = t.PurchasesPrev90.ToString("0.00", inv),
                }));
        return list;
    }

    // ------------------------------------------------------------------ Journal (§7)

    private sealed record LedgerRow(
        Guid Id, DateTimeOffset OccurredAt, string EntryType, decimal Debit, decimal Credit, decimal Balance, string Currency,
        string? Reference, string? Description, Guid? ReceivableId, Guid? PaymentId, Guid? CreditMemoId, string SourceType,
        Guid? SourceId, string? CreatedByName, string? EstablishmentName);

    public async Task<CustomerLedgerDto?> LedgerAsync(Guid customerId, DateOnly? from, DateOnly? to, string? currency = null, CancellationToken ct = default)
    {
        var customerCurrency = await CustomerCurrencyAsync(customerId, ct);
        if (customerCurrency is null) return null;
        currency = string.IsNullOrWhiteSpace(currency) ? customerCurrency : currency.Trim().ToUpperInvariant();
        var rows = await db.Database.SqlQuery<LedgerRow>($"""
            SELECT l.id AS "Id", l.occurred_at AS "OccurredAt", l.entry_type AS "EntryType", l.debit AS "Debit", l.credit AS "Credit",
                   SUM(l.debit - l.credit) OVER (ORDER BY l.occurred_at, l.created_at, l.id) AS "Balance",
                   cur.code AS "Currency", l.reference AS "Reference", l.description AS "Description",
                   l.receivable_id AS "ReceivableId", l.payment_id AS "PaymentId", l.credit_memo_id AS "CreditMemoId",
                   l.source_type AS "SourceType", l.source_id AS "SourceId", u.full_name AS "CreatedByName", e.name AS "EstablishmentName"
            FROM nexora.customer_ledger_entry l
            JOIN nexora.currency cur ON cur.id = l.currency_id
            LEFT JOIN nexora.app_user u ON u.id = l.created_by
            LEFT JOIN nexora.establishment e ON e.id = l.establishment_id
            WHERE l.company_id = {Company} AND l.customer_id = {customerId} AND cur.code = {currency}
            ORDER BY l.occurred_at, l.created_at, l.id
            """).ToListAsync(ct);

        var opening = 0m;
        var shown = new List<CustomerLedgerEntryDto>();
        foreach (var r in rows)
        {
            var day = calendar.DateOf(r.OccurredAt);
            if (from is { } f && day < f) { opening = r.Balance; continue; }
            if (to is { } t && day > t) continue;
            shown.Add(new CustomerLedgerEntryDto(r.Id, r.OccurredAt, r.EntryType, EntryLabels.GetValueOrDefault(r.EntryType, r.EntryType),
                r.Debit, r.Credit, r.Balance, r.Currency, r.Reference, r.Description, r.ReceivableId, r.PaymentId, r.CreditMemoId,
                r.SourceType, r.SourceId, r.CreatedByName, r.EstablishmentName));
        }
        var closing = shown.Count > 0 ? shown[^1].Balance : opening;
        return new CustomerLedgerDto(customerId, currency, from, to, opening, shown.Sum(x => x.Debit), shown.Sum(x => x.Credit), closing, shown);
    }

    private Task<string?> CustomerCurrencyAsync(Guid customerId, CancellationToken ct)
        => db.Database.SqlQuery<string?>($"""
            SELECT cur.code AS "Value" FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.id = {customerId} AND c.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ Crédits (§8)

    private sealed record CreditRow(
        Guid Id, string CreditNumber, Guid CustomerId, string CustomerName, string SourceType, Guid? SourceId, string? SourceReference,
        string Currency, decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount, DateOnly IssueDate, DateOnly? DueDate,
        string Status, string? EstablishmentName, string? CreatedByName, DateTimeOffset CreatedAt, bool HasSchedule);

    private IQueryable<CreditRow> CreditQuery(Guid? customerId, Guid? receivableId) => db.Database.SqlQuery<CreditRow>($"""
        SELECT r.id AS "Id", r.credit_number AS "CreditNumber", r.customer_id AS "CustomerId", c.name AS "CustomerName",
               r.source_type AS "SourceType", r.source_id AS "SourceId",
               CASE r.source_type WHEN 'sale' THEN s.document_number WHEN 'customer_expense' THEN e.expense_number END AS "SourceReference",
               cur.code AS "Currency", r.original_amount AS "OriginalAmount", r.paid_amount AS "PaidAmount",
               r.remaining_amount AS "RemainingAmount", r.issue_date AS "IssueDate", r.due_date AS "DueDate", r.status AS "Status",
               est.name AS "EstablishmentName", u.full_name AS "CreatedByName", r.created_at AS "CreatedAt",
               EXISTS(SELECT 1 FROM nexora.credit_schedule cs WHERE cs.receivable_id = r.id AND cs.status = 'active') AS "HasSchedule"
        FROM nexora.customer_receivable r
        JOIN nexora.customer c ON c.id = r.customer_id
        JOIN nexora.currency cur ON cur.id = r.currency_id
        LEFT JOIN nexora.sale s ON s.id = r.sale_id
        LEFT JOIN nexora.customer_expense e ON e.id = r.source_id AND r.source_type = 'customer_expense'
        LEFT JOIN nexora.establishment est ON est.id = r.establishment_id
        LEFT JOIN nexora.app_user u ON u.id = r.created_by
        WHERE r.company_id = {Company}
          AND ({customerId}::uuid IS NULL OR r.customer_id = {customerId}::uuid)
          AND ({receivableId}::uuid IS NULL OR r.id = {receivableId}::uuid)
        """);

    internal static string DisplayStatus(string status, decimal remaining, DateOnly? due, DateOnly today) => status switch
    {
        "CANCELLED" => "cancelled",
        "WRITTEN_OFF" => "written_off",
        "PAID" => "paid",
        _ when remaining <= 0m => "paid",
        "IN_DISPUTE" => "blocked",
        _ when due is { } d && d < today => "overdue",
        "PARTIALLY_PAID" => "partial",
        _ => "open",
    };

    private CustomerCreditDto ToCredit(CreditRow r)
    {
        var display = DisplayStatus(r.Status, r.RemainingAmount, r.DueDate, Today);
        var late = display is "overdue" or "blocked" && r.DueDate is { } d && d < Today ? Today.DayNumber - d.DayNumber : 0;
        return new CustomerCreditDto(r.Id, r.CreditNumber, r.CustomerId, r.CustomerName, r.SourceType, r.SourceId, r.SourceReference,
            r.Currency, r.OriginalAmount, r.PaidAmount, r.RemainingAmount, r.IssueDate, r.DueDate, r.Status, display, late,
            r.EstablishmentName, r.CreatedByName, r.CreatedAt, r.HasSchedule);
    }

    public async Task<IReadOnlyList<CustomerCreditDto>> CreditsAsync(Guid? customerId, string? status, CancellationToken ct = default)
    {
        var rows = await CreditQuery(customerId, null)
            .OrderByDescending(r => r.IssueDate).ThenByDescending(r => r.CreatedAt).Take(2000).ToListAsync(ct);
        var list = rows.Select(ToCredit);
        if (!string.IsNullOrWhiteSpace(status))
            list = status == "active"
                ? list.Where(c => c.DisplayStatus is "open" or "partial" or "overdue" or "blocked")
                : list.Where(c => c.DisplayStatus == status);
        return list.ToList();
    }

    private sealed record SettlementRow(
        string Kind, Guid? PaymentId, Guid? CreditMemoId, string Reference, DateTimeOffset At, decimal Amount, bool Voided,
        DateTimeOffset? VoidedAt, string? Method, string? ReceivedByName, string? Note);

    public async Task<CustomerCreditDetailDto?> CreditAsync(Guid receivableId, CancellationToken ct = default)
    {
        var row = await CreditQuery(null, receivableId).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var settlements = await db.Database.SqlQuery<SettlementRow>($"""
            SELECT 'payment' AS "Kind", p.id AS "PaymentId", NULL::uuid AS "CreditMemoId", p.payment_number AS "Reference",
                   a.allocated_at AS "At", a.allocated_amount AS "Amount", (a.voided_at IS NOT NULL) AS "Voided",
                   a.voided_at AS "VoidedAt", coalesce(pm.name, fa.name) AS "Method", u.full_name AS "ReceivedByName",
                   coalesce(p.note, p.void_reason) AS "Note"
            FROM nexora.payment_allocation a
            JOIN nexora.payment p ON p.id = a.payment_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            LEFT JOIN nexora.financial_account fa ON fa.id = p.financial_account_id
            LEFT JOIN nexora.app_user u ON u.id = p.created_by
            WHERE a.receivable_id = {receivableId} AND a.company_id = {Company}
            UNION ALL
            SELECT 'credit_memo', NULL::uuid, m.id, m.memo_number, x.applied_at, x.amount, (x.voided_at IS NOT NULL), x.voided_at,
                   'Avoir', u.full_name, m.reason
            FROM nexora.customer_credit_application x
            JOIN nexora.customer_credit_memo m ON m.id = x.credit_memo_id
            LEFT JOIN nexora.app_user u ON u.id = x.created_by
            WHERE x.receivable_id = {receivableId} AND x.company_id = {Company}
            UNION ALL
            SELECT 'write_off', NULL::uuid, NULL::uuid, 'Radiation', w.written_off_at, w.amount, false, NULL::timestamptz,
                   NULL, u.full_name, w.reason
            FROM nexora.receivable_write_off w
            LEFT JOIN nexora.app_user u ON u.id = w.created_by
            WHERE w.receivable_id = {receivableId} AND w.company_id = {Company}
            ORDER BY 5
            """).ToListAsync(ct);
        return new CustomerCreditDetailDto(ToCredit(row), settlements.Select(x => new CreditSettlementDto(x.Kind, x.PaymentId,
            x.CreditMemoId, x.Reference, x.At, x.Amount, x.Voided, x.VoidedAt, x.Method, x.ReceivedByName, x.Note)).ToList(),
            await ScheduleAsync(receivableId, row.PaidAmount, ct));
    }

    private sealed record ScheduleRow(Guid Id, int Version, decimal PaidBefore, decimal TotalAmount, string Reason,
        string? CreatedByName, DateTimeOffset CreatedAt);
    private sealed record InstallmentRow(int Number, DateOnly DueDate, decimal Amount);

    /// <summary>Échéancier actif ; la couverture de chaque échéance est dérivée du réglé actuel du crédit.</summary>
    private async Task<CreditScheduleDto?> ScheduleAsync(Guid receivableId, decimal paidNow, CancellationToken ct)
    {
        var schedule = await db.Database.SqlQuery<ScheduleRow>($"""
            SELECT s.id AS "Id", s.version AS "Version", s.paid_before AS "PaidBefore", s.total_amount AS "TotalAmount",
                   s.reason AS "Reason", u.full_name AS "CreatedByName", s.created_at AS "CreatedAt"
            FROM nexora.credit_schedule s LEFT JOIN nexora.app_user u ON u.id = s.created_by
            WHERE s.receivable_id = {receivableId} AND s.company_id = {Company} AND s.status = 'active'
            """).FirstOrDefaultAsync(ct);
        if (schedule is null) return null;
        var rows = await db.Database.SqlQuery<InstallmentRow>($"""
            SELECT installment_number AS "Number", due_date AS "DueDate", amount AS "Amount"
            FROM nexora.credit_installment WHERE schedule_id = {schedule.Id} ORDER BY installment_number
            """).ToListAsync(ct);
        var paidSince = paidNow - schedule.PaidBefore;
        var before = 0m;
        var today = Today;
        var items = new List<CreditInstallmentDto>();
        foreach (var i in rows)
        {
            var covered = Math.Clamp(paidSince - before, 0m, i.Amount);
            before += i.Amount;
            var status = covered >= i.Amount ? "paid" : i.DueDate < today ? "overdue" : covered > 0m ? "partial" : "upcoming";
            items.Add(new CreditInstallmentDto(i.Number, i.DueDate, i.Amount, covered, i.Amount - covered, status));
        }
        return new CreditScheduleDto(schedule.Id, schedule.Version, schedule.PaidBefore, schedule.TotalAmount, schedule.Reason,
            schedule.CreatedByName, schedule.CreatedAt, items);
    }

    // ------------------------------------------------------------------ Versements (§9)

    private sealed record PaymentRow(
        Guid Id, string PaymentNumber, Guid CustomerId, string CustomerName, decimal Amount, decimal AllocatedAmount,
        decimal UnallocatedAmount, string Currency, string Status, DateTimeOffset ReceivedAt, string? PaymentMethod,
        string? FinancialAccountName, Guid? CashSessionId, string? Reference, string? Note, string? ReceivedByName,
        DateTimeOffset? VoidedAt, string? VoidedByName, string? VoidReason, Guid? CreditMemoId, string? CreditMemoNumber);
    private sealed record AllocationRow(Guid PaymentId, Guid ReceivableId, string CreditNumber, decimal Amount, bool Voided);

    private async Task<List<CustomerPaymentDto>> PaymentsCoreAsync(Guid? customerId, Guid? receivableId, Guid? paymentId, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<PaymentRow>($"""
            SELECT p.id AS "Id", p.payment_number AS "PaymentNumber", p.customer_id AS "CustomerId", c.name AS "CustomerName",
                   p.amount AS "Amount", p.allocated_amount AS "AllocatedAmount", p.unallocated_amount AS "UnallocatedAmount",
                   cur.code AS "Currency", p.status AS "Status", p.received_at AS "ReceivedAt", pm.name AS "PaymentMethod",
                   fa.name AS "FinancialAccountName", p.cash_session_id AS "CashSessionId", p.reference AS "Reference",
                   p.note AS "Note", u.full_name AS "ReceivedByName", p.voided_at AS "VoidedAt", vu.full_name AS "VoidedByName",
                   p.void_reason AS "VoidReason", m.id AS "CreditMemoId", m.memo_number AS "CreditMemoNumber"
            FROM nexora.payment p
            JOIN nexora.customer c ON c.id = p.customer_id
            JOIN nexora.currency cur ON cur.id = p.currency_id
            LEFT JOIN nexora.payment_method pm ON pm.id = p.payment_method_id
            LEFT JOIN nexora.financial_account fa ON fa.id = p.financial_account_id
            LEFT JOIN nexora.app_user u ON u.id = p.created_by
            LEFT JOIN nexora.app_user vu ON vu.id = p.voided_by
            LEFT JOIN nexora.customer_credit_memo m ON m.payment_id = p.id
            WHERE p.company_id = {Company} AND p.direction = 'inbound'
              AND NOT EXISTS(SELECT 1 FROM nexora.sale_payment sp WHERE sp.payment_id = p.id)
              AND ({customerId}::uuid IS NULL OR p.customer_id = {customerId}::uuid)
              AND ({paymentId}::uuid IS NULL OR p.id = {paymentId}::uuid)
              AND ({receivableId}::uuid IS NULL OR EXISTS(SELECT 1 FROM nexora.payment_allocation a
                                                        WHERE a.payment_id = p.id AND a.receivable_id = {receivableId}::uuid))
            ORDER BY p.received_at DESC
            LIMIT 1000
            """).ToListAsync(ct);
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToArray();
        var allocations = await db.Database.SqlQuery<AllocationRow>($"""
            SELECT a.payment_id AS "PaymentId", a.receivable_id AS "ReceivableId", r.credit_number AS "CreditNumber",
                   a.allocated_amount AS "Amount", (a.voided_at IS NOT NULL) AS "Voided"
            FROM nexora.payment_allocation a JOIN nexora.customer_receivable r ON r.id = a.receivable_id
            WHERE a.payment_id = ANY({ids})
            ORDER BY r.credit_number
            """).ToListAsync(ct);
        var byPayment = allocations.ToLookup(a => a.PaymentId);
        return rows.Select(r => new CustomerPaymentDto(r.Id, r.PaymentNumber, r.CustomerId, r.CustomerName, r.Amount,
            r.AllocatedAmount, r.UnallocatedAmount, r.Currency, r.Status, r.ReceivedAt, r.PaymentMethod, r.FinancialAccountName,
            r.CashSessionId, r.Reference, r.Note, r.ReceivedByName, r.VoidedAt, r.VoidedByName, r.VoidReason, r.CreditMemoId,
            r.CreditMemoNumber,
            byPayment[r.Id].Select(a => new PaymentAllocationLineDto(a.ReceivableId, a.CreditNumber, a.Amount, a.Voided)).ToList())).ToList();
    }

    public async Task<IReadOnlyList<CustomerPaymentDto>> PaymentsAsync(Guid? customerId, Guid? receivableId, CancellationToken ct = default)
        => await PaymentsCoreAsync(customerId, receivableId, null, ct);

    public async Task<CustomerPaymentDto?> PaymentAsync(Guid paymentId, CancellationToken ct = default)
        => (await PaymentsCoreAsync(null, null, paymentId, ct)).FirstOrDefault();

    // ------------------------------------------------------------------ Achats

    public async Task<IReadOnlyList<CustomerPurchaseDto>> PurchasesAsync(Guid customerId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
        => await db.Database.SqlQuery<CustomerPurchaseDto>($"""
            SELECT s.id AS "SaleId", s.document_number AS "DocumentNumber", s.sold_at AS "SoldAt", s.sale_type AS "SaleType",
                   s.status AS "Status", s.total AS "Total",
                   COALESCE((SELECT SUM(sp.amount) FROM nexora.sale_payment sp WHERE sp.sale_id = s.id), 0) AS "PaidAtSale",
                   COALESCE(r.original_amount, 0) AS "CreditAmount",
                   COALESCE((SELECT SUM(sr.refund_total) FROM nexora.sale_return sr WHERE sr.sale_id = s.id), 0) AS "ReturnedAmount",
                   cur.code AS "Currency", est.name AS "EstablishmentName", u.full_name AS "CashierName",
                   r.id AS "ReceivableId", r.credit_number AS "CreditNumber"
            FROM nexora.sale s
            JOIN nexora.currency cur ON cur.id = s.currency_id
            LEFT JOIN nexora.customer_receivable r ON r.sale_id = s.id
            LEFT JOIN nexora.establishment est ON est.id = s.establishment_id
            LEFT JOIN nexora.app_user u ON u.id = s.created_by
            WHERE s.company_id = {Company} AND s.customer_id = {customerId}
              AND s.status IN ('completed','partially_refunded','refunded','void')
              AND ({from}::date IS NULL OR s.sold_at::date >= {from}::date)
              AND ({to}::date IS NULL OR s.sold_at::date <= {to}::date)
            ORDER BY s.sold_at DESC
            LIMIT 1000
            """).ToListAsync(ct);

    // ------------------------------------------------------------------ Avoirs (§11)

    private sealed record MemoRow(
        Guid Id, string MemoNumber, Guid CustomerId, string CustomerName, string SourceType, string? SourceReference,
        string Currency, decimal Amount, decimal UsedAmount, decimal RemainingAmount, string Status, string? Reason,
        string? CreatedByName, DateTimeOffset CreatedAt);
    private sealed record MemoUseRow(Guid MemoId, string Kind, string Reference, decimal Amount, DateTimeOffset At, bool Voided);

    private async Task<List<CreditMemoDto>> MemosCoreAsync(Guid? customerId, Guid? memoId, bool onlyAvailable, CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<MemoRow>($"""
            SELECT m.id AS "Id", m.memo_number AS "MemoNumber", m.customer_id AS "CustomerId", c.name AS "CustomerName",
                   m.source_type AS "SourceType", coalesce(sr.document_number, p.payment_number) AS "SourceReference",
                   cur.code AS "Currency", m.amount AS "Amount", m.used_amount AS "UsedAmount", m.remaining_amount AS "RemainingAmount",
                   m.status AS "Status", m.reason AS "Reason", u.full_name AS "CreatedByName", m.created_at AS "CreatedAt"
            FROM nexora.customer_credit_memo m
            JOIN nexora.customer c ON c.id = m.customer_id
            JOIN nexora.currency cur ON cur.id = m.currency_id
            LEFT JOIN nexora.sale_return sr ON sr.id = m.sale_return_id
            LEFT JOIN nexora.payment p ON p.id = m.payment_id
            LEFT JOIN nexora.app_user u ON u.id = m.created_by
            WHERE m.company_id = {Company}
              AND ({customerId}::uuid IS NULL OR m.customer_id = {customerId}::uuid)
              AND ({memoId}::uuid IS NULL OR m.id = {memoId}::uuid)
              AND (NOT {onlyAvailable} OR m.status IN ('available','partially_used'))
            ORDER BY m.created_at DESC
            LIMIT 1000
            """).ToListAsync(ct);
        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToArray();
        var uses = await db.Database.SqlQuery<MemoUseRow>($"""
            SELECT x.credit_memo_id AS "MemoId",
                   CASE WHEN x.receivable_id IS NOT NULL THEN 'credit' ELSE 'refund' END AS "Kind",
                   coalesce(r.credit_number, f.refund_number) AS "Reference", x.amount AS "Amount", x.applied_at AS "At",
                   (x.voided_at IS NOT NULL) AS "Voided"
            FROM nexora.customer_credit_application x
            LEFT JOIN nexora.customer_receivable r ON r.id = x.receivable_id
            LEFT JOIN nexora.customer_refund f ON f.id = x.refund_id
            WHERE x.credit_memo_id = ANY({ids})
            ORDER BY x.applied_at
            """).ToListAsync(ct);
        var byMemo = uses.ToLookup(u => u.MemoId);
        return rows.Select(r => new CreditMemoDto(r.Id, r.MemoNumber, r.CustomerId, r.CustomerName, r.SourceType, r.SourceReference,
            r.Currency, r.Amount, r.UsedAmount, r.RemainingAmount, r.Status, r.Reason, r.CreatedByName, r.CreatedAt,
            byMemo[r.Id].Select(u => new CreditMemoUseDto(u.Kind, u.Reference, u.Amount, u.At, u.Voided)).ToList())).ToList();
    }

    public async Task<IReadOnlyList<CreditMemoDto>> CreditMemosAsync(Guid? customerId, bool onlyAvailable, CancellationToken ct = default)
        => await MemosCoreAsync(customerId, null, onlyAvailable, ct);

    public async Task<CreditMemoDto?> CreditMemoAsync(Guid creditMemoId, CancellationToken ct = default)
        => (await MemosCoreAsync(null, creditMemoId, false, ct)).FirstOrDefault();

    // ------------------------------------------------------------------ Options d'encaissement

    public async Task<CustomerPaymentOptionsDto> PaymentOptionsAsync(CancellationToken ct = default)
    {
        var accounts = await db.Database.SqlQuery<PaymentAccountOptionDto>($"""
            SELECT fa.id AS "Id", fa.name AS "Name", fa.account_type AS "AccountType", c.code AS "Currency"
            FROM nexora.financial_account fa JOIN nexora.currency c ON c.id = fa.currency_id
            WHERE fa.company_id = {Company} AND fa.status = 'active'
            ORDER BY fa.account_type, fa.name
            """).ToListAsync(ct);
        var methods = await db.Database.SqlQuery<PaymentMethodOptionDto>($"""
            SELECT id AS "Id", name AS "Name", type AS "Type" FROM nexora.payment_method
            WHERE company_id = {Company} AND status = 'active' AND type NOT IN ('credit', 'store_credit')
            ORDER BY name
            """).ToListAsync(ct);
        return new CustomerPaymentOptionsDto(accounts, methods);
    }

    // ------------------------------------------------------------------ Historique (§14)

    private sealed record AuditRow(DateTimeOffset At, string Action, string? OldValues, string? NewValues, string? ActorName);

    private static readonly (string Key, string Label)[] TrackedFields =
    [
        ("name", "nom"), ("first_name", "prénom"), ("phone", "téléphone"), ("email", "email"), ("address", "adresse"),
        ("city", "ville"), ("country", "pays"), ("segment", "segment"), ("credit_days", "durée de crédit"),
        ("credit_terms", "conditions de crédit"), ("account_manager_id", "responsable"), ("customer_group_id", "groupe"),
    ];

    public async Task<IReadOnlyList<CustomerTimelineEventDto>> TimelineAsync(Guid customerId, int limit = 200, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var events = await db.Database.SqlQuery<CustomerTimelineEventDto>($"""
            SELECT * FROM (
                SELECT l.occurred_at AS "At", 'finance' AS "Category", l.entry_type AS "Title", l.description AS "Detail",
                       (l.debit - l.credit) AS "Amount", cur.code AS "Currency", l.reference AS "Reference", u.full_name AS "ActorName",
                       CASE WHEN l.payment_id IS NOT NULL THEN 'payment' WHEN l.credit_memo_id IS NOT NULL THEN 'credit_memo'
                            WHEN l.receivable_id IS NOT NULL THEN 'credit' END AS "LinkType",
                       coalesce(l.payment_id, l.credit_memo_id, l.receivable_id) AS "LinkId"
                FROM nexora.customer_ledger_entry l JOIN nexora.currency cur ON cur.id = l.currency_id
                LEFT JOIN nexora.app_user u ON u.id = l.created_by
                WHERE l.company_id = {Company} AND l.customer_id = {customerId}
                UNION ALL
                SELECT s.sold_at, 'purchase',
                       CASE s.sale_type WHEN 'cash' THEN 'Achat comptant' WHEN 'credit' THEN 'Achat à crédit' ELSE 'Achat mixte' END
                         || CASE WHEN s.status = 'void' THEN ' (annulé)' ELSE '' END,
                       NULL, s.total, cur.code, s.document_number, u.full_name, 'sale', s.id
                FROM nexora.sale s JOIN nexora.currency cur ON cur.id = s.currency_id
                LEFT JOIN nexora.app_user u ON u.id = s.created_by
                WHERE s.company_id = {Company} AND s.customer_id = {customerId}
                  AND s.status IN ('completed','partially_refunded','refunded','void')
                UNION ALL
                SELECT sr.returned_at, 'return', 'Retour de marchandise (' || sr.refund_method || ')', sr.reason, sr.refund_total, cur.code,
                       sr.document_number, u.full_name, 'sale_return', sr.id
                FROM nexora.sale_return sr JOIN nexora.sale s ON s.id = sr.sale_id JOIN nexora.currency cur ON cur.id = sr.currency_id
                LEFT JOIN nexora.app_user u ON u.id = sr.created_by
                WHERE sr.company_id = {Company} AND s.customer_id = {customerId}
                UNION ALL
                SELECT e.created_at, 'expense', 'Dépense saisie', e.description, e.amount, cur.code, e.expense_number, u.full_name,
                       'customer_expense', e.id
                FROM nexora.customer_expense e JOIN nexora.currency cur ON cur.id = e.currency_id
                LEFT JOIN nexora.app_user u ON u.id = e.created_by
                WHERE e.company_id = {Company} AND e.customer_id = {customerId}
                UNION ALL
                SELECT e.decided_at, 'expense', CASE e.status WHEN 'rejected' THEN 'Dépense rejetée' ELSE 'Dépense validée' END,
                       e.decision_note, e.amount, cur.code, e.expense_number, u.full_name, 'customer_expense', e.id
                FROM nexora.customer_expense e JOIN nexora.currency cur ON cur.id = e.currency_id
                LEFT JOIN nexora.app_user u ON u.id = e.decided_by
                WHERE e.company_id = {Company} AND e.customer_id = {customerId} AND e.decided_at IS NOT NULL
                UNION ALL
                SELECT e.cancelled_at, 'expense', 'Dépense annulée', e.cancel_reason, e.amount, cur.code, e.expense_number, u.full_name,
                       'customer_expense', e.id
                FROM nexora.customer_expense e JOIN nexora.currency cur ON cur.id = e.currency_id
                LEFT JOIN nexora.app_user u ON u.id = e.cancelled_by
                WHERE e.company_id = {Company} AND e.customer_id = {customerId} AND e.cancelled_at IS NOT NULL
                UNION ALL
                SELECT x.requested_at, 'refund', 'Remboursement demandé', x.reason, x.amount, cur.code, x.refund_number, u.full_name,
                       'customer_refund', x.id
                FROM nexora.customer_refund x JOIN nexora.currency cur ON cur.id = x.currency_id
                LEFT JOIN nexora.app_user u ON u.id = x.requested_by
                WHERE x.company_id = {Company} AND x.customer_id = {customerId}
                UNION ALL
                SELECT x.decided_at, 'refund', CASE x.status WHEN 'rejected' THEN 'Remboursement refusé' ELSE 'Remboursement approuvé' END,
                       x.decision_note, x.amount, cur.code, x.refund_number, u.full_name, 'customer_refund', x.id
                FROM nexora.customer_refund x JOIN nexora.currency cur ON cur.id = x.currency_id
                LEFT JOIN nexora.app_user u ON u.id = x.decided_by
                WHERE x.company_id = {Company} AND x.customer_id = {customerId} AND x.decided_at IS NOT NULL
                UNION ALL
                SELECT b.blocked_at, 'block', 'Blocage ' || b.block_type, b.reason, NULL::numeric, NULL, NULL, u.full_name, 'customer_block', b.id
                FROM nexora.customer_block b LEFT JOIN nexora.app_user u ON u.id = b.blocked_by
                WHERE b.company_id = {Company} AND b.customer_id = {customerId}
                UNION ALL
                SELECT b.released_at, 'block', 'Déblocage ' || b.block_type, b.release_reason, NULL::numeric, NULL, NULL, u.full_name,
                       'customer_block', b.id
                FROM nexora.customer_block b LEFT JOIN nexora.app_user u ON u.id = b.released_by
                WHERE b.company_id = {Company} AND b.customer_id = {customerId} AND b.released_at IS NOT NULL
                UNION ALL
                SELECT p.created_at, 'collection', 'Promesse de paiement au ' || to_char(p.promised_date, 'DD/MM/YYYY') || ' (' || p.status || ')',
                       p.note, p.amount, NULL, NULL, u.full_name, 'customer_promise', p.id
                FROM nexora.customer_promise p LEFT JOIN nexora.app_user u ON u.id = p.created_by
                WHERE p.company_id = {Company} AND p.customer_id = {customerId}
                UNION ALL
                SELECT d.created_at, 'collection', 'Litige (' || d.status || ')', d.reason, d.disputed_amount, NULL, NULL, u.full_name,
                       'customer_dispute', d.id
                FROM nexora.customer_dispute d LEFT JOIN nexora.app_user u ON u.id = d.created_by
                WHERE d.company_id = {Company} AND d.customer_id = {customerId}
                UNION ALL
                SELECT a.created_at, 'collection', 'Recouvrement : ' || a.action_type || coalesce(' → ' || a.outcome, ''), a.note,
                       NULL::numeric, NULL, NULL, u.full_name, 'collection_action', a.id
                FROM nexora.collection_action a LEFT JOIN nexora.app_user u ON u.id = a.created_by
                WHERE a.company_id = {Company} AND a.customer_id = {customerId}
                UNION ALL
                SELECT coalesce(doc.issued_at, doc.created_at), 'document', 'Document émis : ' || doc.document_type,
                       CASE WHEN doc.cancelled_at IS NOT NULL THEN 'Annulé — ' || coalesce(doc.cancel_reason, '') END,
                       doc.total, cur.code, doc.document_number, u.full_name, 'issued_document', doc.id
                FROM nexora.issued_document doc LEFT JOIN nexora.currency cur ON cur.id = doc.currency_id
                LEFT JOIN nexora.app_user u ON u.id = coalesce(doc.issued_by, doc.created_by)
                WHERE doc.company_id = {Company} AND doc.party_type = 'customer' AND doc.party_id = {customerId}
            ) t
            ORDER BY "At" DESC
            LIMIT {limit}
            """).ToListAsync(ct);

        var mapped = events.Select(e => e.Category == "finance"
            ? e with { Title = EntryLabels.GetValueOrDefault(e.Title, e.Title) }
            : e).ToList();

        // Création / modifications de la fiche : lues dans l'audit immuable (qui, quand, avant → après).
        var audit = await db.Database.SqlQuery<AuditRow>($"""
            SELECT a.occurred_at AS "At", a.action AS "Action", a.old_values::text AS "OldValues", a.new_values::text AS "NewValues",
                   u.full_name AS "ActorName"
            FROM nexora.audit_log a LEFT JOIN nexora.app_user u ON u.id = a.user_id
            WHERE a.company_id = {Company} AND a.entity_type = 'customer' AND a.entity_id = {customerId}
            ORDER BY a.occurred_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);
        foreach (var a in audit)
        {
            if (a.Action == "INSERT")
            {
                mapped.Add(new CustomerTimelineEventDto(a.At, "profile", "Fiche client créée", null, null, null, null, a.ActorName, "customer", customerId));
                continue;
            }
            if (a.Action != "UPDATE" || a.OldValues is null || a.NewValues is null) continue;
            using var oldDoc = JsonDocument.Parse(a.OldValues);
            using var newDoc = JsonDocument.Parse(a.NewValues);
            string? Val(JsonDocument d, string k) => d.RootElement.TryGetProperty(k, out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null;

            var limitBefore = Val(oldDoc, "credit_limit");
            var limitAfter = Val(newDoc, "credit_limit");
            if (limitBefore != limitAfter)
                mapped.Add(new CustomerTimelineEventDto(a.At, "profile", "Limite de crédit modifiée",
                    $"{limitBefore ?? "—"} → {limitAfter ?? "—"}", null, null, null, a.ActorName, "customer", customerId));
            var statusBefore = Val(oldDoc, "status");
            var statusAfter = Val(newDoc, "status");
            if (statusBefore != statusAfter)
                mapped.Add(new CustomerTimelineEventDto(a.At, "profile", "Statut de la fiche modifié",
                    $"{statusBefore} → {statusAfter}", null, null, null, a.ActorName, "customer", customerId));
            var changed = TrackedFields.Where(f => Val(oldDoc, f.Key) != Val(newDoc, f.Key)).Select(f => f.Label).ToList();
            if (changed.Count > 0)
                mapped.Add(new CustomerTimelineEventDto(a.At, "profile", "Fiche modifiée", string.Join(", ", changed),
                    null, null, null, a.ActorName, "customer", customerId));
        }
        return mapped.OrderByDescending(e => e.At).Take(limit).ToList();
    }
}
