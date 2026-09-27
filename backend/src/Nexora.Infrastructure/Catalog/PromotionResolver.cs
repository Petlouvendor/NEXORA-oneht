using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Catalog;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Catalog;

/// <summary>
/// Sélection unique de la promotion applicable à un produit (encaissement ET simulateur de prix).
/// Ordre déterministe : priorité décroissante, puis la plus ancienne, puis l'identifiant — deux
/// promotions de même priorité ne donnent jamais un résultat différent d'une lecture à l'autre.
/// </summary>
internal static class PromotionResolver
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Row(Guid Id, string Name, string PromoType, decimal Value, string Conditions);

    public static async Task<PromotionRule?> FindAsync(
        NexoraDbContext db, Guid company, Guid productId, Guid? categoryId, Guid? brandId,
        DateTimeOffset at, CancellationToken ct)
    {
        var row = await db.Database.SqlQuery<Row>($"""
            SELECT pr.id AS "Id", pr.name AS "Name", pr.promo_type AS "PromoType", pr.value AS "Value",
                   pr.conditions::text AS "Conditions"
            FROM nexora.promotion pr
            WHERE pr.company_id = {company} AND pr.status = 'active'
              AND (pr.starts_at IS NULL OR pr.starts_at <= {at})
              AND (pr.ends_at IS NULL OR pr.ends_at >= {at})
              AND EXISTS (SELECT 1 FROM nexora.promotion_target pt
                          WHERE pt.promotion_id = pr.id
                            AND (pt.target_type = 'all'
                                 OR (pt.target_type = 'product'  AND pt.product_id  = {productId})
                                 OR (pt.target_type = 'category' AND pt.category_id = {categoryId})
                                 OR (pt.target_type = 'brand'    AND pt.brand_id    = {brandId})))
            ORDER BY pr.priority DESC, pr.created_at, pr.id
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        return row is null ? null : new PromotionRule(row.Id, row.Name, row.PromoType, row.Value, ParseConditions(row.Conditions));
    }

    public static PromotionConditions ParseConditions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new PromotionConditions();
        try { return JsonSerializer.Deserialize<PromotionConditions>(json, Json) ?? new PromotionConditions(); }
        catch (JsonException) { return new PromotionConditions(); }
    }
}
