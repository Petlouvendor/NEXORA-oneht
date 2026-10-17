using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Notifications;
using Nexora.Application.Platform;
using Nexora.Contracts.Notifications;
using Nexora.Contracts.Platform;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Platform;

// =============================================================================
// Facturation NEXORA (Phase 38 §13-16, §41-44, §54). Distincte de la comptabilité
// des clients : les événements de facturation sont séparés des écritures (§44).
//   • Webhook de paiement idempotent par provider_event_id (§54)
//   • Un événement ambigu → revue manuelle, jamais de suspension automatique (§54)
// =============================================================================
public sealed class SqlPlatformBillingService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    IDateTimeProvider clock,
    ISubscriptionService subscriptions,
    INotificationService notifications) : IPlatformBillingService
{
    private sealed record InvoiceRow(Guid Id, string Number, Guid CustomerId, Guid? SubscriptionId, string Kind, string Status,
        string CurrencyCode, DateOnly IssueDate, DateOnly DueDate, DateOnly? PeriodStart, DateOnly? PeriodEnd,
        decimal SubtotalAmount, decimal DiscountAmount, decimal TaxAmount, decimal TaxRate, decimal TotalAmount,
        decimal PaidAmount, string? CouponCode, Guid? ParentInvoiceId, DateTimeOffset? PaidAt, DateTimeOffset CreatedAt);

    private sealed record LineRow(string LineType, string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

    private sealed record PaymentRow(Guid Id, Guid? CustomerId, Guid? InvoiceId, string Method, string? Provider, string? ProviderRef,
        decimal Amount, string CurrencyCode, string Status, string? FailureReason, bool ReviewRequired, bool IsManual,
        DateTimeOffset ReceivedAt, DateTimeOffset? ReconciledAt, decimal RefundedAmount);

    private sealed record CustomerRefRow(Guid Id);
    private sealed record InvoiceLiteRow(Guid Id, Guid CustomerId, string Status, decimal TotalAmount);
    private sealed record SubLiteRow(Guid Id, string Status);

    private const string InvoiceSelect = """
        SELECT id AS "Id", number AS "Number", customer_id AS "CustomerId", subscription_id AS "SubscriptionId", kind AS "Kind",
               status AS "Status", currency_code AS "CurrencyCode", issue_date AS "IssueDate", due_date AS "DueDate",
               period_start AS "PeriodStart", period_end AS "PeriodEnd", subtotal_amount AS "SubtotalAmount",
               discount_amount AS "DiscountAmount", tax_amount AS "TaxAmount", tax_rate AS "TaxRate", total_amount AS "TotalAmount",
               paid_amount AS "PaidAmount", coupon_code AS "CouponCode", parent_invoice_id AS "ParentInvoiceId",
               paid_at AS "PaidAt", created_at AS "CreatedAt"
        FROM nexora.platform_invoice
        """;

    // ------------------------------------------------------------- Factures

    public async Task<IReadOnlyList<InvoiceDto>> ListInvoicesAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<InvoiceRow>(InvoiceSelect + " WHERE customer_id = {0} ORDER BY issue_date DESC, number DESC", customerId).ToListAsync(ct);
        var result = new List<InvoiceDto>(rows.Count);
        foreach (var r in rows) result.Add(await ToDtoAsync(r, ct));
        return result;
    }

    public async Task<Result<InvoiceDto>> GetInvoiceAsync(Guid invoiceId, Guid? restrictToCustomer, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<InvoiceRow>(InvoiceSelect + " WHERE id = {0}", invoiceId).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("platform.invoice", "Facture introuvable.");
        if (restrictToCustomer is { } rc && rc != row.CustomerId) return Error.Forbidden("platform.not_your_invoice", "Facture hors de votre périmètre.");
        return Result.Success(await ToDtoAsync(row, ct));
    }

    public async Task<Result<InvoiceDto>> IssueCreditNoteAsync(Guid invoiceId, CreditNoteRequest request, CancellationToken ct = default)
    {
        var inv = await db.Database.SqlQueryRaw<InvoiceRow>(InvoiceSelect + " WHERE id = {0}", invoiceId).FirstOrDefaultAsync(ct);
        if (inv is null) return Error.NotFound("platform.invoice", "Facture introuvable.");
        if (inv.Kind != "invoice") return Error.Conflict("platform.not_invoice", "Un avoir ne s'émet que sur une facture.");
        if (inv.Status is "void") return Error.Conflict("platform.invoice_void", "Facture annulée.");

        var amount = request.Amount is { } a && a > 0 ? Math.Min(a, inv.TotalAmount) : inv.TotalAmount;
        var now = clock.UtcNow;
        Guid creditId = Guid.Empty;
        await uow.ExecuteInTransactionAsync(async token =>
        {
            creditId = await PlatformInvoices.IssueAsync(db, inv.CustomerId, inv.SubscriptionId, "credit_note",
                inv.PeriodStart, inv.PeriodEnd, inv.CurrencyCode,
                [new InvoiceLineSpec("proration", request.Reason ?? $"Avoir sur {inv.Number}", 1, -amount, -amount)],
                0m, null, 0m, inv.Id, 0, now, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.platform_invoice SET status = {(amount >= inv.TotalAmount ? "credited" : inv.Status)}, updated_at = now()
                WHERE id = {inv.Id}
                """, token);
            await PlatformInvoices.LogEventAsync(db, inv.CustomerId, inv.SubscriptionId, "CreditNoteIssued",
                new { parent = inv.Number, amount }, tenant.UserId, now, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);
        return await GetInvoiceAsync(creditId, null, ct);
    }

    // ------------------------------------------------------------- Paiements

    public async Task<IReadOnlyList<PaymentDto>> ListPaymentsAsync(Guid customerId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<PaymentRow>($"""
            SELECT id AS "Id", customer_id AS "CustomerId", invoice_id AS "InvoiceId", method AS "Method", provider AS "Provider",
                   provider_ref AS "ProviderRef", amount AS "Amount", currency_code AS "CurrencyCode", status AS "Status",
                   failure_reason AS "FailureReason", review_required AS "ReviewRequired", is_manual AS "IsManual",
                   received_at AS "ReceivedAt", reconciled_at AS "ReconciledAt", refunded_amount AS "RefundedAmount"
            FROM nexora.platform_payment WHERE customer_id = {customerId} ORDER BY received_at DESC
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<PaymentDto>> ListPaymentsAwaitingReviewAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<PaymentRow>($"""
            SELECT id AS "Id", customer_id AS "CustomerId", invoice_id AS "InvoiceId", method AS "Method", provider AS "Provider",
                   provider_ref AS "ProviderRef", amount AS "Amount", currency_code AS "CurrencyCode", status AS "Status",
                   failure_reason AS "FailureReason", review_required AS "ReviewRequired", is_manual AS "IsManual",
                   received_at AS "ReceivedAt", reconciled_at AS "ReconciledAt", refunded_amount AS "RefundedAmount"
            FROM nexora.platform_payment WHERE review_required ORDER BY received_at
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<PaymentDto>> RecordManualPaymentAsync(RecordManualPaymentRequest r, CancellationToken ct = default)
    {
        if (r.Amount <= 0m) return Error.Validation("platform.payment_amount", "Montant invalide.");
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.platform_customer WHERE id = {r.CustomerId}) AS "Value"
            """).FirstAsync(ct);
        if (!exists) return Error.NotFound("platform.customer", "Client introuvable.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_payment
                (id, customer_id, invoice_id, method, provider, provider_ref, amount, currency_code, status, is_manual)
            VALUES ({id}, {r.CustomerId}, {r.InvoiceId}, {r.Method}, {r.Provider}, {r.ProviderRef}, {r.Amount},
                    {r.CurrencyCode.ToUpperInvariant()}, 'pending', true)
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await LoadPaymentDtoAsync(id, ct);
    }

    public async Task<Result<PaymentDto>> ConfirmPaymentAsync(Guid paymentId, CancellationToken ct = default)
    {
        var p = await LoadPaymentAsync(paymentId, ct);
        if (p is null) return Error.NotFound("platform.payment", "Paiement introuvable.");
        if (p.Status == "succeeded") return await LoadPaymentDtoAsync(paymentId, ct);
        if (p.Status == "refunded") return Error.Conflict("platform.payment_refunded", "Paiement déjà remboursé.");
        if (p.CustomerId is not { } custId) return Error.Conflict("platform.payment_unlinked", "Rattachez ce paiement à un client avant de le confirmer.");

        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.platform_payment SET status = 'succeeded', review_required = false, confirmed_by = {tenant.UserId},
                        reconciled_at = {now}, updated_at = now()
                    WHERE id = {paymentId}
                    """, token);
                if (!await ApplySuccessfulPaymentAsync(custId, p.InvoiceId, p.Amount, p.CurrencyCode, now, token))
                    throw new InvalidOperationException(
                        "Paiement non imputable : la facture n'est pas à ce client, pas dans cette devise ou n'est plus ouverte.");
                await PlatformInvoices.LogEventAsync(db, p.CustomerId, null, PlatformEvents.PaymentSucceeded,
                    new { paymentId, amount = p.Amount, manual = true }, tenant.UserId, now, token);
                return 0;
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            db.ChangeTracker.Clear();
            return Error.Conflict("platform.payment_unapplied", ex.Message);
        }
        await uow.SaveChangesAsync(ct);
        return await LoadPaymentDtoAsync(paymentId, ct);
    }

    public async Task<Result<PaymentDto>> HandlePaymentEventAsync(PaymentWebhookEvent evt, CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        if (string.IsNullOrWhiteSpace(evt.EventId)) return Error.Validation("platform.event_id", "Identifiant d'événement requis (idempotence).");

        // Idempotence (§54) : un événement déjà traité renvoie le paiement existant, sans effet de bord.
        var existing = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.platform_payment WHERE provider_event_id = {evt.EventId}
            """).FirstOrDefaultAsync(ct);
        if (existing is { } dupId) return await LoadPaymentDtoAsync(dupId, ct);

        var now = clock.UtcNow;
        var customerId = evt.CustomerId ?? await ResolveCustomerAsync(evt.CustomerRef, ct);
        var invoiceId = evt.InvoiceId ?? await ResolveInvoiceAsync(evt.InvoiceNumber, ct);
        var ambiguous = evt.Outcome == "ambiguous" || customerId is null;

        var id = Guid.NewGuid();
        var status = ambiguous ? "pending" : evt.Outcome == "succeeded" ? "succeeded" : "failed";
        var unapplied = false;

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.platform_payment
                    (id, customer_id, invoice_id, method, provider, provider_ref, provider_event_id, amount, currency_code,
                     status, failure_reason, review_required, reconciled_at)
                VALUES ({id}, {customerId}, {invoiceId}, 'card', {evt.Provider}, {evt.ProviderRef}, {evt.EventId},
                        {evt.Amount}, {evt.CurrencyCode.ToUpperInvariant()}, {status},
                        {(status == "failed" ? evt.Outcome : null)}, {ambiguous}, {(ambiguous ? (DateTimeOffset?)null : now)})
                """, token);

            if (ambiguous)
            {
                // §54 : aucun changement d'état d'abonnement sur un événement ambigu — revue humaine.
                await PlatformInvoices.LogEventAsync(db, customerId, null, "PaymentAmbiguous",
                    new { evt.EventId, evt.EventType, evt.Amount, resolvedCustomer = customerId, resolvedInvoice = invoiceId },
                    null, now, token);
                return 0;
            }

            if (status == "succeeded")
            {
                if (await ApplySuccessfulPaymentAsync(customerId!.Value, invoiceId, evt.Amount, evt.CurrencyCode.ToUpperInvariant(), now, token))
                    await PlatformInvoices.LogEventAsync(db, customerId, null, PlatformEvents.PaymentSucceeded,
                        new { evt.EventId, amount = evt.Amount }, null, now, token);
                else
                {
                    // A-049 : facture d'un autre client, autre devise ou déjà soldée — revue humaine, aucun effet.
                    unapplied = true;
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.platform_payment SET review_required = true, reconciled_at = NULL, updated_at = now() WHERE id = {id}
                        """, token);
                    await PlatformInvoices.LogEventAsync(db, customerId, null, "PaymentUnapplied",
                        new { evt.EventId, evt.Amount, evt.CurrencyCode, requestedInvoice = invoiceId }, null, now, token);
                }
            }
            else
            {
                // Échec : on compte, on borne la grâce, on notifie — jamais de suspension ici (§16, §54).
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.subscription SET payment_failures = payment_failures + 1,
                        grace_ends_at = COALESCE(grace_ends_at, {now.AddDays(7)}),
                        status = CASE WHEN status = 'active' THEN 'past_due' ELSE status END, updated_at = now()
                    WHERE customer_id = {customerId} AND status IN ('active','past_due','grace')
                    """, token);
                await PlatformInvoices.LogEventAsync(db, customerId, null, PlatformEvents.PaymentFailed,
                    new { evt.EventId, reason = evt.Outcome }, null, now, token);
            }
            return 0;
        }, ct);

        await uow.SaveChangesAsync(ct);
        if (unapplied) await AlertPlatformBillingAsync("Paiement non imputé", $"Événement {evt.EventId} : facture d'un autre client, d'une autre devise ou déjà soldée — revue manuelle requise.", ct);
        if (ambiguous) await AlertPlatformBillingAsync("Paiement à examiner", $"Événement {evt.EventId} ambigu ou non rattaché — revue manuelle requise (§54).", ct);
        else if (status == "failed" && customerId is { } cid)
            await NotifyCustomerAsync(cid, "Paiement échoué", "Votre dernier paiement NEXORA a échoué. Mettez à jour votre moyen de paiement avant la fin de la période de grâce.", 1, ct);
        return await LoadPaymentDtoAsync(id, ct);
    }

    public async Task<int> ReconcileAsync(CancellationToken ct = default)
    {
        using var _ = tenant.BeginSystemScope();
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

        var overdue = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.platform_invoice SET status = 'overdue', updated_at = now()
            WHERE status = 'issued' AND due_date < {today}
            """, ct);

        var review = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.platform_payment WHERE review_required
            """).FirstAsync(ct);
        if (review > 0)
            await AlertPlatformBillingAsync("Paiements en attente de revue",
                $"{review} paiement(s) plateforme attendent une revue manuelle (§54).", ct);

        return overdue + review;
    }

    // ------------------------------------------------------------- Méthodes de paiement

    public async Task<IReadOnlyList<PaymentMethodDto>> ListPaymentMethodsAsync(Guid customerId, CancellationToken ct = default)
        => await db.Database.SqlQuery<PaymentMethodDto>($"""
            SELECT id AS "Id", method AS "Method", provider AS "Provider", brand AS "Brand", last4 AS "Last4",
                   exp_month AS "ExpMonth", exp_year AS "ExpYear", is_default AS "IsDefault", status AS "Status"
            FROM nexora.platform_payment_method WHERE customer_id = {customerId} AND status <> 'removed' ORDER BY is_default DESC, created_at DESC
            """).ToListAsync(ct);

    public async Task<Result<PaymentMethodDto>> AddPaymentMethodAsync(Guid customerId, AddPaymentMethodRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.ProviderToken)) return Error.Validation("platform.pm_token", "Jeton du fournisseur requis (jamais de numéro de carte §53).");
        if (r.Last4 is { } l4 && (l4.Length != 4 || !l4.All(char.IsDigit))) return Error.Validation("platform.pm_last4", "Derniers chiffres invalides.");

        var id = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            if (r.MakeDefault)
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.platform_payment_method SET is_default = false WHERE customer_id = {customerId}", token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.platform_payment_method
                    (id, customer_id, method, provider, provider_token, brand, last4, exp_month, exp_year, is_default)
                VALUES ({id}, {customerId}, {r.Method}, {r.Provider}, {r.ProviderToken}, {r.Brand}, {r.Last4}, {r.ExpMonth}, {r.ExpYear}, {r.MakeDefault})
                """, token);
            return 0;
        }, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success((await ListPaymentMethodsAsync(customerId, ct)).First(m => m.Id == id));
    }

    // ------------------------------------------------------------- Helpers

    /// <summary>
    /// Imputation d'un paiement réussi (audit 2026-10 éd. 2, A-049) : la facture visée devait seulement être ouverte —
    /// elle passait payée en totalité quel que soit le montant, le client ou la devise. Désormais elle doit appartenir au
    /// client et être dans la devise du paiement ; le montant s'ajoute au déjà payé et la facture n'est soldée qu'une
    /// fois son total atteint. Renvoie faux si le paiement n'a pu être imputé (revue humaine).
    /// </summary>
    private async Task<bool> ApplySuccessfulPaymentAsync(Guid customerId, Guid? invoiceId, decimal amount, string currency,
        DateTimeOffset now, CancellationToken ct)
    {
        var target = invoiceId ?? await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.platform_invoice
            WHERE customer_id = {customerId} AND status IN ('issued','overdue') AND currency_code = {currency}
            ORDER BY issue_date LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (target is not { } iid) return false;

        var settled = await db.Database.SqlQuery<string>($"""
            UPDATE nexora.platform_invoice
               SET paid_amount = paid_amount + {amount},
                   status = CASE WHEN paid_amount + {amount} >= total_amount THEN 'paid' ELSE status END,
                   paid_at = CASE WHEN paid_amount + {amount} >= total_amount THEN {now} ELSE paid_at END,
                   updated_at = now()
             WHERE id = {iid} AND customer_id = {customerId} AND currency_code = {currency}
               AND status IN ('issued','overdue') AND {amount} > 0
            RETURNING status AS "Value"
            """).ToListAsync(ct);
        if (settled.Count == 0) return false;
        if (settled[0] != "paid") return true;   // paiement partiel : imputé, facture encore ouverte
        await PlatformInvoices.LogEventAsync(db, customerId, null, PlatformEvents.InvoicePaid, new { invoiceId = iid }, null, now, ct);

        // Le paiement d'une échéance réactive un abonnement en attente / en grâce / suspendu (§19, §33).
        var sub = await db.Database.SqlQuery<SubLiteRow>($"""
            SELECT id AS "Id", status AS "Status" FROM nexora.subscription
            WHERE customer_id = {customerId} AND status IN ('trialing','past_due','grace','suspended') LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (sub is not null)
            await subscriptions.ActivateAsync(sub.Id, ct);
        return true;
    }

    private async Task<Guid?> ResolveCustomerAsync(string? customerRef, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(customerRef)) return null;
        var row = await db.Database.SqlQuery<CustomerRefRow>($"""
            SELECT id AS "Id" FROM nexora.platform_customer WHERE customer_ref = {customerRef.Trim()}
            """).FirstOrDefaultAsync(ct);
        return row?.Id;
    }

    private async Task<Guid?> ResolveInvoiceAsync(string? number, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        return await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.platform_invoice WHERE number = {number.Trim()}
            """).FirstOrDefaultAsync(ct);
    }

    private sealed record PaymentBasic(Guid Id, Guid? CustomerId, Guid? InvoiceId, decimal Amount, string CurrencyCode, string Status);

    private async Task<PaymentBasic?> LoadPaymentAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<PaymentBasic>($"""
            SELECT id AS "Id", customer_id AS "CustomerId", invoice_id AS "InvoiceId", amount AS "Amount",
                   currency_code AS "CurrencyCode", status AS "Status"
            FROM nexora.platform_payment WHERE id = {id}
            """).FirstOrDefaultAsync(ct);

    private async Task<Result<PaymentDto>> LoadPaymentDtoAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<PaymentRow>($"""
            SELECT id AS "Id", customer_id AS "CustomerId", invoice_id AS "InvoiceId", method AS "Method", provider AS "Provider",
                   provider_ref AS "ProviderRef", amount AS "Amount", currency_code AS "CurrencyCode", status AS "Status",
                   failure_reason AS "FailureReason", review_required AS "ReviewRequired", is_manual AS "IsManual",
                   received_at AS "ReceivedAt", reconciled_at AS "ReconciledAt", refunded_amount AS "RefundedAmount"
            FROM nexora.platform_payment WHERE id = {id}
            """).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("platform.payment", "Paiement introuvable.") : Result.Success(ToDto(row));
    }

    private async Task<InvoiceDto> ToDtoAsync(InvoiceRow r, CancellationToken ct)
    {
        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT line_type AS "LineType", description AS "Description", quantity AS "Quantity", unit_price AS "UnitPrice", amount AS "Amount"
            FROM nexora.platform_invoice_line WHERE invoice_id = {r.Id} ORDER BY sort_order
            """).ToListAsync(ct);
        return new InvoiceDto(r.Id, r.Number, r.CustomerId, r.SubscriptionId, r.Kind, r.Status, r.CurrencyCode, r.IssueDate,
            r.DueDate, r.PeriodStart, r.PeriodEnd, r.SubtotalAmount, r.DiscountAmount, r.TaxAmount, r.TaxRate, r.TotalAmount,
            r.PaidAmount, r.CouponCode, r.ParentInvoiceId, r.PaidAt,
            lines.Select(l => new InvoiceLineDto(l.LineType, l.Description, l.Quantity, l.UnitPrice, l.Amount)).ToList(),
            r.CreatedAt);
    }

    private static PaymentDto ToDto(PaymentRow p) => new(p.Id, p.CustomerId, p.InvoiceId, p.Method, p.Provider, p.ProviderRef,
        p.Amount, p.CurrencyCode, p.Status, p.FailureReason, p.ReviewRequired, p.IsManual, p.ReceivedAt, p.ReconciledAt, p.RefundedAmount);

    private async Task AlertPlatformBillingAsync(string title, string message, CancellationToken ct)
    {
        try
        {
            var staff = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.user_role ur
                JOIN nexora.role_permission rp ON rp.role_id = ur.role_id
                JOIN nexora.permission p ON p.id = rp.permission_id
                WHERE ur.revoked_at IS NULL AND p.code = 'platform.manage_billing'
                LIMIT 20
                """).ToListAsync(ct);
            if (staff.Count == 0) return;
            await notifications.NotifyAsync(new NotifyRequest("platform.billing", staff, "platform.billing",
                new Dictionary<string, string?> { ["title"] = title, ["message"] = message },
                Priority: 2, Severity: "warning", Source: "system",
                DedupKey: $"platform.billing.staff:{title}:{clock.UtcNow:yyyyMMddHH}"), ct);
        }
        catch { /* best-effort */ }
    }

    private async Task NotifyCustomerAsync(Guid customerId, string title, string message, int priority, CancellationToken ct)
    {
        try
        {
            var users = await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.platform_customer_company pcc
                JOIN nexora.access_scope sc ON sc.company_id = pcc.company_id
                JOIN nexora.user_role ur ON ur.scope_id = sc.id AND ur.revoked_at IS NULL
                WHERE pcc.customer_id = {customerId} LIMIT 25
                """).ToListAsync(ct);
            if (users.Count == 0) return;
            await notifications.NotifyAsync(new NotifyRequest("platform.billing", users, "platform.billing",
                new Dictionary<string, string?> { ["title"] = title, ["message"] = message },
                Priority: priority, Severity: priority == 1 ? "warning" : "info", Source: "system",
                DedupKey: $"platform.billing:{customerId}:{title}:{clock.UtcNow:yyyyMMddHH}"), ct);
        }
        catch { /* best-effort */ }
    }
}
