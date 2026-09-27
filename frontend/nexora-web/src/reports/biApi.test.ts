import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  approveBudget, createBudget, dismissInsight, generateForecast, listKpiDefinitions,
  listReports, runReport, saveReport, setObjective,
} from "@/reports/biApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("biApi — Business Intelligence, Reporting & Pilotage (Phase 42 §33-41, §48-50, §55)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("kpi-definitions and reports catalog hit the expected GET routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return json(200, url.includes("reports") ? { standard: [], saved: [] } : []); }));

    await listKpiDefinitions();
    await listReports();

    expect(urls[0]).toContain("/kpi-definitions");
    expect(urls[1]).toContain("/reports");
  });

  it("objectives: setObjective posts the target payload", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => { body = init.body as string; return json(200, { id: "o1" }); }));

    await setObjective({ kpiKey: "revenue", periodType: "month", targetValue: 5000, direction: "min", weight: 2 });

    expect(JSON.parse(body)).toMatchObject({ kpiKey: "revenue", periodType: "month", targetValue: 5000, weight: 2 });
  });

  it("saved report: save then run hit their routes", async () => {
    const calls: { url: string; method: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return json(200, { id: "r1", result: { rows: [] } });
    }));

    await saveReport({ name: "Mes ventes", reportKey: "sales_journal", filter: {} });
    await runReport("r1");

    expect(calls[0]).toMatchObject({ url: expect.stringContaining("/reports"), method: "POST" });
    expect(calls[1]).toMatchObject({ url: expect.stringContaining("/reports/r1/run"), method: "POST" });
  });

  it("forecasts: generateForecast carries metric/horizon/scenario", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => { body = init.body as string; return json(200, { id: "f1" }); }));

    await generateForecast({ metric: "revenue", horizonDays: 30, scenario: "optimistic" });

    expect(JSON.parse(body)).toMatchObject({ metric: "revenue", horizonDays: 30, scenario: "optimistic" });
  });

  it("insights: dismiss posts a reason; budgets: approve hits its route", async () => {
    const calls: { url: string; body?: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, body: init.body as string | undefined }); return json(200, { id: "b1", status: "active" });
    }));

    await dismissInsight("i1", "déjà traité");
    await approveBudget("b1");
    await createBudget({ name: "Budget 2027", fiscalYear: 2027, currencyCode: "HTG" });

    expect(calls[0].url).toContain("/insights/i1/dismiss");
    expect(JSON.parse(calls[0].body!)).toMatchObject({ reason: "déjà traité" });
    expect(calls[1].url).toContain("/budgets/b1/approve");
    expect(JSON.parse(calls[2].body!)).toMatchObject({ name: "Budget 2027", fiscalYear: 2027 });
  });
});
