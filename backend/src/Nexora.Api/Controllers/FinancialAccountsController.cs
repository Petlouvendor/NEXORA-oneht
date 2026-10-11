using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Treasury;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>Comptes de trésorerie (Phase 28 ; gestion complète en finalisation, phase 7).</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/financial-accounts")]
public sealed class FinancialAccountsController(ITreasuryQueries queries, ITreasuryManagementService treasury) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await queries.AccountsAsync(ct));

    /// <summary>Fiche de gestion : compte comptable, nombre de mouvements, dernier mouvement.</summary>
    [HttpGet("manage")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Manage(CancellationToken ct) => Ok(await treasury.AccountsAsync(ct));

    [HttpPost]
    [RequirePermission(Permissions.Finance.Update)]
    public async Task<IActionResult> Create([FromBody] SaveFinancialAccountRequest r, CancellationToken ct)
        => FromResult(await treasury.CreateAccountAsync(r, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Finance.Update)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveFinancialAccountRequest r, CancellationToken ct)
        => FromResult(await treasury.UpdateAccountAsync(id, r, ct));

    [HttpPost("{id:guid}/status")]
    [RequirePermission(Permissions.Finance.Update)]
    public async Task<IActionResult> Status(Guid id, [FromBody] FinancialAccountStatusRequest r, CancellationToken ct)
        => FromResult(await treasury.SetAccountStatusAsync(id, r, ct));

    [HttpGet("{id:guid}/transactions")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Transactions(Guid id, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int limit = 200, CancellationToken ct = default)
        => FromResult(await treasury.TransactionsAsync(id, from, to, limit, ct));

    /// <summary>Comptes du plan comptable proposés dans les formulaires : asset (trésorerie), expense (charges).</summary>
    [HttpGet("~/api/v{version:apiVersion}/ledger-options")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> LedgerOptions([FromQuery] string type = "asset", CancellationToken ct = default)
        => type is "asset" or "expense" ? Ok(await treasury.LedgerOptionsAsync(type, ct)) : BadRequest();

    /// <summary>Devises et fournisseurs actifs (identifiant, nom) pour les formulaires financiers.</summary>
    [HttpGet("~/api/v{version:apiVersion}/finance-lookups")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Lookups(CancellationToken ct) => Ok(await treasury.LookupsAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/treasury/transfers")]
    [RequirePermission(Permissions.Finance.Transfer)]
    [Idempotent]
    public async Task<IActionResult> Transfer([FromBody] TreasuryTransferRequest r, CancellationToken ct)
        => FromResult(await treasury.TransferAsync(r, ct));

    [HttpPost("~/api/v{version:apiVersion}/treasury/charges")]
    [RequirePermission(Permissions.Finance.Reconcile)]
    [Idempotent]
    public async Task<IActionResult> Charge([FromBody] TreasuryChargeRequest r, CancellationToken ct)
        => FromResult(await treasury.ChargeAsync(r, ct));
}

/// <summary>Dépenses (finalisation, phase 7) : saisie, approbation (quatre yeux), paiement comptabilisé.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/expenses")]
public sealed class ExpensesController(IExpenseService expenses) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to,
        [FromQuery] int limit = 200, CancellationToken ct = default)
        => Ok(await expenses.ListAsync(status, from, to, limit, ct));

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => FromResult(await expenses.GetAsync(id, ct));

    [HttpPost]
    [RequirePermission(Permissions.Finance.Create)]
    public async Task<IActionResult> Create([FromBody] SaveExpenseRequest r, CancellationToken ct) => FromResult(await expenses.CreateAsync(r, ct));

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.Finance.Create)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveExpenseRequest r, CancellationToken ct)
        => FromResult(await expenses.UpdateAsync(id, r, ct));

    [HttpPost("{id:guid}/submit")]
    [RequirePermission(Permissions.Finance.Create)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct) => FromResult(await expenses.SubmitAsync(id, ct));

    [HttpPost("{id:guid}/approve")]
    [RequirePermission(Permissions.Finance.Pay)]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct) => FromResult(await expenses.ApproveAsync(id, ct));

    [HttpPost("{id:guid}/reject")]
    [RequirePermission(Permissions.Finance.Pay)]
    public async Task<IActionResult> Reject(Guid id, [FromBody] ExpenseDecisionRequest r, CancellationToken ct)
        => FromResult(await expenses.RejectAsync(id, r, ct));

    [HttpPost("{id:guid}/cancel")]
    [RequirePermission(Permissions.Finance.Create)]
    public async Task<IActionResult> Cancel(Guid id, [FromBody] ExpenseDecisionRequest r, CancellationToken ct)
        => FromResult(await expenses.CancelAsync(id, r, ct));

    [HttpPost("{id:guid}/pay")]
    [RequireStepUp(StepUpPurposes.Payment)]
    [RequirePermission(Permissions.Finance.Pay)]
    [Idempotent]
    public async Task<IActionResult> Pay(Guid id, [FromBody] PayExpenseRequest r, CancellationToken ct)
        => FromResult(await expenses.PayAsync(id, r, ct));

    [HttpGet("~/api/v{version:apiVersion}/expense-categories")]
    [RequirePermission(Permissions.Finance.View)]
    public async Task<IActionResult> Categories(CancellationToken ct) => Ok(await expenses.CategoriesAsync(ct));

    [HttpPost("~/api/v{version:apiVersion}/expense-categories")]
    [RequirePermission(Permissions.Finance.Update)]
    public async Task<IActionResult> CreateCategory([FromBody] SaveExpenseCategoryRequest r, CancellationToken ct)
        => FromResult(await expenses.CreateCategoryAsync(r, ct));

    [HttpPut("~/api/v{version:apiVersion}/expense-categories/{id:guid}")]
    [RequirePermission(Permissions.Finance.Update)]
    public async Task<IActionResult> UpdateCategory(Guid id, [FromBody] SaveExpenseCategoryRequest r, CancellationToken ct)
        => FromResult(await expenses.UpdateCategoryAsync(id, r, ct));
}
