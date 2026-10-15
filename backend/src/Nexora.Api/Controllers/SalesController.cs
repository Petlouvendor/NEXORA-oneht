using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Ventes (Phase 25 §43). Routes canoniques <c>/sales</c> ; la caisse tactile continue
/// d'utiliser <c>/pos/*</c>. Une vente reste enregistrée une seule fois (idempotence).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sales")]
public sealed class SalesController(
    SalesService sales, SalesReturnService returns, ISalesQueries queries,
    Nexora.Application.Customers.ICustomerAccountService accounts) : ApiControllerBase
{
    [HttpPost]
    [RequirePermission(Permissions.Pos.Sell)]
    [Idempotent]
    public async Task<IActionResult> Create([FromBody] CheckoutCommand cmd, CancellationToken ct)
        // Datée par le serveur, comme /pos/checkout (audit 2026-10, A-003).
        => FromResult(await sales.CheckoutAsync(cmd with { SoldAt = null }, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var s = await queries.GetSaleAsync(id, ct);
        return s is null ? NotFound() : Ok(s);
    }

    /// <summary>
    /// Règlement d'une vente à crédit. Passe par le service des comptes clients (audit 2026-10, A-014) : même droit
    /// (<c>credit.receive_payment</c>), mêmes contrôles (session de caisse ouverte, devise du compte, mouvement de
    /// caisse pour des espèces) qu'un versement saisi sur la fiche client. Avant, <c>pos.sell</c> suffisait et les
    /// espèces n'entraient pas dans le tiroir de la session.
    /// </summary>
    [HttpPost("{id:guid}/payments")]
    [RequirePermission(Permissions.Credit.ReceivePayment)]
    [Idempotent]
    public async Task<IActionResult> Pay(Guid id, [FromBody] SalePaymentBody body, CancellationToken ct)
    {
        var (customerId, receivableId, currency) = await queries.SaleSettlementTargetAsync(id, ct);
        if (customerId is not { } customer)
            throw new DomainException(Error.Validation("sales.no_customer", "Règlement possible seulement sur une vente client à crédit."));
        if (receivableId is not { } receivable)
            throw new DomainException(Error.Conflict("sales.no_open_receivable", "Aucune créance ouverte pour cette vente."));

        return FromResult(await accounts.RecordPaymentAsync(customer, new Nexora.Contracts.Customers.RecordCustomerPaymentRequest(
            body.OperationUuid, body.Amount, body.TenderFinancialAccountId, body.PaymentMethodId, body.CashSessionId, body.Reference,
            null, [new Nexora.Contracts.Customers.PaymentAllocationInput(receivable, body.Amount)], currency), ct));
    }

    /// <summary>Retour exécuté immédiatement — réservé à <c>pos.refund</c> (guide §10) ; le caissier
    /// passe par <c>POST /pos/return-requests</c>.</summary>
    [HttpPost("{id:guid}/returns")]
    [RequirePermission(Permissions.Pos.Refund)]
    [Idempotent]
    public async Task<IActionResult> Return(Guid id, [FromBody] ReturnCommand cmd, CancellationToken ct)
        => FromResult(await returns.ReturnAsync(cmd with { SaleId = id }, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Pos.VoidSale)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] ReasonBody body, CancellationToken ct)
        => FromResult(await sales.VoidAsync(id, body.Reason, ct));

    public sealed record SalePaymentBody(Guid OperationUuid, decimal Amount, Guid TenderFinancialAccountId, string? Reference,
        Guid? PaymentMethodId = null, Guid? CashSessionId = null);
    public sealed record ReasonBody(string Reason);
}
