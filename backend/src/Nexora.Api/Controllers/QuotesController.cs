using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Documents;

namespace Nexora.Api.Controllers;

/// <summary>Devis (Phase 30 §4-5, §48) : cycle brouillon → envoyé → accepté / refusé → commande.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/quotes")]
public sealed class QuotesController(QuoteService quotes) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? customerId, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await quotes.ListAsync(status, customerId, limit, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Documents.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => FromResult(await quotes.GetAsync(id, ct));

    [HttpPost]
    [RequirePermission(Permissions.Documents.CreateQuote)]
    public async Task<IActionResult> Create([FromBody] CreateQuoteRequest r, CancellationToken ct)
        => FromResult(await quotes.CreateAsync(r, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Documents.CreateQuote)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateQuoteRequest r, CancellationToken ct)
        => FromResult(await quotes.UpdateAsync(id, r, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Documents.CreateQuote)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] QuoteDecisionRequest r, CancellationToken ct)
        => FromResult(await quotes.CancelAsync(id, r?.Reason, ct));

    [HttpPost("{id:guid}/send")]
    [RequirePermission(Permissions.Documents.CreateQuote)]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
        => FromResult(await quotes.SendAsync(id, ct));

    [HttpPost("{id:guid}/accept")]
    [RequirePermission(Permissions.Documents.CreateQuote)]
    public async Task<IActionResult> Accept(Guid id, CancellationToken ct)
        => FromResult(await quotes.AcceptAsync(id, ct));

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(Permissions.Documents.CreateQuote)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] QuoteDecisionRequest r, CancellationToken ct)
        => FromResult(await quotes.RejectAsync(id, r?.Reason, ct));

    [HttpPost("{id:guid}/convert")]
    [RequirePermission(Permissions.Pos.ManageOrder)]
    public async Task<IActionResult> Convert(Guid id, [FromBody] ConvertQuoteRequest r, CancellationToken ct)
        => FromResult(await quotes.ConvertToOrderAsync(id, r ?? new ConvertQuoteRequest(), ct));
}
