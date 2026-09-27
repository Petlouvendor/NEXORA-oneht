import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { buildPalette, norm } from "@/app/paletteModel";
import { getActivity, globalSearch, type SearchGroup } from "@/app/experienceApi";
import { NAV_GROUPS, ALL_NAV } from "@/app/nav";

const nav = ALL_NAV;
const can = (granted: string[]) => (p: string) => granted.includes(p);
const rice: SearchGroup = {
  kind: "product", label: "Produits",
  hits: [{ kind: "product", id: "p1", title: "Riz 25kg", subtitle: "RIZ25 · stock 40", route: "/catalog?q=RIZ25" }],
};

describe("Ctrl+K — palette NEXORA ONE (§5)", () => {
  it("a free query offers « Demander à NEXORA AI » first, then server hits, only with the assistant permission", () => {
    const withAi = buildPalette({ query: "Riz 25kg", nav, search: [rice], can: can(["intelligence.use_assistant", "inventory.view"]) });
    expect(withAi[0].title).toBe("NEXORA AI");
    expect(withAi[0].items[0].effect).toEqual({ type: "ask", question: "Riz 25kg" });
    expect(withAi[1].items[0].effect).toEqual({ type: "route", to: "/catalog?q=RIZ25" });

    const without = buildPalette({ query: "Riz 25kg", nav, search: [rice], can: can([]) });
    expect(without.some((s) => s.title === "NEXORA AI")).toBe(false);
  });

  it("product hits expose Stock → Ventes → Achats → Recommandations shortcuts, filtered by permission", () => {
    const all = buildPalette({ query: "riz", nav, search: [rice], can: can(["inventory.view", "reporting.view_sales", "purchasing.view", "intelligence.view"]) });
    const hit = all.flatMap((s) => s.items).find((i) => i.id === "product:p1")!;
    expect(hit.links!.map((l) => l.label)).toEqual(["Stock", "Ventes", "Fournisseur & achats", "Recommandations"]);

    const cashier = buildPalette({ query: "riz", nav, search: [rice], can: can(["pos.sell"]) });
    expect(cashier.flatMap((s) => s.items).find((i) => i.id === "product:p1")!.links).toEqual([]);
  });

  it("pages, actions and settings respect permissions — a cashier never sees accounting or administration", () => {
    const cashier = buildPalette({ query: "", nav, search: [], can: can(["pos.sell"]) });
    const labels = cashier.flatMap((s) => s.items).map((i) => i.label);
    expect(labels).toContain("Nouvelle vente");
    expect(labels).toContain("POS");
    expect(labels).not.toContain("Comptabilité");
    expect(labels).not.toContain("Administration");
  });

  it("matching ignores case and accents (« tresorerie » finds « Finance & trésorerie »)", () => {
    expect(norm("Trésorerie")).toBe("tresorerie");
    const r = buildPalette({ query: "tresorerie", nav, search: [], can: can(["finance.view"]) });
    expect(r.flatMap((s) => s.items).map((i) => i.label)).toContain("Finance & trésorerie");
  });

  it("navigation follows the brief's module order and every entry but Dashboard/Préférences/Notifications is permission-gated", () => {
    expect(NAV_GROUPS.map((g) => g.title)).toEqual(["NEXORA ONE", "Commerce", "Opérations", "Finance", "RH", "Pilotage", "Paramètres"]);
    const open = ALL_NAV.filter((n) => !n.permission).map((n) => n.to);
    expect(open.sort()).toEqual(["/", "/notifications", "/settings"]);
    expect(ALL_NAV.map((n) => n.label)).toEqual(expect.arrayContaining(["POS", "Stock", "Achats", "Clients", "Fournisseurs", "Rapports", "Intelligence", "Automatisation"]));
  });
});

describe("experienceApi — recherche globale & activité", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("search encodes the query; activity passes the limit", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(JSON.stringify(url.includes("/search") ? { query: "x", groups: [] } : []), { status: 200, headers: { "Content-Type": "application/json" } });
    }));
    await globalSearch("Riz & Cie 25kg");
    await getActivity(7);
    expect(urls[0]).toContain("/search?q=Riz%20%26%20Cie%2025kg&limit=5");
    expect(urls[1]).toContain("/dashboard/activity?limit=7");
  });
});
