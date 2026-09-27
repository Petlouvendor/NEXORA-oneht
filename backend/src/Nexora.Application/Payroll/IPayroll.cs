using Nexora.Application.Printing;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;

namespace Nexora.Application.Payroll;

// Module « Paie, RH & avances sur salaire » (docs/module-paie-rh-avances.md). Toute lecture est bornée à
// l'entreprise active et aux droits de l'appelant : un identifiant hors périmètre répond « introuvable »,
// jamais la donnée (§5 « protégés contre l'accès direct par identifiant »).

/// <summary>Dossier employé, départements, contrats, rémunération, éléments récurrents, documents (§7, §8, §20).</summary>
public interface IEmployeeService
{
    Task<IReadOnlyList<DepartmentDto>> DepartmentsAsync(CancellationToken ct = default);
    Task<Result<DepartmentDto>> SaveDepartmentAsync(Guid? id, SaveDepartmentRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<EmployeeListItemDto>> ListAsync(string? search, string? status, Guid? departmentId, CancellationToken ct = default);
    Task<EmployeeDto?> GetAsync(Guid employeeId, CancellationToken ct = default);
    Task<Result<EmployeeDto>> CreateAsync(SaveEmployeeRequest request, CancellationToken ct = default);
    Task<Result<EmployeeDto>> UpdateAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken ct = default);
    Task<Result<EmployeeDto>> SetStatusAsync(Guid employeeId, EmployeeStatusRequest request, CancellationToken ct = default);
    Task<Result<EmployeeDto>> LinkUserAsync(Guid employeeId, LinkEmployeeUserRequest request, CancellationToken ct = default);
    Task<Result<EmployeeSensitiveDto>> SensitiveAsync(Guid employeeId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<ContractDto>>> ContractsAsync(Guid employeeId, CancellationToken ct = default);
    Task<Result<ContractDto>> CreateContractAsync(Guid employeeId, CreateContractRequest request, CancellationToken ct = default);
    Task<Result<ContractDto>> EndContractAsync(Guid contractId, EndContractRequest request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<SalaryHistoryDto>>> SalaryHistoryAsync(Guid employeeId, CancellationToken ct = default);
    Task<Result<SalaryHistoryDto>> ChangeSalaryAsync(Guid employeeId, ChangeSalaryRequest request, CancellationToken ct = default);
    Task<Result<ApplicableSalaryDto>> ApplicableSalaryAsync(Guid employeeId, DateOnly date, CancellationToken ct = default);

    Task<Result<IReadOnlyList<PayElementDto>>> PayElementsAsync(Guid employeeId, CancellationToken ct = default);
    Task<Result<PayElementDto>> AddPayElementAsync(Guid employeeId, SavePayElementRequest request, CancellationToken ct = default);
    Task<Result<PayElementDto>> EndPayElementAsync(Guid elementId, EndPayElementRequest request, CancellationToken ct = default);

    Task<Result<IReadOnlyList<EmployeeDocumentDto>>> DocumentsAsync(Guid employeeId, CancellationToken ct = default);
    Task<Result<EmployeeDocumentDto>> UploadDocumentAsync(Guid employeeId, UploadEmployeeDocumentRequest request, CancellationToken ct = default);
    Task<Result<EmployeeDocumentContent>> DocumentContentAsync(Guid documentId, CancellationToken ct = default);
    Task<Result<EmployeeDocumentDto>> ArchiveDocumentAsync(Guid documentId, CancellationToken ct = default);
}

/// <summary>Présences, congés/absences, heures supplémentaires, éléments variables et file de validation (§18-§20).</summary>
public interface IHrTimeService
{
    Task<IReadOnlyList<AttendanceDto>> AttendanceAsync(Guid? employeeId, DateOnly? from, DateOnly? to, string? validationStatus, CancellationToken ct = default);
    Task<Result<AttendanceImportResultDto>> RecordAttendanceAsync(RecordAttendanceRequest request, CancellationToken ct = default);
    Task<Result<AttendanceDto>> DecideAttendanceAsync(Guid id, bool validate, DecisionRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<LeaveDto>> LeavesAsync(Guid? employeeId, string? status, CancellationToken ct = default);
    Task<Result<LeaveDto>> RequestLeaveAsync(RequestLeaveRequest request, CancellationToken ct = default);
    Task<Result<LeaveDto>> DecideLeaveAsync(Guid id, bool approve, DecisionRequest request, CancellationToken ct = default);
    Task<Result<LeaveDto>> CancelLeaveAsync(Guid id, CancelRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<OvertimeDto>> OvertimeAsync(Guid? employeeId, string? status, CancellationToken ct = default);
    Task<Result<OvertimeDto>> RecordOvertimeAsync(RecordOvertimeRequest request, CancellationToken ct = default);
    Task<Result<OvertimeDto>> DecideOvertimeAsync(Guid id, bool approve, DecisionRequest request, CancellationToken ct = default);
    Task<Result<OvertimeDto>> CancelOvertimeAsync(Guid id, CancelRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<PayrollVariableDto>> VariablesAsync(Guid? periodId, Guid? employeeId, string? status, CancellationToken ct = default);
    Task<Result<PayrollVariableDto>> SubmitVariableAsync(SubmitVariableRequest request, CancellationToken ct = default);
    Task<Result<PayrollVariableDto>> DecideVariableAsync(Guid id, bool approve, DecisionRequest request, CancellationToken ct = default);
    Task<Result<PayrollVariableDto>> CancelVariableAsync(Guid id, CancelRequest request, CancellationToken ct = default);

    /// <summary>Demandes en attente que l'appelant peut voir, avec la possibilité (ou non) de les décider.</summary>
    Task<IReadOnlyList<HrApprovalItemDto>> PendingApprovalsAsync(CancellationToken ct = default);
}

/// <summary>Composants de paie et règles légales configurables et versionnées (§11, §20, §31).</summary>
public interface IPayrollRuleService
{
    Task<IReadOnlyList<PayComponentDto>> ComponentsAsync(CancellationToken ct = default);
    Task<Result<PayComponentDto>> SaveComponentAsync(Guid? id, SavePayComponentRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<PayrollRuleDto>> RulesAsync(bool includeTemplates, CancellationToken ct = default);
    Task<Result<PayrollRuleDto>> CreateRuleAsync(SavePayrollRuleRequest request, CancellationToken ct = default);
    Task<Result<PayrollRuleDto>> UpdateDraftAsync(Guid ruleId, SavePayrollRuleRequest request, CancellationToken ct = default);
    Task<Result<PayrollRuleDto>> NewVersionAsync(Guid ruleId, SavePayrollRuleRequest request, CancellationToken ct = default);
    Task<Result<PayrollRuleDto>> ValidateAsync(Guid ruleId, ValidateRuleRequest request, CancellationToken ct = default);
    Task<Result<PayrollRuleDto>> RetireAsync(Guid ruleId, RetireRuleRequest request, CancellationToken ct = default);
    Task<Result<PayrollRuleDto>> AdoptTemplateAsync(Guid templateId, CancellationToken ct = default);
}

/// <summary>Cycle de paie : périodes, calcul, contrôle, validation, approbation, paiement, bulletins, clôture (§9, §22-§25).</summary>
public interface IPayrollService
{
    Task<IReadOnlyList<PayrollPeriodDto>> PeriodsAsync(int? year, string? status, CancellationToken ct = default);
    Task<Result<PayrollPeriodDto>> PeriodAsync(Guid periodId, CancellationToken ct = default);
    Task<Result<PayrollPeriodDto>> CreatePeriodAsync(CreatePeriodRequest request, CancellationToken ct = default);

    Task<Result<PayrollRunDto>> CalculateAsync(Guid periodId, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> RunAsync(Guid runId, CancellationToken ct = default);
    Task<Result<PayrollItemDetailDto>> ItemAsync(Guid itemId, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> ExcludeItemAsync(Guid itemId, ExcludeItemRequest request, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> IncludeItemAsync(Guid itemId, CancellationToken ct = default);

    Task<Result<PayrollRunDto>> ReviewAsync(Guid runId, WorkflowNoteRequest request, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> ValidateAsync(Guid runId, WorkflowNoteRequest request, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> ApproveAsync(Guid runId, WorkflowNoteRequest request, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> ReturnAsync(Guid runId, WorkflowNoteRequest request, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> CancelAsync(Guid runId, CancelRequest request, CancellationToken ct = default);
    Task<Result<PayrollRunDto>> CloseAsync(Guid runId, WorkflowNoteRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<PaymentAccountOptionDto>> PaymentAccountsAsync(CancellationToken ct = default);
    Task<Result<PayRunResultDto>> PayAsync(Guid runId, PayRunRequest request, CancellationToken ct = default);
    Task<Result<SalaryPaymentDto>> CancelPaymentAsync(Guid paymentId, CancelRequest request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<SalaryPaymentDto>>> PaymentsAsync(Guid runId, CancellationToken ct = default);

    Task<Result<IReadOnlyList<PayslipDto>>> PayslipsForRunAsync(Guid runId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<PayslipDto>>> GeneratePayslipsAsync(Guid runId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<PayslipDto>>> PayslipsForEmployeeAsync(Guid employeeId, CancellationToken ct = default);
    Task<Result<RenderedFile>> PayslipFileAsync(Guid payslipId, string format, CancellationToken ct = default);
    Task<Result<PayslipVerificationDto>> VerifyPayslipAsync(Guid payslipId, CancellationToken ct = default);

    Task<Result<ReconciliationDto>> ReconcileAsync(Guid runId, CancellationToken ct = default);
}

/// <summary>Avances sur salaire, acomptes et prêts au personnel (§12-§17).</summary>
public interface ISalaryAdvanceService
{
    Task<IReadOnlyList<AdvanceDto>> ListAsync(Guid? employeeId, string? status, string? kind, CancellationToken ct = default);
    Task<Result<AdvanceDto>> GetAsync(Guid advanceId, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AdvanceCheckDto>>> PreviewAsync(Guid employeeId, RequestAdvanceRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> RequestAsync(Guid employeeId, RequestAdvanceRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> ApproveAsync(Guid advanceId, ApproveAdvanceRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> RejectAsync(Guid advanceId, DecisionRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> CancelAsync(Guid advanceId, CancelRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> PayAsync(Guid advanceId, PayAdvanceRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> RepayAsync(Guid advanceId, RepayAdvanceRequest request, CancellationToken ct = default);
    Task<Result<AdvanceDto>> RescheduleAsync(Guid advanceId, RescheduleAdvanceRequest request, CancellationToken ct = default);
    Task<Result<AdvanceHistoryDto>> HistoryAsync(Guid advanceId, CancellationToken ct = default);
}

/// <summary>Remboursements de frais — dette de l'entreprise envers l'employé (§17, §32).</summary>
public interface IExpenseClaimService
{
    Task<IReadOnlyList<ExpenseClaimDto>> ListAsync(Guid? employeeId, string? status, CancellationToken ct = default);
    Task<Result<ExpenseClaimDto>> SubmitAsync(SubmitExpenseClaimRequest request, CancellationToken ct = default);
    Task<Result<ExpenseClaimDto>> ApproveAsync(Guid claimId, ApproveExpenseClaimRequest request, CancellationToken ct = default);
    Task<Result<ExpenseClaimDto>> RejectAsync(Guid claimId, DecisionRequest request, CancellationToken ct = default);
    Task<Result<ExpenseClaimDto>> CancelAsync(Guid claimId, CancelRequest request, CancellationToken ct = default);
    Task<Result<ExpenseClaimDto>> PayAsync(Guid claimId, PayExpenseClaimRequest request, CancellationToken ct = default);
}

/// <summary>Régularisations reliées à la paie d'origine (§36).</summary>
public interface IPayrollAdjustmentService
{
    Task<IReadOnlyList<PayrollAdjustmentDto>> ListAsync(Guid? employeeId, string? status, CancellationToken ct = default);
    Task<Result<PayrollAdjustmentDto>> RequestAsync(RequestAdjustmentRequest request, CancellationToken ct = default);
    Task<Result<PayrollAdjustmentDto>> ApproveAsync(Guid adjustmentId, DecisionRequest request, CancellationToken ct = default);
    Task<Result<PayrollAdjustmentDto>> RejectAsync(Guid adjustmentId, DecisionRequest request, CancellationToken ct = default);
    Task<Result<PayrollAdjustmentDto>> CancelAsync(Guid adjustmentId, CancelRequest request, CancellationToken ct = default);
}

/// <summary>Tableau de bord, rapports, historique financier de l'employé et intelligence de paie (§26-§28, §33).</summary>
public interface IPayrollReports
{
    Task<PayrollDashboardDto> DashboardAsync(CancellationToken ct = default);
    IReadOnlyList<PayrollReportInfoDto> Catalog();
    Task<Result<PayrollReportDto>> ReportAsync(string key, PayrollReportQuery query, CancellationToken ct = default);
    Task<Result<RenderedFile>> ExportAsync(string key, PayrollReportQuery query, string format, CancellationToken ct = default);
    Task<Result<IReadOnlyList<EmployeeHistoryEntryDto>>> EmployeeHistoryAsync(Guid employeeId, CancellationToken ct = default);
    Task<IReadOnlyList<PayrollInsightDto>> InsightsAsync(CancellationToken ct = default);

    /// <summary>Solde des avances et prêts restant à récupérer, par devise (assistant §33).</summary>
    Task<IReadOnlyList<(string Currency, decimal Advances, decimal Loans, int Count)>> OutstandingAdvancesAsync(CancellationToken ct = default);
    /// <summary>Masse salariale des paies calculées dont la période couvre le mois donné (assistant §33).</summary>
    Task<IReadOnlyList<CurrencyTotalsDto>> PayrollMassAsync(DateOnly monthStart, CancellationToken ct = default);
}

/// <summary>Espace employé (§5) : l'appelant n'accède qu'à SON dossier.</summary>
public interface IHrSelfService
{
    Task<Guid?> MyEmployeeIdAsync(CancellationToken ct = default);
    Task<Result<MyHrProfileDto>> ProfileAsync(CancellationToken ct = default);
}

/// <summary>Alertes planifiées : échéances d'avance proches et anomalies de paie (§33, §34).</summary>
public interface IPayrollAlerts
{
    Task<int> ScanAsync(CancellationToken ct = default);
}
