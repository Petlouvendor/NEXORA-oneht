import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  approveDocument, createDocument, requestDeletion, saveRetentionPolicy, searchDocuments, setLegalHold, signAs,
} from "@/ged/gedApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("gedApi — GED, documents, archivage & signature (Phase 45)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("uses /ged-documents (not /documents, already owned by Phase 30) for the library resource", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, []); }));

    await searchDocuments({ text: "facture" });

    expect(urls[0]).toContain("/ged-documents/search");
    expect(urls[0]).not.toMatch(/\/documents\/search/);
  });

  it("create posts the document payload and approve/legal-hold hit their routes", async () => {
    const calls: { url: string; method: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, method: (init?.method ?? "GET").toUpperCase(), body: init?.body as string | undefined });
      return json(200, { document: { id: "d1" }, potentialDuplicateOf: null });
    }));

    await createDocument({ name: "Facture", documentType: "invoice_purchase", contentBase64: "Zm9v", contentType: "application/pdf" });
    await approveDocument("d1");
    await setLegalHold("d1", true, "litige");

    expect(calls[0]).toMatchObject({ url: expect.stringContaining("/ged-documents"), method: "POST" });
    expect(calls[1].url).toContain("/ged-documents/d1/approve");
    expect(calls[2].url).toContain("/ged-documents/d1/legal-hold?hold=true");
  });

  it("retention: savePolicy and requestDeletion post the expected payloads", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body as string | undefined }); return json(200, { id: "p1" });
    }));

    await saveRetentionPolicy({ documentType: "contract", retentionYears: 5, legalBasis: "Politique interne" });
    await requestDeletion("d1", "plus nécessaire");

    expect(calls[0].url).toContain("/document-retention-policies");
    expect(JSON.parse(calls[0].body!)).toMatchObject({ documentType: "contract", retentionYears: 5 });
    expect(calls[1].url).toContain("/ged-documents/d1/request-deletion");
  });

  it("signAs posts to the nested signer route", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, { id: "r1", signers: [] }); }));

    await signAs("r1", "s1", "signé");

    expect(urls[0]).toContain("/signature-requests/r1/signers/s1/sign");
  });
});
