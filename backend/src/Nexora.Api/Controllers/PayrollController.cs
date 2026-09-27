using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;

namespace Nexora.Api.Controllers;

/// <summary>
/// Cycle de paie (docs/module-paie-rh-avances.md §9, §22-§25, §29, §32, §36) : périodes, calcul, contrôle,
/// validation, approbation finale, paiements (clé d'idempotence obligatoire), bulletins, rapprochement RH ↔ Finance,
/// règles légales versionnées et régularisations. Une étape = une permission distincte (séparation des tâches, §39).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class PayrollController(
    IPayrollService payroll,
    IPayrollRuleService rules,
    IPayrollAdjustmentService adjustments) : ApiControllerBase
{
    // --- Périodes (§9) -----------------------------------------------------------------------------

    [HttpGet("payroll/periods")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Periods([FromQuery] int? year, [FromQuery] string? status, CancellationToken ct)
        => Ok(await payroll.PeriodsAsync(year, status, ct));

    [HttpPost("payroll/periods")]
    [RequirePermission(Permissions.Payroll.Calculate)]
    public async Task<IActionResult> CreatePeriod([FromBody] CreatePeriodRequest r, CancellationToken ct)
        => FromResult(await payroll.CreatePeriodAsync(r, ct));

    [HttpGet("payroll/periods/{id:guid}")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Period(Guid id, CancellationToken ct) => FromResult(await payroll.PeriodAsync(id, ct));

    [HttpPost("payroll/periods/{id:guid}/calculate")]
    [RequirePermission(Permissions.Payroll.Calculate)]
    public async Task<IActionResult> Calculate(Guid id, CancellationToken ct) => FromResult(await payroll.CalculateAsync(id, ct));

    // --- Paie et lignes (§10, §11, §23) --------------------------------------------------------------

    [HttpGet("payroll/{id:guid}")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Run(Guid id, CancellationToken ct) => FromResult(await payroll.RunAsync(id, ct));

    [HttpGet("payroll/items/{id:guid}")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Item(Guid id, CancellationToken ct) => FromResult(await payroll.ItemAsync(id, ct));

    [HttpPost("payroll/items/{id:guid}/exclude")]
    [RequirePermission(Permissions.Payroll.Calculate)]
    public async Task<IActionResult> Exclude(Guid id, [FromBody] ExcludeItemRequest r, CancellationToken ct)
        => FromResult(await payroll.ExcludeItemAsync(id, r, ct));

    [HttpPost("payroll/items/{id:guid}/include")]
    [RequirePermission(Permissions.Payroll.Calculate)]
    public async Task<IActionResult> Include(Guid id, CancellationToken ct) => FromResult(await payroll.IncludeItemAsync(id, ct));

    // --- Workflow (§22) -------------------------------------------------------------------------------

    [HttpPost("payroll/{id:guid}/review")]
    [RequirePermission(Permissions.Payroll.Review)]
    public async Task<IActionResult> Review(Guid id, [FromBody] WorkflowNoteRequest r, CancellationToken ct)
        => FromResult(await payroll.ReviewAsync(id, r, ct));

    [HttpPost("payroll/{id:guid}/validate")]
    [RequirePermission(Permissions.Payroll.Validate)]
    public async Task<IActionResult> Validate(Guid id, [FromBody] WorkflowNoteRequest r, CancellationToken ct)
        => FromResult(await payroll.ValidateAsync(id, r, ct));

    [HttpPost("payroll/{id:guid}/approve")]
    [RequirePermission(Permissions.Payroll.Approve)]
    public async Task<IActionResult> Approve(Guid id, [FromBody] WorkflowNoteRequest r, CancellationToken ct)
        => FromResult(await payroll.ApproveAsync(id, r, ct));

    /// <summary>Renvoi en correction (contrôleur, valideur ou approbateur — vérifié par le service).</summary>
    [HttpPost("payroll/{id:guid}/return")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Return(Guid id, [FromBody] WorkflowNoteRequest r, CancellationToken ct)
        => FromResult(await payroll.ReturnAsync(id, r, ct));

    [HttpPost("payroll/{id:guid}/cancel")]
    [RequirePermission(Permissions.Payroll.Calculate)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await payroll.CancelAsync(id, r, ct));

    [HttpPost("payroll/{id:guid}/close")]
    [RequirePermission(Permissions.Payroll.Close)]
    public async Task<IActionResult> Close(Guid id, [FromBody] WorkflowNoteRequest r, CancellationToken ct)
        => FromResult(await payroll.CloseAsync(id, r, ct));

    // --- Paiements (§24, §39) ---------------------------------------------------------------------------

    [HttpGet("payroll/payment-accounts")]
    [RequirePermission(Permissions.Payroll.Pay)]
    public async Task<IActionResult> PaymentAccounts(CancellationToken ct) => Ok(await payroll.PaymentAccountsAsync(ct));

    [HttpPost("payroll/{id:guid}/pay")]
    [RequirePermission(Permissions.Payroll.Pay)]
    [Idempotent]
    public async Task<IActionResult> Pay(Guid id, [FromBody] PayRunRequest r, CancellationToken ct) => FromResult(await payroll.PayAsync(id, r, ct));

    [HttpGet("payroll/{id:guid}/payments")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Payments(Guid id, CancellationToken ct) => FromResult(await payroll.PaymentsAsync(id, ct));

    [HttpPost("salary-payments/{id:guid}/cancel")]
    [RequirePermission(Permissions.Payroll.Pay)]
    [Idempotent]
    public async Task<IActionResult> CancelPayment(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await payroll.CancelPaymentAsync(id, r, ct));

    // --- Bulletins (§25) --------------------------------------------------------------------------------

    [HttpGet("payroll/{id:guid}/payslips")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Payslips(Guid id, CancellationToken ct) => FromResult(await payroll.PayslipsForRunAsync(id, ct));

    [HttpPost("payroll/{id:guid}/payslips")]
    [RequirePermission(Permissions.Payroll.Pay)]
    public async Task<IActionResult> GeneratePayslips(Guid id, CancellationToken ct) => FromResult(await payroll.GeneratePayslipsAsync(id, ct));

    [HttpGet("payslips/{id:guid}/file")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> PayslipFile(Guid id, [FromQuery] string? format, CancellationToken ct)
    {
        var file = await payroll.PayslipFileAsync(id, format ?? "pdf", ct);
        if (!file.IsSuccess) return FromResult(file);
        return Download(file.Value!.Content, file.Value.ContentType, file.Value.FileName);
    }

    [HttpGet("payslips/{id:guid}/verify")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> VerifyPayslip(Guid id, CancellationToken ct) => FromResult(await payroll.VerifyPayslipAsync(id, ct));

    // --- Rapprochement RH ↔ Finance (§32) ---------------------------------------------------------------

    [HttpGet("payroll/{id:guid}/reconciliation")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Reconciliation(Guid id, CancellationToken ct) => FromResult(await payroll.ReconcileAsync(id, ct));

    // --- Composants et règles légales (§11, §20, §31) ---------------------------------------------------

    [HttpGet("payroll/components")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Components(CancellationToken ct) => Ok(await rules.ComponentsAsync(ct));

    [HttpPost("payroll/components")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> CreateComponent([FromBody] SavePayComponentRequest r, CancellationToken ct)
        => FromResult(await rules.SaveComponentAsync(null, r, ct));

    [HttpPut("payroll/components/{id:guid}")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> UpdateComponent(Guid id, [FromBody] SavePayComponentRequest r, CancellationToken ct)
        => FromResult(await rules.SaveComponentAsync(id, r, ct));

    [HttpGet("payroll/rules")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> Rules([FromQuery] bool includeTemplates = true, CancellationToken ct = default)
        => Ok(await rules.RulesAsync(includeTemplates, ct));

    [HttpPost("payroll/rules")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> CreateRule([FromBody] SavePayrollRuleRequest r, CancellationToken ct) => FromResult(await rules.CreateRuleAsync(r, ct));

    [HttpPut("payroll/rules/{id:guid}")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> UpdateRule(Guid id, [FromBody] SavePayrollRuleRequest r, CancellationToken ct)
        => FromResult(await rules.UpdateDraftAsync(id, r, ct));

    [HttpPost("payroll/rules/{id:guid}/versions")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> NewRuleVersion(Guid id, [FromBody] SavePayrollRuleRequest r, CancellationToken ct)
        => FromResult(await rules.NewVersionAsync(id, r, ct));

    [HttpPost("payroll/rules/{id:guid}/validate")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> ValidateRule(Guid id, [FromBody] ValidateRuleRequest r, CancellationToken ct)
        => FromResult(await rules.ValidateAsync(id, r, ct));

    [HttpPost("payroll/rules/{id:guid}/retire")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> RetireRule(Guid id, [FromBody] RetireRuleRequest r, CancellationToken ct)
        => FromResult(await rules.RetireAsync(id, r, ct));

    [HttpPost("payroll/rule-templates/{id:guid}/adopt")]
    [RequirePermission(Permissions.Payroll.Configure)]
    public async Task<IActionResult> AdoptTemplate(Guid id, CancellationToken ct) => FromResult(await rules.AdoptTemplateAsync(id, ct));

    // --- Régularisations (§36) --------------------------------------------------------------------------

    [HttpGet("payroll/adjustments")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Adjustments([FromQuery] Guid? employeeId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await adjustments.ListAsync(employeeId, status, ct));

    [HttpPost("payroll/adjustments")]
    [RequirePermission(Permissions.Payroll.Adjust)]
    public async Task<IActionResult> RequestAdjustment([FromBody] RequestAdjustmentRequest r, CancellationToken ct)
        => FromResult(await adjustments.RequestAsync(r, ct));

    [HttpPost("payroll/adjustments/{id:guid}/approve")]
    [RequirePermission(Permissions.Payroll.ApproveAdjustment)]
    public async Task<IActionResult> ApproveAdjustment(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await adjustments.ApproveAsync(id, r, ct));

    [HttpPost("payroll/adjustments/{id:guid}/reject")]
    [RequirePermission(Permissions.Payroll.ApproveAdjustment)]
    public async Task<IActionResult> RejectAdjustment(Guid id, [FromBody] DecisionRequest r, CancellationToken ct)
        => FromResult(await adjustments.RejectAsync(id, r, ct));

    [HttpPost("payroll/adjustments/{id:guid}/cancel")]
    [RequirePermission(Permissions.Payroll.Adjust)]
    public async Task<IActionResult> CancelAdjustment(Guid id, [FromBody] CancelRequest r, CancellationToken ct)
        => FromResult(await adjustments.CancelAsync(id, r, ct));
}
