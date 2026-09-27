using System.Text.Json;

namespace Nexora.Contracts.Payroll;

// Module « Paie, RH & avances sur salaire » (docs/module-paie-rh-avances.md). Les montants viennent
// toujours du serveur (§39 « ne jamais faire confiance à un montant envoyé par le frontend ») ; une
// donnée salariale n'est renvoyée qu'à un appelant qui y a droit (§5).

// ------------------------------------------------------------------ Organisation & dossier employé (§7)

public sealed record DepartmentDto(Guid Id, string Code, string Name, Guid? ManagerEmployeeId, string? ManagerName, string Status, int EmployeeCount);

public sealed record SaveDepartmentRequest(string Code, string Name, Guid? ManagerEmployeeId, string? Status);

public sealed record EmployeeListItemDto(
    Guid Id, string EmployeeNumber, string FirstName, string LastName, string? JobTitle, Guid? DepartmentId, string? DepartmentName,
    string Status, DateOnly HireDate, string? EstablishmentName, bool HasUser, string? ContractType,
    decimal? CurrentSalary, string? SalaryBasis, string? Currency);

public sealed record EmployeeCompensationDto(
    Guid? ContractId, string? ContractNumber, string? ContractType, string? PayFrequency, string? PayBasis, string? Currency,
    decimal? BaseSalary, decimal? HourlyRate, DateOnly? EffectiveDate, decimal AdvanceBalance, decimal LoanBalance, int OpenAdvances);

public sealed record EmployeeDto(
    Guid Id, string EmployeeNumber, string FirstName, string LastName, DateOnly? BirthDate, string? Phone, string? Email,
    string? Address, string? City, string? EmergencyContactName, string? EmergencyContactPhone, string? JobTitle,
    Guid? DepartmentId, string? DepartmentName, Guid? ManagerEmployeeId, string? ManagerName, Guid? EstablishmentId,
    string? EstablishmentName, DateOnly HireDate, DateOnly? TerminationDate, string? TerminationReason, string Status,
    Guid? UserId, string? UserEmail, string PaymentMethod, string? BankName, string? BankAccountMasked, string? MobileMoneyMasked,
    string? TaxIdMasked, string? SocialIdMasked, string? Notes, DateTimeOffset CreatedAt, string? CreatedByName, DateTimeOffset UpdatedAt,
    EmployeeCompensationDto? Compensation, bool CanViewSalaries, bool CanViewSensitive, bool CanManage);

/// <summary>Identifiants en clair — réservé à <c>payroll.view_sensitive</c>, chaque lecture est journalisée (§35).</summary>
public sealed record EmployeeSensitiveDto(
    Guid EmployeeId, string? BankName, string? BankAccount, string? MobileMoney, string? TaxId, string? SocialId, JsonElement TaxParameters);

public sealed record SaveEmployeeRequest(
    string? EmployeeNumber, string FirstName, string LastName, DateOnly? BirthDate, string? Phone, string? Email, string? Address,
    string? City, string? EmergencyContactName, string? EmergencyContactPhone, string? JobTitle, Guid? DepartmentId,
    Guid? ManagerEmployeeId, Guid? EstablishmentId, DateOnly HireDate, string? PaymentMethod, string? BankName,
    string? BankAccount, string? MobileMoney, string? TaxId, string? SocialId, JsonElement? TaxParameters, string? Notes,
    CreateContractRequest? Contract = null);

public sealed record EmployeeStatusRequest(string Status, DateOnly? Date, string? Reason);

public sealed record LinkEmployeeUserRequest(Guid? UserId);

// ------------------------------------------------------------------ Contrats & rémunération (§8)

public sealed record CreateContractRequest(
    string ContractType, DateOnly StartDate, DateOnly? EndDate, DateOnly? TrialEndDate, string PayFrequency, string PayBasis,
    string Currency, decimal? WeeklyHours, string? JobTitle, string? Benefits, string? Conditions,
    decimal? BaseSalary, decimal? HourlyRate, string? SalaryReason);

public sealed record ContractDto(
    Guid Id, string ContractNumber, string ContractType, DateOnly StartDate, DateOnly? EndDate, DateOnly? TrialEndDate,
    string PayFrequency, string PayBasis, string Currency, decimal WeeklyHours, string? JobTitle, string? Benefits, string? Conditions,
    string Status, string? EndReason, DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record EndContractRequest(DateOnly EndDate, string Reason, bool Terminated = false);

public sealed record SalaryHistoryDto(
    Guid Id, Guid ContractId, string ContractNumber, DateOnly EffectiveDate, string PayBasis, decimal Amount, decimal? HourlyRate,
    string Currency, decimal? PreviousAmount, DateOnly? PreviousEffectiveDate, string Reason, string? CreatedByName, DateTimeOffset CreatedAt);

public sealed record ChangeSalaryRequest(Guid? ContractId, DateOnly EffectiveDate, decimal Amount, decimal? HourlyRate, string Reason);

/// <summary>Rémunération applicable à une date donnée, reconstituée depuis l'historique (§8).</summary>
public sealed record ApplicableSalaryDto(
    DateOnly Date, Guid? ContractId, string? ContractNumber, Guid? SalaryId, decimal? Amount, decimal? HourlyRate, string? PayBasis,
    string? Currency, DateOnly? EffectiveDate);

public sealed record PayElementDto(
    Guid Id, Guid ComponentId, string ComponentCode, string ComponentName, string Kind, string Category, string CalcMethod,
    decimal? Amount, decimal? Rate, string Currency, DateOnly StartDate, DateOnly? EndDate, string Status, string? Note);

public sealed record SavePayElementRequest(
    string ComponentCode, string CalcMethod, decimal? Amount, decimal? Rate, DateOnly StartDate, DateOnly? EndDate, string? Note);

public sealed record EndPayElementRequest(DateOnly EndDate);

// ------------------------------------------------------------------ Documents RH (§7)

public sealed record EmployeeDocumentDto(
    Guid Id, Guid EmployeeId, string DocumentType, string Title, string FileName, string ContentType, int SizeBytes, string Sha256,
    bool VisibleToEmployee, Guid? SupersedesId, DateTimeOffset? ArchivedAt, string? UploadedByName, DateTimeOffset CreatedAt);

public sealed record UploadEmployeeDocumentRequest(
    string DocumentType, string Title, string FileName, string ContentType, string ContentBase64, bool VisibleToEmployee = false,
    Guid? SupersedesId = null);

public sealed record EmployeeDocumentContent(string FileName, string ContentType, byte[] Content);

// ------------------------------------------------------------------ Temps : présences, congés, heures sup., variables (§18-§20)

public sealed record AttendanceDto(
    Guid Id, Guid EmployeeId, string EmployeeName, DateOnly WorkDate, string Status, decimal? HoursWorked, bool Justified,
    string Source, string? Note, string ValidationStatus, string? ValidatedByName, DateTimeOffset? ValidatedAt);

public sealed record AttendanceInput(Guid EmployeeId, DateOnly WorkDate, string Status, decimal? HoursWorked, bool Justified, string? Note);

public sealed record RecordAttendanceRequest(IReadOnlyList<AttendanceInput> Records, string? Source);

public sealed record AttendanceImportResultDto(int Created, int Updated, IReadOnlyList<string> Errors);

public sealed record LeaveDto(
    Guid Id, Guid EmployeeId, string EmployeeName, string LeaveType, DateOnly StartDate, DateOnly EndDate, decimal Days, bool Paid,
    string? Reason, string Status, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName, DateTimeOffset? DecidedAt,
    string? DecisionNote);

public sealed record RequestLeaveRequest(
    Guid EmployeeId, string LeaveType, DateOnly StartDate, DateOnly EndDate, decimal? Days, bool? Paid, string? Reason);

public sealed record DecisionRequest(string? Note);

public sealed record OvertimeDto(
    Guid Id, Guid EmployeeId, string EmployeeName, DateOnly WorkDate, decimal Hours, string OvertimeType, decimal Coefficient,
    string? Reason, string Status, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName, DateTimeOffset? DecidedAt,
    string? DecisionNote, Guid? PayrollItemId);

public sealed record RecordOvertimeRequest(Guid EmployeeId, DateOnly WorkDate, decimal Hours, string OvertimeType, string? Reason);

public sealed record PayrollVariableDto(
    Guid Id, Guid EmployeeId, string EmployeeName, Guid PeriodId, string PeriodLabel, string ComponentCode, string ComponentName,
    string Kind, decimal Amount, string Description, string? LegalBasis, string Status, string? SubmittedByName, DateTimeOffset SubmittedAt,
    string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? PayrollItemId);

public sealed record SubmitVariableRequest(
    Guid EmployeeId, Guid PeriodId, string ComponentCode, decimal Amount, string Description, string? LegalBasis);

/// <summary>Demande à valider (congé, heures sup., variable, présence, avance, frais, régularisation).</summary>
public sealed record HrApprovalItemDto(
    string Kind, Guid Id, Guid EmployeeId, string EmployeeName, string Summary, decimal? Amount, string? Currency,
    DateTimeOffset RequestedAt, string? RequestedByName, bool CanDecide, string? CannotDecideReason);

// ------------------------------------------------------------------ Paramétrage : composants & règles légales (§11, §20)

public sealed record PayComponentDto(
    Guid Id, bool IsSystem, string Code, string Name, string Kind, string Category, bool Taxable, bool SubjectToSocial, bool InGross,
    bool Cash, bool RequiresLegalBasis, decimal? DefaultAmount, string Status);

public sealed record SavePayComponentRequest(
    string Code, string Name, string Kind, string Category, bool Taxable, bool SubjectToSocial, bool InGross, bool Cash,
    bool RequiresLegalBasis, decimal? DefaultAmount, string? Status);

public sealed record RuleBracketDto(decimal From, decimal? To, decimal Rate);

public sealed record PayrollRuleDto(
    Guid Id, bool IsTemplate, string Code, string Name, string Jurisdiction, string RuleType, string BaseType, string Method,
    decimal? Rate, decimal? FixedAmount, IReadOnlyList<RuleBracketDto> Brackets, bool Annualize, decimal? BaseFloor,
    decimal? BaseCeiling, decimal? MinAmount, decimal? MaxAmount, DateOnly ValidFrom, DateOnly? ValidTo, int Version, string Status,
    string? LegalReference, string? Notes, Guid? SupersedesId, string? ValidatedByName, DateTimeOffset? ValidatedAt, string? ValidationNote);

public sealed record SavePayrollRuleRequest(
    string Code, string Name, string? Jurisdiction, string RuleType, string BaseType, string Method, decimal? Rate,
    decimal? FixedAmount, IReadOnlyList<RuleBracketDto>? Brackets, bool Annualize, decimal? BaseFloor, decimal? BaseCeiling,
    decimal? MinAmount, decimal? MaxAmount, DateOnly ValidFrom, DateOnly? ValidTo, string? LegalReference, string? Notes);

public sealed record ValidateRuleRequest(string Note);

public sealed record RetireRuleRequest(DateOnly ValidTo, string? Note);

// ------------------------------------------------------------------ Périodes, exécutions, lignes (§9, §22, §23)

public sealed record CreatePeriodRequest(
    string Frequency, DateOnly StartDate, DateOnly EndDate, DateOnly? CutoffDate, DateOnly? PaymentDate, string? Kind, string? Label,
    IReadOnlyList<Guid>? EmployeeIds);

public sealed record CurrencyTotalsDto(
    string Currency, decimal Gross, decimal Deductions, decimal Net, decimal EmployerCost, decimal Paid, decimal Unpaid);

public sealed record CurrencyAmountDto(string Currency, decimal Amount);

public sealed record PayrollPeriodDto(
    Guid Id, string Code, string Label, string Kind, string Frequency, DateOnly StartDate, DateOnly EndDate, DateOnly CutoffDate,
    DateOnly PaymentDate, string Status, Guid? RunId, string? RunNumber, string? RunStatus, int EmployeeCount,
    IReadOnlyList<CurrencyTotalsDto> Totals, DateTimeOffset CreatedAt);

public sealed record AnomalyDto(string Code, string Severity, string Message);

public sealed record PayrollItemSummaryDto(
    Guid Id, Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? DepartmentName, string Currency, decimal Gross,
    decimal TotalDeductions, decimal Net, decimal EmployerCost, decimal AdvanceRecovery, decimal LoanRecovery, string Status,
    string? ExcludedReason, int BlockingCount, int WarningCount, IReadOnlyList<AnomalyDto> Anomalies, Guid? PaymentId,
    string? PaymentNumber, Guid? PayslipId, string? PayslipNumber, string PaymentMethod);

public sealed record PayrollRunDto(
    Guid Id, string RunNumber, Guid PeriodId, string PeriodCode, string PeriodLabel, string PeriodKind, string Frequency,
    DateOnly StartDate, DateOnly EndDate, DateOnly CutoffDate, DateOnly PaymentDate, string Status, bool Stale, int CalculationCount,
    string? CalculatedByName, DateTimeOffset? CalculatedAt, string? ReviewedByName, DateTimeOffset? ReviewedAt, string? ReviewNote,
    bool WarningsAcknowledged, string? ValidatedByName, DateTimeOffset? ValidatedAt, string? ValidationNote, string? ApprovedByName,
    DateTimeOffset? ApprovedAt, string? ApprovalNote, Guid? JournalEntryId, string? JournalEntryNumber, DateTimeOffset? PaidAt,
    string? ClosedByName, DateTimeOffset? ClosedAt, string? ReturnedNote, string? CancelReason, int ItemCount, int ExcludedCount,
    int BlockingCount, int WarningCount, int PaidCount, IReadOnlyList<CurrencyTotalsDto> Totals,
    IReadOnlyList<PayrollItemSummaryDto> Items, IReadOnlyList<string> AllowedActions);

public sealed record PayrollLineDto(
    Guid Id, string Code, string Category, string Label, decimal? Quantity, decimal? Rate, decimal? BaseAmount, decimal? PlannedAmount,
    decimal Amount, string? BorneBy, string SourceType, Guid? SourceId, Guid? RuleId, int? RuleVersion, bool? Taxable, bool? InGross);

public sealed record TraceStepDto(string Step, string Detail, decimal? Amount);

public sealed record PayrollItemDetailDto(
    PayrollItemSummaryDto Summary, Guid RunId, string RunNumber, string RunStatus, string PeriodLabel, DateOnly PeriodStart,
    DateOnly PeriodEnd, string? JobTitle, string? ContractType, string? PayBasis, string? PayFrequency, decimal BaseSalary, int PeriodDays,
    decimal DaysEmployed, decimal AbsenceDays, decimal Prorata, decimal DailyRate, decimal HourlyRate, decimal BaseAmount,
    decimal Reimbursements, decimal TaxableBase, decimal SocialBase, decimal LegalDeductions, decimal AbsenceDeduction,
    decimal BenefitOffset, decimal OtherDeductions, decimal EmployerContributions, IReadOnlyList<PayrollLineDto> Earnings,
    IReadOnlyList<PayrollLineDto> Deductions, IReadOnlyList<TraceStepDto> Trace, IReadOnlyList<SalaryPaymentDto> Payments);

public sealed record WorkflowNoteRequest(string? Note, bool AcknowledgeWarnings = false);

public sealed record ExcludeItemRequest(string Reason);

public sealed record CancelRequest(string Reason);

public sealed record PaymentAccountOptionDto(Guid Id, string Name, string AccountType, string Currency);

/// <summary>Paiement individuel (ItemIds = une ligne) ou groupé (toutes les lignes approuvées non payées).
/// <c>OperationUuid</c> fixé à l'ouverture du formulaire : un double envoi rejoue, ne paie jamais deux fois (§39).</summary>
public sealed record PayRunRequest(
    Guid FinancialAccountId, string? PaymentMethod, IReadOnlyList<Guid>? ItemIds, string? Reference, DateOnly? PaidOn,
    Guid? CashSessionId, Guid OperationUuid);

public sealed record SalaryPaymentDto(
    Guid Id, string PaymentNumber, Guid PayrollItemId, Guid RunId, string RunNumber, Guid EmployeeId, string EmployeeName,
    decimal Amount, string Currency, string PaymentMethod, Guid FinancialAccountId, string FinancialAccountName, string? Reference,
    string? BeneficiaryAccount, DateTimeOffset PaidAt, string Status, Guid JournalEntryId, string? CancelReason,
    DateTimeOffset? CancelledAt, Guid? PayslipId, string? PayslipNumber);

public sealed record PayRunResultDto(
    int Paid, int AlreadyPaid, int Skipped, IReadOnlyList<SalaryPaymentDto> Payments, IReadOnlyList<string> Errors, string RunStatus);

public sealed record PayslipDto(
    Guid Id, string PayslipNumber, Guid PayrollItemId, Guid RunId, Guid EmployeeId, string EmployeeName, string PeriodLabel,
    DateOnly PeriodStart, DateOnly PeriodEnd, string Currency, decimal Gross, decimal Net, string Status, DateTimeOffset IssuedAt,
    string Sha256, Guid? SalaryPaymentId, string? PaymentReference);

public sealed record PayslipVerificationDto(Guid Id, string PayslipNumber, bool IntegrityOk, string StoredHash, string ComputedHash);

public sealed record ReconciliationLineDto(string Key, string Label, string Currency, decimal Hr, decimal Ledger, decimal Difference);

public sealed record ReconciliationEmployeeDto(
    Guid EmployeeId, string EmployeeName, string Currency, decimal NetApproved, decimal Paid, decimal LedgerPayable, bool Ok);

/// <summary>Rapprochement RH ↔ Finance d'une paie (§32, §37 test 30).</summary>
public sealed record ReconciliationDto(
    Guid RunId, string RunNumber, bool Reconciled, IReadOnlyList<ReconciliationLineDto> Lines,
    IReadOnlyList<ReconciliationEmployeeDto> Employees);

// ------------------------------------------------------------------ Avances, acomptes, prêts (§12-§17)

public sealed record RequestAdvanceRequest(
    string Kind, decimal Amount, string? Reason, string? RecoveryMethod, int? Installments, decimal? RecoveryPercent,
    DateOnly? FirstRecoveryDate, string? Conditions);

public sealed record AdvanceCheckDto(string Code, bool Passed, bool Blocking, string Message);

public sealed record InstallmentDto(
    Guid Id, int SeqNo, int ScheduleVersion, DateOnly DueDate, decimal Amount, decimal RecoveredAmount, string Status);

public sealed record AdvanceDto(
    Guid Id, string AdvanceNumber, Guid EmployeeId, string EmployeeName, string EmployeeNumber, string Kind, string Currency,
    DateOnly RequestDate, decimal AmountRequested, decimal? AmountApproved, decimal AmountPaid, decimal RecoveredAmount, decimal Balance,
    string? Reason, string? Conditions, string RecoveryMethod, int InstallmentsCount, decimal? RecoveryPercent, DateOnly? FirstRecoveryDate,
    string Status, string? RequestedByName, DateTimeOffset RequestedAt, string? ApprovedByName, DateTimeOffset? ApprovedAt,
    string? DecisionNote, string? PaidByName, DateTimeOffset? PaidAt, string? FinancialAccountName, string? PaymentMethod,
    string? PaymentReference, Guid? JournalEntryId, DateTimeOffset? SettledAt, string? CancelReason, int ApprovalLevel, int RequiredLevels,
    IReadOnlyList<AdvanceCheckDto> Checks, IReadOnlyList<InstallmentDto> Schedule);

public sealed record ApproveAdvanceRequest(
    decimal? AmountApproved, string? Note, string? RecoveryMethod, int? Installments, decimal? RecoveryPercent, DateOnly? FirstRecoveryDate);

public sealed record PayAdvanceRequest(
    Guid FinancialAccountId, string PaymentMethod, string? Reference, Guid? CashSessionId, DateOnly? PaidOn, Guid OperationUuid);

public sealed record RepayAdvanceRequest(
    decimal Amount, Guid FinancialAccountId, string PaymentMethod, string? Reference, string? Note, Guid OperationUuid);

public sealed record InstallmentInput(DateOnly DueDate, decimal Amount);

public sealed record RescheduleAdvanceRequest(IReadOnlyList<InstallmentInput> Installments, string Reason);

public sealed record AdvanceEventDto(
    Guid Id, string EventType, decimal? Amount, decimal? BalanceAfter, string? Reference, string? Note, string? ActorName, DateTimeOffset OccurredAt);

public sealed record AdvanceRepaymentDto(
    Guid Id, string Source, decimal Amount, string Currency, DateTimeOffset RepaidAt, string? RunNumber, string? PeriodLabel,
    string? Reference, Guid? JournalEntryId, string? CreatedByName);

public sealed record AdvanceHistoryDto(AdvanceDto Advance, IReadOnlyList<AdvanceEventDto> Events, IReadOnlyList<AdvanceRepaymentDto> Repayments);

// ------------------------------------------------------------------ Remboursements de frais (§17, §32)

public sealed record ExpenseClaimDto(
    Guid Id, string ClaimNumber, Guid EmployeeId, string EmployeeName, DateOnly ExpenseDate, string Category, string Description,
    decimal Amount, string Currency, Guid? ReceiptDocumentId, string Settlement, string Status, string? SubmittedByName,
    DateTimeOffset SubmittedAt, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? ApprovalJournalEntryId,
    Guid? PayrollItemId, DateTimeOffset? PaidAt, string? PaymentReference);

public sealed record SubmitExpenseClaimRequest(
    Guid EmployeeId, DateOnly ExpenseDate, string Category, string Description, decimal Amount, string? Currency, string? Settlement,
    Guid? ReceiptDocumentId, UploadEmployeeDocumentRequest? Receipt);

public sealed record ApproveExpenseClaimRequest(string? Note, Guid? ExpenseAccountId);

public sealed record PayExpenseClaimRequest(Guid FinancialAccountId, string? Reference, Guid OperationUuid);

// ------------------------------------------------------------------ Régularisations (§36)

public sealed record PayrollAdjustmentDto(
    Guid Id, string AdjustmentNumber, Guid EmployeeId, string EmployeeName, Guid OriginalRunId, string OriginalRunNumber,
    string OriginalPeriodLabel, Guid OriginalItemId, string Kind, string? ComponentCode, decimal Amount, string Currency, bool Taxable,
    string Reason, string Status, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName, DateTimeOffset? DecidedAt,
    string? DecisionNote, Guid? AppliedItemId, string? AppliedRunNumber, DateTimeOffset? AppliedAt);

public sealed record RequestAdjustmentRequest(
    Guid OriginalItemId, string Kind, decimal Amount, bool? Taxable, string Reason, string? ComponentCode);

// ------------------------------------------------------------------ Historique, rapports, tableau de bord, intelligence (§26-§28, §33)

public sealed record EmployeeHistoryEntryDto(
    DateTimeOffset OccurredAt, string Type, string Title, string? Detail, decimal? Amount, string? Currency, string? Reference,
    Guid? SourceId, string? ActorName);

public sealed record PayrollInsightDto(
    string Code, string Severity, string Title, string Detail, Guid? EmployeeId, string? EmployeeName, decimal? Amount, string? Currency);

public sealed record PayrollDashboardDto(
    int ActiveEmployees, string? LastPeriodLabel, IReadOnlyList<CurrencyTotalsDto> LastPeriodTotals,
    IReadOnlyList<CurrencyAmountDto> PayrollToPay, int OpenAdvances, IReadOnlyList<CurrencyAmountDto> AdvanceBalance,
    IReadOnlyList<CurrencyAmountDto> LoanBalance, IReadOnlyList<CurrencyAmountDto> DeductionsOfPeriod, int Anomalies,
    int RunsInValidation, IReadOnlyList<CurrencyAmountDto> PaymentsThisMonth, int PaymentsThisMonthCount, int PendingRequests,
    IReadOnlyList<PayrollInsightDto> Insights);

public sealed record PayrollReportInfoDto(string Key, string Title, string Description, bool NeedsPeriod);

public sealed record PayrollReportColumnDto(string Key, string Label, string Kind, string? Currency = null);

/// <summary>Rapport tabulaire (§27) : colonnes typées, lignes de valeurs brutes, totaux facultatifs — rendu à
/// l'écran et exporté tel quel en PDF / Excel / CSV.</summary>
public sealed record PayrollReportDto(
    string Key, string Title, string? Subtitle, IReadOnlyList<PayrollReportColumnDto> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Totals);

public sealed record PayrollReportQuery(DateOnly? From, DateOnly? To, Guid? PeriodId, string? Status, Guid? EmployeeId);

// ------------------------------------------------------------------ Espace employé (§5)

public sealed record MyHrProfileDto(
    Guid EmployeeId, string EmployeeNumber, string FullName, string? JobTitle, string? DepartmentName, DateOnly HireDate, string Status,
    string? ContractType, string? PayFrequency, string? Currency, decimal? BaseSalary, string PaymentMethod, string? BankAccountMasked,
    decimal AdvanceBalance, decimal LoanBalance, decimal AnnualLeaveEntitlement, decimal AnnualLeaveTaken);
