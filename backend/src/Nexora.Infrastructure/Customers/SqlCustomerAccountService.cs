using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Customers;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Finance.Reversals;
using Nexora.Contracts.Customers;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>
/// Versements clients (§9), annulation contrôlée (§22) et imputation d'avoirs (§7). Chaque versement
/// passe par le moteur financier (écriture + trésorerie + affectations dans UNE transaction) ; le
/// reste dû est recalculé par la base depuis les affectations actives, jamais saisi.
/// </summary>
public sealed class SqlCustomerAccountService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IFinancialEngine engine,
    IReversalService reversals,
    IFinancialAccountStore financialAccounts,
    ICustomerCreditMemoWriter creditMemos,
    ICustomerAccountQueries queries,
    ISettingsService settings,
    IDateTimeProvider clock,
    CustomerAccountNotifier notifier,
    Nexora.Application.Sales.ICreditControl creditControl,
    ILogger<SqlCustomerAccountService> logger, IBusinessCalendar calendar) : ICustomerAccountService
{
    private Guid Company => CustomerAccountRules.Company(tenant);
    private Guid UserId => CustomerAccountRules.User(tenant);

    private sealed record CustomerRow(Guid Id, string Name, string Status, Guid CurrencyId, string Currency);
    private sealed record OpenCredit(Guid Id, string CreditNumber, decimal Remaining, Guid CurrencyId);
    private sealed record AccountRow(Guid Id, string Status, string Currency);
    private sealed record SessionRow(string Status, Guid? CashAccountId);

    // ------------------------------------------------------------------ Versement

    public async Task<Result<CustomerPaymentDto>> RecordPaymentAsync(Guid customerId, RecordCustomerPaymentRequest r, CancellationToken ct = default)
    {
        if (await ReplayAsync(r.OperationUuid, customerId, ct) is { } replay) return replay;
        var customer = await CustomerAsync(customerId, ct);
        if (customer is null) return Error.NotFound("customer.not_found", "Client introuvable.");
        return await RecordAsync(customer, r.OperationUuid, r.Amount, r.FinancialAccountId, r.PaymentMethodId, r.CashSessionId,
            r.Reference, r.Note, r.Allocations, r.Currency, ct);
    }

    public async Task<Result<CustomerPaymentDto>> RecordCreditPaymentAsync(Guid receivableId, RecordCreditPaymentRequest r, CancellationToken ct = default)
    {
        var owner = await db.Database.SqlQuery<Guid?>($"""
            SELECT customer_id AS "Value" FROM nexora.customer_receivable WHERE id = {receivableId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (owner is not { } customerId) return Error.NotFound("credit.not_found", "Crédit introuvable.");
        if (await ReplayAsync(r.OperationUuid, customerId, ct) is { } replay) return replay;
        var customer = (await CustomerAsync(customerId, ct))!;
        return await RecordAsync(customer, r.OperationUuid, r.Amount, r.FinancialAccountId, r.PaymentMethodId, r.CashSessionId,
            r.Reference, r.Note, [new PaymentAllocationInput(receivableId, r.Amount)], null, ct, singleCredit: true);
    }

    private async Task<Result<CustomerPaymentDto>> RecordAsync(
        CustomerRow customer, Guid operationUuid, decimal rawAmount, Guid financialAccountId, Guid? paymentMethodId,
        Guid? cashSessionId, string? reference, string? note, IReadOnlyList<PaymentAllocationInput>? requested,
        string? currencyCode, CancellationToken ct, bool singleCredit = false)
    {
        var amount = CustomerAccountRules.Money(rawAmount);
        if (amount <= 0m) return Error.Validation("credit.payment_amount", "Le montant du versement doit être positif.");
        if (customer.Status == "archived")
            return Error.Conflict("customer.archived", "Client archivé : réactivez la fiche avant d'enregistrer un versement.");

        var currency = string.IsNullOrWhiteSpace(currencyCode) ? customer.Currency : currencyCode.Trim().ToUpperInvariant();
        var currencyId = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.currency WHERE code = {currency}
            """).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("credit.currency", $"Devise « {currency} » inconnue.");

        var account = await db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "Id", fa.status AS "Status", c.code AS "Currency"
            FROM nexora.financial_account fa JOIN nexora.currency c ON c.id = fa.currency_id
            WHERE fa.id = {financialAccountId} AND fa.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (account is null) return Error.Validation("credit.financial_account", "Compte d'encaissement introuvable.");
        if (account.Status != "active") return Error.Validation("credit.financial_account", "Compte d'encaissement inactif.");
        if (account.Currency != currency)
            return Error.Validation("credit.financial_account", $"Le compte d'encaissement est en {account.Currency}, le versement en {currency}.");

        if (paymentMethodId is { } pm && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.payment_method WHERE id = {pm} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("credit.payment_method", "Moyen de paiement introuvable.");

        Guid? sessionCashAccount = null;
        if (cashSessionId is { } sid)
        {
            var session = await db.Database.SqlQuery<SessionRow>($"""
                SELECT cs.status AS "Status", cr.financial_account_id AS "CashAccountId"
                FROM nexora.cash_session cs JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
                WHERE cs.id = {sid} AND cs.company_id = {Company}
                """).FirstOrDefaultAsync(ct);
            if (session is null) return Error.Validation("credit.cash_session", "Session de caisse introuvable.");
            if (session.Status != "open") return Error.Conflict("credit.cash_session_closed", "La session de caisse n'est pas ouverte.");
            sessionCashAccount = session.CashAccountId;
        }

        var policy = await CustomerAccountRules.OverpaymentPolicyAsync(settings, ct);
        var now = clock.UtcNow;

        try
        {
            var paymentId = await uow.ExecuteInTransactionAsync(async token =>
            {
                // Crédits ouverts du client, verrouillés : deux versements simultanés ne soldent pas deux fois.
                var open = await db.Database.SqlQuery<OpenCredit>($"""
                    SELECT id AS "Id", credit_number AS "CreditNumber", remaining_amount AS "Remaining", currency_id AS "CurrencyId"
                    FROM nexora.customer_receivable
                    WHERE company_id = {Company} AND customer_id = {customer.Id}
                      AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND remaining_amount > 0
                    ORDER BY due_date NULLS LAST, issue_date, created_at
                    FOR UPDATE
                    """).ToListAsync(token);
                var eligible = open.Where(o => o.CurrencyId == currencyId).ToList();

                var allocations = new List<AllocationInstruction>();
                if (requested is { Count: > 0 })
                {
                    foreach (var g in requested.GroupBy(a => a.ReceivableId))
                    {
                        var credit = eligible.FirstOrDefault(o => o.Id == g.Key);
                        if (credit is null)
                            throw new AccountRefusal(Error.Conflict("credit.not_payable",
                                "Crédit introuvable, déjà soldé ou d'une autre devise pour ce client."));
                        var part = CustomerAccountRules.Money(g.Sum(a => a.Amount));
                        if (part <= 0m) throw new AccountRefusal(Error.Validation("credit.allocation_amount", "Montant d'affectation invalide."));
                        if (singleCredit) part = Math.Min(part, credit.Remaining);
                        else if (part > credit.Remaining)
                            throw new AccountRefusal(Error.Validation("credit.allocation_exceeds",
                                $"{credit.CreditNumber} : {part} affecté pour {credit.Remaining} restant dû."));
                        allocations.Add(new AllocationInstruction(credit.Id, part, IsReceivable: true));
                    }
                    if (allocations.Sum(a => a.Amount) > amount)
                        throw new AccountRefusal(Error.Validation("credit.allocation_exceeds", "Les affectations dépassent le montant versé."));
                }
                else
                {
                    var left = amount;
                    foreach (var credit in eligible)
                    {
                        if (left <= 0m) break;
                        var part = Math.Min(left, credit.Remaining);
                        allocations.Add(new AllocationInstruction(credit.Id, part, IsReceivable: true));
                        left -= part;
                    }
                }

                // §31 test 4 : un versement au-delà du dû est refusé, sauf règle « avoir » configurée.
                var excess = amount - allocations.Sum(a => a.Amount);
                if (excess > 0m && policy != CustomerAccountRules.OverpaymentCreditMemo)
                {
                    var due = eligible.Sum(o => o.Remaining);
                    throw new AccountRefusal(Error.Validation("credit.overpayment", singleCredit
                        ? $"Le versement ({amount} {currency}) dépasse le reste dû du crédit ({allocations.Sum(a => a.Amount)} {currency})."
                        : $"Le versement ({amount} {currency}) dépasse le solde dû ({due} {currency})."));
                }

                var posted = await engine.HandleAsync(new CustomerPaymentReceived(
                    Company, operationUuid, now, customer.Id, new Money(amount, currency), financialAccountId, allocations,
                    string.IsNullOrWhiteSpace(reference) ? null : reference.Trim())
                {
                    UserId = tenant.UserId,
                    PaymentMethodId = paymentMethodId,
                    CashSessionId = cashSessionId,
                    Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                }, token);
                if (!posted.IsSuccess) throw new AccountRefusal(posted.Error!);
                var id = posted.Value!.PaymentId ?? throw new AccountRefusal(
                    Error.Conflict("credit.payment_replayed", "Cette opération a déjà été enregistrée."));

                if (excess > 0m)
                    await creditMemos.IssueForOverpaymentAsync(customer.Id, id, currencyId.Value, excess, token);

                // Espèces reçues à la caisse : la session doit les attendre à la clôture.
                if (cashSessionId is { } session && sessionCashAccount == financialAccountId)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.cash_movement
                            (company_id, cash_session_id, movement_type, amount, currency_id, reference_type, reference_id,
                             note, occurred_at, user_id, source_operation_uuid)
                        VALUES ({Company}, {session}, 'payin', {amount}, {currencyId}, 'payment', {id},
                                {"Versement client — " + customer.Name}, {now}, {UserId}, {operationUuid})
                        """, token);
                return id;
            }, ct);

            logger.LogInformation("Versement {Payment} de {Amount} {Currency} pour le client {Customer}", paymentId, amount, currency, customer.Id);
            var dto = (await queries.PaymentAsync(paymentId, ct))!;
            await notifier.PaymentEventAsync(customer.Id, paymentId, "Versement reçu",
                $"{dto.PaymentNumber} : {amount:N2} {currency} reçu{(dto.Allocations.Count > 0 ? " sur " + string.Join(", ", dto.Allocations.Select(a => a.CreditNumber)) : "")}.",
                voided: false, ct);
            return Result.Success(dto);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Versement refusé pour le client {Customer}", customer.Id);
            return Error.Conflict("credit.payment_failed", ex.Message);
        }
    }

    /// <summary>§23 : un retry réseau renvoie le versement déjà enregistré, jamais un doublon.</summary>
    private async Task<Result<CustomerPaymentDto>?> ReplayAsync(Guid operationUuid, Guid customerId, CancellationToken ct)
    {
        var existing = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.payment WHERE source_operation_uuid = {operationUuid} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (existing is not { } id) return null;
        var dto = await queries.PaymentAsync(id, ct);
        if (dto is null || dto.CustomerId != customerId)
            return Error.Conflict("credit.operation_reused", "Cet identifiant d'opération a déjà servi à une autre opération.");
        return Result.Success(dto);
    }

    private Task<CustomerRow?> CustomerAsync(Guid customerId, CancellationToken ct)
        => db.Database.SqlQuery<CustomerRow>($"""
            SELECT c.id AS "Id", c.name AS "Name", c.status AS "Status", c.default_currency_id AS "CurrencyId", cur.code AS "Currency"
            FROM nexora.customer c JOIN nexora.currency cur ON cur.id = c.default_currency_id
            WHERE c.id = {customerId} AND c.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    // ------------------------------------------------------------------ Annulation contrôlée

    private sealed record VoidRow(
        Guid Id, string PaymentNumber, Guid? CustomerId, string Direction, string Status, decimal Amount, Guid? FinancialAccountId,
        Guid? CashSessionId, Guid? SourceOperationUuid, Guid CurrencyId, string Currency, bool FromSale,
        Guid? MemoId, decimal MemoUsed, int FrozenCredits);

    public async Task<Result<CustomerPaymentDto>> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest r, CancellationToken ct = default)
    {
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Error.Validation("credit.void_reason", "Le motif d'annulation est obligatoire (5 caractères au moins).");
        var now = clock.UtcNow;

        try
        {
            var customerId = await uow.ExecuteInTransactionAsync(async token =>
            {
                var p = await db.Database.SqlQuery<VoidRow>($"""
                    SELECT p.id AS "Id", p.payment_number AS "PaymentNumber", p.customer_id AS "CustomerId",
                           p.direction AS "Direction", p.status AS "Status", p.amount AS "Amount",
                           p.financial_account_id AS "FinancialAccountId", p.cash_session_id AS "CashSessionId",
                           p.source_operation_uuid AS "SourceOperationUuid", p.currency_id AS "CurrencyId", cur.code AS "Currency",
                           EXISTS(SELECT 1 FROM nexora.sale_payment sp WHERE sp.payment_id = p.id) AS "FromSale",
                           (SELECT m.id FROM nexora.customer_credit_memo m WHERE m.payment_id = p.id) AS "MemoId",
                           COALESCE((SELECT m.used_amount FROM nexora.customer_credit_memo m WHERE m.payment_id = p.id), 0) AS "MemoUsed",
                           (SELECT COUNT(*)::int FROM nexora.payment_allocation a
                              JOIN nexora.customer_receivable rc ON rc.id = a.receivable_id
                             WHERE a.payment_id = p.id AND a.voided_at IS NULL AND rc.status IN ('WRITTEN_OFF','CANCELLED')) AS "FrozenCredits"
                    FROM nexora.payment p JOIN nexora.currency cur ON cur.id = p.currency_id
                    WHERE p.id = {paymentId} AND p.company_id = {Company}
                    FOR UPDATE OF p
                    """).FirstOrDefaultAsync(token);
                if (p is null || p.CustomerId is null || p.Direction != "inbound")
                    throw new AccountRefusal(Error.NotFound("credit.payment_not_found", "Versement client introuvable."));
                if (p.Status == "void")
                    throw new AccountRefusal(Error.Conflict("credit.payment_already_void", $"Le versement {p.PaymentNumber} est déjà annulé."));
                if (p.FromSale)
                    throw new AccountRefusal(Error.Conflict("credit.payment_from_sale",
                        "Encaissement d'une vente : il se corrige par un retour ou une annulation de la vente, pas ici."));
                if (p.FrozenCredits > 0)
                    throw new AccountRefusal(Error.Conflict("credit.payment_credit_closed",
                        "Un crédit réglé par ce versement a été radié ou annulé depuis : annulation impossible."));
                if (p.MemoUsed > 0m)
                    throw new AccountRefusal(Error.Conflict("credit.memo_used",
                        "L'avoir né de l'excédent de ce versement a déjà été utilisé : annulez d'abord cette utilisation."));

                // 1. Affectations neutralisées (jamais supprimées) : la base recalcule chaque crédit
                //    et inscrit la contre-écriture dans le journal client.
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payment_allocation SET voided_at = {now}, voided_by = {UserId}
                    WHERE payment_id = {p.Id} AND voided_at IS NULL
                    """, token);
                if (p.MemoId is { } memo)
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.customer_credit_memo
                           SET status = 'cancelled', cancelled_at = {now}, cancelled_by = {UserId},
                               cancel_reason = {"Annulation du versement " + p.PaymentNumber + " — " + reason}
                         WHERE id = {memo}
                        """, token);

                // 2. Extourne comptable de l'écriture d'encaissement.
                Guid? reversal = null;
                if (p.SourceOperationUuid is { } op)
                {
                    var entry = await db.Database.SqlQuery<Guid?>($"""
                        SELECT id AS "Value" FROM nexora.journal_entry
                        WHERE company_id = {Company} AND source_operation_uuid = {op}
                        """).FirstOrDefaultAsync(token);
                    if (entry is { } je)
                    {
                        var reversed = await reversals.ReverseAsync(je, $"Annulation du versement {p.PaymentNumber} — {reason}",
                            calendar.DateOf(now), token);
                        if (!reversed.IsSuccess) throw new AccountRefusal(reversed.Error!);
                        reversal = reversed.Value;
                    }
                }

                // 3. Sortie de trésorerie symétrique à l'encaissement.
                if (p.FinancialAccountId is { } accountId)
                {
                    await financialAccounts.AddTransactionAsync(new FinancialTransaction
                    {
                        Id = Guid.NewGuid(), CompanyId = Company, FinancialAccountId = accountId, TransactionType = "withdrawal",
                        Amount = -p.Amount, CurrencyId = p.CurrencyId, ReferenceType = "payment_void", ReferenceId = p.Id,
                        Description = $"Annulation du versement {p.PaymentNumber}", OccurredAt = now, Status = "cleared",
                        CreatedBy = tenant.UserId,
                    }, token);
                    await financialAccounts.AdjustBalanceAsync(accountId, -p.Amount, token);

                    if (p.CashSessionId is { } session && await db.Database.SqlQuery<bool>($"""
                            SELECT EXISTS(SELECT 1 FROM nexora.cash_session cs JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
                                          WHERE cs.id = {session} AND cs.status = 'open' AND cr.financial_account_id = {accountId}) AS "Value"
                            """).FirstAsync(token))
                        await db.Database.ExecuteSqlAsync($"""
                            INSERT INTO nexora.cash_movement
                                (company_id, cash_session_id, movement_type, amount, currency_id, reference_type, reference_id, note, occurred_at, user_id)
                            VALUES ({Company}, {session}, 'payout', {-p.Amount}, {p.CurrencyId}, 'payment_void', {p.Id},
                                    {"Annulation du versement " + p.PaymentNumber}, {now}, {UserId})
                            """, token);
                }

                // 4. Le versement reste visible, marqué annulé, avec son motif et son auteur.
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payment
                       SET status = 'void', voided_at = {now}, voided_by = {UserId}, void_reason = {reason},
                           void_journal_entry_id = {reversal}
                     WHERE id = {p.Id}
                    """, token);
                return p.CustomerId.Value;
            }, ct);

            var dto = (await queries.PaymentAsync(paymentId, ct))!;
            logger.LogWarning("Versement {Payment} annulé par {User} — {Reason}", dto.PaymentNumber, tenant.UserId, reason);
            await notifier.PaymentEventAsync(customerId, paymentId, "Versement annulé",
                $"{dto.PaymentNumber} ({dto.Amount:N2} {dto.Currency}) annulé. Motif : {reason}", voided: true, ct);
            return Result.Success(dto);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Annulation refusée pour le versement {Payment}", paymentId);
            return Error.Conflict("credit.void_failed", ex.Message);
        }
    }

    // ------------------------------------------------------------------ Imputation d'un avoir

    private sealed record MemoRow(Guid Id, Guid CustomerId, string Status, decimal Remaining, Guid CurrencyId, string Currency, string GlAccountKey);

    public async Task<Result<ApplyCreditMemoResultDto>> ApplyCreditMemoAsync(Guid creditMemoId, ApplyCreditMemoRequest r, CancellationToken ct = default)
    {
        if (r.Amount is <= 0m) return Error.Validation("credit.memo_amount", "Le montant à imputer doit être positif.");
        var now = clock.UtcNow;

        // Idempotence : chaque utilisation porte l'identifiant d'opération.
        var already = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.customer_credit_application WHERE operation_uuid = {r.OperationUuid}
                          AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);
        if (already) return await ApplicationResultAsync(creditMemoId, r.OperationUuid, ct);

        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var memo = await db.Database.SqlQuery<MemoRow>($"""
                    SELECT m.id AS "Id", m.customer_id AS "CustomerId", m.status AS "Status", m.remaining_amount AS "Remaining",
                           m.currency_id AS "CurrencyId", c.code AS "Currency", m.gl_account_key AS "GlAccountKey"
                    FROM nexora.customer_credit_memo m JOIN nexora.currency c ON c.id = m.currency_id
                    WHERE m.id = {creditMemoId} AND m.company_id = {Company}
                    FOR UPDATE OF m
                    """).FirstOrDefaultAsync(token)
                    ?? throw new AccountRefusal(Error.NotFound("credit.memo_not_found", "Avoir introuvable."));
                if (memo.Status is "cancelled" or "used" || memo.Remaining <= 0m)
                    throw new AccountRefusal(Error.Conflict("credit.memo_unavailable", "Cet avoir n'a plus de montant disponible."));
                var wanted = CustomerAccountRules.Money(r.Amount ?? memo.Remaining);
                if (wanted > memo.Remaining)
                    throw new AccountRefusal(Error.Validation("credit.memo_exceeds", $"Disponible sur l'avoir : {memo.Remaining} {memo.Currency}."));

                var open = await db.Database.SqlQuery<OpenCredit>($"""
                    SELECT id AS "Id", credit_number AS "CreditNumber", remaining_amount AS "Remaining", currency_id AS "CurrencyId"
                    FROM nexora.customer_receivable
                    WHERE company_id = {Company} AND customer_id = {memo.CustomerId} AND currency_id = {memo.CurrencyId}
                      AND status IN ('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE') AND remaining_amount > 0
                      AND ({r.ReceivableId}::uuid IS NULL OR id = {r.ReceivableId}::uuid)
                    ORDER BY due_date NULLS LAST, issue_date, created_at
                    FOR UPDATE
                    """).ToListAsync(token);
                if (open.Count == 0)
                    throw new AccountRefusal(Error.Conflict("credit.nothing_to_apply",
                        r.ReceivableId is null ? "Aucun crédit ouvert à solder pour ce client." : "Crédit introuvable ou déjà soldé."));

                var parts = new List<(OpenCredit Credit, decimal Amount)>();
                var left = wanted;
                foreach (var credit in open)
                {
                    if (left <= 0m) break;
                    var part = Math.Min(left, credit.Remaining);
                    parts.Add((credit, part));
                    left -= part;
                }
                if (left > 0m && r.Amount is not null)
                    throw new AccountRefusal(Error.Validation("credit.memo_exceeds_due",
                        $"Le montant demandé dépasse le reste dû ({parts.Sum(p => p.Amount)} {memo.Currency})."));
                var applied = parts.Sum(p => p.Amount);

                // Un avoir porté par le compte clients (reprise V074) se rapproche sans écriture.
                Guid? entryId = null;
                if (memo.GlAccountKey != "accounts_receivable")
                {
                    var posted = await engine.HandleAsync(new CustomerCreditApplied(
                        Company, r.OperationUuid, now, memo.Id, memo.CustomerId, new Money(applied, memo.Currency), memo.GlAccountKey)
                        { UserId = tenant.UserId }, token);
                    if (!posted.IsSuccess) throw new AccountRefusal(posted.Error!);
                    entryId = posted.Value!.JournalEntryId;
                }

                foreach (var (credit, part) in parts)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.customer_credit_application
                            (company_id, credit_memo_id, receivable_id, currency_id, amount, journal_entry_id, applied_at, created_by, operation_uuid)
                        VALUES ({Company}, {memo.Id}, {credit.Id}, {memo.CurrencyId}, {part}, {entryId}, {now}, {tenant.UserId}, {r.OperationUuid})
                        """, token);
                return 0;
            }, ct);
            return await ApplicationResultAsync(creditMemoId, r.OperationUuid, ct);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("credit.memo_apply_failed", ex.Message);
        }
    }

    private sealed record ApplicationRow(Guid ReceivableId, string CreditNumber, decimal Amount, bool Voided);

    private sealed record EntryRow(Guid? Value);

    private async Task<Result<ApplyCreditMemoResultDto>> ApplicationResultAsync(Guid memoId, Guid operationUuid, CancellationToken ct)
    {
        var memo = await queries.CreditMemoAsync(memoId, ct);
        if (memo is null) return Error.NotFound("credit.memo_not_found", "Avoir introuvable.");
        var rows = await db.Database.SqlQuery<ApplicationRow>($"""
            SELECT a.receivable_id AS "ReceivableId", r.credit_number AS "CreditNumber", a.amount AS "Amount",
                   (a.voided_at IS NOT NULL) AS "Voided"
            FROM nexora.customer_credit_application a JOIN nexora.customer_receivable r ON r.id = a.receivable_id
            WHERE a.credit_memo_id = {memoId} AND a.operation_uuid = {operationUuid}
            """).ToListAsync(ct);
        var entry = await db.Database.SqlQuery<EntryRow>($"""
            SELECT journal_entry_id AS "Value" FROM nexora.customer_credit_application
            WHERE credit_memo_id = {memoId} AND operation_uuid = {operationUuid} LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return Result.Success(new ApplyCreditMemoResultDto(memoId, rows.Sum(x => x.Amount), memo.RemainingAmount,
            rows.Select(x => new PaymentAllocationLineDto(x.ReceivableId, x.CreditNumber, x.Amount, x.Voided)).ToList(), entry?.Value));
    }

    // ------------------------------------------------------------------ Crédit hors vente (§22)

    public async Task<Result<CustomerCreditDetailDto>> OpenCreditAsync(Guid customerId, OpenCustomerCreditRequest r, CancellationToken ct = default)
    {
        // Idempotence : la reprise porte son identifiant d'opération (source_id).
        var replay = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.customer_receivable
            WHERE company_id = {Company} AND source_type = 'opening' AND source_id = {r.OperationUuid} AND customer_id = {customerId}
            """).FirstOrDefaultAsync(ct);
        if (replay is { } existing) return Result.Success((await queries.CreditAsync(existing, ct))!);

        var customer = await CustomerAsync(customerId, ct);
        if (customer is null) return Error.NotFound("customer.not_found", "Client introuvable.");
        if (customer.Status == "archived") return Error.Conflict("customer.archived", "Client archivé : aucun crédit ne peut lui être ouvert.");
        var amount = CustomerAccountRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("credit.amount", "Le montant du crédit doit être positif.");
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Error.Validation("credit.reason", "Le motif (origine de la dette) est obligatoire.");
        var currency = string.IsNullOrWhiteSpace(r.Currency) ? customer.Currency : r.Currency.Trim().ToUpperInvariant();
        if (!await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.currency WHERE code = {currency}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("credit.currency", $"Devise « {currency} » inconnue.");

        var now = clock.UtcNow;
        var due = r.DueDate ?? await creditControl.DefaultDueDateAsync(customerId, calendar.DateOf(now), ct);
        try
        {
            var receivableId = await uow.ExecuteInTransactionAsync(async token =>
            {
                var posted = await engine.HandleAsync(new CustomerCreditOpened(
                    Company, r.OperationUuid, now, customerId, new Money(amount, currency), due, reason) { UserId = tenant.UserId }, token);
                if (!posted.IsSuccess) throw new AccountRefusal(posted.Error!);
                return posted.Value!.ReceivableId ?? throw new AccountRefusal(
                    Error.Conflict("credit.operation_reused", "Cet identifiant d'opération a déjà servi à une autre opération."));
            }, ct);
            logger.LogWarning("Crédit hors vente {Amount} {Currency} ouvert pour {Customer} par {User} — {Reason}",
                amount, currency, customerId, tenant.UserId, reason);
            return Result.Success((await queries.CreditAsync(receivableId, ct))!);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("credit.open_failed", ex.Message);
        }
    }

    // ------------------------------------------------------------------ Échéancier (§8)

    private sealed record ScheduleTarget(string Status, decimal PaidAmount, decimal RemainingAmount, DateOnly IssueDate);

    public async Task<Result<CustomerCreditDetailDto>> ScheduleAsync(Guid receivableId, ScheduleInstallmentsRequest r, CancellationToken ct = default)
    {
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Error.Validation("credit.schedule_reason", "Le motif de l'échéancier est obligatoire.");
        var today = calendar.Today;

        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var credit = await db.Database.SqlQuery<ScheduleTarget>($"""
                    SELECT status AS "Status", paid_amount AS "PaidAmount", remaining_amount AS "RemainingAmount", issue_date AS "IssueDate"
                    FROM nexora.customer_receivable WHERE id = {receivableId} AND company_id = {Company}
                    FOR UPDATE
                    """).FirstOrDefaultAsync(token)
                    ?? throw new AccountRefusal(Error.NotFound("credit.not_found", "Crédit introuvable."));
                if (credit.Status is "PAID" or "CANCELLED" or "WRITTEN_OFF" || credit.RemainingAmount <= 0m)
                    throw new AccountRefusal(Error.Conflict("credit.not_schedulable", "Ce crédit n'a plus de reste dû à échelonner."));

                var lines = BuildInstallments(r, credit.RemainingAmount, today, credit.IssueDate);

                var version = await db.Database.SqlQuery<int>($"""
                    SELECT COALESCE(MAX(version), 0) + 1 AS "Value" FROM nexora.credit_schedule WHERE receivable_id = {receivableId}
                    """).FirstAsync(token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.credit_schedule SET status = 'replaced', replaced_at = now(), replaced_by = {UserId}
                    WHERE receivable_id = {receivableId} AND status = 'active'
                    """, token);
                var scheduleId = Guid.NewGuid();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.credit_schedule (id, company_id, receivable_id, version, paid_before, total_amount, reason, created_by)
                    VALUES ({scheduleId}, {Company}, {receivableId}, {version}, {credit.PaidAmount}, {credit.RemainingAmount}, {reason}, {UserId})
                    """, token);
                var n = 1;
                foreach (var (due, amount) in lines)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.credit_installment (company_id, schedule_id, receivable_id, installment_number, due_date, amount)
                        VALUES ({Company}, {scheduleId}, {receivableId}, {n++}, {due}, {amount})
                        """, token);
                // L'échéance du crédit devient la première échéance non couverte.
                await db.Database.ExecuteSqlAsync($"SELECT nexora.recompute_customer_receivable({receivableId})", token);
                return 0;
            }, ct);
        }
        catch (AccountRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("credit.schedule_failed", ex.Message);
        }
        return Result.Success((await queries.CreditAsync(receivableId, ct))!);
    }

    /// <summary>Lignes explicites (somme = reste dû) ou N échéances égales, la dernière absorbant l'arrondi.</summary>
    private static List<(DateOnly Due, decimal Amount)> BuildInstallments(ScheduleInstallmentsRequest r, decimal remaining, DateOnly today, DateOnly issue)
    {
        var lines = new List<(DateOnly Due, decimal Amount)>();
        if (r.Installments is { Count: > 0 } explicitLines)
        {
            if (explicitLines.Count > 120) throw new AccountRefusal(Error.Validation("credit.schedule_count", "120 échéances au plus."));
            DateOnly? previous = null;
            foreach (var l in explicitLines)
            {
                var amount = CustomerAccountRules.Money(l.Amount);
                if (amount <= 0m) throw new AccountRefusal(Error.Validation("credit.schedule_amount", "Chaque échéance doit avoir un montant positif."));
                if (l.DueDate < issue) throw new AccountRefusal(Error.Validation("credit.schedule_date", "Une échéance ne peut précéder la date du crédit."));
                if (previous is { } p && l.DueDate <= p)
                    throw new AccountRefusal(Error.Validation("credit.schedule_order", "Les échéances doivent être à des dates strictement croissantes."));
                previous = l.DueDate;
                lines.Add((l.DueDate, amount));
            }
        }
        else
        {
            var count = r.Count ?? 0;
            if (count is < 2 or > 120) throw new AccountRefusal(Error.Validation("credit.schedule_count", "Indiquez entre 2 et 120 échéances."));
            var first = r.FirstDueDate ?? today.AddMonths(1);
            if (first < today) throw new AccountRefusal(Error.Validation("credit.schedule_date", "La première échéance ne peut être dans le passé."));
            if (!r.Monthly && r.IntervalDays is < 1 or > 365)
                throw new AccountRefusal(Error.Validation("credit.schedule_interval", "Intervalle entre 1 et 365 jours."));
            var part = Math.Floor(remaining / count * 100m) / 100m;
            for (var i = 0; i < count; i++)
            {
                var due = r.Monthly ? first.AddMonths(i) : first.AddDays(r.IntervalDays * i);
                lines.Add((due, i == count - 1 ? remaining - part * (count - 1) : part));
            }
        }
        if (lines.Sum(l => l.Amount) != remaining)
            throw new AccountRefusal(Error.Validation("credit.schedule_total",
                $"La somme des échéances ({lines.Sum(l => l.Amount)}) doit égaler le reste dû ({remaining})."));
        return lines;
    }
}
