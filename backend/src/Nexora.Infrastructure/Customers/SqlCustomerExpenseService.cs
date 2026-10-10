using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Reversals;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>
/// Dépenses imputées au client (§10), distinctes des dépenses générales (<c>nexora.expense</c>).
/// Brouillon → en attente → validée par une autre personne (<c>customers.approve_expense</c>). Validée
/// et refacturée, elle devient un crédit client (Dr clients / Cr produits) ; elle ne se supprime
/// jamais : l'annulation extourne l'écriture et annule le crédit s'il n'a reçu aucun règlement.
/// </summary>
public sealed class SqlCustomerExpenseService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IPermissionService permissions,
    IFinancialEngine engine,
    IReversalService reversals,
    ICreditControl creditControl,
    IPosApprovalNotifier notifier,
    IDateTimeProvider clock,
    ILogger<SqlCustomerExpenseService> logger, IBusinessCalendar calendar) : ICustomerExpenseService
{
    private const string Kind = "Dépense client";
    private Guid Company => CustomerAccountRules.Company(tenant);
    private Guid UserId => CustomerAccountRules.User(tenant);

    private sealed record Row(
        Guid Id, string ExpenseNumber, Guid CustomerId, string CustomerName, string CustomerNumber, string Category,
        string Description, decimal Amount, string Currency, DateOnly ExpenseDate, bool Billable, DateOnly? DueDate,
        string Status, Guid? DocumentId, Guid? ReceivableId, string? CreditNumber, Guid CreatedBy, string? CreatedByName,
        DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt, string? DecidedByName, DateTimeOffset? DecidedAt,
        string? DecisionNote, string? CancelledByName, DateTimeOffset? CancelledAt, string? CancelReason,
        Guid? JournalEntryId, Guid OperationUuid);

    private IQueryable<Row> Query() => db.Database.SqlQuery<Row>($"""
        SELECT e.id AS "Id", e.expense_number AS "ExpenseNumber", e.customer_id AS "CustomerId", c.name AS "CustomerName",
               c.customer_number AS "CustomerNumber", e.category AS "Category", e.description AS "Description",
               e.amount AS "Amount", cur.code AS "Currency", e.expense_date AS "ExpenseDate", e.billable AS "Billable",
               e.due_date AS "DueDate", e.status AS "Status", e.document_id AS "DocumentId", e.receivable_id AS "ReceivableId",
               r.credit_number AS "CreditNumber", e.created_by AS "CreatedBy", cu.full_name AS "CreatedByName",
               e.created_at AS "CreatedAt", e.submitted_at AS "SubmittedAt", du.full_name AS "DecidedByName",
               e.decided_at AS "DecidedAt", e.decision_note AS "DecisionNote", xu.full_name AS "CancelledByName",
               e.cancelled_at AS "CancelledAt", e.cancel_reason AS "CancelReason", e.journal_entry_id AS "JournalEntryId",
               e.operation_uuid AS "OperationUuid"
        FROM nexora.customer_expense e
        JOIN nexora.customer c ON c.id = e.customer_id
        JOIN nexora.currency cur ON cur.id = e.currency_id
        LEFT JOIN nexora.customer_receivable r ON r.id = e.receivable_id
        LEFT JOIN nexora.app_user cu ON cu.id = e.created_by
        LEFT JOIN nexora.app_user du ON du.id = e.decided_by
        LEFT JOIN nexora.app_user xu ON xu.id = e.cancelled_by
        WHERE e.company_id = {Company}
        """);

    private static CustomerExpenseDto ToDto(Row r) => new(r.Id, r.ExpenseNumber, r.CustomerId, r.CustomerName, r.CustomerNumber,
        r.Category, r.Description, r.Amount, r.Currency, r.ExpenseDate, r.Billable, r.DueDate, r.Status, r.DocumentId,
        r.ReceivableId, r.CreditNumber, r.CreatedByName, r.CreatedAt, r.SubmittedAt, r.DecidedByName, r.DecidedAt,
        r.DecisionNote, r.CancelledByName, r.CancelledAt, r.CancelReason);

    public async Task<CustomerExpenseDto?> GetAsync(Guid expenseId, CancellationToken ct = default)
        => await Query().Where(e => e.Id == expenseId).FirstOrDefaultAsync(ct) is { } row ? ToDto(row) : null;

    public async Task<IReadOnlyList<CustomerExpenseDto>> ListAsync(Guid? customerId, string? status, CancellationToken ct = default)
    {
        var q = Query();
        if (customerId is { } c) q = q.Where(e => e.CustomerId == c);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(e => e.Status == status);
        return (await q.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.CreatedAt).Take(500).ToListAsync(ct))
            .Select(ToDto).ToList();
    }

    private sealed record CustomerRow(string Status, Guid CurrencyId, Guid? EstablishmentId);

    public async Task<Result<CustomerExpenseDto>> CreateAsync(Guid customerId, CreateCustomerExpenseRequest r, CancellationToken ct = default)
    {
        var replay = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.customer_expense WHERE operation_uuid = {r.OperationUuid} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (replay is { } existing) return Result.Success((await GetAsync(existing, ct))!);

        var customer = await db.Database.SqlQuery<CustomerRow>($"""
            SELECT status AS "Status", default_currency_id AS "CurrencyId", establishment_id AS "EstablishmentId"
            FROM nexora.customer WHERE id = {customerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (customer is null) return Error.NotFound("customer.not_found", "Client introuvable.");
        if (customer.Status == "archived") return Error.Conflict("customer.archived", "Client archivé : aucune dépense ne peut lui être imputée.");

        var category = r.Category?.Trim().ToLowerInvariant() ?? "";
        if (!CustomerAccountRules.ExpenseCategories.Contains(category))
            return Error.Validation("customer_expense.category", $"Catégorie « {r.Category} » inconnue.");
        if (string.IsNullOrWhiteSpace(r.Description))
            return Error.Validation("customer_expense.description", "Le motif / la description est obligatoire.");
        var amount = CustomerAccountRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("customer_expense.amount", "Le montant doit être positif.");
        if (r.DocumentId is { } doc && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.document WHERE id = {doc} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("customer_expense.document", "Justificatif introuvable dans la GED.");

        var id = Guid.NewGuid();
        var number = await CustomerAccountRules.NextNumberAsync(db, Company, "customer_expense", "EXP-CLI-", ct);
        var status = r.Submit ? "pending" : "draft";
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_expense
                (id, company_id, expense_number, customer_id, category, description, amount, currency_id, expense_date,
                 billable, due_date, status, document_id, establishment_id, created_by, submitted_at, operation_uuid)
            VALUES ({id}, {Company}, {number}, {customerId}, {category}, {r.Description.Trim()}, {amount}, {customer.CurrencyId},
                    {r.ExpenseDate ?? calendar.Today}, {r.Billable}, {r.DueDate}, {status},
                    {r.DocumentId}, {customer.EstablishmentId}, {UserId}, {(r.Submit ? clock.UtcNow : (DateTimeOffset?)null)},
                    {r.OperationUuid})
            """, ct);

        var dto = (await GetAsync(id, ct))!;
        if (r.Submit) await NotifyPendingAsync(dto, ct);
        return Result.Success(dto);
    }

    public async Task<Result<CustomerExpenseDto>> SubmitAsync(Guid expenseId, CancellationToken ct = default)
    {
        var row = await Query().Where(e => e.Id == expenseId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("customer_expense.not_found", "Dépense introuvable.");
        if (row.Status != "draft") return Error.Conflict("customer_expense.not_draft", "Seul un brouillon peut être transmis.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_expense SET status = 'pending', submitted_at = now()
            WHERE id = {expenseId} AND company_id = {Company} AND status = 'draft'
            """, ct);
        var dto = (await GetAsync(expenseId, ct))!;
        await NotifyPendingAsync(dto, ct);
        return Result.Success(dto);
    }

    private Task NotifyPendingAsync(CustomerExpenseDto dto, CancellationToken ct)
        => notifier.NotifyApproversAsync(Permissions.Customers.ApproveExpense, Kind, dto.ExpenseNumber,
            $"{dto.CustomerName} — {dto.Description} : {dto.Amount:N2} {dto.Currency}{(dto.Billable ? " (refacturée)" : "")}",
            $"/customers/{dto.CustomerId}?tab=expenses", "customer_expense", dto.Id, ct);

    // ------------------------------------------------------------------ Validation

    public async Task<Result<CustomerExpenseDto>> ApproveAsync(Guid expenseId, CustomerDecisionRequest decision, CancellationToken ct = default)
    {
        var row = await Query().Where(e => e.Id == expenseId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("customer_expense.not_found", "Dépense introuvable.");
        if (row.Status != "pending") return Error.Conflict("customer_expense.not_pending", "Seule une dépense en attente peut être validée.");
        if (row.CreatedBy == UserId)
            return Error.Forbidden("customer_expense.self_approval", "Vous ne pouvez pas valider une dépense que vous avez saisie.");

        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                Guid? receivableId = null, entryId = null;
                var dueDate = row.DueDate;
                if (row.Billable)
                {
                    dueDate ??= await creditControl.DefaultDueDateAsync(row.CustomerId, calendar.DateOf(now), token);
                    var posted = await engine.HandleAsync(new CustomerExpenseBilled(
                        Company, row.OperationUuid, now, row.Id, row.CustomerId, new Money(row.Amount, row.Currency), dueDate,
                        $"{row.ExpenseNumber} — {row.Description}") { UserId = tenant.UserId }, token);
                    if (!posted.IsSuccess) throw new AccountRefusal(posted.Error!);
                    receivableId = posted.Value!.ReceivableId;
                    entryId = posted.Value.JournalEntryId;
                }
                var updated = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.customer_expense
                       SET status = 'validated', decided_by = {UserId}, decided_at = {now}, decision_note = {decision.Note?.Trim()},
                           receivable_id = {receivableId}, journal_entry_id = {entryId}, due_date = {dueDate}
                     WHERE id = {row.Id} AND company_id = {Company} AND status = 'pending'
                    """, token);
                if (updated != 1) throw new AccountRefusal(Error.Conflict("customer_expense.not_pending", "La dépense a été traitée entre-temps."));
                return 0;
            }, ct);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Validation refusée pour la dépense {Expense}", row.ExpenseNumber);
            return Error.Conflict("customer_expense.approve_failed", ex.Message);
        }

        await notifier.NotifyRequesterAsync(row.CreatedBy, Kind, row.ExpenseNumber, true, decision.Note?.Trim(),
            $"/customers/{row.CustomerId}?tab=expenses", "customer_expense", row.Id, ct);
        return Result.Success((await GetAsync(expenseId, ct))!);
    }

    public async Task<Result<CustomerExpenseDto>> RejectAsync(Guid expenseId, CustomerDecisionRequest decision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(decision.Note)) return Error.Validation("customer_expense.note_required", "Indiquez le motif du rejet.");
        var row = await Query().Where(e => e.Id == expenseId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("customer_expense.not_found", "Dépense introuvable.");
        if (row.Status != "pending") return Error.Conflict("customer_expense.not_pending", "Seule une dépense en attente peut être rejetée.");
        if (row.CreatedBy == UserId)
            return Error.Forbidden("customer_expense.self_approval", "Vous ne pouvez pas décider d'une dépense que vous avez saisie.");

        var updated = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.customer_expense
               SET status = 'rejected', decided_by = {UserId}, decided_at = now(), decision_note = {decision.Note.Trim()}
             WHERE id = {expenseId} AND company_id = {Company} AND status = 'pending'
            """, ct);
        if (updated != 1) return Error.Conflict("customer_expense.not_pending", "La dépense a été traitée entre-temps.");
        await notifier.NotifyRequesterAsync(row.CreatedBy, Kind, row.ExpenseNumber, false, decision.Note.Trim(),
            $"/customers/{row.CustomerId}?tab=expenses", "customer_expense", row.Id, ct);
        return Result.Success((await GetAsync(expenseId, ct))!);
    }

    // ------------------------------------------------------------------ Annulation (contre-écriture)

    private sealed record CreditState(string Status, decimal PaidAmount);

    public async Task<Result<CustomerExpenseDto>> CancelAsync(Guid expenseId, CustomerCancelRequest r, CancellationToken ct = default)
    {
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Error.Validation("customer_expense.cancel_reason", "Le motif d'annulation est obligatoire (5 caractères au moins).");
        var row = await Query().Where(e => e.Id == expenseId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("customer_expense.not_found", "Dépense introuvable.");

        var granted = await permissions.CurrentPermissionsAsync(tenant, ct);
        var approver = granted.Contains(Permissions.Customers.ApproveExpense);
        if (row.Status is "cancelled" or "rejected") return Error.Conflict("customer_expense.closed", "Cette dépense est déjà close.");
        if (row.Status == "validated" && !approver)
            return Error.Forbidden("customer_expense.cancel_forbidden", "Annuler une dépense validée exige customers.approve_expense.");
        if (row.Status is "draft" or "pending" && row.CreatedBy != UserId && !approver)
            return Error.Forbidden("customer_expense.cancel_forbidden", "Seul l'auteur ou un valideur peut annuler cette dépense.");

        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                Guid? reversal = null;
                if (row.Status == "validated" && row.ReceivableId is { } rid)
                {
                    var credit = await db.Database.SqlQuery<CreditState>($"""
                        SELECT status AS "Status", paid_amount AS "PaidAmount" FROM nexora.customer_receivable
                        WHERE id = {rid} AND company_id = {Company} FOR UPDATE
                        """).FirstAsync(token);
                    if (credit.PaidAmount > 0m)
                        throw new AccountRefusal(Error.Conflict("customer_expense.credit_settled",
                            $"Le crédit {row.CreditNumber} a reçu {credit.PaidAmount} de règlements : annulez-les d'abord."));
                    if (row.JournalEntryId is { } je)
                    {
                        var reversed = await reversals.ReverseAsync(je, $"Annulation de la dépense {row.ExpenseNumber} — {reason}",
                            calendar.DateOf(now), token);
                        if (!reversed.IsSuccess) throw new AccountRefusal(reversed.Error!);
                        reversal = reversed.Value;
                    }
                    // Le crédit est annulé (jamais supprimé) : la base inscrit la contre-écriture au journal client.
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.customer_receivable
                           SET status = 'CANCELLED', cancelled_at = {now}, cancel_reason = {"Dépense " + row.ExpenseNumber + " annulée — " + reason}
                         WHERE id = {rid} AND company_id = {Company}
                        """, token);
                }
                var updated = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.customer_expense
                       SET status = 'cancelled', cancelled_by = {UserId}, cancelled_at = {now}, cancel_reason = {reason},
                           cancel_journal_entry_id = {reversal}
                     WHERE id = {row.Id} AND company_id = {Company} AND status = {row.Status}
                    """, token);
                if (updated != 1) throw new AccountRefusal(Error.Conflict("customer_expense.closed", "La dépense a été modifiée entre-temps."));
                return 0;
            }, ct);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("customer_expense.cancel_failed", ex.Message);
        }
        return Result.Success((await GetAsync(expenseId, ct))!);
    }
}
