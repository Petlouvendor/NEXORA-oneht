using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Customers;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Notifications;
using Nexora.Contracts.Notifications;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>Règles et lectures partagées par les services du compte client.</summary>
internal static class CustomerAccountRules
{
    public const string OverpaymentRefuse = "refuse";
    public const string OverpaymentCreditMemo = "credit_memo";

    /// <summary>Statuts d'un crédit encore dû (le litige n'efface pas la dette).</summary>
    public const string OpenStatuses = "('OPEN','PARTIALLY_PAID','OVERDUE','IN_DISPUTE')";

    public static readonly IReadOnlySet<string> ExpenseCategories = new HashSet<string>(
        ["delivery", "transport", "installation", "handling", "repair", "file_fee", "penalty", "other"]);

    public static readonly IReadOnlySet<string> RefundMethods = new HashSet<string>(
        ["cash", "card", "bank_transfer", "mobile_money", "check", "other"]);

    public static readonly IReadOnlySet<string> ActionTypes = new HashSet<string>(
        ["call", "sms", "email", "whatsapp", "visit", "letter", "reminder", "note", "other"]);

    public static readonly IReadOnlySet<string> ActionOutcomes = new HashSet<string>(
        ["reached", "no_answer", "promise", "refused", "dispute", "paid", "other"]);

    public static decimal Money(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static Guid Company(ITenantContext tenant)
        => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    public static Guid User(ITenantContext tenant)
        => tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");

    public static async Task<string> NextNumberAsync(NexoraDbContext db, Guid company, string type, string prefix, CancellationToken ct)
    {
        var year = DateTime.UtcNow.Year;
        var rows = await db.Database.SqlQuery<string>(
            $"""SELECT nexora.next_document_number({company}, {type}, {(Guid?)null}, {year}) AS "Value" """).ToListAsync(ct);
        return prefix + rows[0];
    }

    /// <summary><c>credit.overpayment_policy</c> résolu (défaut : refus).</summary>
    public static async Task<string> OverpaymentPolicyAsync(ISettingsService settings, CancellationToken ct)
    {
        try
        {
            var s = await settings.ResolveAsync("credit.overpayment_policy", null, ct);
            return s.Value.ValueKind == System.Text.Json.JsonValueKind.String ? s.Value.GetString() ?? OverpaymentRefuse : OverpaymentRefuse;
        }
        catch (InvalidOperationException)
        {
            return OverpaymentRefuse;
        }
    }

    /// <summary>Utilisateurs actifs de l'entreprise active détenant <paramref name="permission"/> (hors
    /// <paramref name="except"/>). Lecture en portée système : les affectations sont hors RLS entreprise.</summary>
    public static async Task<List<Guid>> HoldersAsync(NexoraDbContext db, ITenantContext tenant, string permission, Guid? except, CancellationToken ct)
    {
        var company = Company(tenant);
        using (tenant.BeginSystemScope())
            return await db.Database.SqlQuery<Guid>($"""
                SELECT DISTINCT ur.user_id AS "Value"
                FROM nexora.user_role ur
                JOIN nexora.role_permission rp ON rp.role_id = ur.role_id
                JOIN nexora.permission p ON p.id = rp.permission_id
                JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                JOIN nexora.app_user u ON u.id = ur.user_id AND u.status = 'active'
                WHERE p.code = {permission}
                  AND ur.revoked_at IS NULL
                  AND (ur.valid_from IS NULL OR ur.valid_from <= now())
                  AND (ur.valid_until IS NULL OR ur.valid_until > now())
                  AND (sc.company_id = {company}
                       OR (sc.group_id IS NOT NULL AND sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {company})))
                  AND ({except}::uuid IS NULL OR ur.user_id <> {except}::uuid)
                LIMIT 50
                """).ToListAsync(ct);
    }
}

/// <summary>Refus métier levé dans une transaction pour l'annuler entièrement.</summary>
internal sealed class AccountRefusal(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}

/// <summary>
/// Notifications du compte client (§25) : versement reçu (au responsable du client), versement annulé
/// (aux détenteurs de <c>credit.void_payment</c> + responsable). Un échec de notification ne remet
/// jamais en cause l'opération financière déjà enregistrée.
/// </summary>
public sealed class CustomerAccountNotifier(
    NexoraDbContext db, ITenantContext tenant, INotificationService notifications, ILogger<CustomerAccountNotifier> logger)
{
    public async Task PaymentEventAsync(Guid customerId, Guid paymentId, string title, string detail, bool voided, CancellationToken ct)
    {
        try
        {
            var company = CustomerAccountRules.Company(tenant);
            var info = await db.Database.SqlQuery<CustomerRef>($"""
                SELECT name AS "Name", account_manager_id AS "ManagerId" FROM nexora.customer
                WHERE id = {customerId} AND company_id = {company}
                """).FirstOrDefaultAsync(ct);
            if (info is null) return;

            var recipients = new HashSet<Guid>();
            if (info.ManagerId is { } manager && manager != tenant.UserId) recipients.Add(manager);
            if (voided)
                foreach (var u in await CustomerAccountRules.HoldersAsync(db, tenant, Application.Security.Permissions.Credit.VoidPayment, tenant.UserId, ct))
                    recipients.Add(u);
            if (recipients.Count == 0) return;

            await notifications.NotifyAsync(new NotifyRequest(
                Category: "customer.payment",
                Recipients: recipients.ToList(),
                TemplateKey: "customer.payment_event",
                Variables: new Dictionary<string, string?>
                {
                    ["event.title"] = title, ["customer.name"] = info.Name, ["event.detail"] = detail,
                },
                Priority: voided ? 2 : 4, Severity: voided ? "warning" : "info", Source: "workflow",
                ActionUrl: $"/customers/{customerId}?tab=payments", ReferenceType: "payment", ReferenceId: paymentId,
                DedupKey: $"payment:{paymentId}:{(voided ? "void" : "received")}"), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Notification de versement non émise pour {Payment}", paymentId);
        }
    }

    private sealed record CustomerRef(string Name, Guid? ManagerId);
}

/// <summary>Émission des avoirs clients (§7, §11) — voir <see cref="ICustomerCreditMemoWriter"/>.</summary>
public sealed class SqlCustomerCreditMemoWriter(NexoraDbContext db, ITenantContext tenant, IFinancialEngine engine) : ICustomerCreditMemoWriter
{
    private sealed record MemoRow(Guid Id, decimal Remaining, string GlAccountKey, Guid CurrencyId);

    public Task<decimal> AvailableAsync(Guid customerId, string currency, CancellationToken ct = default)
        => db.Database.SqlQuery<decimal>($"""
            SELECT COALESCE(SUM(m.remaining_amount), 0) AS "Value"
            FROM nexora.customer_credit_memo m JOIN nexora.currency c ON c.id = m.currency_id
            WHERE m.company_id = {CustomerAccountRules.Company(tenant)} AND m.customer_id = {customerId} AND c.code = {currency}
              AND m.status IN ('available', 'partially_used')
            """).FirstAsync(ct);

    public async Task<decimal> ApplyToReceivableAsync(Guid customerId, Guid receivableId, string currency, decimal amount,
        DateTimeOffset at, CancellationToken ct = default)
    {
        var company = CustomerAccountRules.Company(tenant);
        // Avoirs verrouillés, du plus ancien au plus récent : deux caisses ne consomment pas le même avoir.
        var memos = await db.Database.SqlQuery<MemoRow>($"""
            SELECT m.id AS "Id", m.remaining_amount AS "Remaining", m.gl_account_key AS "GlAccountKey", m.currency_id AS "CurrencyId"
            FROM nexora.customer_credit_memo m JOIN nexora.currency c ON c.id = m.currency_id
            WHERE m.company_id = {company} AND m.customer_id = {customerId} AND c.code = {currency}
              AND m.status IN ('available', 'partially_used') AND m.remaining_amount > 0
            ORDER BY m.created_at, m.id
            FOR UPDATE OF m
            """).ToListAsync(ct);

        var left = amount;
        foreach (var memo in memos)
        {
            if (left <= 0m) break;
            var part = Math.Min(left, memo.Remaining);
            var operation = Guid.NewGuid();
            Guid? entry = null;
            // Un avoir porté par le compte clients (reprise V074) n'a pas d'écriture à passer.
            if (memo.GlAccountKey != "accounts_receivable")
            {
                var posted = await engine.HandleAsync(new CustomerCreditApplied(
                    company, operation, at, memo.Id, customerId, new Money(part, currency), memo.GlAccountKey)
                    { UserId = tenant.UserId }, ct);
                if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);
                entry = posted.Value!.JournalEntryId;
            }
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.customer_credit_application
                    (company_id, credit_memo_id, receivable_id, currency_id, amount, journal_entry_id, applied_at, created_by, operation_uuid)
                VALUES ({company}, {memo.Id}, {receivableId}, {memo.CurrencyId}, {part}, {entry}, {at}, {tenant.UserId}, {operation})
                """, ct);
            left -= part;
        }
        return amount - left;
    }

    public async Task<Guid> IssueForReturnAsync(Guid customerId, Guid saleReturnId, Guid currencyId, decimal amount,
        Guid? receivableId, decimal appliedToReceivable, string? reason, CancellationToken ct = default)
    {
        var company = CustomerAccountRules.Company(tenant);
        var id = Guid.NewGuid();
        var number = await CustomerAccountRules.NextNumberAsync(db, company, "customer_credit_memo", "AVC-", ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_credit_memo
                (id, company_id, memo_number, customer_id, source_type, sale_return_id, currency_id, amount,
                 gl_account_key, reason, establishment_id, created_by)
            VALUES ({id}, {company}, {number}, {customerId}, 'sale_return', {saleReturnId}, {currencyId}, {amount},
                    'store_credit', {reason},
                    (SELECT s.establishment_id FROM nexora.sale_return sr JOIN nexora.sale s ON s.id = sr.sale_id WHERE sr.id = {saleReturnId}),
                    {tenant.UserId})
            """, ct);
        if (receivableId is { } rid && appliedToReceivable > 0m)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.customer_credit_application
                    (company_id, credit_memo_id, receivable_id, currency_id, amount, created_by)
                VALUES ({company}, {id}, {rid}, {currencyId}, {appliedToReceivable}, {tenant.UserId})
                """, ct);
        return id;
    }

    public async Task<Guid> IssueForOverpaymentAsync(Guid customerId, Guid paymentId, Guid currencyId, decimal amount,
        CancellationToken ct = default)
    {
        var company = CustomerAccountRules.Company(tenant);
        var id = Guid.NewGuid();
        var number = await CustomerAccountRules.NextNumberAsync(db, company, "customer_credit_memo", "AVC-", ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_credit_memo
                (id, company_id, memo_number, customer_id, source_type, payment_id, currency_id, amount,
                 gl_account_key, reason, created_by)
            VALUES ({id}, {company}, {number}, {customerId}, 'overpayment', {paymentId}, {currencyId}, {amount},
                    'suspense', 'Excédent de versement', {tenant.UserId})
            """, ct);
        return id;
    }
}
