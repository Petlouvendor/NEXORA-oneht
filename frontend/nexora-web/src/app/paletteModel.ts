import type { IconName } from "@/shared/icons";
import type { NavItem } from "@/app/nav";
import type { SearchGroup, SearchKind } from "@/app/experienceApi";

/**
 * Modèle de la palette Ctrl+K (NEXORA ONE §5) — pur, sans React : ce qui est proposé, dans quel ordre,
 * filtré par permission. Recherche possible : produit, client, fournisseur, employé, facture, vente
 * (serveur), rapport/page, action, paramètre (client).
 */

export type PaletteEffect =
  | { type: "route"; to: string }
  | { type: "ask"; question?: string }
  | { type: "toggle-theme" };

export interface PaletteItem {
  id: string; icon: IconName; label: string; hint?: string; effect: PaletteEffect;
  /** Raccourcis contextuels (§5 : « Riz 25kg → Produit → Stock → ventes → fournisseur → recommandations »). */
  links?: Array<{ label: string; to: string }>;
}
export interface PaletteSection { title: string; items: PaletteItem[] }

interface Gated { label: string; icon: IconName; effect: PaletteEffect; permission?: string; keywords?: string }

export const PALETTE_ACTIONS: Gated[] = [
  { label: "Nouvelle vente", icon: "Pos", effect: { type: "route", to: "/pos" }, permission: "pos.sell", keywords: "caisse vendre encaisser ticket" },
  { label: "Réceptionner une livraison", icon: "Truck", effect: { type: "route", to: "/purchasing" }, permission: "purchasing.receive", keywords: "réception fournisseur achat" },
  { label: "Enregistrer un encaissement", icon: "Wallet", effect: { type: "route", to: "/finance" }, permission: "finance.pay", keywords: "paiement trésorerie" },
  { label: "Nouveau client", icon: "Users", effect: { type: "route", to: "/customers" }, permission: "customers.create", keywords: "créer client fiche" },
  { label: "Voir les recommandations", icon: "Bulb", effect: { type: "route", to: "/intelligence" }, permission: "intelligence.view", keywords: "intelligence alertes opportunités" },
  { label: "Ouvrir un rapport", icon: "Chart", effect: { type: "route", to: "/reports" }, permission: "reporting.view", keywords: "rapport analyse pilotage kpi" },
  { label: "Ouvrir NEXORA AI", icon: "Sparkles", effect: { type: "ask" }, permission: "intelligence.use_assistant", keywords: "assistant ia question" },
];

export const PALETTE_SETTINGS: Gated[] = [
  { label: "Préférences d'affichage", icon: "Sliders", effect: { type: "route", to: "/settings" }, keywords: "langue densité accessibilité raccourcis" },
  { label: "Basculer thème clair / sombre", icon: "Moon", effect: { type: "toggle-theme" }, keywords: "dark light apparence" },
  { label: "Notifications", icon: "Bell", effect: { type: "route", to: "/notifications" }, keywords: "alertes messages" },
  { label: "Administration", icon: "Cog", effect: { type: "route", to: "/admin" }, permission: "admin.settings", keywords: "utilisateurs rôles paramètres" },
];

const KIND_ICON: Record<SearchKind, IconName> = {
  product: "Box", customer: "Users", supplier: "Truck", employee: "IdCard", document: "Ledger", sale: "Pos",
};

/** Normalisation insensible à la casse et aux accents (comme `nexora.search_norm` côté serveur). */
export function norm(s: string): string {
  return s.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase();
}

const matches = (needle: string, ...hay: Array<string | undefined>) =>
  !needle || hay.some((h) => h && norm(h).includes(needle));

export function buildPalette(input: {
  query: string; nav: NavItem[]; search: SearchGroup[]; can: (permission: string) => boolean;
}): PaletteSection[] {
  const needle = norm(input.query.trim());
  const allowed = (g: Gated) => !g.permission || input.can(g.permission);
  const toItem = (prefix: string) => (g: Gated): PaletteItem => ({ id: `${prefix}:${g.label}`, icon: g.icon, label: g.label, effect: g.effect });
  const sections: PaletteSection[] = [];

  if (needle && input.can("intelligence.use_assistant")) {
    sections.push({ title: "NEXORA AI", items: [{
      id: "ai:ask", icon: "Sparkles", label: `Demander à NEXORA AI : « ${input.query.trim()} »`,
      effect: { type: "ask", question: input.query.trim() },
    }] });
  }

  for (const group of input.search) {
    sections.push({
      title: group.label,
      items: group.hits.map((h) => ({
        id: `${h.kind}:${h.id}`, icon: KIND_ICON[h.kind] ?? "Search", label: h.title, hint: h.subtitle ?? undefined,
        effect: { type: "route", to: h.route },
        links: h.kind === "product" ? productLinks(input.can) : undefined,
      })),
    });
  }

  const actions = PALETTE_ACTIONS.filter(allowed).filter((a) => matches(needle, a.label, a.keywords));
  if (actions.length) sections.push({ title: "Actions", items: actions.map(toItem("action")) });

  const pages = input.nav.filter((n) => !n.permission || input.can(n.permission)).filter((n) => matches(needle, n.label));
  if (pages.length) sections.push({
    title: needle ? "Pages & rapports" : "Aller à",
    items: pages.slice(0, needle ? 6 : 8).map((n) => ({ id: `page:${n.to}`, icon: n.icon, label: n.label, hint: n.to, effect: { type: "route", to: n.to } })),
  });

  const settings = PALETTE_SETTINGS.filter(allowed).filter((s) => matches(needle, s.label, s.keywords));
  if (settings.length && (needle || sections.length < 3)) sections.push({ title: "Paramètres", items: settings.map(toItem("setting")) });

  return sections;
}

/** Produit → Stock → Ventes → Achats (fournisseur) → Recommandations, selon les droits. */
function productLinks(can: (p: string) => boolean) {
  return [
    can("inventory.view") && { label: "Stock", to: "/inventory" },
    can("reporting.view_sales") && { label: "Ventes", to: "/reports" },
    can("purchasing.view") && { label: "Fournisseur & achats", to: "/purchasing" },
    can("intelligence.view") && { label: "Recommandations", to: "/intelligence" },
  ].filter((l): l is { label: string; to: string } => Boolean(l));
}
