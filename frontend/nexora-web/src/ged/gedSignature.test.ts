import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { cancelSignature, fileName, listSignatureRequests, requestSignature, validateSigners } from "@/ged/gedApi";

const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

describe("GED — workflow de signature (finalisation phase 10)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("valide la liste des signataires comme le serveur", () => {
    expect(validateSigners([])).toBe("Ajoutez au moins un signataire.");
    expect(validateSigners([{ signerType: "user", userId: "" }])).toBe("Signataire 1 : choisissez l'utilisateur.");
    expect(validateSigners([{ signerType: "user", userId: "u1" }, { signerType: "user", userId: "u1" }])).toBe("Signataire 2 : déjà présent dans la liste.");
    expect(validateSigners([{ signerType: "external", externalName: " " }])).toBe("Signataire 1 : nom requis.");
    expect(validateSigners([{ signerType: "external", externalName: "M. X", externalEmail: "pas-un-mail" }])).toBe("Signataire 1 : e-mail invalide.");
    expect(validateSigners([{ signerType: "user", userId: "u1", dueAt: "2000-01-01T00:00:00Z" }])).toContain("échéance");
    expect(validateSigners([{ signerType: "user", userId: "u1" }, { signerType: "external", externalName: "M. X" }])).toBeNull();
  });

  it("envoie la demande avec le message, filtre « mes signatures » et annule avec un motif", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body as string | undefined });
      return url.endsWith("/cancel") ? new Response(null, { status: 204 }) : json(200, []);
    }));
    await requestSignature("d1", [{ signerType: "user", userId: "u1" }], "avant vendredi");
    await listSignatureRequests({ mine: true });
    await cancelSignature("r1", "mauvais signataire");
    expect(calls[0].url).toContain("/ged-documents/d1/signature");
    expect(JSON.parse(calls[0].body!)).toEqual({ signers: [{ signerType: "user", userId: "u1" }], message: "avant vendredi" });
    expect(calls[1].url).toContain("/signature-requests?mine=true");
    expect(calls[2].url).toContain("/signature-requests/r1/cancel");
    expect(JSON.parse(calls[2].body!)).toBe("mauvais signataire");
  });

  it("nomme le fichier téléchargé d'après le document, la version et le format", () => {
    expect(fileName("Bail : local/2", "application/pdf", 2)).toBe("Bail _ local_2 (v2).pdf");
    expect(fileName("Photo", "image/png")).toBe("Photo.png");
  });
});
