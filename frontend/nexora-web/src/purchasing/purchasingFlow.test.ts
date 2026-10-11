import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import type { ProductDetail } from "@/catalog/catalogApi";
import {
  receiveGoods, recordSupplierInvoice, updatePurchaseOrder, type PurchaseOrderDetail,
} from "@/purchasing/purchasingApi";
import { estimateLine, estimateTotals, orderEditable, orderReceivable, purchaseUnits } from "@/purchasing/purchasingShared";
import { receiveLinesFromOrder } from "@/purchasing/ReceiptsPanel";
import { invoiceLinesFromOrder } from "@/purchasing/InvoicesPanel";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const order: PurchaseOrderDetail = {
  id: "po1", documentNumber: "PO-1", status: "partially_received", supplierId: "s1", supplierName: "Grossiste", warehouseId: null,
  currency: "HTG", orderDate: "2026-09-01", expectedDate: null, notes: null, subtotal: 24000, taxTotal: 0, total: 24000, createdByName: null,
  lines: [
    { id: "l1", productId: "p1", sku: "BIERE", productName: "Bière", baseUnitCode: "BTL", quantityOrdered: 240, quantityReceived: 192,
      remaining: 48, unitPrice: 100, taxId: null, discount: 240, lineTotal: 23760, taxAmount: 0,
      purchaseUnitId: "crt", purchaseUnitCode: "CRT", purchaseQuantity: 10 },
    { id: "l2", productId: "p2", sku: "EAU", productName: "Eau", baseUnitCode: "BTL", quantityOrdered: 5, quantityReceived: 5,
      remaining: 0, unitPrice: 20, taxId: "t1", discount: 0, lineTotal: 100, taxAmount: 10,
      purchaseUnitId: null, purchaseUnitCode: null, purchaseQuantity: null },
  ],
  receipts: [], invoices: [],
};

describe("Achats — calculs d'estimation (même règle que le serveur)", () => {
  it("net = qté × prix − remise ; taxe arrondie au centime", () => {
    expect(estimateLine(3, 33.333, 0, 10)).toEqual({ net: 100, tax: 10, total: 110 });
    expect(estimateLine(20, 100, 200, 10)).toEqual({ net: 1800, tax: 180, total: 1980 });
    expect(estimateTotals([
      { quantity: 10, unitPrice: 100, discount: 0, ratePercent: 10 },
      { quantity: 1, unitPrice: 50, discount: 0, ratePercent: 0 },
    ])).toEqual({ subtotal: 1050, tax: 100, total: 1150 });
  });

  it("états de commande : modifiable avant approbation, réceptionnable après", () => {
    expect(orderEditable("draft")).toBe(true);
    expect(orderEditable("approved")).toBe(false);
    expect(orderReceivable("sent")).toBe(true);
    expect(orderReceivable("submitted")).toBe(false);
  });

  it("unités d'achat : unité de base + unités convertibles vers elle", () => {
    const p = {
      unitId: "btl", unitCode: "BTL",
      conversions: [
        { id: "c1", productId: "p1", fromUnitId: "crt", fromUnitCode: "CRT", toUnitId: "btl", toUnitCode: "BTL", factor: 24 },
        { id: "c2", productId: null, fromUnitId: "btl", fromUnitCode: "BTL", toUnitId: "ml", toUnitCode: "ML", factor: 330 },
      ],
    } as unknown as ProductDetail;
    expect(purchaseUnits(p)).toEqual([{ id: "btl", code: "BTL", factor: 1 }, { id: "crt", code: "CRT", factor: 24 }]);
  });
});

describe("Achats — préremplissage depuis une commande", () => {
  it("la réception propose le reste à recevoir, en cartons s'il tombe juste", () => {
    const lines = receiveLinesFromOrder(order);
    expect(lines).toHaveLength(1);                        // la ligne entièrement reçue n'est pas proposée
    expect(lines[0].unitId).toBe("crt");
    expect(lines[0].accepted).toBe("2");                  // 48 BTL = 2 CRT
    expect(lines[0].unitCost).toBe("2400");               // 100 × 24
    expect(lines[0].poLineId).toBe("l1");
  });

  it("la facture reprend les quantités REÇUES au prix commandé, remise au prorata", () => {
    const lines = invoiceLinesFromOrder(order);
    expect(lines[0].label).toBe("Bière");                 // désignation sans le SKU (affiché à part)
    expect(lines.map((l) => [l.productId, l.quantity, l.unitPrice, l.discount, l.taxId])).toEqual([
      ["p1", "192", "100", "192", ""],
      ["p2", "5", "20", "", "t1"],
    ]);
  });
});

describe("Achats — appels API", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("réception et facture portent l'identifiant d'opération comme clé d'idempotence", async () => {
    const seen: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seen.push({ url, init });
      return jsonResponse(200, url.includes("invoices") ? { invoiceId: "i1", payableId: "p1", journalEntryId: "j", entryNumber: "E", wasReplay: false } : "r1");
    }));
    await receiveGoods({ operationUuid: "op-r", supplierId: "s1", inventoryLocationId: "loc", purchaseOrderId: "po1", receivedAt: null, notes: null,
      lines: [{ productId: "p1", purchaseOrderLineId: "l1", quantity: 2, unitCost: 2400, lotNumber: null, expirationDate: null, unitId: "crt",
        quantityRejected: 1, rejectionReason: "casse" }] });
    await recordSupplierInvoice({ operationUuid: "op-i", supplierId: "s1", purchaseOrderId: "po1", currency: "HTG", invoiceDate: "2026-09-02",
      dueDate: null, onCredit: true, tenderFinancialAccountId: null, lines: [], capitaliseToInventory: true, supplierReference: "F1", goodsReceiptId: null });
    expect(new Headers(seen[0].init.headers).get("Idempotency-Key")).toBe("op-r");
    expect(JSON.parse(seen[0].init.body as string).lines[0]).toMatchObject({ unitId: "crt", quantityRejected: 1, rejectionReason: "casse" });
    expect(new Headers(seen[1].init.headers).get("Idempotency-Key")).toBe("op-i");
  });

  it("modification d'une commande : PUT sur la commande", async () => {
    let seen: { url: string; init: RequestInit } | null = null;
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => { seen = { url, init }; return new Response(null, { status: 204 }); }));
    await updatePurchaseOrder("po1", { supplierId: "s1", warehouseId: null, currency: "HTG", expectedDate: null, notes: null, submit: false,
      lines: [{ productId: "p1", quantity: 1, unitPrice: 10, taxId: null, discount: 0, unitId: null }] });
    expect(seen!.url).toContain("/purchasing/orders/po1");
    expect(seen!.init.method).toBe("PUT");
  });
});
