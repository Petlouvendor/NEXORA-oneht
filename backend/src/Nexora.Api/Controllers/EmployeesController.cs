using Nexora.Application.Abstractions;
using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;

namespace Nexora.Api.Controllers;

/// <summary>
/// Dossier employé, départements, contrats, rémunération, éléments récurrents, documents RH et avances d'un employé
/// (docs/module-paie-rh-avances.md §7, §8, §12, §29). Chaque route exige sa permission ; le service borne ensuite la
/// lecture au périmètre de l'appelant (équipe d'un Manager) et masque les salaires sans <c>payroll.view_salaries</c> —
/// un identifiant hors périmètre répond 404, jamais la donnée (§5).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class EmployeesController(
    IEmployeeService employees,
    IPayrollService payroll,
    ISalaryAdvanceService advances,
    IPayrollReports reports) : ApiControllerBase
{
    // --- Départements ----------------------------------------------------------------------------

    [HttpGet("departments")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Departments(CancellationToken ct) => Ok(await employees.DepartmentsAsync(ct));

    [HttpPost("departments")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> CreateDepartment([FromBody] SaveDepartmentRequest r, CancellationToken ct)
        => FromResult(await employees.SaveDepartmentAsync(null, r, ct));

    [HttpPut("departments/{id:guid}")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> UpdateDepartment(Guid id, [FromBody] SaveDepartmentRequest r, CancellationToken ct)
        => FromResult(await employees.SaveDepartmentAsync(id, r, ct));

    // --- Employés (§7) ---------------------------------------------------------------------------

    [HttpGet("employees")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? status, [FromQuery] Guid? departmentId, CancellationToken ct)
        => Ok(await employees.ListAsync(search, status, departmentId, ct));

    [HttpPost("employees")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> Create([FromBody] SaveEmployeeRequest r, CancellationToken ct) => FromResult(await employees.CreateAsync(r, ct));

    [HttpGet("employees/{id:guid}")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await employees.GetAsync(id, ct) is { } e ? Ok(e) : NotFound();

    [HttpPut("employees/{id:guid}")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveEmployeeRequest r, CancellationToken ct)
        => FromResult(await employees.UpdateAsync(id, r, ct));

    [HttpPost("employees/{id:guid}/status")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] EmployeeStatusRequest r, CancellationToken ct)
        => FromResult(await employees.SetStatusAsync(id, r, ct));

    [HttpPost("employees/{id:guid}/user")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> LinkUser(Guid id, [FromBody] LinkEmployeeUserRequest r, CancellationToken ct)
        => FromResult(await employees.LinkUserAsync(id, r, ct));

    /// <summary>Identifiants bancaires et fiscaux en clair — chaque lecture est journalisée (§35).</summary>
    [HttpGet("employees/{id:guid}/sensitive")]
    [RequireStepUp(StepUpPurposes.SensitiveData)]
    [RequirePermission(Permissions.Payroll.ViewSensitive)]
    public async Task<IActionResult> Sensitive(Guid id, CancellationToken ct) => FromResult(await employees.SensitiveAsync(id, ct));

    // --- Contrats et rémunération (§8) ------------------------------------------------------------

    [HttpGet("employees/{id:guid}/contracts")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Contracts(Guid id, CancellationToken ct) => FromResult(await employees.ContractsAsync(id, ct));

    [HttpPost("employees/{id:guid}/contracts")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> CreateContract(Guid id, [FromBody] CreateContractRequest r, CancellationToken ct)
        => FromResult(await employees.CreateContractAsync(id, r, ct));

    [HttpPost("employee-contracts/{id:guid}/end")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> EndContract(Guid id, [FromBody] EndContractRequest r, CancellationToken ct)
        => FromResult(await employees.EndContractAsync(id, r, ct));

    [HttpGet("employees/{id:guid}/salary-history")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> SalaryHistory(Guid id, CancellationToken ct) => FromResult(await employees.SalaryHistoryAsync(id, ct));

    [HttpPost("employees/{id:guid}/salary-changes")]
    [RequirePermission(Permissions.Payroll.ManageCompensation)]
    public async Task<IActionResult> ChangeSalary(Guid id, [FromBody] ChangeSalaryRequest r, CancellationToken ct)
        => FromResult(await employees.ChangeSalaryAsync(id, r, ct));

    /// <summary>Rémunération applicable à une date, reconstituée depuis l'historique (§8).</summary>
    [HttpGet("employees/{id:guid}/compensation")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Compensation(Guid id, [FromQuery] DateOnly? date, [FromServices] IBusinessCalendar calendar, CancellationToken ct)
        => FromResult(await employees.ApplicableSalaryAsync(id, date ?? await calendar.TodayAsync(ct), ct));

    [HttpGet("employees/{id:guid}/pay-elements")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> PayElements(Guid id, CancellationToken ct) => FromResult(await employees.PayElementsAsync(id, ct));

    [HttpPost("employees/{id:guid}/pay-elements")]
    [RequirePermission(Permissions.Payroll.ManageCompensation)]
    public async Task<IActionResult> AddPayElement(Guid id, [FromBody] SavePayElementRequest r, CancellationToken ct)
        => FromResult(await employees.AddPayElementAsync(id, r, ct));

    [HttpPost("employee-pay-elements/{id:guid}/end")]
    [RequirePermission(Permissions.Payroll.ManageCompensation)]
    public async Task<IActionResult> EndPayElement(Guid id, [FromBody] EndPayElementRequest r, CancellationToken ct)
        => FromResult(await employees.EndPayElementAsync(id, r, ct));

    // --- Documents RH -----------------------------------------------------------------------------

    [HttpGet("employees/{id:guid}/documents")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> Documents(Guid id, CancellationToken ct) => FromResult(await employees.DocumentsAsync(id, ct));

    [HttpPost("employees/{id:guid}/documents")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> UploadDocument(Guid id, [FromBody] UploadEmployeeDocumentRequest r, CancellationToken ct)
        => FromResult(await employees.UploadDocumentAsync(id, r, ct));

    [HttpGet("employee-documents/{id:guid}/content")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> DocumentContent(Guid id, CancellationToken ct)
    {
        var doc = await employees.DocumentContentAsync(id, ct);
        if (!doc.IsSuccess) return FromResult(doc);
        return Download(doc.Value!.Content, doc.Value.ContentType, doc.Value.FileName);
    }

    [HttpPost("employee-documents/{id:guid}/archive")]
    [RequirePermission(Permissions.Hr.ManageEmployees)]
    public async Task<IActionResult> ArchiveDocument(Guid id, CancellationToken ct) => FromResult(await employees.ArchiveDocumentAsync(id, ct));

    // --- Historique, bulletins, avances de l'employé (§26, §29) -------------------------------------

    [HttpGet("employees/{id:guid}/history")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> History(Guid id, CancellationToken ct) => FromResult(await reports.EmployeeHistoryAsync(id, ct));

    [HttpGet("employees/{id:guid}/payslips")]
    [RequirePermission(Permissions.Payroll.ViewSalaries)]
    public async Task<IActionResult> Payslips(Guid id, CancellationToken ct) => FromResult(await payroll.PayslipsForEmployeeAsync(id, ct));

    [HttpGet("employees/{id:guid}/salary-advances")]
    [RequirePermission(Permissions.Hr.ViewEmployees)]
    public async Task<IActionResult> EmployeeAdvances(Guid id, [FromQuery] string? status, CancellationToken ct)
        => Ok(await advances.ListAsync(id, status, null, ct));

    [HttpPost("employees/{id:guid}/salary-advances/preview")]
    [RequirePermission(Permissions.Payroll.AdvanceRequest)]
    public async Task<IActionResult> PreviewAdvance(Guid id, [FromBody] RequestAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.PreviewAsync(id, r, ct));

    [HttpPost("employees/{id:guid}/salary-advances")]
    [RequirePermission(Permissions.Payroll.AdvanceRequest)]
    public async Task<IActionResult> RequestAdvance(Guid id, [FromBody] RequestAdvanceRequest r, CancellationToken ct)
        => FromResult(await advances.RequestAsync(id, r, ct));
}
