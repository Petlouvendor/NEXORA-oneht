namespace Nexora.Contracts.Finance;

public sealed record TrialBalanceRow(string AccountCode, string AccountName, string AccountType, decimal Debit, decimal Credit);
public sealed record TrialBalanceResponse(DateOnly AsOf, string Currency, IReadOnlyList<TrialBalanceRow> Rows, decimal TotalDebit, decimal TotalCredit);

public sealed record LedgerLine(DateOnly Date, string EntryNumber, string Journal, string? Description, decimal Debit, decimal Credit, decimal RunningBalance);
public sealed record GeneralLedgerResponse(string AccountCode, string AccountName, DateOnly From, DateOnly To, decimal OpeningBalance, IReadOnlyList<LedgerLine> Lines, decimal ClosingBalance);

public sealed record AgedBucket(string Label, decimal Amount);
public sealed record AgedRow(string PartyName, decimal Total, IReadOnlyList<AgedBucket> Buckets);
public sealed record AgedResponse(DateOnly AsOf, string Currency, IReadOnlyList<AgedRow> Rows, decimal GrandTotal);

public sealed record StatementLine(string Label, decimal Amount);
public sealed record StatementSection(string Title, IReadOnlyList<StatementLine> Lines, decimal Subtotal);
public sealed record IncomeStatementResponse(DateOnly From, DateOnly To, string Currency, StatementSection Revenue, StatementSection Expenses, decimal NetResult);
public sealed record BalanceSheetResponse(DateOnly AsOf, string Currency, StatementSection Assets, StatementSection Liabilities, StatementSection Equity, decimal Result, bool IsBalanced);

public sealed record CashFlowForecastPoint(DateOnly Date, decimal ExpectedInflow, decimal ExpectedOutflow, decimal ProjectedBalance);
public sealed record CashFlowForecastResponse(DateOnly From, DateOnly To, string Currency, decimal OpeningBalance, IReadOnlyList<CashFlowForecastPoint> Points);
