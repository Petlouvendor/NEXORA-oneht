import type { IconName } from "@/shared/icons";
import type { AlertSummary } from "@/dashboard/dashboardApi";
import type { Recommendation } from "@/intelligence/intelligenceApi";

/** Dashboard NEXORA ONE (§6) — logique pure : salutation, période de comparaison, risques par domaine. */

export function greeting(now: Date, name: string | null | undefined): string {
  const h = now.getHours();
  const hello = h >= 5 && h < 12 ? "Bonjour" : h >= 12 && h < 18 ? "Bon après-midi" : "Bonsoir";
  return name ? `${hello}, ${name}` : hello;
}

/** Prénom affichable : premier mot du nom complet, sinon partie locale de l'email. */
export function firstName(fullName: string | null | undefined, email?: string | null): string | null {
  const n = fullName?.trim().split(/\s+/)[0];
  if (n) return n;
  const local = email?.split("@")[0];
  return local ? local.charAt(0).toUpperCase() + local.slice(1) : null;
}

const iso = (d: Date) => d.toISOString().slice(0, 10);

/** Période précédente de même durée, juste avant [from ; to] (dates ISO yyyy-mm-dd, bornes incluses). */
export function previousWindow(from: string, to: string): { from: string; to: string } {
  const f = new Date(`${from}T00:00:00Z`);
  const t = new Date(`${to}T00:00:00Z`);
  const days = Math.round((t.getTime() - f.getTime()) / 86_400_000);
  const prevTo = new Date(f.getTime() - 86_400_000);
  const prevFrom = new Date(prevTo.getTime() - days * 86_400_000);
  return { from: iso(prevFrom), to: iso(prevTo) };
}

/** Variation en % ; null quand la base est nulle (une variation infinie n'a pas de sens affichable). */
export function pctChange(current: number, previous: number | null | undefined): number | null {
  if (previous === null || previous === undefined || previous === 0) return null;
  return Math.round(((current - previous) / Math.abs(previous)) * 1000) / 10;
}

export type RiskDomain = "stock" | "finance" | "clients" | "operations";
export interface RiskItem { key: string; title: string; detail: string; tone: "error" | "warning" | "info"; to: string }
export interface RiskGroup { domain: RiskDomain; label: string; icon: IconName; items: RiskItem[] }

export const RISK_DOMAINS: Array<{ domain: RiskDomain; label: string; icon: IconName; to: string }> = [
  { domain: "stock", label: "Stock", icon: "Box", to: "/inventory" },
  { domain: "finance", label: "Finance", icon: "Wallet", to: "/finance" },
  { domain: "clients", label: "Clients", icon: "Users", to: "/credit" },
  { domain: "operations", label: "Opérations", icon: "Activity", to: "/pos" },
];

export function alertDomain(key: string): RiskDomain {
  const k = key.toLowerCase();
  if (/(stock|expir|lot|rupture|reorder)/.test(k)) return "stock";
  if (/(receivable|customer|credit|client)/.test(k)) return "clients";
  if (/(cash_variance|session|sync|register|caisse)/.test(k)) return "operations";
  if (/(cash|payable|budget|expense|margin|sales|revenue|treasury)/.test(k)) return "finance";
  return "operations";
}

const RECOMMENDATION_DOMAIN: Record<string, RiskDomain> = {
  stock_reorder: "stock", stock_dormant: "stock",
  cashflow_risk: "finance", budget_overrun: "finance", supplier_price_increase: "finance", sales_decline: "finance",
  receivable_priority: "clients", customer_inactive: "clients",
};

/** Regroupe alertes du Command Center et recommandations « risque » ouvertes en 4 domaines (§6 « RISKS »). */
export function classifyRisks(alerts: AlertSummary[], recommendations: Recommendation[]): RiskGroup[] {
  const groups = new Map<RiskDomain, RiskItem[]>(RISK_DOMAINS.map((d) => [d.domain, []]));
  const target = (d: RiskDomain) => RISK_DOMAINS.find((x) => x.domain === d)!.to;
  for (const a of alerts) {
    const d = alertDomain(a.key);
    groups.get(d)!.push({
      key: `alert:${a.key}`, title: a.title, detail: a.detail,
      tone: a.severity === "critical" ? "error" : a.severity === "warning" ? "warning" : "info", to: target(d),
    });
  }
  for (const r of recommendations) {
    const d = RECOMMENDATION_DOMAIN[r.category];
    if (!d || r.nature !== "risk") continue;
    groups.get(d)!.push({
      key: `rec:${r.id}`, title: r.title, detail: r.proposedAction,
      tone: r.priorityLevel === "critical" ? "error" : r.priorityLevel === "important" ? "warning" : "info", to: "/intelligence",
    });
  }
  const rank = { error: 0, warning: 1, info: 2 } as const;
  return RISK_DOMAINS.map((d) => ({
    domain: d.domain, label: d.label, icon: d.icon,
    items: groups.get(d.domain)!.sort((a, b) => rank[a.tone] - rank[b.tone]),
  }));
}
