using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Contracts.Catalog;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Catalog;

/// <summary>
/// Promotions et coupons (finalisation produit, phase 1) sur les tables existantes
/// <c>promotion</c>, <c>promotion_target</c>, <c>coupon</c> (V018). Le calcul appliqué en caisse
/// est <see cref="PromotionCalculator"/> ; ce service garantit qu'une règle enregistrée est
/// calculable (type connu, conditions complètes, cibles existantes, période cohérente).
/// </summary>
public sealed class SqlPromotionService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IPromotionService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] TargetTypes = ["all", "product", "category", "brand"];
    private static readonly string[] SimpleStatuses = ["active", "inactive"];

    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Aucune entreprise active.");

    private sealed record PromoRow(
        Guid Id, string Name, string PromoType, decimal Value, short Priority,
        DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string Status, string Conditions);

    private sealed record TargetRow(Guid PromotionId, string TargetType, Guid? ProductId, Guid? CategoryId, Guid? BrandId, string? Label);

    public async Task<IReadOnlyList<PromotionDto>> ListAsync(CancellationToken ct = default)
    {
        var promos = await db.Database.SqlQuery<PromoRow>($"""
            SELECT id AS "Id", name AS "Name", promo_type AS "PromoType", value AS "Value", priority AS "Priority",
                   starts_at AS "StartsAt", ends_at AS "EndsAt", status AS "Status", conditions::text AS "Conditions"
            FROM nexora.promotion WHERE company_id = {Company}
            ORDER BY status, priority DESC, name
            """).ToListAsync(ct);
        var targets = await db.Database.SqlQuery<TargetRow>($"""
            SELECT pt.promotion_id AS "PromotionId", pt.target_type AS "TargetType", pt.product_id AS "ProductId",
                   pt.category_id AS "CategoryId", pt.brand_id AS "BrandId",
                   COALESCE(p.name, c.name, b.name) AS "Label"
            FROM nexora.promotion_target pt
            LEFT JOIN nexora.product p ON p.id = pt.product_id
            LEFT JOIN nexora.product_category c ON c.id = pt.category_id
            LEFT JOIN nexora.brand b ON b.id = pt.brand_id
            WHERE pt.company_id = {Company}
            """).ToListAsync(ct);
        var byPromo = targets.ToLookup(t => t.PromotionId);

        return promos.Select(p =>
        {
            var c = PromotionResolver.ParseConditions(p.Conditions);
            return new PromotionDto(p.Id, p.Name, p.PromoType, p.Value, p.Priority, p.StartsAt, p.EndsAt, p.Status,
                new PromotionConditionsDto(c.MinQuantity, c.BuyQuantity, c.GetQuantity, c.BundleQuantity),
                byPromo[p.Id].Select(t => new PromotionTargetDto(t.TargetType, t.ProductId, t.CategoryId, t.BrandId, t.Label)).ToList());
        }).ToList();
    }

    public async Task<Result<Guid>> CreateAsync(SavePromotionRequest r, CancellationToken ct = default)
    {
        var check = await ValidateAsync(r, ct);
        if (!check.IsSuccess) return check.Error!;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.promotion (id, company_id, name, promo_type, value, priority, starts_at, ends_at, conditions, status)
            VALUES ({id}, {Company}, {r.Name.Trim()}, {r.PromoType}, {r.Value}, {r.Priority}, {r.StartsAt}, {r.EndsAt},
                    {ConditionsJson(r)}::jsonb, 'active')
            """, ct);
        await InsertTargetsAsync(id, r.Targets, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> UpdateAsync(Guid id, SavePromotionRequest r, CancellationToken ct = default)
    {
        var check = await ValidateAsync(r, ct);
        if (!check.IsSuccess) return check.Error!;
        return await uow.ExecuteInTransactionAsync<Result>(async token =>
        {
            var n = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.promotion
                   SET name = {r.Name.Trim()}, promo_type = {r.PromoType}, value = {r.Value}, priority = {r.Priority},
                       starts_at = {r.StartsAt}, ends_at = {r.EndsAt}, conditions = {ConditionsJson(r)}::jsonb, updated_at = now()
                 WHERE id = {id} AND company_id = {Company}
                """, token);
            if (n == 0) return Error.NotFound("catalog.promotion_not_found", "Promotion introuvable.");
            await db.Database.ExecuteSqlAsync($"""
                DELETE FROM nexora.promotion_target WHERE promotion_id = {id} AND company_id = {Company}
                """, token);
            await InsertTargetsAsync(id, r.Targets, token);
            return Result.Success();
        }, ct);
    }

    public async Task<Result> SetStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        if (!SimpleStatuses.Contains(status))
            return Error.Validation("catalog.promotion_status", "Statut attendu : active ou inactive.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.promotion SET status = {status}, updated_at = now() WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (n == 0) return Error.NotFound("catalog.promotion_not_found", "Promotion introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ Coupons

    public async Task<IReadOnlyList<CouponDto>> ListCouponsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<CouponDto>($"""
            SELECT id AS "Id", code AS "Code", promotion_id AS "PromotionId", discount_type AS "DiscountType",
                   discount_value AS "DiscountValue", usage_limit AS "UsageLimit", used_count AS "UsedCount",
                   valid_from AS "ValidFrom", valid_to AS "ValidTo", status AS "Status"
            FROM nexora.coupon WHERE company_id = {Company} ORDER BY status, code
            """).ToListAsync(ct);

    public async Task<Result<Guid>> CreateCouponAsync(CreateCouponRequest r, CancellationToken ct = default)
    {
        var code = (r.Code ?? "").Trim().ToUpperInvariant();
        if (code.Length is < 3 or > 40)
            return Error.Validation("catalog.coupon_code", "Le code du coupon doit compter de 3 à 40 caractères.");
        if (r.DiscountType is not ("percentage" or "amount"))
            return Error.Validation("catalog.coupon_type", "Type de remise attendu : percentage ou amount.");
        if (r.DiscountValue <= 0 || (r.DiscountType == "percentage" && r.DiscountValue > 100))
            return Error.Validation("catalog.coupon_value", "La remise doit être positive (100 % au plus).");
        if (r.UsageLimit is <= 0)
            return Error.Validation("catalog.coupon_limit", "La limite d'utilisation doit être positive (ou vide = illimitée).");
        if (r.ValidFrom is { } f && r.ValidTo is { } t && t <= f)
            return Error.Validation("catalog.coupon_period", "La fin de validité doit suivre le début.");
        var exists = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.coupon WHERE company_id = {Company} AND upper(code) = {code}) AS "Value"
            """).FirstAsync(ct);
        if (exists) return Error.Conflict("catalog.coupon_exists", $"Le coupon « {code} » existe déjà.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.coupon (id, company_id, code, discount_type, discount_value, usage_limit, valid_from, valid_to, status)
            VALUES ({id}, {Company}, {code}, {r.DiscountType}, {r.DiscountValue}, {r.UsageLimit}, {r.ValidFrom}, {r.ValidTo}, 'active')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> SetCouponStatusAsync(Guid id, string status, CancellationToken ct = default)
    {
        if (!SimpleStatuses.Contains(status))
            return Error.Validation("catalog.coupon_status", "Statut attendu : active ou inactive.");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.coupon SET status = {status}, updated_at = now() WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (n == 0) return Error.NotFound("catalog.coupon_not_found", "Coupon introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // ------------------------------------------------------------------ Contrôles

    private async Task<Result> ValidateAsync(SavePromotionRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name))
            return Error.Validation("catalog.promotion_name", "Le nom de la promotion est obligatoire.");
        if (!PromotionCalculator.Types.Contains(r.PromoType))
            return Error.Validation("catalog.promotion_type", $"Type de promotion « {r.PromoType} » inconnu.");
        if (r.Value < 0) return Error.Validation("catalog.promotion_value", "La valeur ne peut être négative.");
        if (r.PromoType == "percentage" && (r.Value <= 0 || r.Value > 100))
            return Error.Validation("catalog.promotion_value", "Un pourcentage doit être compris entre 0 et 100.");
        if (r.PromoType is "amount" or "price_override" && r.Value <= 0)
            return Error.Validation("catalog.promotion_value", "Le montant doit être strictement positif.");
        if (r.StartsAt is { } s && r.EndsAt is { } e && e <= s)
            return Error.Validation("catalog.promotion_period", "La fin de la promotion doit suivre son début.");

        var c = r.Conditions;
        if (c?.MinQuantity is < 0) return Error.Validation("catalog.promotion_conditions", "La quantité minimale ne peut être négative.");
        if (r.PromoType == "buy_x_get_y" && (c?.BuyQuantity is not > 0 || c.GetQuantity is not > 0
                                             || c.BuyQuantity % 1 != 0 || c.GetQuantity % 1 != 0))
            return Error.Validation("catalog.promotion_conditions",
                "« X achetés, Y offerts » exige deux quantités entières positives (ex. 1 acheté + 1 offert = 2 pour 1).");
        if (r.PromoType == "bundle" && (c?.BundleQuantity is not >= 2 || c.BundleQuantity % 1 != 0 || r.Value <= 0))
            return Error.Validation("catalog.promotion_conditions",
                "Un lot exige une taille entière d'au moins 2 unités et un prix de lot positif.");

        if (r.Targets is null || r.Targets.Count == 0)
            return Error.Validation("catalog.promotion_targets", "Choisissez au moins une cible (tout le catalogue, un produit, une catégorie ou une marque).");
        if (r.Targets.Any(t => t.TargetType == "all") && r.Targets.Count > 1)
            return Error.Validation("catalog.promotion_targets", "« Tout le catalogue » ne se combine pas avec d'autres cibles.");
        foreach (var t in r.Targets)
        {
            if (!TargetTypes.Contains(t.TargetType))
                return Error.Validation("catalog.promotion_targets", $"Type de cible « {t.TargetType} » inconnu.");
            var (table, target) = t.TargetType switch
            {
                "product" => ("product", t.ProductId),
                "category" => ("product_category", t.CategoryId),
                "brand" => ("brand", t.BrandId),
                _ => ((string?)null, (Guid?)null),
            };
            if (table is null) continue;
            if (target is null) return Error.Validation("catalog.promotion_targets", "Chaque cible doit désigner un élément.");
            var ok = table switch
            {
                "product" => await db.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM nexora.product WHERE id = {target} AND company_id = {Company}) AS "Value" """).FirstAsync(ct),
                "product_category" => await db.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM nexora.product_category WHERE id = {target} AND company_id = {Company}) AS "Value" """).FirstAsync(ct),
                _ => await db.Database.SqlQuery<bool>($"""SELECT EXISTS(SELECT 1 FROM nexora.brand WHERE id = {target} AND company_id = {Company}) AS "Value" """).FirstAsync(ct),
            };
            if (!ok) return Error.Validation("catalog.promotion_targets", "Une cible désignée n'existe pas dans cette entreprise.");
        }
        return Result.Success();
    }

    private async Task InsertTargetsAsync(Guid promotionId, IReadOnlyList<PromotionTargetDto> targets, CancellationToken ct)
    {
        foreach (var t in targets)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.promotion_target (id, promotion_id, company_id, target_type, product_id, category_id, brand_id)
                VALUES ({Guid.NewGuid()}, {promotionId}, {Company}, {t.TargetType},
                        {(t.TargetType == "product" ? t.ProductId : null)},
                        {(t.TargetType == "category" ? t.CategoryId : null)},
                        {(t.TargetType == "brand" ? t.BrandId : null)})
                """, ct);
    }

    private static string ConditionsJson(SavePromotionRequest r)
    {
        var c = r.Conditions;
        return JsonSerializer.Serialize(new PromotionConditions(
            c?.MinQuantity,
            r.PromoType == "buy_x_get_y" ? c?.BuyQuantity : null,
            r.PromoType == "buy_x_get_y" ? c?.GetQuantity : null,
            r.PromoType == "bundle" ? c?.BundleQuantity : null), Json);
    }
}
