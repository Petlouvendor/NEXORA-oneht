using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Credit;
using Nexora.Application.Customers;
using Nexora.Application.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Compte client — objets transverses (module clients §22) : crédits, versements, dépenses imputées,
/// avoirs, remboursements, recouvrement et rapports. Toute lecture/écriture est bornée à l'entreprise
/// active : un identifiant d'une autre entreprise répond 404, jamais la donnée (§23 IDOR/BOLA).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class CustomerAccountsController(
    ICustomerAccountQueries account,
    ICustomerAccountService payments,
    ICustomerExpenseService expenses,
    ICustomerRefundService refunds,
    ICollectionActionService collectionActions,
    ICreditPortfolioQueries portfolio,
    ICustomerReports reports,
    ICreditOverrideService creditOverrides,
    IPrintService print) : ApiControllerBase
{
    // --- Crédits (§8) -------------------------------------------------------------------------

    [HttpGet("credits")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Credits([FromQuery] Guid? customerId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await account.CreditsAsync(customerId, status, ct));

    [HttpGet("credits/{id:guid}")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Credit(Guid id, CancellationToken ct)
        => await account.CreditAsync(id, ct) is { } c ? Ok(c) : NotFound();

    [HttpGet("credits/{id:guid}/payments")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> CreditPayments(Guid id, CancellationToken ct)
        => Ok(await account.PaymentsAsync(null, id, ct));

    [HttpPost("credits/{id:guid}/payments")]
    [RequirePermission(Permissions.Credit.ReceivePayment)]
    [Idempotent]
    public async Task<IActionResult> PayCredit(Guid id, [FromBody] RecordCreditPaymentRequest r, CancellationToken ct)
        => FromResult(await payments.RecordCreditPaymentAsync(id, r, ct));

    /// <summary>§8 : échéancier du reste dû (remplace l'actif, versionné) — conditions de crédit.</summary>
    [HttpPost("credits/{id:guid}/installments")]
    [RequirePermission(Permissions.Credit.SetLimit)]
    public async Task<IActionResult> Schedule(Guid id, [FromBody] ScheduleInstallmentsRequest r, CancellationToken ct)
        => FromResult(await payments.ScheduleAsync(id, r, ct));

    // --- Dépassement de crédit au POS (§12, workflow d'approbation configuré) ------------------

    [HttpPost("pos/credit-override-requests")]
    [RequirePermission(Permissions.Pos.SellCredit)]
    public async Task<IActionResult> RequestOverride([FromBody] CreditOverrideRequestInput r, CancellationToken ct)
        => FromResult(await creditOverrides.RequestAsync(r, ct));

    [HttpGet("pos/credit-override-requests/{id:guid}")]
    [RequirePermission(Permissions.Pos.SellCredit)]
    public async Task<IActionResult> GetOverride(Guid id, CancellationToken ct) => FromResult(await creditOverrides.GetAsync(id, ct));

    [HttpGet("pos/credit-override-requests")]
    [RequirePermission(Permissions.Pos.CreditOverride)]
    public async Task<IActionResult> ListOverrides([FromQuery] string? status, CancellationToken ct)
        => Ok(await creditOverrides.ListAsync(status, ct));

    [HttpPost("pos/credit-override-requests/{id:guid}/approve")]
    [RequirePermission(Permissions.Pos.CreditOverride)]
    public async Task<IActionResult> ApproveOverride(Guid id, [FromBody] CustomerDecisionRequest r, CancellationToken ct)
        => FromResult(await creditOverrides.ApproveAsync(id, r.Note, ct));

    [HttpPost("pos/credit-override-requests/{id:guid}/reject")]
    [RequirePermission(Permissions.Pos.CreditOverride)]
    public async Task<IActionResult> RejectOverride(Guid id, [FromBody] CustomerDecisionRequest r, CancellationToken ct)
        => FromResult(await creditOverrides.RejectAsync(id, r.Note, ct));

    // --- Versements (§9, §22) -----------------------------------------------------------------

    /// <summary>Comptes d'encaissement et moyens de paiement, sans solde de trésorerie (formulaires).</summary>
    [HttpGet("customer-payment-options")]
    [RequirePermission(Permissions.Credit.ReceivePayment)]
    public async Task<IActionResult> PaymentOptions(CancellationToken ct) => Ok(await account.PaymentOptionsAsync(ct));

    [HttpGet("payments/{id:guid}")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Payment(Guid id, CancellationToken ct)
        => await account.PaymentAsync(id, ct) is { } p ? Ok(p) : NotFound();

    /// <summary>Annulation contrôlée : contre-écriture, motif obligatoire, audit ; rien n'est supprimé.</summary>
    [HttpPost("payments/{id:guid}/void")]
    [RequirePermission(Permissions.Credit.VoidPayment)]
    [Idempotent]
    public async Task<IActionResult> VoidPayment(Guid id, [FromBody] VoidPaymentRequest r, CancellationToken ct)
        => FromResult(await payments.VoidPaymentAsync(id, r, ct));

    // --- Dépenses imputées (§10) --------------------------------------------------------------

    [HttpGet("customer-expenses")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Expenses([FromQuery] Guid? customerId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await expenses.ListAsync(customerId, status, ct));

    [HttpGet("customer-expenses/{id:guid}")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Expense(Guid id, CancellationToken ct)
        => await expenses.GetAsync(id, ct) is { } e ? Ok(e) : NotFound();

    [HttpPost("customer-expenses/{id:guid}/submit")]
    [RequirePermission(Permissions.Customers.CreateExpense)]
    public async Task<IActionResult> SubmitExpense(Guid id, CancellationToken ct) => FromResult(await expenses.SubmitAsync(id, ct));

    [HttpPost("customer-expenses/{id:guid}/approve")]
    [RequirePermission(Permissions.Customers.ApproveExpense)]
    [Idempotent]
    public async Task<IActionResult> ApproveExpense(Guid id, [FromBody] CustomerDecisionRequest r, CancellationToken ct)
        => FromResult(await expenses.ApproveAsync(id, r, ct));

    [HttpPost("customer-expenses/{id:guid}/reject")]
    [RequirePermission(Permissions.Customers.ApproveExpense)]
    public async Task<IActionResult> RejectExpense(Guid id, [FromBody] CustomerDecisionRequest r, CancellationToken ct)
        => FromResult(await expenses.RejectAsync(id, r, ct));

    [HttpPost("customer-expenses/{id:guid}/cancel")]
    [RequirePermission(Permissions.Customers.CreateExpense)]
    [Idempotent]
    public async Task<IActionResult> CancelExpense(Guid id, [FromBody] CustomerCancelRequest r, CancellationToken ct)
        => FromResult(await expenses.CancelAsync(id, r, ct));

    // --- Avoirs & remboursements (§11) --------------------------------------------------------

    [HttpGet("credit-memos")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> CreditMemos([FromQuery] Guid? customerId, [FromQuery] bool onlyAvailable = false, CancellationToken ct = default)
        => Ok(await account.CreditMemosAsync(customerId, onlyAvailable, ct));

    [HttpGet("credit-memos/{id:guid}")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> CreditMemo(Guid id, CancellationToken ct)
        => await account.CreditMemoAsync(id, ct) is { } m ? Ok(m) : NotFound();

    [HttpPost("credit-memos/{id:guid}/apply")]
    [RequirePermission(Permissions.Credit.ReceivePayment)]
    [Idempotent]
    public async Task<IActionResult> ApplyCreditMemo(Guid id, [FromBody] ApplyCreditMemoRequest r, CancellationToken ct)
        => FromResult(await payments.ApplyCreditMemoAsync(id, r, ct));

    [HttpGet("customer-refunds")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Refunds([FromQuery] Guid? customerId, [FromQuery] string? status, CancellationToken ct)
        => Ok(await refunds.ListAsync(customerId, status, ct));

    [HttpPost("customer-refunds")]
    [RequirePermission(Permissions.Credit.ReceivePayment)]
    [Idempotent]
    public async Task<IActionResult> RequestRefund([FromBody] RequestCustomerRefundRequest r, CancellationToken ct)
        => FromResult(await refunds.RequestAsync(r, ct));

    [HttpPost("customer-refunds/{id:guid}/approve")]
    [RequirePermission(Permissions.Credit.Refund)]
    public async Task<IActionResult> ApproveRefund(Guid id, [FromBody] CustomerDecisionRequest r, CancellationToken ct)
        => FromResult(await refunds.ApproveAsync(id, r, ct));

    [HttpPost("customer-refunds/{id:guid}/reject")]
    [RequirePermission(Permissions.Credit.Refund)]
    public async Task<IActionResult> RejectRefund(Guid id, [FromBody] CustomerDecisionRequest r, CancellationToken ct)
        => FromResult(await refunds.RejectAsync(id, r, ct));

    /// <summary>Décaissement d'un remboursement approuvé (<c>credit.refund</c> ou <c>credit.receive_payment</c>,
    /// contrôlé par le service).</summary>
    [HttpPost("customer-refunds/{id:guid}/execute")]
    [RequirePermission(Permissions.Customers.View)]
    [Idempotent]
    public async Task<IActionResult> ExecuteRefund(Guid id, [FromBody] ExecuteCustomerRefundRequest r, CancellationToken ct)
        => FromResult(await refunds.ExecuteAsync(id, r, ct));

    [HttpPost("customer-refunds/{id:guid}/cancel")]
    [RequirePermission(Permissions.Customers.View)]
    public async Task<IActionResult> CancelRefund(Guid id, [FromBody] CustomerCancelRequest r, CancellationToken ct)
        => FromResult(await refunds.CancelAsync(id, r, ct));

    // --- Recouvrement (§13, §22 « GET /collections ») -----------------------------------------

    [HttpGet("collections")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Collections([FromQuery] bool onlyOverdue = true, CancellationToken ct = default)
        => Ok(await portfolio.CollectionsAsync(onlyOverdue, ct));

    [HttpGet("collection-actions")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> CollectionActions([FromQuery] bool due = false, CancellationToken ct = default)
        => Ok(await collectionActions.ListAsync(null, due, ct));

    // --- Rapports (§26) -----------------------------------------------------------------------

    [HttpGet("customer-reports")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public IActionResult ReportCatalog() => Ok(reports.Catalog());

    [HttpGet("customer-reports/{key}")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    public async Task<IActionResult> Report(string key, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
        => FromResult(await reports.RunAsync(key, from, to, ct));

    /// <summary>Export d'un rapport client en vrai Excel (.xlsx), PDF ou CSV (UTF-8 avec BOM, « ; ») — exige en
    /// plus <c>customers.export</c>. Les colonnes chiffrées restent des nombres dans Excel.</summary>
    [HttpGet("customer-reports/{key}/export")]
    [RequirePermission(Permissions.Credit.ViewPortfolio)]
    [RequirePermission(Permissions.Customers.Export)]
    public async Task<IActionResult> Export(
        string key, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? format, CancellationToken ct)
    {
        var f = PrintFormats.Normalize(format ?? PrintFormats.Csv);
        if (!PrintFormats.IsValid(f))
            throw new DomainException(Error.Validation("export.format", "Format inconnu : xlsx, pdf ou csv."));
        var result = await reports.RunAsync(key, from, to, ct);
        if (!result.IsSuccess) return FromResult(result);
        var report = result.Value!;
        var period = (report.From, report.To) switch
        {
            ({ } a, { } b) => $"Du {a:dd/MM/yyyy} au {b:dd/MM/yyyy}",
            _ => null,
        };
        var doc = new PrintDocument(report.Title, Subtitle: period,
            Tables: [PrintTableInference.Build(null, report.Columns, report.Rows)],
            Summary: report.Totals.Select(t => new PrintFigure(t.Key, t.Value, PrintKinds.Money)).ToList());
        var file = await print.RenderAsync(doc, f, $"customer-report:{report.Key}", $"clients-{report.Key}", "financial", ct: ct);
        return Download(file.Content, file.ContentType, file.FileName);
    }
}
