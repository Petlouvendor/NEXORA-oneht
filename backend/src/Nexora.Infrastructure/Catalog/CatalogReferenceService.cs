using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Mobile;
using Nexora.Contracts.Catalog;
using Nexora.Contracts.Mobile;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Catalog;

/// <summary>
/// Référentiels du catalogue (finalisation produit, phase 1) : unités, conversions, catégories,
/// image produit. L'image réutilise le stockage <c>nexora.attachment</c> (Phase 44) — aucun second
/// système de fichiers ; le produit ne garde qu'un pointeur <c>attachment:{id}</c> dans
/// <c>image_url</c>, servi par la route catalogue sous <c>catalog.view</c>.
/// </summary>
public sealed class SqlCatalogReferenceService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IAttachmentService attachments) : ICatalogReferenceService
{
    public const string ImagePrefix = "attachment:";
    private const int MaxImageBytes = 2 * 1024 * 1024;
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp"];
    private static readonly string[] CategoryStatuses = ["active", "inactive", "archived"];

    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    // --------------------------------------------------- Marques & groupes clients

    public async Task<IReadOnlyList<CurrencyDto>> ListCurrenciesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<CurrencyDto>($"""
            SELECT cur.code AS "Code", cur.name AS "Name", cur.symbol AS "Symbol", cur.decimal_places AS "DecimalPlaces",
                   (cur.id = co.default_currency_id) AS "IsCompanyDefault"
            FROM nexora.currency cur
            CROSS JOIN nexora.company co
            WHERE co.id = {Company} AND cur.status = 'active'
            ORDER BY (cur.id = co.default_currency_id) DESC, cur.code
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<BrandDto>> ListBrandsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<BrandDto>($"""
            SELECT id AS "Id", name AS "Name", status AS "Status" FROM nexora.brand
            WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);

    public async Task<Result<Guid>> CreateBrandAsync(CreateNamedItemRequest r, CancellationToken ct = default)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0) return Error.Validation("catalog.brand_name", "Le nom de la marque est obligatoire.");
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.brand WHERE company_id = {Company} AND lower(name) = lower({name})) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("catalog.brand_exists", $"La marque « {name} » existe déjà.");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.brand (id, company_id, name) VALUES ({id}, {Company}, {name})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<IReadOnlyList<CustomerGroupDto>> ListCustomerGroupsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<CustomerGroupDto>($"""
            SELECT id AS "Id", name AS "Name", description AS "Description", status AS "Status"
            FROM nexora.customer_group WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);

    public async Task<Result<Guid>> CreateCustomerGroupAsync(CreateNamedItemRequest r, CancellationToken ct = default)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0) return Error.Validation("catalog.customer_group_name", "Le nom du groupe est obligatoire.");
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.customer_group WHERE company_id = {Company} AND lower(name) = lower({name})) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("catalog.customer_group_exists", $"Le groupe « {name} » existe déjà.");
        var id = Guid.NewGuid();
        var description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description!.Trim();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.customer_group (id, company_id, name, description) VALUES ({id}, {Company}, {name}, {description})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    // ------------------------------------------------------------------ Unités

    public async Task<IReadOnlyList<UnitDto>> ListUnitsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<UnitDto>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", precision AS "Precision", is_weight AS "IsWeight"
            FROM nexora.unit WHERE company_id = {Company} ORDER BY code
            """).ToListAsync(ct);

    public async Task<Result<Guid>> CreateUnitAsync(CreateUnitRequest r, CancellationToken ct = default)
    {
        var code = (r.Code ?? "").Trim();
        var name = (r.Name ?? "").Trim();
        if (code.Length == 0 || code.Length > 20)
            return Error.Validation("catalog.unit_code", "Le code de l'unité est obligatoire (20 caractères au plus).");
        if (name.Length == 0)
            return Error.Validation("catalog.unit_name", "Le nom de l'unité est obligatoire.");
        if (r.Precision is < 0 or > 6)
            return Error.Validation("catalog.unit_precision", "La précision doit être comprise entre 0 et 6 décimales.");
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.unit WHERE company_id = {Company} AND lower(code) = lower({code})) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("catalog.unit_exists", $"L'unité « {code} » existe déjà.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.unit (id, company_id, code, name, precision, is_weight)
            VALUES ({id}, {Company}, {code}, {name}, {r.Precision}, {r.IsWeight})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    // ------------------------------------------------------------- Conversions

    public async Task<IReadOnlyList<UnitConversionDto>> ListConversionsAsync(Guid? productId, CancellationToken ct = default)
        => await db.Database.SqlQuery<UnitConversionDto>($"""
            SELECT uc.id AS "Id", uc.product_id AS "ProductId",
                   uc.from_unit_id AS "FromUnitId", fu.code AS "FromUnitCode",
                   uc.to_unit_id AS "ToUnitId", tu.code AS "ToUnitCode", uc.factor AS "Factor"
            FROM nexora.unit_conversion uc
            JOIN nexora.unit fu ON fu.id = uc.from_unit_id
            JOIN nexora.unit tu ON tu.id = uc.to_unit_id
            WHERE uc.company_id = {Company}
              AND ({productId}::uuid IS NULL OR uc.product_id = {productId} OR uc.product_id IS NULL)
            ORDER BY uc.product_id NULLS LAST, fu.code, tu.code
            """).ToListAsync(ct);

    public async Task<Result<Guid>> CreateConversionAsync(CreateUnitConversionRequest r, CancellationToken ct = default)
    {
        if (r.Factor <= 0) return Error.Validation("catalog.conversion_factor", "Le facteur de conversion doit être strictement positif.");
        if (r.FromUnitId == r.ToUnitId) return Error.Validation("catalog.conversion_same_unit", "Les deux unités doivent être différentes.");

        var units = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.unit
            WHERE company_id = {Company} AND id IN ({r.FromUnitId}, {r.ToUnitId})
            """).FirstAsync(ct);
        if (units != 2) return Error.Validation("catalog.unit_unknown", "Unité inconnue pour cette entreprise.");
        if (r.ProductId is { } pid)
        {
            var product = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.product WHERE id = {pid} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct);
            if (!product) return Error.NotFound("catalog.product_not_found", "Produit introuvable.");
        }

        // Une conversion et son inverse sont une seule règle : en créer une seconde rendrait le
        // résultat dépendant de l'ordre de lecture — interdit (déterminisme §13).
        var duplicate = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.unit_conversion
                          WHERE company_id = {Company} AND product_id IS NOT DISTINCT FROM {r.ProductId}
                            AND ((from_unit_id = {r.FromUnitId} AND to_unit_id = {r.ToUnitId})
                              OR (from_unit_id = {r.ToUnitId} AND to_unit_id = {r.FromUnitId}))) AS "Value"
            """).FirstAsync(ct);
        if (duplicate)
            return Error.Conflict("catalog.conversion_exists", "Une conversion entre ces deux unités existe déjà (dans un sens ou dans l'autre).");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.unit_conversion (id, company_id, product_id, from_unit_id, to_unit_id, factor)
            VALUES ({id}, {Company}, {r.ProductId}, {r.FromUnitId}, {r.ToUnitId}, {r.Factor})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> DeleteConversionAsync(Guid id, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            DELETE FROM nexora.unit_conversion WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (n == 0) return Error.NotFound("catalog.conversion_not_found", "Conversion introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // -------------------------------------------------------------- Catégories

    private sealed record CategoryRow(Guid Id, Guid? ParentId);

    public async Task<Result> UpdateCategoryAsync(Guid id, UpdateCategoryRequest r, CancellationToken ct = default)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0) return Error.Validation("catalog.category_name_required", "Le nom de catégorie est obligatoire.");
        if (!CategoryStatuses.Contains(r.Status))
            return Error.Validation("catalog.category_status", $"Statut « {r.Status} » inconnu (active, inactive ou archived).");

        var all = await db.Database.SqlQuery<CategoryRow>($"""
            SELECT id AS "Id", parent_id AS "ParentId" FROM nexora.product_category WHERE company_id = {Company}
            """).ToListAsync(ct);
        if (all.All(c => c.Id != id)) return Error.NotFound("catalog.category_not_found", "Catégorie introuvable.");

        if (r.ParentId is { } parent)
        {
            if (all.All(c => c.Id != parent)) return Error.Validation("catalog.category_unknown", "Catégorie parente inconnue.");
            // Remonter les parents depuis le parent choisi : si l'on retombe sur la catégorie, on créerait un cycle.
            var byId = all.ToDictionary(c => c.Id, c => c.ParentId);
            for (Guid? cur = parent; cur is { } c; cur = byId.GetValueOrDefault(c))
                if (c == id) return Error.Validation("catalog.category_cycle", "Une catégorie ne peut pas être rangée sous elle-même ou sous une de ses sous-catégories.");
        }

        var code = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code!.Trim();
        if (code is not null)
        {
            var taken = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.product_category
                              WHERE company_id = {Company} AND code = {code} AND id <> {id}) AS "Value"
                """).FirstAsync(ct);
            if (taken) return Error.Conflict("catalog.category_code_exists", $"Le code « {code} » existe déjà.");
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.product_category
               SET name = {name}, code = {code}, parent_id = {r.ParentId}, status = {r.Status}, updated_at = now()
             WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------ Image produit

    public async Task<Result> SetProductImageAsync(Guid productId, SetProductImageRequest r, CancellationToken ct = default)
    {
        var type = (r.ContentType ?? "").Trim().ToLowerInvariant();
        if (!ImageTypes.Contains(type))
            return Error.Validation("catalog.image_type", "Format d'image accepté : PNG, JPEG ou WebP.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(r.ContentBase64 ?? ""); }
        catch (FormatException) { return Error.Validation("catalog.image_content", "Contenu d'image invalide."); }
        if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
            return Error.Validation("catalog.image_size", "L'image doit peser 2 Mo au plus.");

        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.product WHERE id = {productId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);
        if (!exists) return Error.NotFound("catalog.product_not_found", "Produit introuvable.");

        var uploaded = await attachments.UploadAsync(
            new UploadAttachmentRequest("photo", "product", productId, r.ContentBase64!, type), ct);
        if (!uploaded.IsSuccess) return uploaded.Error!;

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.product SET image_url = {ImagePrefix + uploaded.Value!.Id}, updated_at = now()
             WHERE id = {productId} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<(byte[] Content, string ContentType)>> GetProductImageAsync(Guid productId, CancellationToken ct = default)
    {
        var url = await db.Database.SqlQuery<string?>($"""
            SELECT image_url AS "Value" FROM nexora.product WHERE id = {productId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (url is null || !url.StartsWith(ImagePrefix, StringComparison.Ordinal)
            || !Guid.TryParse(url[ImagePrefix.Length..], out var attachmentId))
            return Error.NotFound("catalog.image_not_found", "Aucune image enregistrée pour ce produit.");
        return await attachments.GetContentAsync(attachmentId, ct);
    }
}

/// <summary>Voir <see cref="IUnitConverter"/> — ordre de résolution figé, jamais de chaînage implicite.</summary>
public sealed class SqlUnitConverter(NexoraDbContext db, ITenantContext tenant) : IUnitConverter
{
    private sealed record Rule(Guid? ProductId, Guid FromUnitId, Guid ToUnitId, decimal Factor);

    public async Task<Result<UnitConversionResultDto>> ConvertAsync(
        Guid? productId, Guid fromUnitId, Guid toUnitId, decimal quantity, CancellationToken ct = default)
    {
        if (fromUnitId == toUnitId) return Result.Success(new UnitConversionResultDto(quantity, quantity, "identity"));
        var company = tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

        var rules = await db.Database.SqlQuery<Rule>($"""
            SELECT product_id AS "ProductId", from_unit_id AS "FromUnitId", to_unit_id AS "ToUnitId", factor AS "Factor"
            FROM nexora.unit_conversion
            WHERE company_id = {company}
              AND (product_id IS NULL OR product_id = {productId})
              AND ((from_unit_id = {fromUnitId} AND to_unit_id = {toUnitId})
                OR (from_unit_id = {toUnitId} AND to_unit_id = {fromUnitId}))
            """).ToListAsync(ct);

        var candidates = new (Rule? Rule, bool Inverse, string Name)[]
        {
            (rules.FirstOrDefault(x => x.ProductId is not null && x.FromUnitId == fromUnitId), false, "product"),
            (rules.FirstOrDefault(x => x.ProductId is not null && x.FromUnitId == toUnitId), true, "product_inverse"),
            (rules.FirstOrDefault(x => x.ProductId is null && x.FromUnitId == fromUnitId), false, "generic"),
            (rules.FirstOrDefault(x => x.ProductId is null && x.FromUnitId == toUnitId), true, "generic_inverse"),
        };
        foreach (var (rule, inverse, name) in candidates)
        {
            if (rule is null) continue;
            var converted = inverse ? quantity / rule.Factor : quantity * rule.Factor;
            return Result.Success(new UnitConversionResultDto(quantity, Math.Round(converted, 6, MidpointRounding.ToEven), name));
        }
        return Error.Validation("catalog.conversion_missing", "Aucune conversion définie entre ces deux unités.");
    }
}
