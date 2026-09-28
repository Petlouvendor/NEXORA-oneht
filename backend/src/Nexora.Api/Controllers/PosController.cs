using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Security;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Sales;

namespace Nexora.Api.Controllers;

/// <summary>
/// POS (Phase 6). Le POS déclenche les services métier ; il ne crée pas directement
/// les écritures comptables (§39). Toute vente est enregistrée une seule fois.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/pos")]
public sealed class PosController(
    SalesService sales,
    SalesReturnService returns,
    CashSessionService sessions,
    IReturnRequestService returnRequests,
    IDiscountRequestService discountRequests,
    ICashSessionQueries sessionQueries,
    IPosCatalogQueries products,
    IManagerOverrideService managerOverride,
    ITenderOptionsQuery tenderOptions) : ApiControllerBase
{
    /// <summary>Moyens d'encaissement (espèces, carte, mobile money…) et comptes compatibles, sans solde (phase 5).</summary>
    [HttpGet("tender-options")]
    [RequirePermission(Permissions.Pos.Sell)]
    public async Task<IActionResult> TenderOptions([FromQuery] string currency = "HTG", CancellationToken ct = default)
        => Ok(await tenderOptions.ListAsync(currency, ct));

    /// <summary>
    /// Validation sur place (guide §10, §12, §13) : le responsable saisit ses identifiants au poste du
    /// caissier. Le poste n'a besoin que de <c>pos.sell</c> ; c'est le responsable dont on vérifie
    /// l'identité et la permission. Débit limité comme la connexion (essais de mot de passe).
    /// </summary>
    [HttpPost("manager-override")]
    [RequirePermission(Permissions.Pos.Sell)]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> ManagerOverride([FromBody] ManagerOverrideRequest request, CancellationToken ct)
        => FromResult(await managerOverride.ExecuteAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct));

    // --- Recherche produit (guide §9, §24) : liste courte, produits vendables, sans coût -----

    [HttpGet("products/search")]
    [RequirePermission(Permissions.Pos.Sell)]
    public async Task<IActionResult> SearchProducts([FromQuery] string? q, [FromQuery] Guid? locationId, CancellationToken ct)
        => Ok(await products.SearchAsync(q ?? "", locationId, ct));

    [HttpGet("products/scan")]
    [RequirePermission(Permissions.Pos.Sell)]
    public async Task<IActionResult> ScanProduct([FromQuery] string code, [FromQuery] Guid? locationId, CancellationToken ct)
        => await products.ScanAsync(code, locationId, ct) is { } hit ? Ok(hit) : NotFound();

    /// <summary>Droits et limites POS de l'utilisateur (adapte l'écran ; l'API reste l'autorité, guide §27).</summary>
    [HttpGet("me/limits")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> MyLimits(CancellationToken ct) => Ok(await discountRequests.MyLimitsAsync(ct));

    // --- Remises exceptionnelles (guide §12) --------------------------------

    [HttpPost("discount-requests")]
    [RequirePermission(Permissions.Pos.Discount)]
    [Idempotent]
    public async Task<IActionResult> RequestDiscount([FromBody] CreateDiscountRequest request, CancellationToken ct)
        => FromResult(await discountRequests.RequestAsync(request, ct));

    /// <summary>Un approbateur voit toutes les demandes ; un vendeur, les siennes.</summary>
    [HttpGet("discount-requests")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> ListDiscountRequests([FromQuery] string? status, CancellationToken ct)
        => Ok(await discountRequests.ListAsync(status, ct));

    [HttpGet("discount-requests/{id:guid}")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> GetDiscountRequest(Guid id, CancellationToken ct)
        => FromResult(await discountRequests.GetAsync(id, ct));

    [HttpPost("discount-requests/{id:guid}/approve")]
    [RequirePermission(Permissions.Pos.ApproveDiscount)]
    public async Task<IActionResult> ApproveDiscount(Guid id, [FromBody] DecideRequest decision, CancellationToken ct)
        => FromResult(await discountRequests.ApproveAsync(id, decision, ct));

    [HttpPost("discount-requests/{id:guid}/reject")]
    [RequirePermission(Permissions.Pos.ApproveDiscount)]
    public async Task<IActionResult> RejectDiscount(Guid id, [FromBody] DecideRequest decision, CancellationToken ct)
        => FromResult(await discountRequests.RejectAsync(id, decision, ct));

    [HttpPost("checkout")]
    [RequirePermission(Permissions.Pos.Sell)]
    [Idempotent]
    public async Task<IActionResult> Checkout([FromBody] CheckoutCommand cmd, CancellationToken ct)
        => FromResult(await sales.CheckoutAsync(cmd, ct));

    [HttpPost("park")]
    [RequirePermission(Permissions.Pos.Sell)]
    public async Task<IActionResult> Park([FromBody] ParkCartCommand cmd, CancellationToken ct)
        => FromResult(await sales.ParkAsync(cmd, ct));

    /// <summary>Annule un panier mis en attente : par son auteur (guide §11), ou par un détenteur de
    /// <c>pos.void_sale</c>. Une vente encaissée ne s'annule pas : elle passe par un retour.</summary>
    [HttpPost("sales/{id:guid}/void")]
    [RequirePermission(Permissions.Pos.Sell)]
    public async Task<IActionResult> Void(Guid id, [FromBody] ReasonBody body, CancellationToken ct)
        => FromResult(await sales.VoidAsync(id, body.Reason, ct));

    /// <summary>Retour exécuté immédiatement — réservé à qui peut rembourser (Manager, guide §8, §10).
    /// Le caissier passe par une demande de retour (<c>return-requests</c>).</summary>
    [HttpPost("returns")]
    [RequirePermission(Permissions.Pos.Refund)]
    [Idempotent]
    public async Task<IActionResult> Return([FromBody] ReturnCommand cmd, CancellationToken ct)
        => FromResult(await returns.ReturnAsync(cmd, ct));

    // --- Retours avec validation Manager (guide §10) -----------------------

    /// <summary>Vente d'origine retrouvée par son numéro de ticket, avec les quantités retournables.</summary>
    [HttpGet("sales/lookup")]
    [RequirePermission(Permissions.Pos.Return)]
    public async Task<IActionResult> LookupSale([FromQuery] string number, CancellationToken ct)
        => await returnRequests.FindSaleForReturnAsync(number, ct) is { } sale ? Ok(sale) : NotFound();

    [HttpPost("return-requests")]
    [RequirePermission(Permissions.Pos.Return)]
    [Idempotent]
    public async Task<IActionResult> RequestReturn([FromBody] CreateReturnRequest request, CancellationToken ct)
        => FromResult(await returnRequests.RequestAsync(request, ct));

    /// <summary>Un approbateur voit toutes les demandes ; un caissier, les siennes.</summary>
    [HttpGet("return-requests")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> ListReturnRequests([FromQuery] string? status, CancellationToken ct)
        => Ok(await returnRequests.ListAsync(status, ct));

    [HttpGet("return-requests/{id:guid}")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> GetReturnRequest(Guid id, CancellationToken ct)
        => FromResult(await returnRequests.GetAsync(id, ct));

    [HttpPost("return-requests/{id:guid}/approve")]
    [RequirePermission(Permissions.Pos.Refund)]
    public async Task<IActionResult> ApproveReturn(Guid id, [FromBody] DecideRequest decision, CancellationToken ct)
        => FromResult(await returnRequests.ApproveAsync(id, decision, ct));

    [HttpPost("return-requests/{id:guid}/reject")]
    [RequirePermission(Permissions.Pos.Refund)]
    public async Task<IActionResult> RejectReturn(Guid id, [FromBody] DecideRequest decision, CancellationToken ct)
        => FromResult(await returnRequests.RejectAsync(id, decision, ct));

    [HttpPost("sessions/open")]
    [RequirePermission(Permissions.Pos.OpenSession)]
    public async Task<IActionResult> OpenSession([FromBody] OpenSessionCommand cmd, CancellationToken ct)
        => FromResult(await sessions.OpenAsync(cmd, ct));

    [HttpPost("sessions/cash")]
    [RequirePermission(Permissions.Pos.CashInOut)]
    public async Task<IActionResult> CashInOut([FromBody] CashInOutCommand cmd, CancellationToken ct)
        => FromResult(await sessions.CashInOutAsync(cmd, ct));

    /// <summary>Clôture comptée par le caissier ; au-delà du seuil d'écart, le résultat est
    /// « closing » en attente de validation Manager (guide §13).</summary>
    [HttpPost("sessions/close")]
    [RequirePermission(Permissions.Pos.CloseSession)]
    public async Task<IActionResult> CloseSession([FromBody] CloseSessionCommand cmd, CancellationToken ct)
        => FromResult(await sessions.CloseAsync(cmd, ct));

    /// <summary>Session de caisse ouverte (ou en attente de validation) de l'utilisateur.</summary>
    [HttpGet("sessions/current")]
    [RequirePermission(Permissions.Pos.View)]
    public async Task<IActionResult> CurrentSession(CancellationToken ct)
        => await sessionQueries.CurrentAsync(ct) is { } current ? Ok(current) : NoContent();

    [HttpGet("registers")]
    [RequirePermission(Permissions.Pos.OpenSession)]
    public async Task<IActionResult> Registers(CancellationToken ct) => Ok(await sessionQueries.RegistersAsync(ct));

    /// <summary>Sessions ouvertes des autres caissiers (supervision, guide §13) — à clôturer via
    /// <c>sessions/close</c> quand une caisse a été oubliée ouverte.</summary>
    [HttpGet("sessions/open")]
    [RequirePermission(Permissions.Pos.ManageSessions)]
    public async Task<IActionResult> OpenSessions(CancellationToken ct) => Ok(await sessionQueries.OpenSessionsAsync(ct));

    [HttpGet("sessions/pending-closings")]
    [RequirePermission(Permissions.Pos.ManageSessions)]
    public async Task<IActionResult> PendingClosings(CancellationToken ct) => Ok(await sessionQueries.PendingClosingsAsync(ct));

    [HttpPost("sessions/{id:guid}/validate-close")]
    [RequirePermission(Permissions.Pos.ManageSessions)]
    public async Task<IActionResult> ValidateClose(Guid id, [FromBody] DecideRequest decision, CancellationToken ct)
        => FromResult(await sessions.ValidateCloseAsync(new ValidateCloseCommand(id, decision.Note), ct));

    public sealed record ReasonBody(string Reason);
}
