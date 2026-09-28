import { describe, expect, it } from "vitest";
import { displayPrice, lineAmounts, unitPriceExcludingTax } from "@/pos/cartMath";

describe("montants de la caisse (même calcul que le serveur)", () => {
  it("prix HT + TVA 10 % : la caisse encaisse 429, pas 390", () => {
    const p = { salePrice: 390, taxRate: 10, priceIncludesTax: false };
    expect(unitPriceExcludingTax(p)).toBe(390);
    expect(lineAmounts(1, 390, 0, 10)).toEqual({ gross: 390, net: 390, tax: 39, total: 429 });
    expect(displayPrice(p)).toBe(429);
  });

  it("prix saisi TTC : ramené au HT à 4 décimales comme le serveur", () => {
    const p = { salePrice: 110, taxRate: 10, priceIncludesTax: true };
    expect(unitPriceExcludingTax(p)).toBe(100);
    expect(displayPrice(p)).toBe(110);
    expect(lineAmounts(3, 100, 20, 10)).toEqual({ gross: 300, net: 280, tax: 28, total: 308 });
  });

  it("sans taxe : inchangé", () => {
    expect(lineAmounts(2, 300, 0, 0).total).toBe(600);
    expect(displayPrice({ salePrice: 300, taxRate: 0, priceIncludesTax: false })).toBe(300);
  });
});
