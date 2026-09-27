using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Parties;
using Nexora.Application.Customers;
using Nexora.Application.Purchasing;
using Nexora.Application.Security;
using Nexora.Contracts.Parties;
using Nexora.Contracts.Purchasing;

namespace Nexora.Api.Controllers;

/// <summary>
/// Cycle d'achat (Phase 5 §21-27, Phase 26 §9-20). Écart de nommage vs §46 : préfixe
/// <c>/purchasing/</c> (cohérence modules) — <c>/purchasing/orders</c>, <c>/purchasing/invoices</c>…
/// </summary>
[ApiVersion("1.0")]
public sealed class PurchasingController(PurchasingService purchasing, IPurchasingQueries queries) : ApiControllerBase
{
    [HttpGet("orders")]
    [RequirePermission(Permissions.Purchasing.View)]
    public async Task<IActionResult> ListOrders([FromQuery] string? status, [FromQuery] int limit = 50, CancellationToken ct = default)
        => Ok(await queries.ListPurchaseOrdersAsync(status, limit, ct));

    [HttpPost("orders")]
    [RequirePermission(Permissions.Purchasing.Order)]
    public async Task<IActionResult> CreateOrder([FromBody] CreatePurchaseOrderCommand c, CancellationToken ct)
        => FromResult(await purchasing.CreateOrderAsync(c, ct));

    [HttpGet("orders/{id:guid}")]
    [RequirePermission(Permissions.Purchasing.View)]
    public async Task<IActionResult> GetOrder(Guid id, CancellationToken ct)
    {
        var o = await queries.GetPurchaseOrderAsync(id, ct);
        return o is null ? NotFound() : Ok(o);
    }

    [HttpPost("orders/{id:guid}/submit")]
    [RequirePermission(Permissions.Purchasing.Order)]
    public async Task<IActionResult> SubmitOrder(Guid id, CancellationToken ct)
        => FromResult(await purchasing.SubmitOrderAsync(id, ct));

    [HttpPost("orders/{id:guid}/approve")]
    [RequirePermission(Permissions.Purchasing.ApproveOrder)]
    public async Task<IActionResult> ApproveOrder(Guid id, CancellationToken ct)
        => FromResult(await purchasing.ApproveOrderAsync(id, ct));

    [HttpPost("orders/{id:guid}/send")]
    [RequirePermission(Permissions.Purchasing.Order)]
    public async Task<IActionResult> SendOrder(Guid id, CancellationToken ct)
        => FromResult(await purchasing.SendOrderAsync(id, ct));

    [HttpPost("orders/{id:guid}/cancel")]
    [RequirePermission(Permissions.Purchasing.Order)]
    public async Task<IActionResult> CancelOrder(Guid id, CancellationToken ct)
        => FromResult(await purchasing.CancelOrderAsync(id, ct));

    [HttpPost("receipts")]
    [RequirePermission(Permissions.Purchasing.Receive)]
    [Idempotent]
    public async Task<IActionResult> Receive([FromBody] ReceiveGoodsCommand c, CancellationToken ct)
        => FromResult(await purchasing.ReceiveAsync(c, ct));

    [HttpPost("invoices")]
    [RequirePermission(Permissions.Purchasing.Invoice)]
    [Idempotent]
    public async Task<IActionResult> RecordInvoice([FromBody] RecordPurchaseInvoiceCommand c, CancellationToken ct)
        => FromResult(await purchasing.RecordInvoiceAsync(c, ct));

    [HttpPost("invoices/{id:guid}/match")]
    [RequirePermission(Permissions.Purchasing.Match)]
    public async Task<IActionResult> MatchInvoice(Guid id, [FromBody] MatchInvoiceRequest r, CancellationToken ct)
        => FromResult(await purchasing.MatchInvoiceAsync(id, r, ct));

    [HttpPost("returns")]
    [RequirePermission(Permissions.Purchasing.Return)]
    [Idempotent]
    public async Task<IActionResult> RecordReturn([FromBody] RecordSupplierReturnCommand c, CancellationToken ct)
        => FromResult(await purchasing.RecordReturnAsync(c, ct));
}

/// <summary>Dettes fournisseurs & relevés (Phase 26 §24-27, §36, §46).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/payables")]
public sealed class PayablesController(
    PurchasingService purchasing, IPurchasingQueries queries, ICustomerAccountQueries accounts) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.ViewPayables)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? supplierId, [FromQuery] bool onlyOpen = true, [FromQuery] int limit = 100, CancellationToken ct = default)
        => Ok(await queries.ListPayablesAsync(supplierId, onlyOpen, limit, ct));

    /// <summary>Comptes utilisables pour payer une dette (caisses, banques…) — nom, type et devise, sans
    /// aucun solde de trésorerie.</summary>
    [HttpGet("payment-accounts")]
    [RequirePermission(Permissions.Purchasing.Pay)]
    public async Task<IActionResult> PaymentAccounts(CancellationToken ct)
        => Ok((await accounts.PaymentOptionsAsync(ct)).Accounts);

    [HttpPost("{id:guid}/payments")]
    [RequirePermission(Permissions.Purchasing.Pay)]
    [Idempotent]
    public async Task<IActionResult> Pay(Guid id, [FromBody] PayPayableRequest r, CancellationToken ct)
        => FromResult(await purchasing.PayPayableAsync(id, r, ct));
}

/// <summary>Fournisseurs — fiche, lecture & relevé (Phase 26 §4, §36, §46 ; création/modification
/// ajoutées par la recette Phase 55 ACH-001).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/suppliers")]
public sealed class SuppliersController(IPurchasingQueries queries, ISupplierService suppliers) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Purchasing.View)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await queries.ListSuppliersAsync(ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Suppliers.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await suppliers.GetAsync(id, ct) is { } s ? Ok(s) : NotFound();

    [HttpPost]
    [RequirePermission(Permissions.Suppliers.Create)]
    public async Task<IActionResult> Create([FromBody] CreateSupplierRequest r, CancellationToken ct)
        => FromResult(await suppliers.CreateAsync(r, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Suppliers.Update)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateSupplierRequest r, CancellationToken ct)
        => FromResult(await suppliers.UpdateAsync(id, r, ct));

    [HttpGet("{id:guid}/statement")]
    [RequirePermission(Permissions.Purchasing.ViewPayables)]
    public async Task<IActionResult> Statement(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var s = await queries.SupplierStatementAsync(id, from, to, ct);
        return s is null ? NotFound() : Ok(s);
    }
}
