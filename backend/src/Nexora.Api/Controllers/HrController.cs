using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Temps et demandes RH (docs/module-paie-rh-avances.md §18-§20), notes de frais (§17), file de validation,
/// tableau de bord et rapports de paie (§27-§28, export PDF / Excel / CSV), intelligence (§33) et audit RH (§35).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/hr")]
public sealed class HrController(
    IHrTimeService time,
    IExpenseClaimService claims,
    IPayrollReports reports,
    IAuditQueryService audit) : ApiControllerBase
{
    // --- Présences (§18) ----------------------------------------------------------------------------

    [HttpGet("attendance")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Attendance([FromQuery] Guid? employeeId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? validationStatus, CancellationToken ct)
        => Ok(await time.AttendanceAsync(employeeId, from, to, validationStatus, ct));

    /// <summary>Saisie ou import par lot (une ligne par employé et par jour).</summary>
    [HttpPost("attendance")]
    [RequirePermission(Permissions.Hr.ManageTime)]
    public async Task<IActionResult> RecordAttendance([FromBody] RecordAttendanceRequest r, CancellationToken ct)
        => FromResult(await time.RecordAttendanceAsync(r, ct));

    [HttpPost("attendance/{id:guid}/validate")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> ValidateAttendance(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideAttendanceAsync(id, true, r, ct));

    [HttpPost("attendance/{id:guid}/reject")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> RejectAttendance(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideAttendanceAsync(id, false, r, ct));

    // --- Congés et absences (§18) ---------------------------------------------------------------------

    [HttpGet("leaves")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Leaves([FromQuery] Guid? employeeId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await time.LeavesAsync(employeeId, status, ct));

    [HttpPost("leaves")]
    [RequirePermission(Permissions.Hr.ManageTime)]
    public async Task<IActionResult> RequestLeave([FromBody] RequestLeaveRequest r, CancellationToken ct) => FromResult(await time.RequestLeaveAsync(r, ct));

    [HttpPost("leaves/{id:guid}/approve")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> ApproveLeave(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideLeaveAsync(id, true, r, ct));

    [HttpPost("leaves/{id:guid}/reject")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> RejectLeave(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideLeaveAsync(id, false, r, ct));

    [HttpPost("leaves/{id:guid}/cancel")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> CancelLeave(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await time.CancelLeaveAsync(id, r, ct));

    // --- Heures supplémentaires (§19) -------------------------------------------------------------------

    [HttpGet("overtime")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Overtime([FromQuery] Guid? employeeId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await time.OvertimeAsync(employeeId, status, ct));

    [HttpPost("overtime")]
    [RequirePermission(Permissions.Hr.ManageTime)]
    public async Task<IActionResult> RecordOvertime([FromBody] RecordOvertimeRequest r, CancellationToken ct)
        => FromResult(await time.RecordOvertimeAsync(r, ct));

    [HttpPost("overtime/{id:guid}/approve")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> ApproveOvertime(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideOvertimeAsync(id, true, r, ct));

    [HttpPost("overtime/{id:guid}/reject")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> RejectOvertime(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideOvertimeAsync(id, false, r, ct));

    [HttpPost("overtime/{id:guid}/cancel")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> CancelOvertime(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await time.CancelOvertimeAsync(id, r, ct));

    // --- Éléments variables (§20) -----------------------------------------------------------------------

    [HttpGet("variables")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Variables([FromQuery] Guid? periodId, [FromQuery] Guid? employeeId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await time.VariablesAsync(periodId, employeeId, status, ct));

    /// <summary>Saisie par la RH (<c>hr.manage_time</c>) ou par un Manager pour son équipe (vérifié par le service).</summary>
    [HttpPost("variables")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> SubmitVariable([FromBody] SubmitVariableRequest r, CancellationToken ct)
        => FromResult(await time.SubmitVariableAsync(r, ct));

    [HttpPost("variables/{id:guid}/approve")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> ApproveVariable(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideVariableAsync(id, true, r, ct));

    [HttpPost("variables/{id:guid}/reject")]
    [RequirePermission(Permissions.Hr.ApproveRequests)]
    public async Task<IActionResult> RejectVariable(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await time.DecideVariableAsync(id, false, r, ct));

    [HttpPost("variables/{id:guid}/cancel")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> CancelVariable(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await time.CancelVariableAsync(id, r, ct));

    /// <summary>Toutes les demandes en attente que l'appelant peut voir, et celles qu'il peut décider.</summary>
    [HttpGet("approvals")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Approvals(CancellationToken ct) => Ok(await time.PendingApprovalsAsync(ct));

    // --- Notes de frais (§17, §32) -------------------------------------------------------------------------

    [HttpGet("expense-claims")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> ExpenseClaims([FromQuery] Guid? employeeId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await claims.ListAsync(employeeId, status, ct));

    [HttpPost("expense-claims")]
    [RequirePermission(Permissions.Hr.ManageTime)]
    public async Task<IActionResult> SubmitExpenseClaim([FromBody] SubmitExpenseClaimRequest r, CancellationToken ct)
        => FromResult(await claims.SubmitAsync(r, ct));

    [HttpPost("expense-claims/{id:guid}/approve")]
    [RequirePermission(Permissions.Payroll.ExpenseApprove)]
    public async Task<IActionResult> ApproveExpenseClaim(Guid id, [FromBody] ApproveExpenseClaimRequest r, CancellationToken ct)
        => FromResult(await claims.ApproveAsync(id, r, ct));

    [HttpPost("expense-claims/{id:guid}/reject")]
    [RequirePermission(Permissions.Payroll.ExpenseApprove)]
    public async Task<IActionResult> RejectExpenseClaim(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await claims.RejectAsync(id, r, ct));

    [HttpPost("expense-claims/{id:guid}/cancel")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> CancelExpenseClaim(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await claims.CancelAsync(id, r, ct));

    [HttpPost("expense-claims/{id:guid}/pay")]
    [RequireStepUp(StepUpPurposes.Payment)]
    [RequirePermission(Permissions.Payroll.ExpenseApprove)]
    [Idempotent]
    public async Task<IActionResult> PayExpenseClaim(Guid id, [FromBody] PayExpenseClaimRequest r, CancellationToken ct)
        => FromResult(await claims.PayAsync(id, r, ct));

    // --- Tableau de bord, rapports, intelligence (§27, §28, §33) -----------------------------------------

    [HttpGet("dashboard")]
    [RequirePermission(Permissions.Payroll.Reports)]
    public async Task<IActionResult> Dashboard(CancellationToken ct) => Ok(await reports.DashboardAsync(ct));

    [HttpGet("insights")]
    [RequirePermission(Permissions.Payroll.Reports)]
    public async Task<IActionResult> Insights(CancellationToken ct) => Ok(await reports.InsightsAsync(ct));

    [HttpGet("reports")]
    [RequirePermission(Permissions.Payroll.Reports)]
    public IActionResult ReportCatalog() => Ok(reports.Catalog());

    [HttpGet("reports/{key}")]
    [RequirePermission(Permissions.Payroll.Reports)]
    public async Task<IActionResult> Report(string key, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] Guid? periodId,
        [FromQuery] Guid? employeeId, CancellationToken ct)
        => FromResult(await reports.ReportAsync(key, new PayrollReportQuery(from, to, periodId, null, employeeId), ct));

    /// <summary>Export PDF / Excel / CSV (journalisé comme export de données sensibles, §27, §35).</summary>
    [HttpGet("reports/{key}/export")]
    [RequirePermission(Permissions.Payroll.Reports)]
    public async Task<IActionResult> ExportReport(string key, [FromQuery] string? format, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] Guid? periodId, [FromQuery] Guid? employeeId, CancellationToken ct)
    {
        var file = await reports.ExportAsync(key, new PayrollReportQuery(from, to, periodId, null, employeeId), format ?? "xlsx", ct);
        if (!file.IsSuccess) return FromResult(file);
        return Download(file.Value!.Content, file.Value.ContentType, file.Value.FileName);
    }

    // --- Audit RH (§35) : le journal immuable existant, restreint aux objets RH et paie ---------------------

    private static readonly HashSet<string> AuditedEntities =
    [
        "employee", "employee_contract", "salary_history", "employee_pay_element", "payroll_period", "payroll_run", "attendance_record",
        "leave_record", "overtime_record", "payroll_variable", "payroll_rule", "employee_advance", "advance_installment", "advance_repayment",
        "employee_expense_claim", "salary_payment", "payslip", "payroll_adjustment", "employee_document", "department", "pay_component",
        "payroll_report",
    ];

    [HttpGet("audit")]
    [RequirePermission(Permissions.Payroll.Audit)]
    public async Task<IActionResult> Audit([FromQuery] string entityType, [FromQuery] Guid? entityId, [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        if (!AuditedEntities.Contains(entityType))
            throw new DomainException(Error.Validation("hr.audit_entity", $"Objet « {entityType} » hors du périmètre de l'audit RH."));
        return Ok(await audit.QueryAsync(new AuditQueryFilter(from, to, null, entityType, entityId, null, page, 100), ct,
            Permissions.Payroll.Audit));   // A-034 : seulement les entreprises où l'appelant détient payroll.audit
    }
}
