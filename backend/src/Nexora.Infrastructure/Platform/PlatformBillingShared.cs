using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Platform;

/// <summary>Ligne de facture NEXORA à créer (§13-14, §24).</summary>
internal sealed record InvoiceLineSpec(string LineType, string Description, decimal Quantity, decimal UnitPrice, decimal Amount);

/// <summary>
/// Écriture d'une facture NEXORA — partagée par l'abonnement et la facturation. Séparée des
/// écritures comptables des clients (§43-44). Numérotation par <c>platform_invoice_seq</c>.
/// </summary>
internal static class PlatformInvoices
{
    /// <summary>Taxe plateforme par défaut — la fiscalité réelle se configure par pays (§41).</summary>
    public const decimal DefaultTaxRatePct = 0m;

    public static async Task<Guid> IssueAsync(
        NexoraDbContext db, Guid customerId, Guid? subscriptionId, string kind,
        DateOnly? periodStart, DateOnly? periodEnd, string currencyCode,
        IReadOnlyList<InvoiceLineSpec> lines, decimal discountAmount, string? couponCode,
        decimal taxRatePct, Guid? parentInvoiceId, int dueInDays, DateTimeOffset now, CancellationToken ct)
    {
        var seq = await db.Database.SqlQuery<long>($"""
            SELECT nextval('nexora.platform_invoice_seq')::bigint AS "Value"
            """).FirstAsync(ct);
        var number = $"NXR-{now:yyyy}-{seq:D6}";
        var id = Guid.NewGuid();

        var subtotal = lines.Sum(l => l.Amount);
        var taxable = Math.Max(0m, subtotal - discountAmount);
        var tax = Math.Round(taxable * taxRatePct / 100m, 4);
        var total = taxable + tax;
        var status = total <= 0m ? "paid" : "issued";   // une facture nulle (essai / crédit total) est déjà « payée »
        var issueDate = DateOnly.FromDateTime(now.UtcDateTime);
        var dueDate = issueDate.AddDays(Math.Max(0, dueInDays));

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.platform_invoice
                (id, number, customer_id, subscription_id, kind, parent_invoice_id, status, currency_code,
                 issue_date, due_date, period_start, period_end, subtotal_amount, discount_amount, tax_amount, tax_rate,
                 total_amount, paid_amount, coupon_code, issued_at, paid_at)
            VALUES ({id}, {number}, {customerId}, {subscriptionId}, {kind}, {parentInvoiceId}, {status}, {currencyCode},
                    {issueDate}, {dueDate}, {periodStart}, {periodEnd}, {subtotal}, {discountAmount}, {tax}, {taxRatePct},
                    {total}, {(total <= 0m ? total : 0m)}, {couponCode}, {now},
                    {(total <= 0m ? now : (DateTimeOffset?)null)})
            """, ct);

        var order = 0;
        foreach (var l in lines)
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.platform_invoice_line (invoice_id, line_type, description, quantity, unit_price, amount, sort_order)
                VALUES ({id}, {l.LineType}, {l.Description}, {l.Quantity}, {l.UnitPrice}, {l.Amount}, {order})
                """, ct);
            order++;
        }
        if (discountAmount > 0m)
        {
            var label = couponCode is null ? "Remise commerciale" : $"Coupon {couponCode}";
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.platform_invoice_line (invoice_id, line_type, description, quantity, unit_price, amount, sort_order)
                VALUES ({id}, 'discount', {label}, 1, {-discountAmount}, {-discountAmount}, {order})
                """, ct);
        }

        return id;
    }

    public static async Task LogEventAsync(NexoraDbContext db, Guid? customerId, Guid? subscriptionId,
        string eventType, object payload, Guid? actorUserId, DateTimeOffset now, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, PlatformJson.Web);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.billing_event (customer_id, subscription_id, event_type, payload, actor_user_id, occurred_at)
            VALUES ({customerId}, {subscriptionId}, {eventType}, {json}::jsonb, {actorUserId}, {now})
            """, ct);
    }
}

/// <summary>Calcul de prorata transparent (§14).</summary>
internal static class Proration
{
    /// <summary>Fraction de période restante à l'instant <paramref name="now"/> (0..1).</summary>
    public static decimal RemainingFraction(DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
    {
        var total = (periodEnd - periodStart).TotalSeconds;
        if (total <= 0) return 0m;
        var remaining = (periodEnd - now).TotalSeconds;
        return (decimal)Math.Clamp(remaining / total, 0d, 1d);
    }

    /// <summary>Montant d'ajustement pour passer d'un prix de période à un autre, au prorata du temps restant.</summary>
    public static decimal Adjustment(decimal currentPeriodPrice, decimal targetPeriodPrice,
        DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset now)
        => Math.Round((targetPeriodPrice - currentPeriodPrice) * RemainingFraction(periodStart, periodEnd, now), 2);
}
