using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Remboursements de frais (§17 « dette de l'entreprise envers l'employé, sens financier inverse », §32) : la note
/// validée crée la dette (Dr charge / Cr frais à rembourser) ; elle est soldée par la paie suivante ou par un
/// paiement direct. Le demandeur ne valide jamais sa propre note (§39).
/// </summary>
public sealed class SqlExpenseClaimService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    PayrollAccess access,
    PayrollNotifier notifier,
    SqlEmployeeService employees,
    IFinancialEngine engine,
    IDateTimeProvider clock) : IExpenseClaimService
{
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    private static readonly string[] ReadWide = [Permissions.Payroll.ExpenseApprove, Permissions.Payroll.ViewSalaries, Permissions.Hr.ManageTime];

    private static Error NotFound() => Error.NotFound("payroll.claim_not_found", "Note de frais introuvable.");

    private sealed record ClaimRow(
        Guid Id, string ClaimNumber, Guid EmployeeId, string EmployeeName, DateOnly ExpenseDate, string Category, string Description,
        decimal Amount, string Currency, Guid? ReceiptDocumentId, string Settlement, string Status, Guid SubmittedBy, string? SubmittedByName,
        DateTimeOffset SubmittedAt, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? ApprovalJournalEntryId,
        Guid? PayrollItemId, DateTimeOffset? PaidAt, string? PaymentReference, Guid? PaymentOperationUuid);

    private IQueryable<ClaimRow> Query() => db.Database.SqlQuery<ClaimRow>($"""
        SELECT c.id AS "Id", c.claim_number AS "ClaimNumber", c.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName",
               c.expense_date AS "ExpenseDate", c.category AS "Category", c.description AS "Description", c.amount AS "Amount",
               cur.code AS "Currency", c.receipt_document_id AS "ReceiptDocumentId", c.settlement AS "Settlement", c.status AS "Status",
               c.submitted_by AS "SubmittedBy", sb.full_name AS "SubmittedByName", c.submitted_at AS "SubmittedAt",
               dc.full_name AS "DecidedByName", c.decided_at AS "DecidedAt", c.decision_note AS "DecisionNote",
               c.approval_journal_entry_id AS "ApprovalJournalEntryId", c.payroll_item_id AS "PayrollItemId", c.paid_at AS "PaidAt",
               c.payment_reference AS "PaymentReference", c.payment_operation_uuid AS "PaymentOperationUuid"
        FROM nexora.employee_expense_claim c
        JOIN nexora.employee e ON e.id = c.employee_id
        JOIN nexora.currency cur ON cur.id = c.currency_id
        LEFT JOIN nexora.app_user sb ON sb.id = c.submitted_by
        LEFT JOIN nexora.app_user dc ON dc.id = c.decided_by
        WHERE c.company_id = {Company}
        """);

    private static ExpenseClaimDto ToDto(ClaimRow r) => new(r.Id, r.ClaimNumber, r.EmployeeId, r.EmployeeName, r.ExpenseDate, r.Category,
        r.Description, r.Amount, r.Currency, r.ReceiptDocumentId, r.Settlement, r.Status, r.SubmittedByName, r.SubmittedAt, r.DecidedByName,
        r.DecidedAt, r.DecisionNote, r.ApprovalJournalEntryId, r.PayrollItemId, r.PaidAt, r.PaymentReference);

    private async Task<ClaimRow?> VisibleAsync(Guid id, CancellationToken ct)
    {
        var row = await Query().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        return row is not null && (await access.ScopeAsync(ReadWide, [], allowSelf: true, ct)).Allows(row.EmployeeId) ? row : null;
    }

    public async Task<IReadOnlyList<ExpenseClaimDto>> ListAsync(Guid? employeeId, string? status, CancellationToken ct = default)
    {
        var scope = await access.ScopeAsync(ReadWide, [], allowSelf: true, ct);
        if (scope.IsEmpty) return [];
        var q = Query();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        // Périmètre appliqué AVANT la limite (audit 2026-10 éd. 2) : tronquée d'abord, la liste pouvait
        // cacher une partie de l'équipe d'un Manager.
        if (!scope.All) { var inScope = scope.Ids.ToArray(); q = q.Where(x => inScope.Contains(x.EmployeeId)); }
        return (await q.OrderByDescending(x => x.SubmittedAt).Take(500).ToListAsync(ct)).Where(x => scope.Allows(x.EmployeeId)).Select(ToDto).ToList();
    }

    public async Task<Result<ExpenseClaimDto>> SubmitAsync(SubmitExpenseClaimRequest r, CancellationToken ct = default)
    {
        if (await access.CanActForAsync(Permissions.Hr.ManageTime, r.EmployeeId, ct) is { } refused) return refused;
        if (!PayrollRules.ExpenseCategories.Contains(r.Category)) return Error.Validation("payroll.claim_category", $"Catégorie « {r.Category} » inconnue.");
        if (string.IsNullOrWhiteSpace(r.Description)) return Error.Validation("payroll.claim_description", "La description de la dépense est obligatoire.");
        var amount = PayrollRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("payroll.claim_amount", "Le montant doit être positif.");
        var settlement = r.Settlement == "direct" ? "direct" : "payroll";
        if (r.ExpenseDate > await PayrollRules.TodayAsync(db, ct)) return Error.Validation("payroll.claim_date", "La dépense ne peut pas être future.");
        var currency = r.Currency?.Trim().ToUpperInvariant() ?? await db.Database.SqlQuery<string>($"""
            SELECT COALESCE(
                (SELECT cur.code FROM nexora.employee_contract c JOIN nexora.currency cur ON cur.id = c.currency_id
                  WHERE c.employee_id = {r.EmployeeId} AND c.status = 'active' ORDER BY c.start_date DESC LIMIT 1),
                (SELECT cur.code FROM nexora.company co JOIN nexora.currency cur ON cur.id = co.default_currency_id WHERE co.id = {Company})) AS "Value"
            """).FirstAsync(ct);
        var currencyId = await db.Database.SqlQuery<Guid?>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {currency}""").FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("payroll.currency", $"Devise « {currency} » inconnue.");

        var id = Guid.NewGuid();
        string number = "";
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var employee = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS(SELECT 1 FROM nexora.employee WHERE id = {r.EmployeeId} AND company_id = {Company}) AS "Value"
                    """).FirstAsync(token);
                if (!employee) throw new PayrollRefusal(Error.NotFound("hr.employee_not_found", "Employé introuvable."));
                var receipt = r.ReceiptDocumentId;
                if (r.Receipt is { } upload)
                {
                    var stored = await employees.StoreDocumentAsync(r.EmployeeId,
                        upload with { DocumentType = "expense_receipt", VisibleToEmployee = true }, token);
                    if (!stored.IsSuccess) throw new PayrollRefusal(stored.Error!);
                    receipt = stored.Value;
                }
                else if (receipt is { } doc)
                {
                    var ok = await db.Database.SqlQuery<bool>($"""
                        SELECT EXISTS(SELECT 1 FROM nexora.employee_document WHERE id = {doc} AND employee_id = {r.EmployeeId}) AS "Value"
                        """).FirstAsync(token);
                    if (!ok) throw new PayrollRefusal(Error.Validation("payroll.claim_receipt", "Justificatif introuvable pour cet employé."));
                }
                number = await PayrollRules.NextNumberAsync(db, Company, "expense_claim", "NDF-", r.ExpenseDate.Year, token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.employee_expense_claim
                        (id, company_id, claim_number, employee_id, expense_date, category, description, amount, currency_id, receipt_document_id,
                         settlement, status, submitted_by)
                    VALUES ({id}, {Company}, {number}, {r.EmployeeId}, {r.ExpenseDate}, {r.Category}, {r.Description.Trim()}, {amount}, {currencyId},
                            {receipt}, {settlement}, 'submitted', {UserId})
                    """, token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        var dto = ToDto(await Query().Where(x => x.Id == id).FirstAsync(ct));
        await notifier.ToPermissionAsync(Permissions.Payroll.ExpenseApprove, "Note de frais à valider",
            $"{dto.EmployeeName} — {number} : {amount:N2} {currency} ({r.Description.Trim()}).",
            "/hr?tab=approvals", "employee_expense_claim", id, $"claim:{id}:submitted", ct);
        return Result.Success(dto);
    }

    private sealed record ExpenseAccountRow(Guid Id, bool IsPostable, string AccountType);

    public async Task<Result<ExpenseClaimDto>> ApproveAsync(Guid claimId, ApproveExpenseClaimRequest r, CancellationToken ct = default)
    {
        var row = await VisibleAsync(claimId, ct);
        if (row is null) return NotFound();
        if (await access.CanDecideAsync(Permissions.Payroll.ExpenseApprove, row.EmployeeId, row.SubmittedBy, ct) is { } refused) return refused;
        if (row.Status != "submitted") return Error.Conflict("payroll.claim_state", "Cette note a déjà été traitée.");
        if (r.ExpenseAccountId is { } accountId)
        {
            var acc = await db.Database.SqlQuery<ExpenseAccountRow>($"""
                SELECT id AS "Id", is_postable AS "IsPostable", account_type AS "AccountType" FROM nexora.account
                WHERE id = {accountId} AND company_id = {Company}
                """).FirstOrDefaultAsync(ct);
            if (acc is null || !acc.IsPostable || acc.AccountType != "expense")
                return Error.Validation("payroll.expense_account", "Compte de charge introuvable ou non imputable.");
        }
        var operation = Guid.NewGuid();
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var posted = await engine.HandleAsync(new EmployeeExpenseApproved(Company, operation, clock.UtcNow, row.Id, row.EmployeeId,
                    new Money(row.Amount, row.Currency), r.ExpenseAccountId, $"{row.ClaimNumber} {row.EmployeeName} — {row.Description}")
                    { UserId = tenant.UserId }, token);
                if (!posted.IsSuccess) throw new PayrollRefusal(posted.Error!);
                var n = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.employee_expense_claim
                       SET status = 'approved', decided_by = {UserId}, decided_at = now(), decision_note = {r.Note?.Trim()},
                           expense_account_id = {r.ExpenseAccountId}, approval_journal_entry_id = {posted.Value!.JournalEntryId},
                           approval_operation_uuid = {operation}
                     WHERE id = {row.Id} AND company_id = {Company} AND status = 'submitted'
                    """, token);
                if (n != 1) throw new PayrollRefusal(Error.Conflict("payroll.claim_state", "Note traitée entre-temps."));
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("payroll.claim_invalid", ex.Message);
        }
        await notifier.ToUsersAsync([row.SubmittedBy, await notifier.EmployeeUserAsync(row.EmployeeId, ct)], "Note de frais approuvée",
            $"{row.ClaimNumber} : {row.Amount:N2} {row.Currency} — {(row.Settlement == "payroll" ? "remboursée avec la prochaine paie" : "remboursement direct")}.",
            "/me/hr?tab=expenses", "employee_expense_claim", row.Id, $"claim:{row.Id}:approved", ct);
        return Result.Success(ToDto(await Query().Where(x => x.Id == claimId).FirstAsync(ct)));
    }

    public async Task<Result<ExpenseClaimDto>> RejectAsync(Guid claimId, DecisionRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("payroll.decision_note", "Indiquez le motif du refus.");
        var row = await VisibleAsync(claimId, ct);
        if (row is null) return NotFound();
        if (await access.CanDecideAsync(Permissions.Payroll.ExpenseApprove, row.EmployeeId, row.SubmittedBy, ct) is { } refused) return refused;
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.employee_expense_claim SET status = 'rejected', decided_by = {UserId}, decided_at = now(), decision_note = {r.Note.Trim()}
            WHERE id = {claimId} AND company_id = {Company} AND status = 'submitted'
            """, ct);
        if (n != 1) return Error.Conflict("payroll.claim_state", "Cette note a déjà été traitée.");
        await notifier.ToUsersAsync([row.SubmittedBy, await notifier.EmployeeUserAsync(row.EmployeeId, ct)], "Note de frais refusée",
            $"{row.ClaimNumber} : {r.Note.Trim()}", "/me/hr?tab=expenses", "employee_expense_claim", row.Id, $"claim:{row.Id}:rejected", ct);
        return Result.Success(ToDto(await Query().Where(x => x.Id == claimId).FirstAsync(ct)));
    }

    public async Task<Result<ExpenseClaimDto>> CancelAsync(Guid claimId, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("payroll.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await VisibleAsync(claimId, ct);
        if (row is null) return NotFound();
        if (row.SubmittedBy != UserId && !await access.HasAsync(Permissions.Payroll.ExpenseApprove, ct))
            return Error.Forbidden("payroll.forbidden", "Seul l'auteur ou un valideur des frais peut annuler cette note.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.employee_expense_claim SET status = 'cancelled', cancelled_at = now(), cancel_reason = {r.Reason.Trim()}
            WHERE id = {claimId} AND company_id = {Company} AND status = 'submitted'
            """, ct);
        if (n != 1) return Error.Conflict("payroll.claim_state", "Seule une note non encore validée peut être annulée.");
        return Result.Success(ToDto(await Query().Where(x => x.Id == claimId).FirstAsync(ct)));
    }

    private sealed record AccountRow(Guid Id, string Status, string Currency);

    public async Task<Result<ExpenseClaimDto>> PayAsync(Guid claimId, PayExpenseClaimRequest r, CancellationToken ct = default)
    {
        if (r.OperationUuid == Guid.Empty) return Error.Validation("payroll.operation_uuid", "Identifiant d'opération obligatoire.");
        var replay = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.employee_expense_claim WHERE payment_operation_uuid = {r.OperationUuid} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (replay is { } done) return Result.Success(ToDto(await Query().Where(x => x.Id == done).FirstAsync(ct)));
        var row = await VisibleAsync(claimId, ct);
        if (row is null) return NotFound();
        if (row.Status != "approved")
            return Error.Conflict("payroll.claim_state", row.Status is "in_payroll" or "paid" ? "Cette note est déjà réglée ou intégrée à une paie." : "La note doit être approuvée.");
        var account = await db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "Id", fa.status AS "Status", cur.code AS "Currency"
            FROM nexora.financial_account fa JOIN nexora.currency cur ON cur.id = fa.currency_id
            WHERE fa.id = {r.FinancialAccountId} AND fa.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (account is null || account.Status != "active") return Error.Validation("payroll.financial_account", "Compte de paiement introuvable ou inactif.");
        if (account.Currency != row.Currency) return Error.Validation("payroll.financial_account", $"Le compte est en {account.Currency}, la note en {row.Currency}.");
        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                if (await PayrollRules.CashShortfallAsync(db, Company, account.Id, row.Amount, token) is { } shortfall)
                    throw new PayrollRefusal(shortfall);
                var posted = await engine.HandleAsync(new EmployeeExpensePaid(Company, r.OperationUuid, now, row.Id, row.EmployeeId,
                    new Money(row.Amount, row.Currency), account.Id, row.ClaimNumber) { UserId = tenant.UserId }, token);
                if (!posted.IsSuccess) throw new PayrollRefusal(posted.Error!);
                var n = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.employee_expense_claim
                       SET status = 'paid', settlement = 'direct', paid_by = {UserId}, paid_at = {now}, financial_account_id = {account.Id},
                           payment_reference = {r.Reference}, payment_journal_entry_id = {posted.Value!.JournalEntryId},
                           payment_operation_uuid = {r.OperationUuid}
                     WHERE id = {row.Id} AND company_id = {Company} AND status = 'approved'
                    """, token);
                if (n != 1) throw new PayrollRefusal(Error.Conflict("payroll.claim_state", "Note réglée entre-temps."));
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("payroll.claim_payment_failed", ex.Message);
        }
        await notifier.ToUsersAsync([await notifier.EmployeeUserAsync(row.EmployeeId, ct)], "Frais remboursés",
            $"{row.ClaimNumber} : {row.Amount:N2} {row.Currency} remboursé(s).", "/me/hr?tab=expenses", "employee_expense_claim", row.Id,
            $"claim:{row.Id}:paid", ct);
        return Result.Success(ToDto(await Query().Where(x => x.Id == claimId).FirstAsync(ct)));
    }
}

/// <summary>
/// Régularisations (§36) : une paie approuvée ou payée n'est jamais modifiée ; la correction est une demande
/// reliée à la ligne d'origine, justifiée, approuvée par une autre personne, puis intégrée à la paie suivante
/// (ou à une paie hors cycle) avec sa référence.
/// </summary>
public sealed class SqlPayrollAdjustmentService(
    NexoraDbContext db,
    ITenantContext tenant,
    PayrollAccess access,
    PayrollNotifier notifier) : IPayrollAdjustmentService
{
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    private static readonly string[] ReadWide = [Permissions.Payroll.ViewSalaries, Permissions.Payroll.Adjust, Permissions.Payroll.ApproveAdjustment];

    private sealed record AdjustmentRow(
        Guid Id, string AdjustmentNumber, Guid EmployeeId, string EmployeeName, Guid OriginalRunId, string OriginalRunNumber,
        string OriginalPeriodLabel, Guid OriginalItemId, string Kind, string? ComponentCode, decimal Amount, string Currency, bool Taxable,
        string Reason, string Status, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName,
        DateTimeOffset? DecidedAt, string? DecisionNote, Guid? AppliedItemId, string? AppliedRunNumber, DateTimeOffset? AppliedAt);

    private IQueryable<AdjustmentRow> Query() => db.Database.SqlQuery<AdjustmentRow>($"""
        SELECT a.id AS "Id", a.adjustment_number AS "AdjustmentNumber", a.employee_id AS "EmployeeId", i.employee_name AS "EmployeeName",
               a.original_run_id AS "OriginalRunId", r.run_number AS "OriginalRunNumber", p.label AS "OriginalPeriodLabel",
               a.original_item_id AS "OriginalItemId", a.kind AS "Kind", pc.code AS "ComponentCode", a.amount AS "Amount", cur.code AS "Currency",
               a.taxable AS "Taxable", a.reason AS "Reason", a.status AS "Status", a.requested_by AS "RequestedBy",
               rq.full_name AS "RequestedByName", a.requested_at AS "RequestedAt", dc.full_name AS "DecidedByName", a.decided_at AS "DecidedAt",
               a.decision_note AS "DecisionNote", a.applied_item_id AS "AppliedItemId", ar.run_number AS "AppliedRunNumber",
               a.applied_at AS "AppliedAt"
        FROM nexora.payroll_adjustment a
        JOIN nexora.payroll_item i ON i.id = a.original_item_id
        JOIN nexora.payroll_run r ON r.id = a.original_run_id
        JOIN nexora.payroll_period p ON p.id = i.period_id
        JOIN nexora.currency cur ON cur.id = i.currency_id
        LEFT JOIN nexora.pay_component pc ON pc.id = a.component_id
        LEFT JOIN nexora.app_user rq ON rq.id = a.requested_by
        LEFT JOIN nexora.app_user dc ON dc.id = a.decided_by
        LEFT JOIN nexora.payroll_item ai ON ai.id = a.applied_item_id
        LEFT JOIN nexora.payroll_run ar ON ar.id = ai.run_id
        WHERE a.company_id = {Company}
        """);

    private static PayrollAdjustmentDto ToDto(AdjustmentRow r) => new(r.Id, r.AdjustmentNumber, r.EmployeeId, r.EmployeeName, r.OriginalRunId,
        r.OriginalRunNumber, r.OriginalPeriodLabel, r.OriginalItemId, r.Kind, r.ComponentCode, r.Amount, r.Currency, r.Taxable, r.Reason,
        r.Status, r.RequestedByName, r.RequestedAt, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.AppliedItemId, r.AppliedRunNumber, r.AppliedAt);

    public async Task<IReadOnlyList<PayrollAdjustmentDto>> ListAsync(Guid? employeeId, string? status, CancellationToken ct = default)
    {
        var scope = await access.ScopeAsync(ReadWide, [], allowSelf: true, ct);
        if (scope.IsEmpty) return [];
        var q = Query();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        // Périmètre appliqué AVANT la limite (audit 2026-10 éd. 2) : tronquée d'abord, la liste pouvait
        // cacher une partie de l'équipe d'un Manager.
        if (!scope.All) { var inScope = scope.Ids.ToArray(); q = q.Where(x => inScope.Contains(x.EmployeeId)); }
        return (await q.OrderByDescending(x => x.RequestedAt).Take(500).ToListAsync(ct)).Where(x => scope.Allows(x.EmployeeId)).Select(ToDto).ToList();
    }

    private sealed record OriginalRow(Guid ItemId, Guid RunId, Guid EmployeeId, string RunStatus, string ItemStatus, string RunNumber);
    private sealed record ComponentRow(Guid Id, string Kind);

    public async Task<Result<PayrollAdjustmentDto>> RequestAsync(RequestAdjustmentRequest r, CancellationToken ct = default)
    {
        if (r.Kind is not ("earning" or "deduction")) return Error.Validation("payroll.adjustment_kind", "Nature : rappel (earning) ou retenue (deduction).");
        var amount = PayrollRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("payroll.adjustment_amount", "Le montant doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            return Error.Validation("payroll.adjustment_reason", "La justification est obligatoire (5 caractères au moins, §36).");
        var original = await db.Database.SqlQuery<OriginalRow>($"""
            SELECT i.id AS "ItemId", i.run_id AS "RunId", i.employee_id AS "EmployeeId", r.status AS "RunStatus", i.status AS "ItemStatus",
                   r.run_number AS "RunNumber"
            FROM nexora.payroll_item i JOIN nexora.payroll_run r ON r.id = i.run_id
            WHERE i.id = {r.OriginalItemId} AND i.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (original is null) return Error.NotFound("payroll.item_not_found", "Ligne de paie d'origine introuvable.");
        if (original.RunStatus is not ("approved" or "paid" or "closed") || original.ItemStatus is not ("approved" or "paid"))
            return Error.Conflict("payroll.adjustment_not_needed",
                "La paie d'origine n'est pas encore approuvée : la corriger directement (recalcul) plutôt que par régularisation.");
        Guid? componentId = null;
        if (!string.IsNullOrWhiteSpace(r.ComponentCode))
        {
            var c = await db.Database.SqlQuery<ComponentRow>($"""
                SELECT id AS "Id", kind AS "Kind" FROM nexora.pay_component
                WHERE code = {r.ComponentCode.Trim().ToUpperInvariant()} AND (company_id = {Company} OR company_id IS NULL) AND status = 'active'
                  AND category NOT IN ('base','overtime','absence','advance','loan','reimbursement','tax','social')
                ORDER BY company_id NULLS LAST LIMIT 1
                """).FirstOrDefaultAsync(ct);
            if (c is null || c.Kind != r.Kind) return Error.Validation("payroll.component", "Composant inconnu ou de nature différente de la régularisation.");
            componentId = c.Id;
        }
        var id = Guid.NewGuid();
        var number = await PayrollRules.NextNumberAsync(db, Company, "payroll_adjustment", "REG-", null, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payroll_adjustment
                (id, company_id, adjustment_number, employee_id, original_run_id, original_item_id, kind, component_id, amount, taxable, reason,
                 status, requested_by)
            VALUES ({id}, {Company}, {number}, {original.EmployeeId}, {original.RunId}, {original.ItemId}, {r.Kind}, {componentId}, {amount},
                    {r.Taxable ?? true}, {r.Reason.Trim()}, 'requested', {UserId})
            """, ct);
        var dto = ToDto(await Query().Where(x => x.Id == id).FirstAsync(ct));
        await notifier.ToPermissionAsync(Permissions.Payroll.ApproveAdjustment, "Régularisation de paie à approuver",
            $"{dto.EmployeeName} — {number} ({(r.Kind == "earning" ? "rappel" : "retenue")} de {amount:N2} {dto.Currency}) sur {original.RunNumber} : {r.Reason.Trim()}.",
            "/hr?tab=adjustments", "payroll_adjustment", id, $"adjustment:{id}:requested", ct);
        return Result.Success(dto);
    }

    public async Task<Result<PayrollAdjustmentDto>> ApproveAsync(Guid adjustmentId, DecisionRequest r, CancellationToken ct = default)
        => await DecideAsync(adjustmentId, true, r.Note, ct);

    public async Task<Result<PayrollAdjustmentDto>> RejectAsync(Guid adjustmentId, DecisionRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("payroll.decision_note", "Indiquez le motif du refus.");
        return await DecideAsync(adjustmentId, false, r.Note, ct);
    }

    private async Task<Result<PayrollAdjustmentDto>> DecideAsync(Guid id, bool approve, string? note, CancellationToken ct)
    {
        var row = await Query().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("payroll.adjustment_not_found", "Régularisation introuvable.");
        if (await access.CanDecideAsync(Permissions.Payroll.ApproveAdjustment, row.EmployeeId, row.RequestedBy, ct) is { } refused) return refused;
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_adjustment SET status = {(approve ? "approved" : "rejected")}, decided_by = {UserId}, decided_at = now(),
                   decision_note = {note?.Trim()}
            WHERE id = {id} AND company_id = {Company} AND status = 'requested'
            """, ct);
        if (n != 1) return Error.Conflict("payroll.adjustment_state", "Cette régularisation a déjà été traitée.");
        await notifier.ToUsersAsync([row.RequestedBy], approve ? "Régularisation approuvée" : "Régularisation refusée",
            $"{row.AdjustmentNumber} — {row.EmployeeName}{(approve ? " : intégrée à la prochaine paie calculée." : " : " + note?.Trim())}",
            "/hr?tab=adjustments", "payroll_adjustment", id, $"adjustment:{id}:decided", ct);
        return Result.Success(ToDto(await Query().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<PayrollAdjustmentDto>> CancelAsync(Guid adjustmentId, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("payroll.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await Query().Where(x => x.Id == adjustmentId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("payroll.adjustment_not_found", "Régularisation introuvable.");
        if (row.RequestedBy != UserId && !await access.HasAsync(Permissions.Payroll.ApproveAdjustment, ct))
            return Error.Forbidden("payroll.forbidden", "Seul le demandeur ou un approbateur peut annuler cette régularisation.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_adjustment SET status = 'cancelled', decision_note = {"Annulée : " + r.Reason.Trim()}
            WHERE id = {adjustmentId} AND company_id = {Company} AND status IN ('requested','approved') AND applied_item_id IS NULL
            """, ct);
        if (n != 1) return Error.Conflict("payroll.adjustment_state", "Une régularisation déjà intégrée à une paie ne s'annule plus.");
        return Result.Success(ToDto(await Query().Where(x => x.Id == adjustmentId).FirstAsync(ct)));
    }
}

/// <summary>Espace employé (§5) : l'appelant ne voit que SON dossier, rattaché à son compte utilisateur.</summary>
public sealed class SqlHrSelfService(NexoraDbContext db, ITenantContext tenant, PayrollAccess access, PayrollConfigurationReader config)
    : IHrSelfService
{
    public async Task<Guid?> MyEmployeeIdAsync(CancellationToken ct = default)
        => await access.HasAsync(Permissions.Payroll.SelfService, ct) ? await access.OwnEmployeeIdAsync(ct) : null;

    private sealed record ProfileRow(
        Guid EmployeeId, string EmployeeNumber, string FullName, string? JobTitle, string? DepartmentName, DateOnly HireDate, string Status,
        string? ContractType, string? PayFrequency, string? Currency, decimal? BaseSalary, string PaymentMethod, string? BankLast4,
        decimal AdvanceBalance, decimal LoanBalance, decimal AnnualLeaveTaken);

    public async Task<Result<MyHrProfileDto>> ProfileAsync(CancellationToken ct = default)
    {
        if (await MyEmployeeIdAsync(ct) is not { } id)
            return Error.NotFound("hr.no_employee_record", "Aucun dossier employé n'est rattaché à votre compte dans cette entreprise.");
        var company = PayrollRules.Company(tenant);
        var r = await db.Database.SqlQuery<ProfileRow>($"""
            SELECT e.id AS "EmployeeId", e.employee_number AS "EmployeeNumber", e.first_name || ' ' || e.last_name AS "FullName",
                   e.job_title AS "JobTitle", d.name AS "DepartmentName", e.hire_date AS "HireDate", e.status AS "Status",
                   c.contract_type AS "ContractType", c.pay_frequency AS "PayFrequency", cur.code AS "Currency", s.amount AS "BaseSalary",
                   e.payment_method AS "PaymentMethod", e.bank_account_last4 AS "BankLast4",
                   COALESCE((SELECT sum(a.balance) FROM nexora.employee_advance a WHERE a.employee_id = e.id AND a.kind <> 'loan'
                               AND a.status IN ('paid','in_repayment')), 0) AS "AdvanceBalance",
                   COALESCE((SELECT sum(a.balance) FROM nexora.employee_advance a WHERE a.employee_id = e.id AND a.kind = 'loan'
                               AND a.status IN ('paid','in_repayment')), 0) AS "LoanBalance",
                   COALESCE((SELECT sum(l.days) FROM nexora.leave_record l WHERE l.employee_id = e.id AND l.leave_type = 'annual'
                               AND l.status = 'approved' AND extract(year FROM l.start_date) = extract(year FROM current_date)), 0) AS "AnnualLeaveTaken"
            FROM nexora.employee e
            LEFT JOIN nexora.department d ON d.id = e.department_id
            LEFT JOIN LATERAL (SELECT * FROM nexora.employee_contract x WHERE x.employee_id = e.id AND x.status IN ('active','ended','terminated')
                               ORDER BY (x.status = 'active') DESC, x.start_date DESC LIMIT 1) c ON true
            LEFT JOIN nexora.currency cur ON cur.id = c.currency_id
            LEFT JOIN LATERAL (SELECT h.amount FROM nexora.salary_history h WHERE h.contract_id = c.id AND h.effective_date <= current_date
                               ORDER BY h.effective_date DESC LIMIT 1) s ON true
            WHERE e.id = {id} AND e.company_id = {company}
            """).FirstAsync(ct);
        var cfg = await config.ReadAsync(ct);
        return Result.Success(new MyHrProfileDto(r.EmployeeId, r.EmployeeNumber, r.FullName, r.JobTitle, r.DepartmentName, r.HireDate, r.Status,
            r.ContractType, r.PayFrequency, r.Currency, r.BaseSalary, r.PaymentMethod, PayrollRules.Masked(r.BankLast4), r.AdvanceBalance,
            r.LoanBalance, cfg.AnnualLeaveDays, r.AnnualLeaveTaken));
    }
}
