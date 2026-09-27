using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Migration;
using Nexora.Application.Security;
using Nexora.Contracts.Migration;

namespace Nexora.Api.Controllers;

/// <summary>
/// Migration &amp; importation (Phase 39 §45). Pipeline :
/// <c>create → profile → mapping → validate → simulate → execute → reconcile</c>.
/// <c>simulate</c> n'a aucun impact production (§23) ; <c>cancel</c> défait les créations (§37).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/imports")]
public sealed class ImportsController(IImportService imports) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await imports.ListAsync(ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await imports.GetAsync(id, ct));

    [HttpPost]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Create([FromBody] CreateImportRequest r, CancellationToken ct) => FromResult(await imports.CreateAsync(r, ct));

    [HttpPost("{id:guid}/profile")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Profile(Guid id, CancellationToken ct) => FromResult(await imports.ProfileAsync(id, ct));

    [HttpPost("{id:guid}/mapping")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Mapping(Guid id, [FromBody] SetMappingRequest r, CancellationToken ct) => FromResult(await imports.SetMappingAsync(id, r, ct));

    [HttpPost("{id:guid}/validate")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Validate(Guid id, CancellationToken ct) => FromResult(await imports.ValidateAsync(id, ct));

    [HttpPost("{id:guid}/simulate")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Simulate(Guid id, CancellationToken ct) => FromResult(await imports.SimulateAsync(id, ct));

    [HttpPost("{id:guid}/execute")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Execute(Guid id, CancellationToken ct) => FromResult(await imports.ExecuteAsync(id, ct));

    [HttpPost("{id:guid}/pause")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Pause(Guid id, CancellationToken ct) => FromResult(await imports.PauseAsync(id, ct));

    [HttpPost("{id:guid}/resume")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Resume(Guid id, CancellationToken ct) => FromResult(await imports.ResumeAsync(id, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) => FromResult(await imports.CancelAsync(id, ct));

    [HttpGet("{id:guid}/rows")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Rows(Guid id, [FromQuery] string? status, CancellationToken ct) => Ok(await imports.ListRowsAsync(id, status, ct));

    /// <summary>Lignes en erreur, pour correction / réimport (§26).</summary>
    [HttpGet("{id:guid}/errors")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Errors(Guid id, CancellationToken ct) => Ok(await imports.ListRowsAsync(id, "error,rejected", ct));

    [HttpPost("{id:guid}/rows")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> CorrectRow(Guid id, [FromBody] CorrectRowRequest r, CancellationToken ct) => FromResult(await imports.CorrectRowAsync(id, r, ct));

    [HttpGet("{id:guid}/reconciliation")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Reconciliation(Guid id, CancellationToken ct) => FromResult(await imports.ReconcileAsync(id, ct));

    [HttpGet("{id:guid}/events")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Events(Guid id, CancellationToken ct) => Ok(await imports.ListEventsAsync(id, ct));

    // --- Assistant : champs cibles, mapping suggéré, gabarits (§7, §33) ---

    [HttpGet("target-fields/{kind}")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> TargetFields(string kind, CancellationToken ct) => Ok(await imports.TargetFieldsAsync(kind, ct));

    [HttpPost("suggest-mapping/{kind}")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> SuggestMapping(string kind, [FromBody] IReadOnlyList<string> columns, CancellationToken ct)
        => Ok(await imports.SuggestMappingAsync(kind, columns, ct));

    [HttpGet("~/api/v{version:apiVersion}/import-templates")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Templates([FromQuery] string? kind, CancellationToken ct) => Ok(await imports.ListTemplatesAsync(kind, ct));
}
