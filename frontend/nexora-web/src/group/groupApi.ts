import { apiFetch } from "@/core/apiClient";

/**
 * Groupe & consolidation (Phase 9 ; finalisation phase 14). Toutes les routes exigent l'en-tête
 * X-Group-Scope: true, et le serveur n'ouvre ces routes qu'à un rôle attribué AU GROUPE : un rôle
 * d'une seule société ne donne jamais accès aux sociétés sœurs.
 */
const GROUP_HEADERS = { "X-Group-Scope": "true" } as const;

export interface GroupCompanyStatus {
  companyId: string; companyName: string; sector: string | null; status: string; latestPeriod: string;
  latestPeriodStatus: string; unmappedAccounts: number; unreconciledIntercompany: number; receivables: number; payables: number;
}
export interface GroupControlCenter {
  groupId: string; currency: string; generatedAt: string; companies: GroupCompanyStatus[]; unreconciledIntercompany: number;
  runsReadyForValidation: number; openTasks: number; groupReceivables: number; groupPayables: number;
}
export interface ConsolidationCompany {
  companyId: string; companyName: string; included: boolean; functionalCurrency: string; fxRate: number;
  periodClosed: boolean; mappingComplete: boolean; contributedDebit: number; contributedCredit: number;
}
export interface ConsolidationRun {
  id: string; periodLabel: string; fromDate: string; toDate: string; currency: string; status: string; coherenceOk: boolean;
  totalDebit: number; totalCredit: number; eliminationTotal: number; diagnostics: string[]; companies: ConsolidationCompany[];
  generatedAt: string | null; validatedAt: string | null; publishedAt: string | null;
}
export interface ConsolidationLine {
  groupAccountCode: string; groupAccountName: string; accountType: string; aggregatedDebit: number; aggregatedCredit: number;
  eliminationDebit: number; eliminationCredit: number; consolidatedDebit: number; consolidatedCredit: number;
}
export interface ConsolidatedStatements {
  runId: string; periodLabel: string; currency: string; status: string; trialBalance: ConsolidationLine[];
  totalAssets: number; totalLiabilities: number; totalEquity: number; totalRevenue: number; totalExpense: number; netResult: number;
  balanceSheetBalanced: boolean; trialBalanceBalanced: boolean; byCompany: ConsolidationCompany[];
}
export interface GroupTask {
  id: string; companyId: string | null; companyName: string | null; title: string; description: string | null;
  assigneeUserId: string | null; priority: string; dueDate: string | null; status: string; notes: string | null;
}
export interface GroupAccount { id: string; code: string; name: string; accountType: string; isEliminable: boolean; sortOrder: number; status: string }
export interface MappingCoverage { companyId: string; companyName: string; mappedAccounts: number; unmappedWithActivity: number; unmappedCodes: string[] }
export interface LocalAccountMapping {
  accountId: string; code: string; name: string; accountType: string; hasActivity: boolean;
  groupAccountCode: string | null; groupAccountName: string | null; effectiveFrom: string | null; versions: number;
}
export interface IntercompanyTx {
  id: string; sourceCompanyId: string; sourceCompany: string; targetCompanyId: string; targetCompany: string;
  transactionType: string; amount: number; currency: string; occurredAt: string; status: string; matchStatus: string;
  sourceDocument: string | null; targetDocument: string | null; counterpartId: string | null; matchDifference: number | null;
}
export interface IntercompanyMatch {
  id: string; sourceTransactionId: string; counterpartTransactionId: string | null; status: string;
  amountDifference: number; currencyMatch: boolean; dateDifferenceDays: number;
}
export interface GroupPolicy { groupId: string; consolidationCurrency: string; fxMethod: string; intercompanyTolerance: number }
export interface GroupOptions {
  groupId: string; groupName: string; currencies: string[]; activities: string[]; supportedFxMethods: string[];
  companies: Array<{ id: string; name: string; status: string; currency: string; sector: string | null; archivedAt: string | null; archiveReason: string | null }>;
  users: Array<{ id: string; name: string; email: string }>;
}

const g = <T,>(path: string, init: RequestInit = {}) =>
  apiFetch<T>(`/groups${path}`, { ...init, headers: { ...GROUP_HEADERS, ...(init.headers ?? {}) } });
const post = (body: unknown = {}) => ({ method: "POST", body: JSON.stringify(body) });
const put = (body: unknown) => ({ method: "PUT", body: JSON.stringify(body) });

// Pilotage
export const getOptions = (gid: string) => g<GroupOptions>(`/${gid}/options`);
export const getControlCenter = (gid: string) => g<GroupControlCenter>(`/${gid}/control-center`);
export const listTasks = (gid: string) => g<GroupTask[]>(`/${gid}/tasks`);
export const createTask = (gid: string, body: { title: string; description: string | null; companyId: string | null; assigneeUserId: string | null; priority: string; dueDate: string | null }) =>
  g<string>(`/${gid}/tasks`, post(body));
export const updateTask = (gid: string, taskId: string, status: string, notes: string | null) =>
  g<void>(`/${gid}/tasks/${taskId}`, put({ status, notes }));

// Plan comptable groupe & correspondances
export const listGroupAccounts = (gid: string) => g<GroupAccount[]>(`/${gid}/accounts`);
export const upsertGroupAccount = (gid: string, body: { code: string; name: string; accountType: string; isEliminable: boolean; sortOrder: number }) =>
  g<string>(`/${gid}/accounts`, post(body));
export const mappingCoverage = (gid: string) => g<MappingCoverage[]>(`/${gid}/accounts/mapping-coverage`);
export const listMappings = (gid: string, companyId: string) =>
  g<LocalAccountMapping[]>(`/${gid}/accounts/mappings?companyId=${encodeURIComponent(companyId)}`);
export const setMapping = (gid: string, companyId: string, localAccountCode: string, groupAccountCode: string) =>
  g<void>(`/${gid}/accounts/mappings`, put({ companyId, localAccountCode, groupAccountCode }));

// Politique & sociétés
export const getPolicy = (gid: string) => g<GroupPolicy>(`/${gid}/policy`);
export const setPolicy = (gid: string, body: { consolidationCurrency: string | null; fxMethod: string | null; intercompanyTolerance: number | null }) =>
  g<void>(`/${gid}/policy`, put(body));
export const provisionCompany = (gid: string, body: { name: string; activityType: string; currency: string }) =>
  g<string>(`/${gid}/companies`, post({ ...body, groupId: gid }));
export const archiveCompany = (gid: string, companyId: string, reason: string) =>
  g<void>(`/${gid}/companies/${companyId}/archive`, post({ reason }));

// Intersociétés
export const listIntercompany = (gid: string) => g<IntercompanyTx[]>(`/${gid}/intercompany`);
export const recordIntercompany = (gid: string, body: {
  sourceCompanyId: string; targetCompanyId: string; transactionType: string; amount: number; currency: string;
  occurredAt: string; sourceReference: string | null; targetReference: string | null;
}) => g<string>(`/${gid}/intercompany`, post(body));
export const confirmIntercompany = (gid: string, id: string) => g<void>(`/${gid}/intercompany/${id}/confirm`, post());
export const reconcileIntercompany = (gid: string, sourceTransactionId: string, counterpartTransactionId: string | null) =>
  g<IntercompanyMatch>(`/${gid}/intercompany/reconcile`, post({ sourceTransactionId, counterpartTransactionId }));

// Consolidation
export const listRuns = (gid: string) => g<ConsolidationRun[]>(`/${gid}/consolidation/runs`);
export const createRun = (gid: string, body: { periodLabel: string; fromDate: string; toDate: string; companyIds: string[]; profileId: null }) =>
  g<ConsolidationRun>(`/${gid}/consolidation/runs`, post(body));
export const executeRun = (gid: string, runId: string) => g<ConsolidationRun>(`/${gid}/consolidation/runs/${runId}/execute`, post());
export const validateRun = (gid: string, runId: string) => g<ConsolidationRun>(`/${gid}/consolidation/runs/${runId}/validate`, post());
export const publishRun = (gid: string, runId: string) => g<ConsolidationRun>(`/${gid}/consolidation/runs/${runId}/publish`, post());
export const reopenRun = (gid: string, runId: string, reason: string) =>
  g<ConsolidationRun>(`/${gid}/consolidation/runs/${runId}/reopen`, post({ reason }));
export const getStatements = (gid: string, runId: string) => g<ConsolidatedStatements>(`/${gid}/consolidation/runs/${runId}/statements`);

// ------------------------------------------------------------------ Libellés et règles d'affichage

export const ACCOUNT_TYPES: Record<string, string> = {
  asset: "Actif", liability: "Passif", equity: "Capitaux propres", revenue: "Produit", expense: "Charge",
};
export const IC_TYPES: Record<string, string> = {
  sale_purchase: "Vente / achat", loan: "Prêt", transfer: "Virement", cost_allocation: "Refacturation de frais",
  dividend: "Dividende", other: "Autre",
};
type Tone = "success" | "warning" | "error" | "info" | undefined;
export const IC_STATUS: Record<string, { label: string; tone: Tone }> = {
  draft: { label: "Brouillon", tone: undefined }, confirmed: { label: "Confirmée", tone: "info" },
  reconciled: { label: "Rapprochée", tone: "success" }, eliminated: { label: "Éliminée", tone: "success" },
};
export const MATCH_STATUS: Record<string, { label: string; tone: Tone }> = {
  unmatched: { label: "Non rapprochée", tone: "warning" }, matched: { label: "Concordante", tone: "success" },
  asymmetric: { label: "Écart", tone: "error" }, missing_counterpart: { label: "Sans contrepartie", tone: "error" },
};
export const RUN_STATUS: Record<string, { label: string; tone: Tone }> = {
  draft: { label: "Brouillon", tone: undefined }, preparing: { label: "En préparation", tone: undefined },
  reconciling: { label: "Rapprochements à faire", tone: "warning" }, ready: { label: "Prête", tone: "info" },
  validated: { label: "Validée", tone: "success" }, published: { label: "Publiée", tone: "success" },
  reopened: { label: "Rouverte", tone: "warning" },
};
export const TASK_STATUS: Record<string, string> = {
  open: "À faire", in_progress: "En cours", blocked: "Bloquée", done: "Terminée", cancelled: "Annulée",
};
export const PRIORITIES: Record<string, string> = { low: "Basse", normal: "Normale", high: "Haute", urgent: "Urgente" };
export const ACTIVITY_LABELS: Record<string, string> = {
  supermarket: "Supermarché", grocery: "Épicerie", pharmacy: "Pharmacie", restaurant: "Restaurant", boutique: "Boutique",
  hotel: "Hôtel", hardware_store: "Quincaillerie", wholesaler: "Grossiste", distributor: "Distributeur", services: "Services",
  production: "Production", ecommerce: "Commerce en ligne", agriculture: "Agriculture", other: "Autre",
};
export const FX_METHODS: Record<string, string> = {
  closing_rate: "Taux de clôture", average_rate: "Taux moyen", historical: "Taux historique",
};

/** Contreparties possibles d'une opération : sens inverse, confirmée, pas encore rapprochée (mêmes règles que le serveur). */
export function counterpartCandidates(source: IntercompanyTx, all: IntercompanyTx[]): IntercompanyTx[] {
  return all
    .filter((t) => t.id !== source.id && t.status === "confirmed" && t.matchStatus !== "matched"
      && t.sourceCompanyId === source.targetCompanyId && t.targetCompanyId === source.sourceCompanyId)
    .sort((a, b) => Math.abs(a.amount - source.amount) - Math.abs(b.amount - source.amount));
}

/** Une opération se rapproche si elle est confirmée et pas déjà concordante. */
export const canReconcile = (t: IntercompanyTx) => t.status === "confirmed" && t.matchStatus !== "matched";

/** Actions possibles sur une consolidation selon son état (cycle du serveur). */
export function runActions(r: Pick<ConsolidationRun, "status" | "coherenceOk">) {
  return {
    execute: ["draft", "preparing", "reconciling", "ready", "reopened"].includes(r.status),
    validate: r.status === "ready" && r.coherenceOk,
    publish: r.status === "validated",
    reopen: r.status === "validated" || r.status === "published",
  };
}
