namespace Nexora.Application.Intelligence;

/// <summary>
/// Confidentialité du centre d'intelligence (audit 2026-10 éd. 2, A-048) : la seule permission
/// <c>intelligence.view</c>, accordée aux responsables stock et achats, donnait accès aux prévisions de trésorerie,
/// aux créances nominatives et aux baisses de chiffre d'affaires — données que les tableaux de bord réservent à
/// <c>reporting.view_financial</c> et <c>reporting.view_sales</c> (même règle que <c>KpiCatalog.IsVisible</c>).
/// </summary>
public static class IntelligenceVisibility
{
    public const string ViewFinancial = "reporting.view_financial";
    public const string ViewSales = "reporting.view_sales";

    /// <summary>Recommandations et anomalies financières : créances, trésorerie, budget, marge, dépenses.</summary>
    private static readonly HashSet<string> Financial =
        ["receivable_priority", "cashflow_risk", "budget_overrun", "margin_drop", "expense_spike", "cash_gap"];

    /// <summary>Recommandations et anomalies commerciales : chiffre d'affaires, clients.</summary>
    private static readonly HashSet<string> Sales = ["sales_spike", "sales_decline", "customer_inactive", "revenue_drop"];

    /// <summary>Une catégorie de recommandation ou d'anomalie est-elle visible avec ces permissions ?</summary>
    public static bool IsVisible(string category, IReadOnlyCollection<string> granted)
        => (!Financial.Contains(category) || granted.Contains(ViewFinancial))
           && (!Sales.Contains(category) || granted.Contains(ViewSales));

    /// <summary>Score visible ? Le risque client (encours) est une donnée financière.</summary>
    public static bool IsScoreVisible(string entityType, IReadOnlyCollection<string> granted)
        => entityType != "customer" || granted.Contains(ViewFinancial);
}
