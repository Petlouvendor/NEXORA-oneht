import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { createCustomer, createSupplier, listCustomers, updateCustomer } from "@/parties/partiesApi";

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

describe("partiesApi (recette Phase 55 — CLI-001/002, ACH-001)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("listCustomers passes the search term and a limit", async () => {
    let url = "";
    vi.stubGlobal("fetch", vi.fn(async (u: string) => { url = u; return json([]); }));

    await listCustomers("  samaritain ");

    expect(url).toContain("/customers?");
    expect(url).toContain("q=samaritain");
    expect(url).toContain("limit=100");
  });

  it("createCustomer posts and updateCustomer puts to the customer card", async () => {
    const calls: Array<{ url: string; method: string }> = [];
    vi.stubGlobal("fetch", vi.fn(async (u: string, init: RequestInit) => {
      calls.push({ url: u, method: init.method ?? "GET" });
      return json({ id: "k1", customerNumber: "CLI-00001" });
    }));

    await createCustomer({ name: "Client", type: "individual" });
    await updateCustomer("k1", { name: "Client SA", type: "business", creditLimit: 0 });

    expect(calls[0]).toMatchObject({ method: "POST" });
    expect(calls[0].url).toMatch(/\/customers$/);
    expect(calls[1]).toMatchObject({ method: "PUT" });
    expect(calls[1].url).toContain("/customers/k1");
  });

  it("createSupplier posts to the suppliers resource", async () => {
    let url = ""; let body = "";
    vi.stubGlobal("fetch", vi.fn(async (u: string, init: RequestInit) => { url = u; body = init.body as string; return json({}); }));

    await createSupplier({ name: "Grossiste", type: "business", email: "achat@g.demo" });

    expect(url).toMatch(/\/suppliers$/);
    expect(JSON.parse(body)).toMatchObject({ name: "Grossiste", email: "achat@g.demo" });
  });
});
