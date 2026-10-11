import { money } from "@/shared/format";
import { num } from "@/shared/forms";
import type { PermissionEntry, Workflow, WorkflowDefinition } from "@/admin/adminApi";

// Règles pures du centre d'administration (finalisation phase 11) — testées dans adminRules.test.ts.

const MODULE_LABELS: Record<string, string> = {
  accounting: "Comptabilité", admin: "Administration", catalog: "Catalogue", credit: "Crédit & recouvrement",
  customers: "Clients", finance: "Finance", ged: "Documents (GED)", hr: "RH", inventory: "Stock", payroll: "Paie",
  pos: "Caisse", purchasing: "Achats", reporting: "Rapports", sales: "Ventes", security: "Sécurité", suppliers: "Fournisseurs",
  integrations: "Intégrations", notifications: "Notifications", operations: "Exploitation", offline: "Hors ligne",
  intelligence: "Intelligence", mobile: "Terrain", platform: "Plateforme NEXORA", group: "Groupe", documents: "Documents émis",
};
export const moduleLabel = (m: string) => MODULE_LABELS[m] ?? m.charAt(0).toUpperCase() + m.slice(1).replace(/_/g, " ");

/** Permissions groupées par module, modules triés par libellé ; filtre texte sur le code et le libellé. */
export function groupPermissions(catalog: PermissionEntry[], filter = "") {
  const f = filter.trim().toLowerCase();
  const groups = new Map<string, PermissionEntry[]>();
  for (const p of catalog) {
    if (f && !p.code.toLowerCase().includes(f) && !p.name.toLowerCase().includes(f) && !moduleLabel(p.module).toLowerCase().includes(f)) continue;
    (groups.get(p.module) ?? groups.set(p.module, []).get(p.module)!).push(p);
  }
  return [...groups.entries()]
    .map(([module, entries]) => ({ module, label: moduleLabel(module), entries }))
    .sort((a, b) => a.label.localeCompare(b.label, "fr"));
}

/** Permissions demandées que l'utilisateur ne détient pas (le serveur les refuse aussi). */
export function notGrantable(selected: Iterable<string>, catalog: PermissionEntry[], already: Iterable<string> = []) {
  const held = new Set(catalog.filter((p) => p.grantable).map((p) => p.code));
  const had = new Set(already);
  return [...selected].filter((c) => !held.has(c) && !had.has(c));
}

// ------------------------------------------------------------------ Workflows (QUAND / SI / ALORS)

export interface WorkflowForm {
  name: string; trigger: string; threshold: "none" | "gte" | "gt"; amount: string; levels: string; role: string;
}
export const emptyWorkflowForm = (): WorkflowForm => ({ name: "", trigger: "", threshold: "none", amount: "", levels: "1", role: "" });

export function formFromWorkflow(w: Workflow): WorkflowForm {
  const c = w.definition.conditions.find((x) => x.field === "amount");
  return {
    name: w.name, trigger: w.triggerEvent,
    threshold: c ? (c.op === "gt" ? "gt" : "gte") : "none", amount: c ? String(c.value) : "",
    levels: String(w.definition.approval?.levels ?? 1), role: w.definition.approval?.approverRoleCode ?? "",
  };
}

/** Contrôle du formulaire (mêmes règles que le serveur). Renvoie [champ, message] ou null. */
export function validateWorkflowForm(f: WorkflowForm): [string, string] | null {
  if (f.name.trim().length < 2) return ["name", "Nom obligatoire."];
  if (!f.trigger) return ["trigger", "Choisissez l'opération concernée."];
  if (f.threshold !== "none") {
    const a = num(f.amount);
    if (a == null || a < 0) return ["amount", "Montant positif requis."];
  }
  const l = num(f.levels);
  if (l == null || !Number.isInteger(l) || l < 1 || l > 5) return ["levels", "De 1 à 5 niveaux."];
  if (!f.role) return ["role", "Choisissez le rôle qui approuve."];
  return null;
}

export function definitionFromForm(f: WorkflowForm): WorkflowDefinition {
  return {
    logic: "all",
    conditions: f.threshold === "none" ? [] : [{ field: "amount", op: f.threshold, value: num(f.amount) }],
    approval: { levels: num(f.levels)!, approverRoleCode: f.role },
    actions: [], notifications: [],
  };
}

/** Code technique stable dérivé du nom (le serveur l'utilise comme clé). */
export const codeFromName = (name: string) =>
  name.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 40) || "workflow";

/** Phrase lisible de la règle : ce qu'elle impose réellement. */
export function describeWorkflow(def: WorkflowDefinition, triggerLabel: string): string {
  const c = def.conditions.find((x) => x.field === "amount");
  const when = c ? ` ${c.op === "gt" ? "au-delà de" : "à partir de"} ${money(Number(c.value))}` : "";
  const a = def.approval;
  const who = a?.approverRoleCode ? ` par un utilisateur ayant le rôle « ${a.approverRoleCode} »` : "";
  return a ? `${triggerLabel}${when} : ${a.levels} approbation${a.levels > 1 ? "s distinctes" : ""}${who}.` : `${triggerLabel} : aucune approbation.`;
}

// ------------------------------------------------------------------ Automatisations (QUAND / SI / ALORS)

/** Seuls les types réellement exécutés par NEXORA (le serveur refuse les autres). */
export const AUTOMATION_KINDS: Record<string, { label: string; when: string; condition: string; then: string }> = {
  dunning: {
    label: "Relance des créances en retard",
    when: "À chaque exécution planifiée",
    condition: "Une créance client est échue et le client accepte les relances ; le niveau dépend du retard (politique de relance).",
    then: "Vous recevez une notification par relance à faire (niveau 1, 2 ou 3).",
  },
  low_stock_alert: {
    label: "Alerte de stock bas",
    when: "À chaque exécution planifiée",
    condition: "Le stock disponible d'un article est au niveau de son point de commande ou en dessous.",
    then: "Vous recevez une notification par article à réapprovisionner.",
  },
  expiry_alert: {
    label: "Alerte de péremption",
    when: "À chaque exécution planifiée",
    condition: "Un lot actif expire dans le nombre de jours choisi.",
    then: "Vous recevez une notification par lot proche de la péremption.",
  },
};
export const FREQUENCIES: Record<string, string> = { hourly: "Toutes les heures", daily: "Chaque jour", weekly: "Chaque semaine", monthly: "Chaque mois" };
