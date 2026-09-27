using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;

namespace Nexora.Api.Controllers;

/// <summary>
/// Avances sur salaire, acomptes et prêts au personnel (docs/module-paie-rh-avances.md §12-§17, §29). La demande se
/// fait sur l'employé (<c>POST /employees/{id}/salary-advances</c>) ou dans l'espace employé ; les versements et
/// remboursements exigent une clé d'idempotence (jamais deux sorties de trésorerie pour une instruction, §39).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/salary-advances")]
public sealed class SalaryAdvancesController(ISalaryAdvanceService advances) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> List([FromQuery] Guid? employeeId, [FromQuery] string? status, [FromQuery] string? kind, CancellationToken ct)
        => Ok(await advances.ListAsync(employeeId, status, kind, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await advances.GetAsync(id, ct));

    [HttpGet("{id:guid}/history")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct) => FromResult(await advances.HistoryAsync(id, ct));

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(Permissions.Payroll.AdvanceApprove)]
    public async Task<IActionResult> Approve(Guid id, [FromBody] ApproveAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.ApproveAsync(id, r, ct));

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(Permissions.Payroll.AdvanceApprove)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await advances.RejectAsync(id, r, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await advances.CancelAsync(id, r, ct));

    [HttpPost("{id:guid}/pay")]
    [RequirePermission(Permissions.Payroll.AdvancePay)]
    [Idempotent]
    public async Task<IActionResult> Pay(Guid id, [FromBody] PayAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.PayAsync(id, r, ct));

    /// <summary>Récupération hors paie (remboursement direct de l'employé) — jamais au-delà du solde (§15).</summary>
    [HttpPost("{id:guid}/repay")]
    [RequirePermission(Permissions.Payroll.AdvancePay)]
    [Idempotent]
    public async Task<IActionResult> Repay(Guid id, [FromBody] RepayAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.RepayAsync(id, r, ct));

    [HttpPut("{id:guid}/schedule")]
    [RequirePermission(Permissions.Payroll.AdvanceSchedule)]
    public async Task<IActionResult> Reschedule(Guid id, [FromBody] RescheduleAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.RescheduleAsync(id, r, ct));
}
