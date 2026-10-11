import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { completeOrder, recordDeposit, updateOrder } from "@/sales/ordersApi";
import { cancelQuote, convertQuote, searchDocuments } from "@/documents/documentsApi";
import { estimateSales, orderDeliverable, toLineInputs, validateLines, type SalesLine } from "@/sales/salesShared";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const line = (patch: Partial<SalesLine> = {}): SalesLine => ({
  productId: "p1", sku: "SKU", name: "Article", catalogPrice: 100, quantity: "2", unitPrice: "", discount: "", taxId: "", ...patch,
});

describe("Ventes — saisie des lignes", () => {
  it("prix vide = prix catalogue (fixé par le serveur), prix saisi = dérogation", () => {
    expect(toLineInputs([line(), line({ productId: "p2", unitPrice: "80", discount: "10" })])).toEqual([
      { productId: "p1", quantity: 2, unitPriceOverride: null, lineDiscount: 0, taxId: null },
      { productId: "p2", quantity: 2, unitPriceOverride: 80, lineDiscount: 10, taxId: null },
    ]);
  });

  it("contrôles : quantité, article sans prix, remise hors montant", () => {
    expect(validateLines([line({ quantity: "0" })]).p1).toMatch(/Quantité/);
    expect(validateLines([line({ catalogPrice: null })]).p1).toMatch(/sans prix/);
    expect(validateLines([line({ discount: "500" })]).p1).toMatch(/Remise/);
    expect(validateLines([line()])).toEqual({});
  });

  it("estimation avec la taxe choisie", () => {
    const taxes = [{ id: "t", code: "TCA", name: "TCA", rate: 10, type: "vat", isInclusive: false, status: "active" }];
    expect(estimateSales([line({ taxId: "t" })], taxes)).toEqual({ subtotal: 200, tax: 20, total: 220 });
  });

  it("livrable seulement une fois confirmée", () => {
    expect(orderDeliverable("draft")).toBe(false);
    expect(orderDeliverable("reserved")).toBe(true);
    expect(orderDeliverable("fulfilled")).toBe(false);
  });
});

describe("Ventes — appels API", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("acompte et livraison réutilisent l'identifiant d'opération du dialogue (jamais deux encaissements)", async () => {
    const seen: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seen.push({ url, init });
      return url.includes("complete") ? jsonResponse(200, { saleId: "s", documentNumber: "V1", total: 1, creditAmount: 0, amountPaid: 1 }) : new Response(null, { status: 204 });
    }));
    await recordDeposit("o1", 200, "acc", "op-dep");
    await completeOrder("o1", [{ financialAccountId: "acc", amount: 300 }], false, null, { l1: 2 }, "op-liv");
    expect(new Headers(seen[0].init.headers).get("Idempotency-Key")).toBe("op-dep");
    expect(JSON.parse(seen[0].init.body as string)).toMatchObject({ operationUuid: "op-dep", amount: 200 });
    const body = JSON.parse(seen[1].init.body as string);
    expect(new Headers(seen[1].init.headers).get("Idempotency-Key")).toBe("op-liv");
    expect(body).toMatchObject({ operationUuid: "op-liv", lineQuantities: { l1: 2 }, onCredit: false });
    expect(body.tenders[0]).toMatchObject({ financialAccountId: "acc", paymentMethodId: null, amount: 300 });
  });

  it("devis : conversion avec emplacement, annulation motivée ; documents d'une source", async () => {
    const seen: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seen.push({ url, init });
      return url.includes("/documents") ? jsonResponse(200, []) : url.includes("convert") ? jsonResponse(200, "order-1") : new Response(null, { status: 204 });
    }));
    await convertQuote("q1", { inventoryLocationId: "loc", orderType: "pickup" });
    await cancelQuote("q1", "Doublon");
    await searchDocuments({ sourceType: "sale", sourceId: "s1" });
    await updateOrder("o1", { currency: "HTG", orderType: "pickup", onCredit: false, lines: [] });
    expect(JSON.parse(seen[0].init.body as string)).toMatchObject({ inventoryLocationId: "loc", orderType: "pickup", onCredit: false });
    expect(seen[1].url).toContain("/quotes/q1/cancel");
    expect(JSON.parse(seen[1].init.body as string)).toEqual({ reason: "Doublon" });
    expect(seen[2].url).toContain("sourceType=sale");
    expect(seen[2].url).toContain("sourceId=s1");
    expect(seen[3].init.method).toBe("PUT");
  });
});
