import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { OUTBOX_STATUS, TEST_LABEL, addressHint, canRetry, listOutbox, retryOutbox, sendTestMessage, testProvider } from "@/notifications/notificationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Finalisation phase 16 — envois : fournisseurs, journal, relances ; jamais de faux « envoyé ». */
describe("administration des envois", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c1", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("relance : échec, abandon, ou attente d'un fournisseur désormais configuré seulement", () => {
    const configured = { email: true, sms: false };
    expect(canRetry({ status: "failed", channel: "sms" }, configured)).toBe(true);
    expect(canRetry({ status: "dead", channel: "sms" }, configured)).toBe(true);
    expect(canRetry({ status: "not_configured", channel: "email" }, configured)).toBe(true);
    expect(canRetry({ status: "not_configured", channel: "sms" }, configured)).toBe(false);
    expect(canRetry({ status: "sent", channel: "email" }, configured)).toBe(false);
  });

  it("« fournisseur non configuré » est un état à part, distinct d'« envoyé » ; les cinq statuts de test ont un libellé", () => {
    expect(OUTBOX_STATUS.not_configured.label).toBe("Fournisseur non configuré");
    expect(OUTBOX_STATUS.not_configured.tone).not.toBe("success");
    expect(TEST_LABEL.NOT_CONFIGURED.label).toBe("Fournisseur non configuré");
    expect(Object.keys(TEST_LABEL)).toHaveLength(5);
    expect(addressHint("sms")).toBe("+50937001234");
  });

  it("routes : test réel, message de test, journal filtré, relance", async () => {
    const fetchMock = vi.fn(async (_u: string, _i?: RequestInit) => jsonResponse(200, {}));
    vi.stubGlobal("fetch", fetchMock);
    await testProvider("email");
    await sendTestMessage("sms", "+50937001234");
    await listOutbox("not_configured", "email");
    await retryOutbox("o1");
    expect(fetchMock.mock.calls[0][0]).toContain("/notification-delivery/providers/email/test");
    expect(JSON.parse(String(fetchMock.mock.calls[1][1]?.body))).toEqual({ channel: "sms", address: "+50937001234" });
    expect(fetchMock.mock.calls[2][0]).toContain("/notification-delivery/outbox?status=not_configured&channel=email");
    expect(fetchMock.mock.calls[3][0]).toContain("/notification-delivery/outbox/o1/retry");
  });
});
