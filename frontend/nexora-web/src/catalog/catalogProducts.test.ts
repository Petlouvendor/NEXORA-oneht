import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  convertUnits, createConversion, createProduct, createPromotion, setProductPrice, setReorderRule, updateCategory,
} from "@/catalog/catalogApi";
import { categoryPath, num, sortedCategories } from "@/catalog/catalogShared";
import { describePromotion } from "@/catalog/PromotionsPanel";
import type { Category } from "@/catalog/catalogApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("catalogApi — finalisation produit (phase 1)", () => {
  const calls: Array<{ url: string; init: RequestInit }> = [];
  beforeEach(() => {
    calls.length = 0;
    configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} });
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, init });
      return jsonResponse(200, url.includes("/convert") ? { quantity: 10, converted: 240, rule: "generic" } : "id-1");
    }));
  });
  afterEach(() => vi.unstubAllGlobals());

  const body = (i = 0) => JSON.parse(calls[i].init.body as string);

  it("createProduct posts the full sheet and returns the new id", async () => {
    const id = await createProduct({
      sku: "BTL-1", name: "Eau 50 cl", description: null, unitId: "u1", productType: "stockable", barcode: "123",
      categoryId: null, brandId: null, defaultTaxId: null, trackLots: false, isWeighed: false, shortName: null,
      pluCode: null, status: "active",
    });
    expect(id).toBe("id-1");
    expect(calls[0].url).toContain("/catalog/products");
    expect(calls[0].init.method).toBe("POST");
    expect(body().unitId).toBe("u1");
  });

  it("setProductPrice and setReorderRule target the product and carry its id", async () => {
    await setProductPrice("p1", {
      priceType: "sale", amount: 75, currency: "HTG", quantityMin: 0, customerGroupId: null, validFrom: null,
      priceListId: null, taxIncluded: true, minPrice: 60,
    });
    await setReorderRule("p1", { minQuantity: 5, reorderPoint: 10, maxQuantity: 50, reorderQuantity: 40, leadTimeDays: 3, autoOrder: false });
    expect(calls[0].url).toContain("/catalog/products/p1/prices");
    expect(body(0)).toMatchObject({ productId: "p1", amount: 75, taxIncluded: true, minPrice: 60 });
    expect(calls[1].url).toContain("/catalog/products/p1/reorder-rule");
    expect(calls[1].init.method).toBe("PUT");
    expect(body(1)).toMatchObject({ productId: "p1", locationId: null, reorderPoint: 10 });
  });

  it("unit conversion is always asked to the server", async () => {
    await createConversion({ productId: null, fromUnitId: "crt", toUnitId: "btl", factor: 24 });
    const r = await convertUnits({ fromUnitId: "crt", toUnitId: "btl", quantity: 10 });
    expect(body(0)).toEqual({ productId: null, fromUnitId: "crt", toUnitId: "btl", factor: 24 });
    expect(calls[1].url).toContain("/catalog/units/convert?fromUnitId=crt&toUnitId=btl&quantity=10");
    expect(calls[1].url).not.toContain("productId");
    expect(r.converted).toBe(240);
  });

  it("updateCategory and createPromotion use the dedicated routes", async () => {
    await updateCategory("c1", { name: "Boissons", code: null, parentId: null, status: "inactive" });
    await createPromotion({
      name: "2 pour 1", promoType: "buy_x_get_y", value: 0, priority: 5, startsAt: null, endsAt: null,
      conditions: { minQuantity: null, buyQuantity: 1, getQuantity: 1, bundleQuantity: null },
      targets: [{ targetType: "product", productId: "p1", categoryId: null, brandId: null }],
    });
    expect(calls[0].url).toContain("/catalog/categories/c1");
    expect(calls[0].init.method).toBe("PUT");
    expect(calls[1].url).toContain("/catalog/promotions");
    expect(body(1).conditions).toMatchObject({ buyQuantity: 1, getQuantity: 1 });
  });
});

describe("catalogShared", () => {
  const cats: Category[] = [
    { id: "a", parentId: null, name: "Boissons", code: null, status: "active" },
    { id: "b", parentId: "a", name: "Sodas", code: null, status: "active" },
    { id: "c", parentId: null, name: "Alimentaire", code: null, status: "active" },
  ];

  it("builds a readable category path and sorts by it", () => {
    expect(categoryPath(cats, "b")).toBe("Boissons › Sodas");
    expect(sortedCategories(cats).map((c) => c.path)).toEqual(["Alimentaire", "Boissons", "Boissons › Sodas"]);
  });

  it("never loops on a corrupted cycle", () => {
    const loop: Category[] = [
      { id: "x", parentId: "y", name: "X", code: null, status: "active" },
      { id: "y", parentId: "x", name: "Y", code: null, status: "active" },
    ];
    expect(categoryPath(loop, "x").split(" › ").length).toBeLessThanOrEqual(10);
  });

  it("parses numbers leniently and rejects garbage", () => {
    expect(num("12,5")).toBe(12.5);
    expect(num("")).toBeNull();
    expect(num("abc")).toBeNull();
  });

  it("describes promotions with the same parameters the server computes with", () => {
    const c = { minQuantity: null, buyQuantity: 1, getQuantity: 1, bundleQuantity: null };
    expect(describePromotion({ promoType: "buy_x_get_y", value: 0, conditions: c }, "HTG")).toBe("1 acheté(s) + 1 offert(s)");
    expect(describePromotion({ promoType: "percentage", value: 10, conditions: { ...c, minQuantity: 3 } }, "HTG")).toBe("−10 % dès 3 unités");
  });
});
