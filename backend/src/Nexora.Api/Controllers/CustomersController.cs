using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Parties;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Parties;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Fiche client et compte client (recette Phase 55 CLI-001/002 ; module « Gestion clients & comptes
/// clients » §6-§17, §22). Le détail financier est réservé à <c>credit.view_portfolio</c> (§18 « Voir détail
/// financier global » : caissier non) ; au POS, la recherche <c>/customers/lookup</c> n'expose que le
/// nécessaire (§17, §19). Promesses, litiges et blocages restent sous <c>/credit/*</c> (Phase 27).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/customers")]
public sealed class CustomersController(
    ICustomerService customers,
    ICustomerAccountQueries account,
    ICustomerAccountService payments,
    ICustomerExpenseService expenses,
    ICustomerRefundService refunds,
    ICollectionActionService collectionActions,
    IPermissionService permissions,
    ITenantContext tenant) : ApiControllerBase
{
    // --- Fiche (§6, §17, §29) -----------------------------------------------------------------

    [HttpGet]
    [RequirePermission(Permissions.Customers.View)]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? status,
        [FromQuery] int limit = 100, [FromQuery] int offset = 0, CancellationToken ct = default)
        => Ok(await customers.ListAsync(q, status, limit, offset, ct));

    /// <summary>Recherche progressive POS (§17) : 2 caractères minimum, 20 résultats au plus, champs utiles seulement.</summary>
    [HttpGet("lookup")]
    [RequirePermission(Permissions.Customers.View)]
    public async Task<IActionResult> Lookup([FromQuery] string q, [FromQuery] int limit = 10, CancellationToken ct = default)
        => Ok(await account.LookupAsync(q, limit, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Customers.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
        => await customers.GetAsync(id, ct) is { } c ? Ok(c) : NotFound();

    [HttpPost]
    [RequirePermission(Permissions.Customers.Create)]
    public async Task<IActionResult> Create([FromBody] CreateCustomerRequest r, CancellationToken ct)
        => FromResult(await customers.CreateAsync(r, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Customers.Update)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCustomerRequest r, CancellationToken ct)
        => FromResult(await customers.UpdateAsync(id, r, ct));

    [HttpPatch("{id:guid}")]
    [RequirePermission(Permissions.Customers.Update)]
    public async Task<IActionResult> Patch(Guid id, [FromBody] PatchCustomerRequest r, CancellationToken ct)
        => FromResult(await customers.PatchAsync(id, r, ct));

    [HttpPost("{id:guid}/status")]
    [RequirePermission(Permissions.Customers.Update)]
    public async Task<IActionResult> ChangeStatus(Guid id, [FromBody] ChangeCustomerStatusRequest r, CancellationToken ct)
        => FromResult(await customers.ChangeStatusAsync(id, r, ct));

    // --- Compte client (§7, §8, §14, §15) ------------------------------------------------------

    [HttpGet("{id:guid}/account")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Account(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => await account.SummaryAsync(id, from, to, ct) is { } s ? Ok(s) : NotFound();

    /// <summary>Journal d'une devise (défaut : celle du client) — deux devises ne se cumulent jamais (§21).</summary>
    [HttpGet("{id:guid}/ledger")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Ledger(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] string? currency, CancellationToken ct)
        => await account.LedgerAsync(id, from, to, currency, ct) is { } l ? Ok(l) : NotFound();

    /// <summary>Crédits du client : portefeuille, ou encaissement autorisé (le caissier habilité doit voir
    /// ce qu'il encaisse, §18 « Enregistrer versement : si autorisé »).</summary>
    [HttpGet("{id:guid}/credits")]
    [RequirePermission(Permissions.Customers.View)]
    public async Task<IActionResult> Credits(Guid id, [FromQuery] string? status, CancellationToken ct)
    {
        await RequireAnyAsync(ct, Permissions.Credit.ViewPortfolio, Permissions.Credit.ReceivePayment);
        return Ok(await account.CreditsAsync(id, status, ct));
    }

    /// <summary>§22 : crédit hors vente (reprise de solde), motivé ; contrepartie en compte d'attente.</summary>
    [HttpPost("{id:guid}/credits")]
    [RequirePermission(Permissions.Credit.Create)]
    [Idempotent]
    public async Task<IActionResult> OpenCredit(Guid id, [FromBody] OpenCustomerCreditRequest r, CancellationToken ct)
        => FromResult(await payments.OpenCreditAsync(id, r, ct));

    [HttpGet("{id:guid}/payments")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Payments(Guid id, CancellationToken ct) => Ok(await account.PaymentsAsync(id, null, ct));

    /// <summary>Versement sur le compte (§9) : réparti sur les crédits indiqués, sinon les plus anciens.</summary>
    [HttpPost("{id:guid}/payments")]
    [RequirePermission(Permissions.Credit.ReceivePayment)]
    [Idempotent]
    public async Task<IActionResult> RecordPayment(Guid id, [FromBody] RecordCustomerPaymentRequest r, CancellationToken ct)
        => FromResult(await payments.RecordPaymentAsync(id, r, ct));

    [HttpGet("{id:guid}/purchases")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Purchases(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => Ok(await account.PurchasesAsync(id, from, to, ct));

    [HttpGet("{id:guid}/expenses")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Expenses(Guid id, [FromQuery] string? status, CancellationToken ct)
        => Ok(await expenses.ListAsync(id, status, ct));

    [HttpPost("{id:guid}/expenses")]
    [RequirePermission(Permissions.Customers.CreateExpense)]
    [Idempotent]
    public async Task<IActionResult> CreateExpense(Guid id, [FromBody] CreateCustomerExpenseRequest r, CancellationToken ct)
        => FromResult(await expenses.CreateAsync(id, r, ct));

    [HttpGet("{id:guid}/credit-memos")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> CreditMemos(Guid id, [FromQuery] bool onlyAvailable = false, CancellationToken ct = default)
        => Ok(await account.CreditMemosAsync(id, onlyAvailable, ct));

    [HttpGet("{id:guid}/refunds")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Refunds(Guid id, [FromQuery] string? status, CancellationToken ct)
        => Ok(await refunds.ListAsync(id, status, ct));

    [HttpGet("{id:guid}/timeline")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Timeline(Guid id, [FromQuery] int limit = 200, CancellationToken ct = default)
        => Ok(await account.TimelineAsync(id, limit, ct));

    // --- Recouvrement (§13) -------------------------------------------------------------------

    [HttpGet("{id:guid}/collection-actions")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> CollectionActions(Guid id, CancellationToken ct)
        => Ok(await collectionActions.ListAsync(id, onlyDue: false, ct));

    [HttpPost("{id:guid}/collection-actions")]
    [RequirePermission(Permissions.Credit.Collect)]
    public async Task<IActionResult> LogCollectionAction(Guid id, [FromBody] CreateCollectionActionRequest r, CancellationToken ct)
        => FromResult(await collectionActions.LogAsync(id, r, ct));

    private async Task RequireAnyAsync(CancellationToken ct, params string[] codes)
    {
        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        if (!codes.Any(granted.Contains))
            throw new DomainException(Error.Forbidden("auth.forbidden", $"Permission requise : {string.Join(" ou ", codes)}."));
    }
}
