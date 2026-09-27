import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  listNotifications,
  markAllRead,
  markRead,
  setPinned,
  unreadCount,
  upsertPreference,
} from "@/notifications/notificationsApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("notificationsApi (Phase 34 §4-5, §45)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("listNotifications passes the state as a query param", async () => {
    let seenUrl = "";
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      seenUrl = url;
      return jsonResponse(200, { items: [], total: 0, unread: 0 });
    }));

    await listNotifications("unread");

    expect(seenUrl).toContain("/notifications?state=unread");
  });

  it("markRead PATCHes the read route, markUnread the unread route", async () => {
    const calls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      calls.push({ url, method: (init.method ?? "GET").toUpperCase() });
      return new Response(null, { status: 204 });
    }));

    await markRead("n1", true);
    await markRead("n1", false);

    expect(calls[0]).toEqual({ url: expect.stringContaining("/notifications/n1/read"), method: "PATCH" });
    expect(calls[1]).toEqual({ url: expect.stringContaining("/notifications/n1/unread"), method: "PATCH" });
  });

  it("setPinned toggles between the pin and unpin routes", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return new Response(null, { status: 204 }); }));

    await setPinned("n1", true);
    await setPinned("n1", false);

    expect(urls[0]).toContain("/notifications/n1/pin");
    expect(urls[1]).toContain("/notifications/n1/unpin");
  });

  it("unreadCount and markAllRead hit their endpoints", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return jsonResponse(200, { unread: 3, updated: 3 });
    }));

    await unreadCount();
    await markAllRead();

    expect(urls[0]).toContain("/notifications/unread-count");
    expect(urls[1]).toContain("/notifications/read-all");
  });

  it("upsertPreference posts the routing body", async () => {
    let body = "";
    vi.stubGlobal("fetch", vi.fn(async (_url: string, init: RequestInit) => {
      body = init.body as string;
      return jsonResponse(200, { id: "p1" });
    }));

    await upsertPreference({
      companyId: null, category: "dunning", inApp: true, email: true, sms: false, webhook: false,
      minPriority: 3, digest: "instant", quietFrom: null, quietTo: null,
    });

    expect(JSON.parse(body)).toMatchObject({ category: "dunning", email: true, minPriority: 3 });
  });
});
