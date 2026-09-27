import { apiFetch } from "@/core/apiClient";

// Intelligence métier & recommandations (Phase 43 §2, §4-5, §31, §33-39, §45-48).

export interface Recommendation {
  id: string; establishmentId: string | null; category: string; ruleKey: string; title: string;
  why: string; evidence: Record<string, unknown>; impactEstimate: Record<string, unknown>;
  proposedAction: string; expectedResult: string;
  impactScore: number; urgencyScore: number; confidenceScore: number;
  confidenceLevel: "high" | "medium" | "low" | "insufficient_data"; confidenceReason: string;
  priorityScore: number;
  /** Phase 54 §7 — colonne générée : dérive de `nature` + `priorityScore`, jamais recalculée côté client. */
  priorityLevel: "critical" | "important" | "watch" | "opportunity";
  nature: "risk" | "opportunity";
  drillPath: string | null; periodStart: string;
  /** Cycle (résumé fonctionnel §17) : new → viewed → accepted / modified / dismissed (ignorée) → resolved (exécutée) / expired. */
  status: "new" | "viewed" | "accepted" | "dismissed" | "snoozed" | "resolved" | "modified" | "expired";
  snoozeUntil: string | null; outcome: "positive" | "negative" | null; outcomeNote: string | null;
  expiresAt: string | null; overrideValue: Record<string, unknown> | null; createdAt: string;
  viewedAt: string | null;
}

/** Libellés français du cycle de vie (résumé fonctionnel §17). */
export const RECOMMENDATION_STATUS_LABEL: Record<Recommendation["status"], string> = {
  new: "Nouvelle", viewed: "Vue", accepted: "Acceptée", modified: "Modifiée", dismissed: "Ignorée",
  snoozed: "Reportée", resolved: "Exécutée", expired: "Expirée",
};

/** Phase 54 §5, §20 — vue générale : comptes par priorité, actions prioritaires, opportunités. */
export interface IntelligenceDashboard {
  criticalCount: number; importantCount: number; watchCount: number; opportunityCount: number;
  openAnomalies: number; topActions: Recommendation[]; topOpportunities: Recommendation[]; generatedAt: string;
}

export interface IntelligenceRule {
  id: string; isSystemDefault: boolean; key: string; category: string; label: string;
  threshold: Record<string, unknown>; periodDays: number | null; defaultPriority: string;
  recipientRole: string | null; actionLabel: string; escalateAfterDays: number | null;
  enabled: boolean; validFrom: string; expiresAt: string | null; version: number; approvedAt: string | null;
}

export interface EntityScore {
  entityId: string; entityLabel: string; entityType: "product" | "customer" | "supplier";
  scoreKey: string; value: number; level: "low" | "medium" | "high"; explanation: Record<string, unknown>; computedAt: string;
}

export interface IntelligenceModel {
  key: string; kind: "rule" | "score"; category: string; name: string; description: string;
  owner: string; version: number; inputs: string[];
}

export interface IntelligenceTask {
  id: string; title: string; description: string; referenceType: string | null; referenceId: string | null;
  assignedTo: string | null; priority: "low" | "normal" | "high" | "urgent";
  status: "open" | "in_progress" | "done" | "cancelled"; dueAt: string | null; createdAt: string;
}

export interface IntelligenceAuditEntry {
  id: string; tableName: string; action: string; changes: Record<string, unknown> | null;
  userId: string | null; occurredAt: string;
}

export interface AssistantBriefing {
  role: string; asOf: string; headline: string; dashboard: Record<string, unknown> | null;
  insights: Array<{ id: string; category: string; severity: string; title: string; detail: string }>;
  recommendations: Recommendation[];
}

export interface AssistantAnswer {
  intent: string; answer: string; facts: Array<{ key: string; label: string; value: number; unit: string; kind: string }>;
  sources: string[]; containsForecast: boolean;
  /** NEXORA ONE §8 — recommandations ouvertes liées à la question (seulement si l'utilisateur voit l'intelligence). */
  related?: Array<{ id: string; title: string; priorityLevel: Recommendation["priorityLevel"]; proposedAction: string }> | null;
}

// --- §2, §4-5, §31, §33, §46 Recommandations --------------------------------
export const listRecommendations = (status?: string, category?: string) => {
  const qs = new URLSearchParams();
  if (status) qs.set("status", status);
  if (category) qs.set("category", category);
  const q = qs.toString();
  return apiFetch<Recommendation[]>(`/intelligence/recommendations${q ? `?${q}` : ""}`);
};
/** Première consultation : « nouvelle » → « vue » (qui, quand). */
export const viewRecommendation = (id: string) =>
  apiFetch<Recommendation>(`/intelligence/recommendations/${id}/view`, { method: "POST" });
export const acceptRecommendation = (id: string, opts?: { createTask?: boolean; assignTo?: string; note?: string }) =>
  apiFetch<Recommendation>(`/intelligence/recommendations/${id}/accept`, { method: "POST", body: JSON.stringify(opts ?? {}) });
export const dismissRecommendation = (id: string, reason?: string) =>
  apiFetch<void>(`/intelligence/recommendations/${id}/dismiss`, { method: "POST", body: JSON.stringify({ reason }) });
export const snoozeRecommendation = (id: string, until: string, reason?: string) =>
  apiFetch<Recommendation>(`/intelligence/recommendations/${id}/snooze`, { method: "POST", body: JSON.stringify({ until, reason }) });
export const resolveRecommendation = (id: string, outcome: "positive" | "negative", note?: string) =>
  apiFetch<Recommendation>(`/intelligence/recommendations/${id}/resolve`, { method: "POST", body: JSON.stringify({ outcome, note }) });
/** Phase 54 §16, §19 — ajuste une valeur proposée avant validation, sans écraser la proposition d'origine. */
export const modifyRecommendation = (id: string, overrideValue: Record<string, unknown>, note?: string) =>
  apiFetch<Recommendation>(`/intelligence/recommendations/${id}/modify`, { method: "POST", body: JSON.stringify({ overrideValue, note }) });

// --- §5, §20 Vue générale ------------------------------------------------------
export const getIntelligenceDashboard = () => apiFetch<IntelligenceDashboard>("/intelligence/dashboard");

// --- §45, §58 Centre d'intelligence (réexpose Phase 42) ---------------------
export const listAnomalies = (status?: string) => apiFetch<AssistantBriefing["insights"]>(`/intelligence/anomalies${status ? `?status=${status}` : ""}`);
export const listIntelligenceForecasts = (metric?: string) => apiFetch<unknown[]>(`/intelligence/forecasts${metric ? `?metric=${metric}` : ""}`);

// --- §35 Scoring -------------------------------------------------------------
export const listScores = (entityType?: string) => apiFetch<EntityScore[]>(`/intelligence/scores${entityType ? `?entityType=${entityType}` : ""}`);

// --- §39 Gouvernance ----------------------------------------------------------
export const listModels = () => apiFetch<IntelligenceModel[]>("/intelligence/models");

// --- §34 Règles configurables --------------------------------------------------
export const listRules = () => apiFetch<IntelligenceRule[]>("/intelligence/rules");
export const saveRule = (body: {
  key: string; category: string; label: string; threshold: Record<string, unknown>; periodDays?: number | null;
  defaultPriority: string; recipientRole?: string | null; actionLabel: string; escalateAfterDays?: number | null;
  enabled: boolean; expiresAt?: string | null;
}) => apiFetch<IntelligenceRule>("/intelligence/rules", { method: "POST", body: JSON.stringify(body) });
export const deleteRule = (id: string) => apiFetch<void>(`/intelligence/rules/${id}`, { method: "DELETE" });

// --- §38 Observabilité ---------------------------------------------------------
export const listIntelligenceAudit = (limit = 100) => apiFetch<IntelligenceAuditEntry[]>(`/intelligence/audit?limit=${limit}`);

// --- §31 Tâches ------------------------------------------------------------------
export const listTasks = (status?: string) => apiFetch<IntelligenceTask[]>(`/intelligence/tasks${status ? `?status=${status}` : ""}`);
export const createTask = (body: { title: string; description?: string; referenceType?: string; referenceId?: string; assignedTo?: string; priority?: string; dueAt?: string }) =>
  apiFetch<IntelligenceTask>("/intelligence/tasks", { method: "POST", body: JSON.stringify(body) });
export const completeTask = (id: string) => apiFetch<IntelligenceTask>(`/intelligence/tasks/${id}/complete`, { method: "POST" });

// --- §6-10, §28-30 Assistant -----------------------------------------------------
export const getBriefing = (role: string, sessionId?: string) =>
  apiFetch<AssistantBriefing>(`/assistant/briefing?role=${role}${sessionId ? `&sessionId=${sessionId}` : ""}`);
export const askAssistant = (question: string) =>
  apiFetch<AssistantAnswer>("/assistant/query", { method: "POST", body: JSON.stringify({ question }) });
