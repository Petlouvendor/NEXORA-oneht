using Nexora.Application.Abstractions;
using Nexora.Contracts.Catalog;
using Nexora.Domain.Catalog;
using Nexora.Domain.Common;

namespace Nexora.Application.Catalog;

// --- Commandes -------------------------------------------------------------

public sealed record CreateProductCommand(
    string Sku, string Name, string? Description, Guid UnitId,
    string ProductType, string? Barcode, Guid? CategoryId, Guid? BrandId,
    Guid? DefaultTaxId, bool TrackLots, bool IsWeighed,
    string? ShortName = null, string? PluCode = null, string? Status = null);

/// <summary>Recette Phase 55 PROD-004 — champs modifiables de la fiche (voir <c>UpdateProductAsync</c>).</summary>
public sealed record UpdateProductCommand(
    string Name, string? Description, string? ShortName, Guid? CategoryId, Guid? BrandId, Guid? DefaultTaxId, string? PluCode);

public sealed record AddProductSupplierCommand(
    Guid ProductId, Guid SupplierId, string? SupplierSku, decimal PurchasePrice,
    string Currency, int LeadTimeDays, bool IsPreferred);

public sealed record SetProductPriceCommand(
    Guid ProductId, string PriceType, decimal Amount, string Currency,
    decimal QuantityMin, Guid? CustomerGroupId, DateTimeOffset? ValidFrom,
    Guid? PriceListId = null, bool TaxIncluded = false, decimal? MinPrice = null);

public sealed record SetReorderRuleCommand(
    Guid ProductId, Guid? LocationId, decimal MinQuantity, decimal MaxQuantity,
    decimal ReorderPoint, decimal ReorderQuantity, int LeadTimeDays, bool AutoOrder);

// --- Store ------------------------------------------------------------------

public interface ICatalogStore
{
    Task<bool> SkuExistsAsync(Guid companyId, string sku, CancellationToken ct = default);
    Task<Guid> ResolveCurrencyIdAsync(string code, CancellationToken ct = default);
    Task<Product?> GetProductAsync(Guid id, CancellationToken ct = default);
    Task AddProductAsync(Product product, CancellationToken ct = default);
    Task UpdateProductAsync(Product product, CancellationToken ct = default);
    Task AddSupplierLinkAsync(ProductSupplier link, bool clearOtherPreferred, CancellationToken ct = default);
    Task ClosePreviousPriceAsync(Guid productId, string priceType, Guid? customerGroupId, Guid? priceListId, decimal quantityMin, Guid currencyId, DateTimeOffset at, CancellationToken ct = default);
    Task AddPriceAsync(ProductPrice price, CancellationToken ct = default);
    Task AddBarcodeAsync(ProductBarcode barcode, bool clearOtherPrimary, CancellationToken ct = default);
    Task UpsertReorderRuleAsync(ProductReorderRule rule, CancellationToken ct = default);
    Task<bool> SupplierLinkExistsAsync(Guid productId, Guid supplierId, CancellationToken ct = default);
    Task<bool> BarcodeExistsAsync(Guid companyId, string barcode, CancellationToken ct = default);
    Task<bool> PluExistsAsync(Guid companyId, string plu, CancellationToken ct = default);
    Task<bool> CategoryCodeExistsAsync(Guid companyId, string code, CancellationToken ct = default);
    Task<bool> CategoryExistsAsync(Guid companyId, Guid categoryId, CancellationToken ct = default);
    Task CreateCategoryAsync(Guid id, Guid companyId, string name, string? code, Guid? parentId, CancellationToken ct = default);
    Task<bool> PriceListCodeExistsAsync(Guid companyId, string code, CancellationToken ct = default);
    Task AddPriceListAsync(PriceList list, CancellationToken ct = default);
    Task<string?> ProductStatusAsync(Guid productId, CancellationToken ct = default);
    Task SetProductStatusAsync(Guid productId, string status, CancellationToken ct = default);
}

/// <summary>Catalogue produit (Phase 5 §4-10, Phase 23 §4-6, §18, §45). Prix historisés (§8, §19) : jamais de réécriture.</summary>
public sealed class CatalogService(
    ICatalogStore store, IUnitOfWork uow, ITenantContext tenant, IDateTimeProvider clock)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public async Task<Result<Guid>> CreateProductAsync(CreateProductCommand c, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.Sku))
            return Error.Validation("catalog.sku_required", "Le SKU est obligatoire.");
        if (await store.SkuExistsAsync(Company, c.Sku, ct))
            return Error.Conflict("catalog.sku_exists", $"Le SKU « {c.Sku} » existe déjà.");
        if (!string.IsNullOrWhiteSpace(c.Barcode) && await store.BarcodeExistsAsync(Company, c.Barcode!.Trim(), ct))
            return Error.Conflict("catalog.barcode_exists", $"Le code-barres « {c.Barcode} » est déjà utilisé.");
        if (!string.IsNullOrWhiteSpace(c.PluCode) && await store.PluExistsAsync(Company, c.PluCode!.Trim(), ct))
            return Error.Conflict("catalog.plu_exists", $"Le code PLU « {c.PluCode} » est déjà utilisé.");

        var status = c.Status ?? ProductStatus.Active;
        if (!ProductStatus.All.Contains(status))
            return Error.Validation("catalog.status_invalid", $"Statut produit « {status} » inconnu.");
        if (c.CategoryId is { } cat && !await store.CategoryExistsAsync(Company, cat, ct))
            return Error.Validation("catalog.category_unknown", "Catégorie inconnue pour cette entreprise.");

        var product = new Product
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            Sku = c.Sku.Trim(),
            Name = c.Name.Trim(),
            ShortName = string.IsNullOrWhiteSpace(c.ShortName) ? null : c.ShortName!.Trim(),
            PluCode = string.IsNullOrWhiteSpace(c.PluCode) ? null : c.PluCode!.Trim(),
            Description = c.Description,
            UnitId = c.UnitId,
            Barcode = string.IsNullOrWhiteSpace(c.Barcode) ? null : c.Barcode!.Trim(),
            CategoryId = c.CategoryId,
            BrandId = c.BrandId,
            DefaultTaxId = c.DefaultTaxId,
            ProductType = c.ProductType,
            Status = status,
            TrackLots = c.TrackLots,
            IsWeighed = c.IsWeighed,
        };
        await store.AddProductAsync(product, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(product.Id);
    }

    // --- Modification de la fiche (recette Phase 55 PROD-004) ------------------------------
    /// <summary>
    /// Met à jour l'identité commerciale d'un produit. Restent volontairement hors de portée :
    /// le SKU (clé métier des imports et intégrations), l'unité et le type (le stock déjà
    /// enregistré en dépend), le statut (workflow §45), les prix (historisés §8) et les
    /// codes-barres (route dédiée).
    /// </summary>
    public async Task<Result> UpdateProductAsync(Guid productId, UpdateProductCommand c, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(c.Name) || c.Name.Trim().Length > 200)
            return Error.Validation("catalog.name_required", "Le nom du produit est obligatoire (200 caractères au plus).");
        var product = await store.GetProductAsync(productId, ct);
        if (product is null || product.CompanyId != Company)
            return Error.NotFound("catalog.product_not_found", "Produit introuvable.");
        if (c.CategoryId is { } cat && !await store.CategoryExistsAsync(Company, cat, ct))
            return Error.Validation("catalog.category_unknown", "Catégorie inconnue pour cette entreprise.");

        var plu = string.IsNullOrWhiteSpace(c.PluCode) ? null : c.PluCode!.Trim();
        if (plu is not null && plu != product.PluCode && await store.PluExistsAsync(Company, plu, ct))
            return Error.Conflict("catalog.plu_exists", $"Le code PLU « {plu} » est déjà utilisé.");

        product.Name = c.Name.Trim();
        product.ShortName = string.IsNullOrWhiteSpace(c.ShortName) ? null : c.ShortName!.Trim();
        product.Description = c.Description;
        // Rattachements : absents = inchangés. Un client qui ne connaît pas ces identifiants (la fiche
        // détaillée n'expose que les libellés) ne doit jamais les effacer par accident.
        product.CategoryId = c.CategoryId ?? product.CategoryId;
        product.BrandId = c.BrandId ?? product.BrandId;
        product.DefaultTaxId = c.DefaultTaxId ?? product.DefaultTaxId;
        product.PluCode = plu;
        await store.UpdateProductAsync(product, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Workflow produit (§45) --------------------------------------------
    public async Task<Result> SetProductStatusAsync(Guid productId, string target, CancellationToken ct = default)
    {
        if (!ProductStatus.All.Contains(target))
            return Error.Validation("catalog.status_invalid", $"Statut produit « {target} » inconnu.");
        var current = await store.ProductStatusAsync(productId, ct);
        if (current is null) return Error.NotFound("catalog.product_not_found", "Produit introuvable.");
        if (current == target) return Result.Success();
        if (!ProductStatus.CanTransition(current, target))
            return Error.Conflict("catalog.status_transition",
                $"Transition de statut interdite : {current} → {target} (§45).");
        await store.SetProductStatusAsync(productId, target, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Catégories (§9) --------------------------------------------------
    public async Task<Result<Guid>> CreateCategoryAsync(CreateCategoryRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
            return Error.Validation("catalog.category_name_required", "Le nom de catégorie est obligatoire.");
        if (!string.IsNullOrWhiteSpace(r.Code) && await store.CategoryCodeExistsAsync(Company, r.Code!.Trim(), ct))
            return Error.Conflict("catalog.category_code_exists", $"Le code « {r.Code} » existe déjà.");
        if (r.ParentId is { } p && !await store.CategoryExistsAsync(Company, p, ct))
            return Error.Validation("catalog.category_unknown", "Catégorie parente inconnue.");

        var id = Guid.NewGuid();
        await store.CreateCategoryAsync(id, Company, r.Name.Trim(),
            string.IsNullOrWhiteSpace(r.Code) ? null : r.Code!.Trim(), r.ParentId, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    // --- Listes tarifaires (§18) ----------------------------------------
    public async Task<Result<Guid>> CreatePriceListAsync(CreatePriceListRequest r, CancellationToken ct = default)
    {
        var listTypes = new[] { "cost", "retail", "wholesale", "member", "promotional", "custom" };
        if (string.IsNullOrWhiteSpace(r.Code))
            return Error.Validation("catalog.price_list_code_required", "Le code de la liste est obligatoire.");
        if (!listTypes.Contains(r.ListType))
            return Error.Validation("catalog.price_list_type", $"Type de liste « {r.ListType} » inconnu.");
        if (await store.PriceListCodeExistsAsync(Company, r.Code.Trim(), ct))
            return Error.Conflict("catalog.price_list_exists", $"La liste « {r.Code} » existe déjà.");

        var list = new PriceList
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            Code = r.Code.Trim(),
            Name = r.Name.Trim(),
            ListType = r.ListType,
            CurrencyId = string.IsNullOrWhiteSpace(r.Currency) ? null : await store.ResolveCurrencyIdAsync(r.Currency!, ct),
            CustomerGroupId = r.CustomerGroupId,
            Priority = r.Priority,
            Status = "active",
        };
        await store.AddPriceListAsync(list, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(list.Id);
    }

    public async Task<Result> AddSupplierAsync(AddProductSupplierCommand c, CancellationToken ct = default)
    {
        if (await store.GetProductAsync(c.ProductId, ct) is null)
            return Error.NotFound("catalog.product_not_found", "Produit introuvable.");
        if (await store.SupplierLinkExistsAsync(c.ProductId, c.SupplierId, ct))
            return Error.Conflict("catalog.supplier_linked", "Ce fournisseur est déjà associé au produit.");

        await store.AddSupplierLinkAsync(new ProductSupplier
        {
            Id = Guid.NewGuid(),
            ProductId = c.ProductId,
            CompanyId = Company,
            SupplierId = c.SupplierId,
            SupplierSku = c.SupplierSku,
            PurchasePrice = c.PurchasePrice,
            CurrencyId = await store.ResolveCurrencyIdAsync(c.Currency, ct),
            LeadTimeDays = c.LeadTimeDays,
            IsPreferred = c.IsPreferred,
        }, clearOtherPreferred: c.IsPreferred, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> SetPriceAsync(SetProductPriceCommand c, CancellationToken ct = default)
    {
        if (c.Amount < 0) return Error.Validation("catalog.price_negative", "Un prix ne peut être négatif.");
        // Mêmes valeurs que la contrainte product_price_type_chk : un type inconnu est un refus clair, pas une erreur 500.
        if (c.PriceType is not ("sale" or "purchase" or "promo"))
            return Error.Validation("catalog.price_type", $"Type de prix « {c.PriceType} » inconnu (sale, purchase ou promo).");
        if (c.QuantityMin < 0) return Error.Validation("catalog.price_quantity", "La quantité minimale ne peut être négative.");
        var at = c.ValidFrom ?? clock.UtcNow;
        var currencyId = await store.ResolveCurrencyIdAsync(c.Currency, ct);

        // Un prix n'en remplace qu'un autre de la même devise : poser le prix USD ne ferme pas le prix HTG.
        await store.ClosePreviousPriceAsync(c.ProductId, c.PriceType, c.CustomerGroupId, c.PriceListId, c.QuantityMin, currencyId, at, ct);
        await store.AddPriceAsync(new ProductPrice
        {
            Id = Guid.NewGuid(),
            ProductId = c.ProductId,
            CompanyId = Company,
            PriceType = c.PriceType,
            CustomerGroupId = c.CustomerGroupId,
            PriceListId = c.PriceListId,
            QuantityMin = c.QuantityMin,
            CurrencyId = currencyId,
            Amount = c.Amount,
            TaxIncluded = c.TaxIncluded,
            MinPrice = c.MinPrice,
            ValidFrom = at,
        }, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> SetReorderRuleAsync(SetReorderRuleCommand c, CancellationToken ct = default)
    {
        await store.UpsertReorderRuleAsync(new ProductReorderRule
        {
            Id = Guid.NewGuid(),
            CompanyId = Company,
            ProductId = c.ProductId,
            InventoryLocationId = c.LocationId,
            MinQuantity = c.MinQuantity,
            MaxQuantity = c.MaxQuantity,
            ReorderPoint = c.ReorderPoint,
            ReorderQuantity = c.ReorderQuantity,
            LeadTimeDays = c.LeadTimeDays,
            AutoOrder = c.AutoOrder,
        }, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> AddBarcodeAsync(Guid productId, string barcode, decimal packSize,
        string? barcodeType = null, bool isPrimary = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(barcode))
            return Error.Validation("catalog.barcode_required", "Le code-barres est obligatoire.");
        if (await store.GetProductAsync(productId, ct) is null)
            return Error.NotFound("catalog.product_not_found", "Produit introuvable.");
        if (await store.BarcodeExistsAsync(Company, barcode.Trim(), ct))
            return Error.Conflict("catalog.barcode_exists", $"Le code-barres « {barcode} » est déjà utilisé (§6).");

        await store.AddBarcodeAsync(new ProductBarcode
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            CompanyId = Company,
            Barcode = barcode.Trim(),
            BarcodeType = string.IsNullOrWhiteSpace(barcodeType) ? "ean13" : barcodeType!.Trim(),
            IsPrimary = isPrimary,
            Status = "active",
            PackSize = packSize <= 0 ? 1m : packSize,
            ActivatedAt = clock.UtcNow,
        }, clearOtherPrimary: isPrimary, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
