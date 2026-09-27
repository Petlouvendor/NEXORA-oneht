import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  createSubscription,
  customerQuotas,
  listCustomers,
  platformDowngrade,
  platformUpgrade,
  saasMetrics,
} from "@/platform/platformApi";
import { previewPlan, selfCancel, selfUpgrade } from "@/billing/billingApi";

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("platform + billing api (Phase 38 §14, §18, §50)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("reads the SaaS metrics and customer endpoints", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string) => { urls.push(u); return json(200, u.includes("metrics") ? { mrr: 0 } : []); }));
    await saasMetrics();
    await listCustomers("active");
    await customerQuotas("c1");
    expect(urls[0]).toContain("/platform/metrics");
    expect(urls[1]).toContain("/platform/customers?status=active");
    expect(urls[2]).toContain("/platform/customers/c1/quotas");
  });

  it("upgrade / downgrade post to the right platform routes", async () => {
    const calls: { url: string; body: string }[] = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string, init: RequestInit) => {
      calls.push({ url: u, body: init.body as string });
      return json(200, { id: "s1", planCode: "biz" });
    }));
    await platformUpgrade("s1", "plan-pro");
    await platformDowngrade("s1", "plan-start");
    await createSubscription("c1", { planId: "plan-biz", startTrial: true });
    expect(calls[0].url).toContain("/platform/subscriptions/s1/upgrade");
    expect(calls[1].url).toContain("/platform/subscriptions/s1/downgrade");
    expect(JSON.parse(calls[2].body)).toMatchObject({ planId: "plan-biz", startTrial: true });
  });

  it("portal self-service hits the unprefixed subscription routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string) => { urls.push(u); return json(200, { direction: "downgrade", quotaBreaches: [] }); }));
    await previewPlan("s1", "p2");
    await selfUpgrade("s1", "p3");
    await selfCancel("s1", false, "trop cher");
    expect(urls[0]).toContain("/subscriptions/s1/preview-plan/p2");
    expect(urls[1]).toContain("/subscriptions/s1/upgrade");
    expect(urls[2]).toContain("/subscriptions/s1/cancel");
  });
});
