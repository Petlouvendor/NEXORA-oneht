import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { cancelDocument, issueDocument, verifyDocument } from "@/documents/documentsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("documentsApi (Phase 30 §19, §27-28, §31)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("issueDocument sends an idempotency key and the source triplet", async () => {
    let seen: RequestInit = {};
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/documents");
      seen = init;
      return jsonResponse(200, { id: "d1", documentNumber: "INV2026-000001" });
    }));

    await issueDocument("invoice", "sale", "s1");

    expect(new Headers(seen.headers).get("Idempotency-Key")).toBeTruthy();
    const body = JSON.parse(seen.body as string);
    expect(body).toEqual({ type: "invoice", sourceType: "sale", sourceId: "s1", language: null });
  });

  it("verifyDocument hits the public route; cancelDocument sends the reason", async () => {
    const calls: string[] = [];
    let cancelBody = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push(url);
      if (url.includes("/cancel")) cancelBody = init.body as string;
      return jsonResponse(200, { found: true });
    }));

    await verifyDocument("ABC123");
    await cancelDocument("d1", "Erreur de montant");

    expect(calls[0]).toContain("/documents/verify/ABC123");
    expect(JSON.parse(cancelBody)).toEqual({ reason: "Erreur de montant", replacedByDocumentId: null });
  });
});
