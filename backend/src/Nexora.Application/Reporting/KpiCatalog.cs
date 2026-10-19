namespace Nexora.Application.Reporting;

/// <summary>
/// Définition documentée de chaque KPI (Phase 7 §34). Clé, libellé, source, formule.
/// Toute valeur affichée doit renvoyer à l'une de ces définitions.
/// </summary>
public sealed record KpiDefinition(string Key, string Label, string Unit, string Source, string Formula, string Kind);

public static class KpiCatalog
{
    public const string Revenue = "revenue";
    public const string GrossMargin = "gross_margin";
    public const string MarginRate = "margin_rate";
    public const string Profit = "profit";
    public const string Expenses = "expenses";
    public const string Cash = "cash";
    public const string OpenReceivables = "open_receivables";
    public const string OpenPayables = "open_payables";
    public const string StockValue = "stock_value";
    public const string Transactions = "transactions";
    public const string AverageBasket = "average_basket";
    public const string TodayRevenue = "today_revenue";
    public const string CashInflows = "cash_inflows";
    public const string CashOutflows = "cash_outflows";

    /// <summary>
    /// KPI financiers sensibles (guide fonctionnel §6, §17) : servis seulement avec
    /// <c>reporting.view_financial</c>. Sans elle (responsable stock, responsable achats), le Command
    /// Center ne garde que l'activité commerciale : CA, nombre de ventes, panier moyen, ventes du jour.
    /// </summary>
    public static readonly IReadOnlySet<string> Financial = new HashSet<string>(StringComparer.Ordinal)
    {
        GrossMargin, MarginRate, Profit, Expenses, Cash, OpenReceivables, OpenPayables, StockValue,
        CashInflows, CashOutflows,
    };

    /// <summary>
    /// KPI d'activité commerciale de l'entreprise (guide §18) : servis seulement avec
    /// <c>reporting.view_sales</c> — Direction, Manager, Comptable ; ni caissier, ni stock, ni achats.
    /// </summary>
    public static readonly IReadOnlySet<string> Sales = new HashSet<string>(StringComparer.Ordinal)
    {
        Revenue, Transactions, AverageBasket, TodayRevenue,
    };

    /// <summary>Un KPI est-il visible avec ces permissions ?</summary>
    public static bool IsVisible(string key, IReadOnlyCollection<string> permissions)
        => (!Financial.Contains(key) || permissions.Contains("reporting.view_financial"))
           && (!Sales.Contains(key) || permissions.Contains("reporting.view_sales"));

    public static readonly IReadOnlyList<KpiDefinition> All =
    [
        new(Revenue, "Chiffre d'affaires", "HTG", "sale (completed)", "Σ (subtotal − discount_total) − retours nets, convertis en devise de l'entreprise au taux de la date de vente", "actual"),
        new(GrossMargin, "Marge brute", "HTG", "sale_line", "CA − Σ (unit_cost × quantité)", "actual"),
        new(MarginRate, "Taux de marge", "%", "dérivé", "marge brute / CA × 100", "actual"),
        new(Profit, "Bénéfice", "HTG", "dérivé", "marge brute − dépenses de la période", "actual"),
        new(Expenses, "Dépenses", "HTG", "expense", "Σ amount (période, statut approuvé/payé), converti en devise de l'entreprise au taux de la date de la dépense", "actual"),
        new(Cash, "Trésorerie", "HTG", "financial_account", "Σ current_balance (comptes actifs caisse/banque/mobile), convertis en devise de l'entreprise au taux du jour", "actual"),
        new(OpenReceivables, "Créances ouvertes", "HTG", "customer_receivable", "Σ remaining_amount (OPEN/PARTIALLY_PAID/OVERDUE), convertis en devise de l'entreprise au taux du jour", "actual"),
        new(OpenPayables, "Dettes ouvertes", "HTG", "supplier_payable", "Σ remaining_amount (OPEN/PARTIALLY_PAID/OVERDUE), convertis en devise de l'entreprise au taux du jour", "actual"),
        new(StockValue, "Stock valorisé", "HTG", "stock_balance", "Σ quantity_on_hand × average_cost", "actual"),
        new(Transactions, "Nombre de ventes", "n", "sale (completed)", "count(*)", "actual"),
        new(AverageBasket, "Panier moyen", "HTG", "dérivé", "CA / nombre de ventes", "actual"),
        new(TodayRevenue, "Ventes du jour", "HTG", "sale", "CA where sold_at::date = current_date", "actual"),
        new(CashInflows, "Encaissements", "HTG", "payment (inbound)", "Σ amount (période), converti en devise de l'entreprise au taux de la date du paiement", "actual"),
        new(CashOutflows, "Décaissements", "HTG", "payment (outbound)", "Σ amount (période), converti en devise de l'entreprise au taux de la date du paiement", "actual"),
    ];
}
