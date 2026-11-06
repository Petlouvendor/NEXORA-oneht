import { describe, expect, it } from "vitest";
import { norm, offlineRefusal, scanOffline, searchOffline, type OfflineCatalogItem, type OfflinePolicy } from "@/pos/offlineStore";

const item = (p: Partial<OfflineCatalogItem>): OfflineCatalogItem => ({
  productId: p.productId ?? "p", name: p.name ?? "Article", sku: p.sku ?? "SKU", barcode: p.barcode ?? null,
  codes: p.codes ?? [], searchText: p.searchText ?? norm(`${p.name ?? "Article"} ${p.sku ?? "SKU"}`),
  salePrice: p.salePrice ?? 100, currency: "HTG", availability: p.availability ?? "in_stock", packCodes: p.packCodes ?? null,
  weightCodes: p.weightCodes ?? null,
});

const catalog = [
  item({ productId: "a", name: "Café moulu 250 g", sku: "CAF-250", codes: ["3017620422003"] }),
  item({ productId: "b", name: "Crème de café", sku: "CRM-01" }),
  item({ productId: "c", name: "Sucre roux", sku: "SUC-1", codes: ["PLU42"] }),
];

const policy: OfflinePolicy = {
  allowOfflineSales: true, allowOfflineCredit: false, offlineCreditLimit: 0, allowOfflineDiscount: true,
  allowOfflineRefund: false, allowOfflineCustomerPayment: true, maxOfflineMinutes: 720, allowedPaymentMethods: ["cash"],
};

describe("caisse hors ligne (phase 6)", () => {
  it("recherche locale : sans accents ni casse, début du nom en premier, jamais de coût", () => {
    const r = searchOffline(catalog, "CAFE");
    expect(r.map((p) => p.productId)).toEqual(["a", "b"]);
    expect(Object.keys(r[0])).not.toContain("unitCost");
  });

  it("un code exact (code-barres, PLU, SKU) passe devant", () => {
    expect(searchOffline(catalog, "PLU42")[0].productId).toBe("c");
    expect(scanOffline(catalog, "3017620422003")?.productId).toBe("a");
    expect(scanOffline(catalog, "caf-250")?.productId).toBe("a");
    expect(scanOffline(catalog, "000")).toBeNull();
  });

  it("un code de carton ajoute son contenu entier (audit 2026-10, A-015)", () => {
    const packed = [item({ productId: "d", sku: "EAU-1", codes: ["111", "999"], packCodes: ["999=12"] })];
    expect(scanOffline(packed, "999")?.packSize).toBe(12);
    expect(scanOffline(packed, "111")?.packSize).toBe(1);
  });

  it("une étiquette de balance ajoute le poids lu (audit 2026-10, A-015)", () => {
    const weighed = [item({ productId: "w", sku: "VIANDE", codes: ["2012345"], weightCodes: ["2012345"] })];
    const hit = scanOffline(weighed, "2012345012509");
    expect(hit?.productId).toBe("w");
    expect(hit?.packSize).toBe(1.25);
    expect(scanOffline(weighed, "2012345012508")).toBeNull();   // clé de contrôle fausse
  });

  it("la politique hors ligne est appliquée avant la mise en file", () => {
    const now = new Date("2026-09-28T12:00:00Z");
    const ok = { hasDiscount: false, creditAmount: 0, tenderTypes: ["cash"] };
    expect(offlineRefusal(policy, ok, new Date("2026-09-28T11:00:00Z"), now)).toBeNull();
    expect(offlineRefusal(policy, { ...ok, tenderTypes: ["cash", "card"] }, null, now)).toContain("card");
    expect(offlineRefusal(policy, { ...ok, creditAmount: 50 }, null, now)).toContain("crédit");
    expect(offlineRefusal({ ...policy, allowOfflineDiscount: false }, { ...ok, hasDiscount: true }, null, now)).toContain("Remise");
    expect(offlineRefusal({ ...policy, maxOfflineMinutes: 60 }, ok, new Date("2026-09-28T09:00:00Z"), now)).toContain("60 min");
    expect(offlineRefusal({ ...policy, allowOfflineSales: false }, ok, null, now)).toContain("désactivées");
  });
});
