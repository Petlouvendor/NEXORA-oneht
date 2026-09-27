using Nexora.Domain.Common;

namespace Nexora.Domain.Accounting;

/// <summary>Familles de comptes (Phase 4 §5).</summary>
public enum AccountType { Asset, Liability, Equity, Revenue, Expense }

public static class AccountTypeExtensions
{
    /// <summary>Sens normal du solde : true = débiteur (actif, charge), false = créditeur.</summary>
    public static bool IsDebitNormal(this AccountType type) =>
        type is AccountType.Asset or AccountType.Expense;
}

/// <summary>Compte du plan comptable — table nexora.account (Phase 4 §5).</summary>
public sealed class Account : AuditableEntity
{
    public Guid ChartOfAccountsId { get; set; }
    public Guid CompanyId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public AccountType AccountType { get; set; }
    public Guid? ParentAccountId { get; set; }
    public bool IsPostable { get; set; } = true;
    public Guid? CurrencyId { get; set; }
    public string Status { get; set; } = "active";
}

/// <summary>Mapping « rôle comptable → compte » par entreprise — table nexora.account_mapping.</summary>
public sealed class AccountMapping : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string MappingKey { get; set; } = string.Empty;
    public Guid AccountId { get; set; }
}

/// <summary>Clés de mapping normalisées (miroir du CHECK de la migration V022).</summary>
public static class AccountKeys
{
    public const string SalesRevenue = "sales_revenue";
    public const string SalesTaxOutput = "sales_tax_output";
    public const string AccountsReceivable = "accounts_receivable";
    public const string AccountsPayable = "accounts_payable";
    public const string Purchases = "purchases";
    public const string Inventory = "inventory";
    public const string Cogs = "cogs";
    public const string PurchaseTaxInput = "purchase_tax_input";
    public const string ExpenseDefault = "expense_default";
    public const string CashShortOver = "cash_short_over";
    public const string FxGain = "fx_gain";
    public const string FxLoss = "fx_loss";
    public const string Rounding = "rounding";
    public const string BankCharges = "bank_charges";
    public const string Suspense = "suspense";
    public const string Cash = "cash";
    public const string Bank = "bank";
    public const string RetainedEarnings = "retained_earnings";
    public const string CurrentYearEarnings = "current_year_earnings";
    public const string InventoryAdjustment = "inventory_adjustment";
    public const string InventoryLoss = "inventory_loss";
    public const string GoodsInTransit = "goods_in_transit";
    public const string PurchasePriceVariance = "purchase_price_variance";
    public const string SalesReturns = "sales_returns";
    public const string StoreCredit = "store_credit";
    public const string TipsPayable = "tips_payable";
    public const string BadDebt = "bad_debt";

    // Module paie, RH & avances (V076).
    public const string SalaryExpense = "salary_expense";
    public const string EmployerChargesExpense = "employer_charges_expense";
    public const string SalariesPayable = "salaries_payable";
    public const string EmployeeAdvances = "employee_advances";
    public const string EmployeeLoans = "employee_loans";
    public const string PayrollWithholdings = "payroll_withholdings";
    public const string PayrollOtherDeductions = "payroll_other_deductions";
    public const string EmployeeReimbursementsPayable = "employee_reimbursements_payable";
}
