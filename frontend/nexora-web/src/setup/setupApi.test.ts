import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, configureApiClient } from "@/core/apiClient";
import { installNexora, passwordProblem, testDatabase, verifySetupToken } from "@/setup/setupApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), {
    status, headers: { "Content-Type": "application/json" },
  });
}

const db = { host: "localhost", port: 5432, database: "nexora", username: "postgres", password: "x", sslMode: "Prefer" };

/** Résumé fonctionnel §20 — contrat du client de l'assistant d'installation avec l'API. */
describe("setupApi (installation initiale)", () => {
  beforeEach(() => configureApiClient({ token: () => null, company: () => null, onUnauthorized: () => {} }));
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("le jeton d'installation part en en-tête, jamais dans l'URL", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) => jsonResponse(204, null));
    vi.stubGlobal("fetch", fetchMock);
    await verifySetupToken("  jeton-123  ");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/setup\/verify-token$/);
    expect(new Headers(init?.headers).get("X-Setup-Token")).toBe("jeton-123");
  });

  it("le test de connexion transmet les paramètres PostgreSQL à l'API (le navigateur ne se connecte jamais à la base)", async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) =>
      jsonResponse(200, { connected: true, ready: true, messages: ["ok"] }));
    vi.stubGlobal("fetch", fetchMock);
    const r = await testDatabase("t", db);
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toMatch(/\/setup\/database\/test$/);
    expect(JSON.parse(String(init?.body))).toEqual(db);
    expect(r.ready).toBe(true);
  });

  it("une installation refusée remonte le message de l'API", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(422, {
      title: "setup.database_not_ready", detail: "La base « nexora » contient déjà des données Nexora One.",
    })));
    const call = installNexora("t", {
      database: db,
      company: { name: "Boutique", activityType: "boutique", currency: "HTG" },
      admin: { fullName: "A", email: "a@b.ht", password: "Motdepasse2026" },
      settings: { requireCashSession: true, cashVarianceThreshold: 500, maxDiscountPercent: 15, allowNegativeStock: false },
    });
    await expect(call).rejects.toBeInstanceOf(ApiError);
    await expect(call).rejects.toMatchObject({ problem: { status: 422, title: "setup.database_not_ready" } });
  });

  it("application non installée : toute réponse 503 setup.required renvoie vers l'assistant", async () => {
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, pathname: "/", assign });
    vi.stubGlobal("fetch", vi.fn(async () => jsonResponse(503, { title: "setup.required", detail: "Pas installé" })));
    await expect(verifySetupToken("t")).rejects.toBeInstanceOf(ApiError);
    expect(assign).toHaveBeenCalledWith("/setup");
  });

  it("mot de passe administrateur : mêmes règles que l'API", () => {
    expect(passwordProblem("court1", "direction@boutique.ht")).toContain("10 caractères");
    expect(passwordProblem("seulementdeslettres", "a@b.ht")).toContain("une lettre et un chiffre");
    expect(passwordProblem("direction2026!", "direction@boutique.ht")).toContain("identifiant");
    expect(passwordProblem("Boutique2026!", "direction@boutique.ht")).toBeNull();
  });
});
