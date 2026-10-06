using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Contracts.Documents;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Domain.Sales;

namespace Nexora.Application.Sales;

public interface IQuoteStore
{
    Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default);
    Task<string> CurrencyCodeAsync(Guid currencyId, CancellationToken ct = default);
    Task<string> NextNumberAsync(string documentType, CancellationToken ct = default);
    Task AddAsync(Quote quote, CancellationToken ct = default);
    Task<Quote?> GetAsync(Guid id, CancellationToken ct = default);
    Task<QuoteDto?> GetDtoAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<QuoteDto>> ListAsync(string? status, Guid? customerId, int limit, CancellationToken ct = default);
    Task<string?> CustomerNameAsync(Guid customerId, CancellationToken ct = default);
    Task<int> ExpireOverdueAsync(DateOnly today, CancellationToken ct = default);
    /// <summary>Phase 4 : devise connue (sinon null).</summary>
    Task<Guid?> CurrencyIdOrNullAsync(string code, CancellationToken ct = default);
    /// <summary>Supprime les lignes d'un brouillon avant leur remplacement.</summary>
    Task ReplaceLinesAsync(Quote quote, IReadOnlyList<QuoteLine> lines, CancellationToken ct = default);
}

/// <summary>
/// Devis (Phase 30 §4-5) : lignes figées au prix du jour, cycle brouillon → envoyé →
/// accepté / refusé / expiré → commande. La conversion en commande réutilise <see cref="OrderService"/>.
/// </summary>
public sealed class QuoteService(
    IQuoteStore store,
    ISalesPricing pricing,
    ISalePricingGuard pricingGuard,
    OrderService orders,
    IUnitOfWork uow,
    ITenantContext tenant,
    IDateTimeProvider clock,
    ILogger<QuoteService> logger)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.ToEven);

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    public async Task<Result<Guid>> CreateAsync(CreateQuoteRequest r, CancellationToken ct = default)
    {
        var valid = await ValidateAsync(r, ct);
        if (!valid.IsSuccess) return valid.Error!;

        var quote = new Quote
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            CustomerId = r.CustomerId,
            DocumentNumber = await store.NextNumberAsync("quote", ct),
            CurrencyId = valid.Value,
            Status = QuoteStatus.Draft,
            QuoteDate = Today,
            ValidUntil = r.ValidUntil,
            Terms = Clean(r.Terms),
            Notes = Clean(r.Notes),
            CreatedBy = tenant.UserId,
        };
        var lines = await PriceAsync(quote, r, ct);
        if (!lines.IsSuccess) return lines.Error!;
        quote.Lines.AddRange(lines.Value!);

        await store.AddAsync(quote, ct);
        await uow.SaveChangesAsync(ct);
        logger.LogInformation("Devis {Number} créé ({Total})", quote.DocumentNumber, quote.Total);
        return Result.Success(quote.Id);
    }

    /// <summary>Modification d'un devis encore en brouillon (phase 4) : client, validité, conditions et lignes.</summary>
    public async Task<Result> UpdateAsync(Guid id, CreateQuoteRequest r, CancellationToken ct = default)
    {
        var quote = await store.GetAsync(id, ct);
        if (quote is null) return Error.NotFound("quote.not_found", "Devis introuvable.");
        if (quote.Status != QuoteStatus.Draft)
            return Error.Conflict("quote.locked", $"Le devis {quote.DocumentNumber} est « {quote.Status} » : il n'est plus modifiable.");
        var valid = await ValidateAsync(r, ct);
        if (!valid.IsSuccess) return valid.Error!;

        quote.CustomerId = r.CustomerId;
        quote.CurrencyId = valid.Value;
        quote.ValidUntil = r.ValidUntil;
        quote.Terms = Clean(r.Terms);
        quote.Notes = Clean(r.Notes);
        quote.Subtotal = quote.DiscountTotal = quote.TaxTotal = quote.Total = 0m;
        var lines = await PriceAsync(quote, r, ct);
        if (!lines.IsSuccess) return lines.Error!;
        await store.ReplaceLinesAsync(quote, lines.Value!, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result<Guid>> ValidateAsync(CreateQuoteRequest r, CancellationToken ct)
    {
        if (r.Lines is null || r.Lines.Count == 0)
            return Error.Validation("quote.no_lines", "Un devis doit comporter au moins une ligne.");
        if (r.CustomerId is { } c && await store.CustomerNameAsync(c, ct) is null)
            return Error.NotFound("quote.customer_not_found", "Client introuvable.");
        if (r.Lines.Any(l => l.Quantity <= 0m))
            return Error.Validation("quote.quantity", "Chaque quantité doit être positive.");
        if (r.Lines.Any(l => l.LineDiscount < 0m || l.UnitPriceOverride < 0m))
            return Error.Validation("quote.line_amount", "Prix et remises ne peuvent être négatifs.");
        if (r.Lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
            return Error.Validation("quote.duplicate_line", "Un même article apparaît deux fois : regroupez les quantités.");
        if (r.ValidUntil is { } until && until < Today)
            return Error.Validation("quote.valid_until", "La date de validité est déjà passée.");
        var currency = await store.CurrencyIdOrNullAsync(r.Currency, ct);
        return currency is { } id ? Result.Success(id) : Error.Validation("quote.currency_unknown", $"Devise « {r.Currency} » inconnue.");
    }

    private async Task<Result<List<QuoteLine>>> PriceAsync(Quote quote, CreateQuoteRequest r, CancellationToken ct)
    {
        var at = clock.UtcNow;
        var requested = r.Lines.Select(l => new CheckoutLine(l.ProductId, l.Quantity, l.UnitPriceOverride, l.LineDiscount, l.TaxId)).ToList();
        if (await pricingGuard.ValidateProductsAsync(requested, ct) is { IsSuccess: false } unsellable)
            return unsellable.Error!;
        var priced = await pricing.PriceCartAsync(r.InventoryLocationId ?? Guid.Empty, r.CustomerId, r.Currency, at, requested, null, ct);
        // Recette Phase 55 : un devis engage un prix qui sera repris tel quel à la commande puis à la vente.
        if (await pricingGuard.ValidateAsync(requested, priced, r.Currency, at, null, ct) is { IsSuccess: false } refused)
            return refused.Error!;

        var lines = new List<QuoteLine>();
        var n = 1;
        foreach (var p in priced)
        {
            var gross = Round(p.UnitPrice * p.Quantity);
            var disc = Round(p.ManualDiscount + p.PromotionDiscount);
            var net = gross - disc;
            var tax = Round(net * p.TaxRate / 100m);
            quote.Subtotal += gross;
            quote.DiscountTotal += disc;
            quote.TaxTotal += tax;
            lines.Add(new QuoteLine
            {
                Id = Guid.NewGuid(), QuoteId = quote.Id, CompanyId = Company, ProductId = p.ProductId,
                Description = p.Description, Quantity = p.Quantity, UnitPrice = p.UnitPrice,
                Discount = disc, TaxId = p.TaxId, TaxAmount = tax, LineTotal = Round(net + tax), LineNumber = n++,
            });
        }
        quote.Total = Round(quote.Subtotal - quote.DiscountTotal + quote.TaxTotal);
        return Result.Success(lines);
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    public Task<Result> SendAsync(Guid id, CancellationToken ct = default) => TransitionAsync(id, QuoteStatus.Sent, null, ct);
    public Task<Result> AcceptAsync(Guid id, CancellationToken ct = default) => TransitionAsync(id, QuoteStatus.Accepted, null, ct);

    public Task<Result> RejectAsync(Guid id, string? reason, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(reason)
            ? Task.FromResult<Result>(Error.Validation("quote.reason_required", "Indiquez le motif du refus du client."))
            : TransitionAsync(id, QuoteStatus.Rejected, $"Refusé : {reason.Trim()}", ct);

    /// <summary>Annulation par l'entreprise (brouillon, envoyé ou accepté non converti), motif obligatoire.</summary>
    public Task<Result> CancelAsync(Guid id, string? reason, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(reason)
            ? Task.FromResult<Result>(Error.Validation("quote.reason_required", "Un motif d'annulation est obligatoire."))
            : TransitionAsync(id, QuoteStatus.Cancelled, $"Annulé : {reason.Trim()}", ct);

    public async Task<Result<Guid>> ConvertToOrderAsync(Guid id, ConvertQuoteRequest r, CancellationToken ct = default)
    {
        var quote = await store.GetAsync(id, ct);
        if (quote is null) return Error.NotFound("quote.not_found", "Devis introuvable.");
        if (quote.Status is QuoteStatus.Expired) return Error.Conflict("quote.expired", "Ce devis a expiré (§5).");
        if (quote.Status is QuoteStatus.Converted) return Error.Conflict("quote.already_converted", "Ce devis est déjà converti en commande.");
        if (await ExpireIfOverdueAsync(quote, ct)) return Error.Conflict("quote.expired", $"Ce devis a expiré le {quote.ValidUntil:dd/MM/yyyy}.");
        if (quote.Status is not (QuoteStatus.Accepted or QuoteStatus.Sent))
            return Error.Conflict("quote.not_convertible", $"Un devis « {quote.Status} » ne se convertit pas en commande.");
        if (r.OnCredit && quote.CustomerId is null)
            return Error.Validation("quote.customer_required", "Un client est obligatoire pour une commande à crédit (§19).");

        var currency = await store.CurrencyCodeAsync(quote.CurrencyId, ct);
        var created = await orders.CreateOrderFromQuoteAsync(new CreateOrderRequest(
            quote.CustomerId, currency, r.OrderType, r.InventoryLocationId, r.OnCredit, null, r.DueDate, null,
            $"Depuis devis {quote.DocumentNumber}",
            quote.Lines.OrderBy(l => l.LineNumber).Select(l => new CreateOrderLineInput(
                l.ProductId, l.Quantity, l.UnitPrice, l.Discount, l.TaxId)).ToList()), ct);
        if (!created.IsSuccess) return created.Error!;

        quote.Status = QuoteStatus.Converted;
        quote.DecidedAt = clock.UtcNow;
        quote.ConvertedOrderId = created.Value;
        await uow.SaveChangesAsync(ct);
        logger.LogInformation("Devis {Number} converti en commande {Order}", quote.DocumentNumber, created.Value);
        return Result.Success(created.Value);
    }

    public async Task<Result<QuoteDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var dto = await store.GetDtoAsync(id, ct);
        return dto is null ? Error.NotFound("quote.not_found", "Devis introuvable.") : Result.Success(dto);
    }

    public Task<IReadOnlyList<QuoteDto>> ListAsync(string? status, Guid? customerId, int limit, CancellationToken ct = default)
        => store.ListAsync(status, customerId, Math.Clamp(limit, 1, 200), ct);

    /// <summary>Marque « expiré » les devis envoyés dont la validité est dépassée (§5) — branchable au planificateur.</summary>
    public Task<int> ExpireOverdueAsync(CancellationToken ct = default)
        => store.ExpireOverdueAsync(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), ct);

    /// <summary>Un devis envoyé dont la validité est dépassée passe « expiré » (sans attendre le planificateur).</summary>
    private async Task<bool> ExpireIfOverdueAsync(Quote quote, CancellationToken ct)
    {
        if (quote.ValidUntil is not { } until || until >= Today) return false;
        if (quote.Status == QuoteStatus.Sent)
        {
            quote.Status = QuoteStatus.Expired;
            await uow.SaveChangesAsync(ct);
        }
        // Un devis déjà accepté dans sa validité reste convertible : le prix a été engagé.
        return quote.Status is QuoteStatus.Sent or QuoteStatus.Expired or QuoteStatus.Draft;
    }

    private async Task<Result> TransitionAsync(Guid id, string target, string? reason, CancellationToken ct)
    {
        var quote = await store.GetAsync(id, ct);
        if (quote is null) return Error.NotFound("quote.not_found", "Devis introuvable.");
        if (quote.Status == target) return Result.Success();
        if (target is QuoteStatus.Sent or QuoteStatus.Accepted && await ExpireIfOverdueAsync(quote, ct))
            return Error.Conflict("quote.expired", $"Ce devis a expiré le {quote.ValidUntil:dd/MM/yyyy}.");
        if (!QuoteStatus.CanTransition(quote.Status, target))
            return Error.Conflict("quote.transition", $"Transition interdite : {quote.Status} → {target}.");

        quote.Status = target;
        if (target == QuoteStatus.Sent) quote.SentAt = clock.UtcNow;
        if (target == QuoteStatus.Cancelled) quote.DecidedAt = clock.UtcNow;
        if (target is QuoteStatus.Accepted or QuoteStatus.Rejected) quote.DecidedAt = clock.UtcNow;
        if (reason is not null) quote.Notes = string.IsNullOrWhiteSpace(quote.Notes) ? reason : $"{quote.Notes}\n{reason}";
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
