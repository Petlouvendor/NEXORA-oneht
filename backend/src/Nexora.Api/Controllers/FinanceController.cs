using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Finance;
using Nexora.Application.Security;
using Nexora.Contracts.Finance;

namespace Nexora.Api.Controllers;

/// <summary>
/// Opérations financières (Phase 4 §16). POS, Achats et Dépenses appellent ces endpoints —
/// aucune règle financière n'est dupliquée ailleurs.
/// </summary>
[ApiVersion("1.0")]
public sealed class FinanceController(IFinanceOperations ops) : ApiControllerBase
{
    // Écriture financière brute à montants fournis par l'appelant (guide §23) : réservée à la finance.
    // Une vente POS passe par /pos/checkout, qui recalcule tout côté serveur.
    [HttpPost("cash-sales")]
    [RequirePermission(Permissions.Finance.Create)]
    [Idempotent]
    public async Task<IActionResult> CashSale([FromBody] RecordCashSaleRequest r, CancellationToken ct)
        => FromResult(await ops.RecordCashSaleAsync(r, ct));

    [HttpPost("credit-sales")]
    [RequirePermission(Permissions.Finance.Create)]
    [Idempotent]
    public async Task<IActionResult> CreditSale([FromBody] RecordCreditSaleRequest r, CancellationToken ct)
        => FromResult(await ops.RecordCreditSaleAsync(r, ct));

    [HttpPost("supplier-invoices")]
    [RequirePermission("purchasing.create")]
    [Idempotent]
    public async Task<IActionResult> SupplierInvoice([FromBody] RecordSupplierInvoiceRequest r, CancellationToken ct)
        => FromResult(await ops.RecordSupplierInvoiceAsync(r, ct));

    [HttpPost("expenses")]
    [RequirePermission("finance.pay")]
    [Idempotent]
    public async Task<IActionResult> Expense([FromBody] RecordExpenseRequest r, CancellationToken ct)
        => FromResult(await ops.RecordExpenseAsync(r, ct));

    [HttpPost("customer-payments")]
    [RequirePermission(Permissions.Finance.Pay)]
    [Idempotent]
    public async Task<IActionResult> CustomerPayment([FromBody] RecordPaymentRequest r, CancellationToken ct)
        => FromResult(await ops.RecordCustomerPaymentAsync(r, ct));

    [HttpPost("supplier-payments")]
    [RequireStepUp(StepUpPurposes.Payment)]
    [RequirePermission(Permissions.Finance.Pay)]
    [Idempotent]
    public async Task<IActionResult> SupplierPayment([FromBody] RecordPaymentRequest r, CancellationToken ct)
        => FromResult(await ops.RecordSupplierPaymentAsync(r, ct));

    [HttpPost("transfers")]
    [RequirePermission(Permissions.Finance.Transfer)]
    [Idempotent]
    public async Task<IActionResult> Transfer([FromBody] RecordTransferRequest r, CancellationToken ct)
        => FromResult(await ops.RecordTransferAsync(r, ct));

    [HttpPost("bank-charges")]
    [RequirePermission(Permissions.Finance.Reconcile)]
    [Idempotent]
    public async Task<IActionResult> BankCharge([FromBody] RecordBankChargeRequest r, CancellationToken ct)
        => FromResult(await ops.RecordBankChargeAsync(r, ct));

    [HttpPost("cash-counts")]
    [RequirePermission(Permissions.Finance.ManageCash)]
    [Idempotent]
    public async Task<IActionResult> CashCount([FromBody] RecordCashCountRequest r, CancellationToken ct)
        => FromResult(await ops.RecordCashCountAsync(r, ct));
}
