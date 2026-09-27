import { money, number as fmtNumber, percent } from "@/shared/format";

/**
 * NEXORA AI (brief NEXORA ONE §8) — logique de présentation pure, sans React. Le serveur répond de façon
 * déterministe (Ask Nexora, aucun LLM, permissions appliquées) ; ici on ne fait que STRUCTURER cette réponse
 * (explication → causes numérotées, faits → graphique, sources → écrans) — jamais recalculer un chiffre.
 */

/** Questions d'exemple du brief (§8) — proposées en pastilles. */
export const AI_SUGGESTIONS = [
  "Pourquoi mes bénéfices ont diminué ?",
  "Quels produits dois-je commander ?",
  "Quels produits se vendent le mieux ?",
  "Pourquoi mon stock diminue ?",
  "Quels clients ont des paiements en retard ?",
  "Montre-moi les dépenses qui ont augmenté.",
];

/** Événement global d'ouverture du panneau (bouton de la barre, Ctrl+J, Ctrl+K « Demander à NEXORA AI »). */
export const AI_OPEN_EVENT = "nexora-ai:open";

export function openNexoraAi(question?: string) {
  window.dispatchEvent(new CustomEvent(AI_OPEN_EVENT, { detail: { question } }));
}

export interface AnswerBlock { label: string | null; text: string | null; items: string[] }

/**
 * Découpe une réponse en blocs lisibles : une phrase « Libellé : a ; b ; c » devient une liste,
 * « 1) … ; 2) … » une liste numérotée sans ses numéros. Le texte n'est jamais réécrit.
 */
export function structureAnswer(answer: string): AnswerBlock[] {
  const sentences = answer.trim().split(/(?<=\.)\s+(?=[A-ZÀ-ÖØ-Ý«0-9])/u).filter(Boolean);
  return sentences.map((sentence) => {
    const colon = sentence.indexOf(" : ");
    const rest = colon >= 0 ? sentence.slice(colon + 3) : "";
    const isList = colon >= 0 && (rest.includes(" ; ") || /^\d+\)\s/.test(rest));
    if (!isList) return { label: null, text: sentence, items: [] };
    const items = rest.replace(/\.$/, "").split(/\s;\s/).map((s) => s.replace(/^\d+\)\s*/, "").trim()).filter(Boolean);
    return { label: sentence.slice(0, colon), text: null, items };
  });
}

/** Source serveur (ex. « reports/aging/receivables ») → écran de l'application et libellé humain. */
export function sourceLink(source: string): { label: string; to: string } {
  const s = source.replace(/^\/+/, "");
  const customer = s.match(/^customers\/([0-9a-f-]{36})/i);
  if (customer) return { label: "Compte client", to: `/customers/${customer[1]}` };
  if (s.startsWith("hr")) return { label: "RH & Paie", to: `/${s.replace(/^hr\/reports\/.*/, "hr")}` };
  const table: Array<[string, string, string]> = [
    ["dashboard/command-center", "/", "Command Center"],
    ["reports/sales/top-products", "/reports", "Meilleures ventes"],
    ["reports/sales", "/reports", "Rapports de ventes"],
    ["reports/stock/valuation", "/reports", "Valorisation du stock"],
    ["reports/stock", "/inventory", "Ruptures de stock"],
    ["reports/aging/receivables", "/credit", "Balance âgée clients"],
    ["reports/finance", "/finance", "Trésorerie"],
    ["inventory/reorder-suggestions", "/inventory", "Suggestions de réapprovisionnement"],
    ["inventory", "/inventory", "Stocks"],
    ["accounting", "/accounting", "Grand livre"],
    ["credit", "/credit", "Crédit & recouvrement"],
    ["customers", "/customers", "Clients"],
  ];
  const hit = table.find(([prefix]) => s === prefix || s.startsWith(prefix + "/") || s.startsWith(prefix + "?"));
  return hit ? { label: hit[2], to: hit[1] } : { label: s, to: "/reports" };
}

export interface AiFact { key: string; label: string; value: number; unit: string; kind: string }

/** Formate un fait selon son unité (devise ISO, %, jours, quantité). */
export function formatFact(f: AiFact): string {
  if (f.unit === "%") return percent(f.value);
  if (f.unit === "jours") return `${fmtNumber(f.value, 1)} j`;
  if (/^[A-Z]{3}$/.test(f.unit)) return money(f.value, f.unit);
  return fmtNumber(f.value, 2);
}

/**
 * Sépare les faits en une série graphique (l'unité la plus représentée, ≥ 2 valeurs, sans les valeurs
 * « période précédente » qui se lisent mieux en tuiles) et des tuiles pour le reste.
 */
export function splitFacts(facts: AiFact[]): { chart: AiFact[]; tiles: AiFact[] } {
  const counts = new Map<string, number>();
  for (const f of facts) if (!f.key.endsWith("_previous")) counts.set(f.unit, (counts.get(f.unit) ?? 0) + 1);
  const [unit, n] = [...counts.entries()].sort((a, b) => b[1] - a[1])[0] ?? ["", 0];
  if (n < 2) return { chart: [], tiles: facts };
  const chart = facts.filter((f) => f.unit === unit && !f.key.endsWith("_previous"));
  return { chart, tiles: facts.filter((f) => !chart.includes(f)) };
}
