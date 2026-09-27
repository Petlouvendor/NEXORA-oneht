using Nexora.Contracts.Finance;

namespace Nexora.Application.Finance.Reports;

/// <summary>
/// Rapports financiers (Phase 4 §34). Lectures dérivées des écritures validées et des sous-livres —
/// jamais de saisies parallèles (§1, §36).
/// </summary>
public interface IFinancialReports
{
    Task<TrialBalanceResponse> TrialBalanceAsync(DateOnly asOf, CancellationToken ct = default);
    Task<GeneralLedgerResponse> GeneralLedgerAsync(string accountCode, DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<IncomeStatementResponse> IncomeStatementAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
    Task<BalanceSheetResponse> BalanceSheetAsync(DateOnly asOf, CancellationToken ct = default);
    Task<AgedResponse> AgedReceivablesAsync(DateOnly asOf, CancellationToken ct = default);
    Task<AgedResponse> AgedPayablesAsync(DateOnly asOf, CancellationToken ct = default);
    Task<CashFlowForecastResponse> CashFlowForecastAsync(DateOnly from, DateOnly to, CancellationToken ct = default);
}
