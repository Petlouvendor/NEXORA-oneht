using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance.Ledger;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>
/// Écritures comptables manuelles (Phase 29 §9-14, §51). Brouillon → contrôles §47 → validation
/// → contrepassation. Le moteur événementiel (Phase 4) produit les écritures automatiques.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/accounting-entries")]
public sealed class AccountingEntriesController(IManualJournalService entries) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => Ok(await entries.ListAsync(status, from, to, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Accounting.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => FromResult(await entries.GetAsync(id, ct));

    [HttpPost]
    [RequirePermission(Permissions.Accounting.Create)]
    public async Task<IActionResult> CreateDraft([FromBody] CreateJournalEntryRequest r, CancellationToken ct)
        => FromResult(await entries.CreateDraftAsync(r, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Accounting.Create)]
    public async Task<IActionResult> UpdateDraft(Guid id, [FromBody] CreateJournalEntryRequest r, CancellationToken ct)
        => FromResult(await entries.UpdateDraftAsync(id, r, ct));

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.Accounting.Create)]
    public async Task<IActionResult> DeleteDraft(Guid id, CancellationToken ct)
        => FromResult(await entries.DeleteDraftAsync(id, ct));

    [HttpPost("{id:guid}/post")]
    [RequirePermission(Permissions.Accounting.Post)]
    [Idempotent]
    public async Task<IActionResult> Post(Guid id, CancellationToken ct)
        => FromResult(await entries.PostAsync(id, ct));

    [HttpPost("{id:guid}/reverse")]
    [RequirePermission(Permissions.Accounting.Reverse)]
    public async Task<IActionResult> Reverse(Guid id, [FromBody] ReverseJournalEntryRequest r, CancellationToken ct)
        => FromResult(await entries.ReverseAsync(id, r, ct));
}
