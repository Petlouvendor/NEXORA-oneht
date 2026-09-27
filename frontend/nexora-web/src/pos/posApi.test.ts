import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  getCurrentSession, lookupSale, managerOverride, requestDiscount, requestReturn, scanProduct, searchProducts,
} from "@/pos/posApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), {
    status, headers: { "Content-Type": "application/json" },
  });
}

/** Guide fonctionnel §9, §10, §12, §13 — contrat du client POS avec l'API. */
describe("posApi (guide fonctionnel développeur)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c1", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("recherche POS : endpoint dédié borné, terme encodé, emplacement transmis", async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(200, { items: [], minChars: 2, maxResults: 8 }));
    vi.stubGlobal("fetch", fetchMock);
    const r = await searchProducts("ea & co", "loc-1");
    const url = fetchMock.mock.calls[0][0];
    expect(url).toContain("/pos/products/search?q=ea%20%26%20co&locationId=loc-1");
    expect(url).not.toContain("/catalog/");   // jamais le catalogue de gestion
    expect(r.maxResults).toBe(8);
  });

  it("scan : code exact encodé", async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(200, { productId: "p1" }));
    vi.stubGlobal("fetch", fetchMock);
    await scanProduct("0123 456");
    expect(fetchMock.mock.calls[0][0]).toContain("/pos/products/scan?code=0123%20456");
  });

  it("session courante : 204 → aucune session", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(204, null)));
    await expect(getCurrentSession()).resolves.toBeUndefined();
  });

  it("demande de retour : aucun montant envoyé, clé d'idempotence = UUID d'opération", async () => {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      const body = JSON.parse(String(init.body));
      expect(body).not.toHaveProperty("refundAmount");
      expect(body.lines[0]).toEqual({ saleLineId: "l1", quantity: 1, restock: true });
      expect(new Headers(init.headers).get("Idempotency-Key")).toBe("op-1");
      return jsonResponse(200, { id: "r1" });
    });
    vi.stubGlobal("fetch", fetchMock);
    await requestReturn({
      operationUuid: "op-1", saleId: "s1", inventoryLocationId: "loc", cashSessionId: null,
      reason: "Défectueux", refundMethod: "cash", refundFinancialAccountId: null,
      lines: [{ saleLineId: "l1", quantity: 1, restock: true }],
    });
    expect(fetchMock.mock.calls[0][0]).toContain("/pos/return-requests");
  });

  it("demande de remise : liée au panier par son UUID d'opération", async () => {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      expect(JSON.parse(String(init.body)).operationUuid).toBe("cart-1");
      expect(new Headers(init.headers).get("Idempotency-Key")).toBeTruthy();
      return jsonResponse(200, { id: "d1" });
    });
    vi.stubGlobal("fetch", fetchMock);
    await requestDiscount({
      operationUuid: "cart-1", productId: "p1", quantity: 2, requestedDiscount: 40,
      currency: "HTG", inventoryLocationId: "loc", reason: "Emballage abîmé",
    });
  });

  it("validation sur place : un seul appel porte les identifiants du responsable, sans en-tête d'autorisation propre", async () => {
    const fetchMock = vi.fn(async (_url: string, init: RequestInit) => {
      const body = JSON.parse(String(init.body));
      expect(body).toMatchObject({ action: "approve_return", targetId: "r1", email: "gerant@x.demo", password: "secret" });
      // Le jeton envoyé reste celui du caissier : aucun jeton de responsable n'est créé côté client.
      expect(new Headers(init.headers).get("Authorization")).toBe("Bearer t");
      return jsonResponse(200, { action: "approve_return", targetId: "r1", approvedBy: "m1", approvedByName: "Gérant", status: "executed" });
    });
    vi.stubGlobal("fetch", fetchMock);
    const r = await managerOverride({ action: "approve_return", targetId: "r1", email: "gerant@x.demo", password: "secret", mfaCode: null, note: null });
    expect(fetchMock.mock.calls[0][0]).toContain("/pos/manager-override");
    expect(r.approvedByName).toBe("Gérant");
  });

  it("recherche d'une vente par ticket : numéro encodé", async () => {
    const fetchMock = vi.fn(async (_url: string) => jsonResponse(200, { saleId: "s1", lines: [] }));
    vi.stubGlobal("fetch", fetchMock);
    await lookupSale("V-2026/001");
    expect(fetchMock.mock.calls[0][0]).toContain("/pos/sales/lookup?number=V-2026%2F001");
  });
});
