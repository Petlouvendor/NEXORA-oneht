import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch, clearStepUpTokens, configureApiClient, setStepUpHandler } from "@/core/apiClient";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Finalisation phase 17 — jeton court renouvelé de façon transparente, confirmation forte des actions critiques. */
describe("client API : sessions et confirmation forte", () => {
  let token = "ancien";
  const onUnauthorized = vi.fn();
  const refresh = vi.fn(async (): Promise<string | null> => { token = "nouveau"; return token; });

  beforeEach(() => {
    token = "ancien";
    onUnauthorized.mockReset();
    refresh.mockClear();
    clearStepUpTokens();
    configureApiClient({ token: () => token, company: () => "c1", onUnauthorized, refresh });
  });
  afterEach(() => { vi.unstubAllGlobals(); setStepUpHandler(null); });

  it("jeton expiré : un seul renouvellement pour des requêtes simultanées, puis nouvel essai avec le nouveau jeton", async () => {
    const fetchMock = vi.fn(async (_u: string, init?: RequestInit) =>
      new Headers(init?.headers).get("Authorization") === "Bearer nouveau" ? json(200, { ok: true }) : json(401, {}));
    vi.stubGlobal("fetch", fetchMock);
    const [a, b] = await Promise.all([apiFetch<{ ok: boolean }>("/a"), apiFetch<{ ok: boolean }>("/b")]);
    expect(a.ok && b.ok).toBe(true);
    expect(refresh).toHaveBeenCalledTimes(1);
    expect(onUnauthorized).not.toHaveBeenCalled();
  });

  it("renouvellement impossible : déconnexion ; jamais de renouvellement sur /auth/", async () => {
    refresh.mockImplementationOnce(async () => null);
    vi.stubGlobal("fetch", vi.fn(async () => json(401, {})));
    await expect(apiFetch("/x")).rejects.toBeInstanceOf(ApiError);
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
    refresh.mockClear();
    await expect(apiFetch("/auth/login", { method: "POST" })).rejects.toBeInstanceOf(ApiError);
    expect(refresh).not.toHaveBeenCalled();
  });

  it("action critique : confirmation demandée, rejouée avec X-Step-Up-Token, puis réutilisée pour la même catégorie", async () => {
    const handler = vi.fn(async (purpose: string) => (purpose === "payment" ? "jeton-confirmation" : null));
    setStepUpHandler(handler);
    const fetchMock = vi.fn(async (_u: string, init?: RequestInit) =>
      new Headers(init?.headers).get("X-Step-Up-Token") === "jeton-confirmation"
        ? json(200, { paid: true })
        : json(403, { title: "security.step_up_required", purpose: "payment", detail: "Confirmez votre identité pour ce paiement." }));
    vi.stubGlobal("fetch", fetchMock);
    expect(await apiFetch<{ paid: boolean }>("/pay/1", { method: "POST" })).toEqual({ paid: true });
    expect(handler).toHaveBeenCalledWith("payment", "Confirmez votre identité pour ce paiement.");
    expect(await apiFetch<{ paid: boolean }>("/pay/2", { method: "POST" })).toEqual({ paid: true });
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it("confirmation annulée : l'action n'est pas faite, erreur explicite", async () => {
    setStepUpHandler(async () => null);
    vi.stubGlobal("fetch", vi.fn(async () => json(403, { title: "security.step_up_required", purpose: "deletion" })));
    await expect(apiFetch("/purge", { method: "POST" })).rejects.toMatchObject({ problem: { title: "security.step_up_cancelled" } });
  });

  it("un 403 ordinaire (permission) n'ouvre pas de confirmation", async () => {
    const handler = vi.fn(async () => "x");
    setStepUpHandler(handler);
    vi.stubGlobal("fetch", vi.fn(async () => json(403, { title: "forbidden" })));
    await expect(apiFetch("/admin")).rejects.toBeInstanceOf(ApiError);
    expect(handler).not.toHaveBeenCalled();
  });
});
