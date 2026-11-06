import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { TEST_STATUS, createIntent, intentActions, needsCredentials, setConnectorCredentials, verifyIntent } from "@/integrations/integrationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Finalisation phase 15 — paiements réels / simulation, connecteurs testés honnêtement. */
describe("paiements en ligne", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c1", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("simulation : boutons « Simuler » seulement en test ; réel : vérification auprès du fournisseur seulement", () => {
    expect(intentActions({ mode: "simulation", status: "pending" }, "test")).toEqual({ verify: false, simulate: true, refund: false, refundManual: false });
    expect(intentActions({ mode: "simulation", status: "pending" }, "production").simulate).toBe(false);
    expect(intentActions({ mode: "real", status: "pending" }, "test")).toEqual({ verify: true, simulate: false, refund: false, refundManual: false });
    expect(intentActions({ mode: "simulation", status: "confirmed" }, "test").refund).toBe(true);
    // Paiement réel : aucun fournisseur ne rembourse par API — la marche à suivre, pas un bouton voué au refus.
    expect(intentActions({ mode: "real", status: "confirmed" }, "production")).toEqual({ verify: false, simulate: false, refund: false, refundManual: true });
  });

  it("identifiants demandés pour MonCash seulement ; les cinq statuts de test ont un libellé", () => {
    expect(needsCredentials({ connectorType: "payment", provider: "moncash" })).toBe(true);
    expect(needsCredentials({ connectorType: "payment", provider: "simulated" })).toBe(false);
    expect(needsCredentials({ connectorType: "payment", provider: "card" })).toBe(false);
    expect(Object.keys(TEST_STATUS).sort()).toEqual(["AUTHENTICATION_FAILED", "CONNECTED", "NOT_CONFIGURED", "TIMEOUT", "UNAVAILABLE"]);
  });

  it("création idempotente, identifiants en écriture seule, vérification", async () => {
    const fetchMock = vi.fn(async (_u: string, _i?: RequestInit) => jsonResponse(200, {}));
    vi.stubGlobal("fetch", fetchMock);
    await createIntent({ connectorId: "k", idempotencyKey: "op-1", amount: 500, currency: "HTG", method: "mobile_money", purpose: "receivable",
      customerId: "cu", settlementAccountId: "fa", saleId: null, allocations: null, expiresInMinutes: 60, reference: null });
    await setConnectorCredentials("k", "id", "secret");
    await verifyIntent("i1");
    expect(new Headers(fetchMock.mock.calls[0][1]?.headers).get("Idempotency-Key")).toBe("op-1");
    expect(fetchMock.mock.calls[1][1]?.method).toBe("PUT");
    expect(fetchMock.mock.calls[1][0]).toContain("/integrations/connectors/k/credentials");
    expect(fetchMock.mock.calls[2][0]).toContain("/integrations/payments/intents/i1/verify");
  });
});
