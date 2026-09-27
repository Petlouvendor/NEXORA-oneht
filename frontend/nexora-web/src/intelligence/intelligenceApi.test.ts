import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  acceptRecommendation, askAssistant, dismissRecommendation, getBriefing, getIntelligenceDashboard,
  listModels, listRecommendations, listScores, modifyRecommendation, saveRule,
} from "@/intelligence/intelligenceApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("intelligenceApi — Intelligence métier & recommandations (Phase 43)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("recommendations: list carries status/category filters and hits the expected route", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, []); }));

    await listRecommendations("new", "stock_reorder");

    expect(urls[0]).toContain("/intelligence/recommendations");
    expect(urls[0]).toContain("status=new");
    expect(urls[0]).toContain("category=stock_reorder");
  });

  it("recommendations: accept posts createTask flag; dismiss posts a reason", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: init.body as string | undefined }); return json(200, { id: "r1", status: "accepted" });
    }));

    await acceptRecommendation("r1", { createTask: true, assignTo: "u1" });
    await dismissRecommendation("r2", "déjà résolu");

    expect(calls[0].url).toContain("/intelligence/recommendations/r1/accept");
    expect(JSON.parse(calls[0].body!)).toMatchObject({ createTask: true, assignTo: "u1" });
    expect(calls[1].url).toContain("/intelligence/recommendations/r2/dismiss");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ reason: "déjà résolu" });
  });

  it("scores and models hit their read-only routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, []); }));

    await listScores("product");
    await listModels();

    expect(urls[0]).toContain("/intelligence/scores");
    expect(urls[0]).toContain("entityType=product");
    expect(urls[1]).toContain("/intelligence/models");
  });

  it("rules: saveRule posts the full threshold payload", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => { body = init.body as string; return json(200, { id: "rule1" }); }));

    await saveRule({ key: "cashflow_risk", category: "treasury", label: "Test", threshold: { minBalance: 1000 }, defaultPriority: "critical", actionLabel: "Agir", enabled: true });

    expect(JSON.parse(body)).toMatchObject({ key: "cashflow_risk", threshold: { minBalance: 1000 }, enabled: true });
  });

  it("recommendations: modify posts the override value without touching the original proposal (Phase 54 §16, §19)", async () => {
    let seenUrl = "";
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seenUrl = url; body = init.body as string; return json(200, { id: "r1", status: "modified" });
    }));

    await modifyRecommendation("r1", { suggestedQuantity: 400 }, "Ajusté manuellement");

    expect(seenUrl).toContain("/intelligence/recommendations/r1/modify");
    expect(JSON.parse(body)).toEqual({ overrideValue: { suggestedQuantity: 400 }, note: "Ajusté manuellement" });
  });

  it("dashboard: reads the general view route (Phase 54 §5, §20)", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return json(200, {
        criticalCount: 1, importantCount: 2, watchCount: 0, opportunityCount: 1, openAnomalies: 3,
        topActions: [], topOpportunities: [], generatedAt: "2026-01-01T00:00:00Z",
      });
    }));

    const dashboard = await getIntelligenceDashboard();

    expect(seenUrl).toContain("/intelligence/dashboard");
    expect(dashboard.criticalCount).toBe(1);
    expect(dashboard.openAnomalies).toBe(3);
  });

  it("assistant: briefing carries the role and query posts the question", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init?: RequestInit) => {
      calls.push({ url, body: init?.body as string | undefined });
      return json(200, url.includes("briefing") ? { role: "manager", insights: [], recommendations: [] } : { intent: "sales_total", answer: "…", facts: [], sources: [], containsForecast: false });
    }));

    await getBriefing("manager");
    await askAssistant("Combien avons-nous vendu ce mois-ci ?");

    expect(calls[0].url).toContain("role=manager");
    expect(calls[1].url).toContain("/assistant/query");
    expect(JSON.parse(calls[1].body!)).toMatchObject({ question: "Combien avons-nous vendu ce mois-ci ?" });
  });
});
