using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Espace employé (docs/module-paie-rh-avances.md §5, §6 « Employé ») : l'identifiant de l'employé n'est JAMAIS lu
/// dans la requête — il est déduit du compte connecté. Un bulletin, une avance ou un document d'un autre employé
/// répond 404, même avec son identifiant exact.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/me/hr")]
[RequirePermission(Permissions.Payroll.SelfService)]
public sealed class MeHrController(
    IHrSelfService self,
    IEmployeeService employees,
    IPayrollService payroll,
    ISalaryAdvanceService advances,
    IHrTimeService time,
    IExpenseClaimService claims,
    IPayrollReports reports) : ApiControllerBase
{
    private async Task<Guid> MeAsync(CancellationToken ct)
        => await self.MyEmployeeIdAsync(ct)
           ?? throw new DomainException(Error.NotFound("hr.no_employee_record",
               "Aucun dossier employé n'est rattaché à votre compte dans cette entreprise."));

    [HttpGet("profile")]
    public async Task<IActionResult> Profile(CancellationToken ct) => FromResult(await self.ProfileAsync(ct));

    [HttpGet("history")]
    public async Task<IActionResult> History(CancellationToken ct) => FromResult(await reports.EmployeeHistoryAsync(await MeAsync(ct), ct));

    // --- Bulletins (§25) ------------------------------------------------------------------------------

    [HttpGet("payslips")]
    public async Task<IActionResult> Payslips(CancellationToken ct) => FromResult(await payroll.PayslipsForEmployeeAsync(await MeAsync(ct), ct));

    [HttpGet("payslips/{id:guid}/file")]
    public async Task<IActionResult> PayslipFile(Guid id, [FromQuery] string? format, CancellationToken ct)
    {
        await MeAsync(ct);
        var file = await payroll.PayslipFileAsync(id, format ?? "pdf", ct);
        if (!file.IsSuccess) return FromResult(file);
        return Download(file.Value!.Content, file.Value.ContentType, file.Value.FileName);
    }

    [HttpGet("payroll-items/{id:guid}")]
    public async Task<IActionResult> PayrollItem(Guid id, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        var item = await payroll.ItemAsync(id, ct);
        if (item.IsSuccess && item.Value!.Summary.EmployeeId != me)
            return FromResult(Result.Failure<PayrollItemDetailDto>(Error.NotFound("payroll.item_not_found", "Ligne de paie introuvable.")));
        return FromResult(item);
    }

    // --- Avances (§13.1 « Employé ou RH crée une demande ») -------------------------------------------------

    [HttpGet("advances")]
    public async Task<IActionResult> Advances(CancellationToken ct) => Ok(await advances.ListAsync(await MeAsync(ct), null, null, ct));

    [HttpPost("advances/preview")]
    public async Task<IActionResult> PreviewAdvance([FromBody] RequestAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.PreviewAsync(await MeAsync(ct), r, ct));

    [HttpPost("advances")]
    public async Task<IActionResult> RequestAdvance([FromBody] RequestAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.RequestAsync(await MeAsync(ct), r, ct));

    [HttpGet("advances/{id:guid}/history")]
    public async Task<IActionResult> AdvanceHistory(Guid id, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        var history = await advances.HistoryAsync(id, ct);
        if (history.IsSuccess && history.Value!.Advance.EmployeeId != me)
            return FromResult(Result.Failure<AdvanceHistoryDto>(Error.NotFound("payroll.advance_not_found", "Avance introuvable.")));
        return FromResult(history);
    }

    [HttpPost("advances/{id:guid}/cancel")]
    public async Task<IActionResult> CancelAdvance(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        var advance = await advances.GetAsync(id, ct);
        if (!advance.IsSuccess || advance.Value!.EmployeeId != me)
            return FromResult(Result.Failure<AdvanceDto>(Error.NotFound("payroll.advance_not_found", "Avance introuvable.")));
        return FromResult(await advances.CancelAsync(id, r, ct));
    }

    // --- Congés, heures sup., notes de frais ----------------------------------------------------------------

    [HttpGet("leaves")]
    public async Task<IActionResult> Leaves(CancellationToken ct) => Ok(await time.LeavesAsync(await MeAsync(ct), null, ct));

    [HttpPost("leaves")]
    public async Task<IActionResult> RequestLeave([FromBody] RequestLeaveRequest r, CancellationToken ct)
        => FromResult(await time.RequestLeaveAsync(r with { EmployeeId = await MeAsync(ct) }, ct));

    [HttpPost("leaves/{id:guid}/cancel")]
    public async Task<IActionResult> CancelLeave(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
    {
        var me = await MeAsync(ct);
        if ((await time.LeavesAsync(me, null, ct)).All(l => l.Id != id))
            return FromResult(Result.Failure<LeaveDto>(Error.NotFound("hr.leave_not_found", "Absence introuvable.")));
        return FromResult(await time.CancelLeaveAsync(id, r, ct));
    }

    [HttpGet("overtime")]
    public async Task<IActionResult> Overtime(CancellationToken ct) => Ok(await time.OvertimeAsync(await MeAsync(ct), null, ct));

    [HttpPost("overtime")]
    public async Task<IActionResult> RecordOvertime([FromBody] RecordOvertimeRequest r, CancellationToken ct)
        => FromResult(await time.RecordOvertimeAsync(r with { EmployeeId = await MeAsync(ct) }, ct));

    [HttpGet("expense-claims")]
    public async Task<IActionResult> ExpenseClaims(CancellationToken ct) => Ok(await claims.ListAsync(await MeAsync(ct), null, ct));

    [HttpPost("expense-claims")]
    public async Task<IActionResult> SubmitExpenseClaim([FromBody] SubmitExpenseClaimRequest r, CancellationToken ct)
        => FromResult(await claims.SubmitAsync(r with { EmployeeId = await MeAsync(ct) }, ct));

    // --- Documents rendus visibles à l'employé ---------------------------------------------------------------

    [HttpGet("documents")]
    public async Task<IActionResult> Documents(CancellationToken ct) => FromResult(await employees.DocumentsAsync(await MeAsync(ct), ct));

    [HttpGet("documents/{id:guid}/content")]
    public async Task<IActionResult> DocumentContent(Guid id, CancellationToken ct)
    {
        await MeAsync(ct);
        var doc = await employees.DocumentContentAsync(id, ct);
        if (!doc.IsSuccess) return FromResult(doc);
        return Download(doc.Value!.Content, doc.Value.ContentType, doc.Value.FileName);
    }
}
