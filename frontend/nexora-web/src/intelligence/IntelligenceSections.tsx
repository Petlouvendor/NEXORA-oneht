import { useCallback, useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { Badge, Button, Card, DataTable, Drawer, Field, Modal, SectionLabel, TextInput, type Column } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { RecommendationCard, SectionHeader, SignalCount, evidenceMetrics } from "@/shared/intel";
import { AiConversation } from "@/ai/AiConversation";
import { useFormDialog } from "@/shared/FormDialog";
import { usePrompt } from "@/shared/PromptDialog";
import {
  acceptRecommendation, completeTask, deleteRule, dismissRecommendation, getBriefing,
  getIntelligenceDashboard, listModels, listRecommendations, listRules, listScores, listTasks,
  modifyRecommendation, resolveRecommendation, saveRule, viewRecommendation,
  RECOMMENDATION_STATUS_LABEL,
  type AssistantBriefing, type EntityScore,
  type IntelligenceModel, type IntelligenceRule, type IntelligenceTask, type Recommendation,
} from "@/intelligence/intelligenceApi";

// Intelligence métier & recommandations (Phase 43 §2, §4-5, §6-10, §31, §33-39, §45-48).

function useAsync<T>(loader: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = useCallback(() => {
    setError(null);
    loader().then(setData).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(load, [load]);
  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }
  return { data, error, busy, load, run };
}

const confidenceTone = (level: string) => level === "high" ? "success" : level === "medium" ? "info" : level === "low" ? "warning" : "error";

/** Phase 54 §7 — priorité catégorielle : CRITIQUE/IMPORTANT/À SURVEILLER/OPPORTUNITÉ. */
const priorityLevelTone = (level: string) =>
  level === "critical" ? "error" : level === "important" ? "warning" : level === "opportunity" ? "success" : "info";
const priorityLevelLabel = (level: string) =>
  level === "critical" ? "CRITIQUE" : level === "important" ? "IMPORTANT" : level === "opportunity" ? "OPPORTUNITÉ" : "À SURVEILLER";

// --- §5, §20 Vue générale — NEXORA INTELLIGENCE (NEXORA ONE §7) -----------------

/** Domaines analysés (NEXORA ONE §7) → catégories de règles qui les couvrent aujourd'hui. */
const DOMAINS: Array<{ id: string; label: string; categories: string[] }> = [
  { id: "all", label: "Tout", categories: [] },
  { id: "sales", label: "Ventes", categories: ["sales_spike", "sales_decline"] },
  { id: "stock", label: "Stock", categories: ["stock_reorder", "stock_dormant"] },
  { id: "purchasing", label: "Achats & fournisseurs", categories: ["supplier_price_increase"] },
  { id: "finance", label: "Finance", categories: ["cashflow_risk", "budget_overrun"] },
  { id: "customers", label: "Clients", categories: ["receivable_priority", "customer_inactive"] },
];

export function IntelligenceDashboardSection({ canAct, onOpenList }: { canAct: boolean; onOpenList: () => void }) {
  const dash = useAsync(getIntelligenceDashboard);
  const d = dash.data;
  const cards = [...(d?.topActions ?? []), ...(d?.topOpportunities ?? [])];
  return (
    <div style={{ display: "grid", gap: 20 }}>
      {dash.error && <div className="nx-error-text">{dash.error}</div>}
      <div className="nx-grid">
        <div className="nx-col-3"><SignalCount count={d?.criticalCount ?? null} label="critiques" tone="error" icon={<Icon.Alert size={20} />} /></div>
        <div className="nx-col-3"><SignalCount count={d?.importantCount ?? null} label="importantes" tone="warning" icon={<Icon.Bulb size={20} />} /></div>
        <div className="nx-col-3"><SignalCount count={d?.watchCount ?? null} label="à surveiller" tone="info" icon={<Icon.Info size={20} />} /></div>
        <div className="nx-col-3"><SignalCount count={d?.opportunityCount ?? null} label="opportunités" tone="success" icon={<Icon.Trend size={20} />} /></div>
      </div>
      {d && <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
        {d.openAnomalies} anomalie(s) ouverte(s) détectée(s) par l'analyse continue des données.
      </div>}
      <div>
        <SectionHeader eyebrow="Priorités" title="Ce que NEXORA vous recommande maintenant"
          action={<Button size="sm" variant="ghost" onClick={onOpenList}>Toutes les recommandations <Icon.ArrowRight size={14} /></Button>} />
        {d && cards.length === 0 && <Card>Aucune recommandation ouverte : rien à décider pour le moment.</Card>}
        <RecommendationGrid recs={cards} canAct={canAct} onChanged={dash.load} />
      </div>
    </div>
  );
}

// --- §2, §4-5, §31, §33, §46 Recommandations ---------------------------------

/** Écran où l'action principale se poursuit (NEXORA ONE §2 : [COMMANDER] mène à l'achat, etc.). */
const ACTION_ROUTE: Record<string, string> = {
  stock_reorder: "/purchasing", sales_spike: "/purchasing", supplier_price_increase: "/purchasing",
  receivable_priority: "/credit", customer_inactive: "/customers", stock_dormant: "/catalog",
  budget_overrun: "/reports", cashflow_risk: "/finance", sales_decline: "/reports",
};

/** Grille de cartes QUOI / POURQUOI / IMPACT / ACTION + analyse détaillée + formulaire « Modifier ». */
function RecommendationGrid({ recs, canAct, onChanged }: { recs: Recommendation[]; canAct: boolean; onChanged: () => void }) {
  const navigate = useNavigate();
  const [analysed, setAnalysed] = useState<Recommendation | null>(null);
  const [editing, setEditing] = useState<Recommendation | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function run(fn: () => Promise<unknown>, then?: () => void) {
    setBusy(true); setError(null);
    try { await fn(); onChanged(); then?.(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action impossible"); }
    finally { setBusy(false); }
  }

  /** « Voir l'analyse » : la consultation d'une recommandation nouvelle est tracée (new → viewed). */
  function analyse(r: Recommendation) {
    setAnalysed(r);
    if (r.status === "new") void viewRecommendation(r.id).then(onChanged).catch(() => {});
  }

  return (
    <>
      {error && <div className="nx-error-text" role="alert">{error}</div>}
      <div className="nx-grid">
        {recs.map((r) => (
          <div key={r.id} className="nx-col-4">
            <RecommendationCard rec={r} busy={busy}
              onAnalyse={() => analyse(r)}
              onModify={canAct ? () => setEditing(r) : undefined}
              onAct={canAct ? () => void run(() => acceptRecommendation(r.id, { createTask: true }),
                () => ACTION_ROUTE[r.category] && navigate(ACTION_ROUTE[r.category])) : undefined}
              onDismiss={canAct ? () => void run(() => dismissRecommendation(r.id)) : undefined} />
          </div>
        ))}
      </div>

      {analysed && (
        <Drawer title="Analyse de la recommandation" onClose={() => setAnalysed(null)}>
          <div style={{ display: "grid", gap: 14 }}>
            <div>
              <Badge tone={priorityLevelTone(analysed.priorityLevel)}>{priorityLevelLabel(analysed.priorityLevel)}</Badge>{" "}
              <Badge>{RECOMMENDATION_STATUS_LABEL[analysed.status] ?? analysed.status}</Badge>
              <h3 style={{ margin: "8px 0 0" }}>{analysed.title}</h3>
            </div>
            <div className="nx-insight__block"><b>Pourquoi ?</b><span>{analysed.why}</span></div>
            <div className="nx-insight__block"><b>Données utilisées</b>
              <div className="nx-insight__metrics">
                {evidenceMetrics(analysed.evidence).map((m) => (
                  <div key={m.key} className="nx-metric"><div className="nx-metric__label">{m.label}</div><div className="nx-metric__value">{m.value}</div></div>
                ))}
              </div>
            </div>
            <div className="nx-insight__block"><b>Impact estimé</b>
              <span>{evidenceMetrics(analysed.impactEstimate).filter((m) => m.key !== "currency").map((m) => `${m.label} : ${m.value}`).join(" · ") || "—"}</span>
            </div>
            <div className="nx-insight__block"><b>Action proposée</b><span>{analysed.proposedAction}</span></div>
            <div className="nx-insight__block"><b>Résultat attendu</b><span>{analysed.expectedResult}</span></div>
            <div className="nx-insight__block"><b>Confiance</b>
              <span><Badge tone={confidenceTone(analysed.confidenceLevel)}>{analysed.confidenceLevel}</Badge> {analysed.confidenceReason}</span>
            </div>
            <div className="nx-insight__block"><b>Score de priorité</b>
              <span>{analysed.priorityScore} = impact {analysed.impactScore} × urgence {analysed.urgencyScore} × confiance {analysed.confidenceScore}</span>
            </div>
            {analysed.overrideValue && (
              <div className="nx-insight__block"><b>Valeur ajustée</b><span>{JSON.stringify(analysed.overrideValue)}</span></div>
            )}
          </div>
        </Drawer>
      )}

      {editing && (
        <ModifyRecommendationDialog rec={editing} busy={busy} onClose={() => setEditing(null)}
          onSubmit={(value, note) => void run(() => modifyRecommendation(editing.id, value, note), () => setEditing(null))} />
      )}
    </>
  );
}

/**
 * « Modifier » (Phase 54 §16, §18-19) : ajuste les valeurs numériques proposées — la proposition d'origine
 * reste conservée côté serveur (`impact_estimate`), la valeur ajustée va dans `override_value`.
 */
function ModifyRecommendationDialog({ rec, busy, onClose, onSubmit }: {
  rec: Recommendation; busy: boolean; onClose: () => void; onSubmit: (value: Record<string, unknown>, note?: string) => void;
}) {
  const numeric = Object.entries({ ...rec.impactEstimate, ...(rec.overrideValue ?? {}) })
    .filter(([, v]) => typeof v === "number") as Array<[string, number]>;
  const [values, setValues] = useState<Record<string, string>>(Object.fromEntries(numeric.map(([k, v]) => [k, String(v)])));
  const [note, setNote] = useState("");
  const labels = Object.fromEntries(evidenceMetrics(Object.fromEntries(numeric)).map((m) => [m.key, m.label]));
  const invalid = Object.values(values).some((v) => v.trim() === "" || Number.isNaN(Number(v)));
  return (
    <Modal title="Modifier la recommandation" onClose={onClose}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Annuler</Button>
        <Button loading={busy} disabled={invalid || numeric.length === 0}
                onClick={() => onSubmit({ ...(rec.overrideValue ?? {}), ...Object.fromEntries(Object.entries(values).map(([k, v]) => [k, Number(v)])) }, note || undefined)}>
          Enregistrer l'ajustement
        </Button>
      </>}>
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ color: "var(--nx-text-2)" }}>{rec.title}</div>
        {numeric.length === 0 && <div>Cette recommandation ne propose aucune valeur chiffrée à ajuster.</div>}
        {numeric.map(([k, original]) => (
          <Field key={k} label={labels[k] ?? k} hint={`Proposé par NEXORA : ${original}`}>
            <TextInput inputMode="decimal" value={values[k]} onChange={(e) => setValues((v) => ({ ...v, [k]: e.target.value }))} />
          </Field>
        ))}
        <Field label="Motif (optionnel)">
          <TextInput value={note} onChange={(e) => setNote(e.target.value)} maxLength={300} />
        </Field>
      </div>
    </Modal>
  );
}

export function RecommendationsSection({ canAct }: { canAct: boolean }) {
  // « À décider » = nouvelles ET déjà vues (résumé fonctionnel §17).
  const recs = useAsync(() => listRecommendations("open"));
  const [domain, setDomain] = useState("all");
  const categories = DOMAINS.find((d) => d.id === domain)?.categories ?? [];
  const shown = (recs.data ?? []).filter((r) => categories.length === 0 || categories.includes(r.category));
  return (
    <div style={{ display: "grid", gap: 14 }}>
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }} role="group" aria-label="Domaine analysé">
        {DOMAINS.map((d) => {
          const n = (recs.data ?? []).filter((r) => d.categories.length === 0 || d.categories.includes(r.category)).length;
          return (
            <button key={d.id} type="button" className="nx-chip" aria-pressed={domain === d.id} onClick={() => setDomain(d.id)}>
              {d.label} <span style={{ color: "var(--nx-text-3)" }}>{n}</span>
            </button>
          );
        })}
      </div>
      {recs.error && <div className="nx-error-text">{recs.error}</div>}
      {recs.data && shown.length === 0 && <Card>Aucune recommandation ouverte dans ce domaine.</Card>}
      <RecommendationGrid recs={shown} canAct={canAct} onChanged={recs.load} />
    </div>
  );
}

/** Boucle d'apprentissage (§33) — résultat positif/négatif sur les recommandations déjà acceptées. */
export function RecommendationOutcomesSection({ canAct }: { canAct: boolean }) {
  // Exécution (résultat constaté) : après acceptation OU modification (résumé fonctionnel §17).
  const recs = useAsync(async () => [...await listRecommendations("accepted"), ...await listRecommendations("modified")]);
  const prompt = usePrompt();
  async function resolve(id: string, outcome: "positive" | "negative") {
    const note = await prompt.ask({
      title: outcome === "positive" ? "Résultat positif" : "Résultat négatif", label: "Note", multiline: true, confirmLabel: "Consigner",
      hint: "Facultative : ce que la recommandation a réellement produit (sert à l'apprentissage des règles).",
      onSubmit: (n) => resolveRecommendation(id, outcome, n || undefined),
    });
    if (note !== null) recs.load();
  }
  const cols: Array<Column<Recommendation>> = [
    { key: "t", header: "Recommandation acceptée", render: (r) => r.title },
    { key: "d", header: "Décidée", render: (r) => new Date(r.createdAt).toLocaleDateString() },
    { key: "o", header: "", render: (r) => canAct && (
      <div style={{ display: "flex", gap: 6 }}>
        <Button size="sm" onClick={() => void resolve(r.id, "positive")}>Résultat positif</Button>
        <Button size="sm" variant="ghost" onClick={() => void resolve(r.id, "negative")}>Résultat négatif</Button>
      </div>
    ) },
  ];
  return (
    <Card>
      {prompt.dialog}
      <SectionLabel>Boucle d'apprentissage (§33) — mesurer le résultat des recommandations acceptées</SectionLabel>
      {recs.error && <div className="nx-error-text">{recs.error}</div>}
      <DataTable columns={cols} rows={recs.data ?? []} rowKey={(r) => r.id} empty="Aucune recommandation en attente de mesure." />
    </Card>
  );
}

// --- §35 Scoring --------------------------------------------------------------

export function ScoresSection() {
  const scores = useAsync(() => listScores());
  const cols: Array<Column<EntityScore>> = [
    { key: "e", header: "Type", render: (s) => <Badge>{s.entityType}</Badge> },
    { key: "l", header: "Entité", render: (s) => s.entityLabel },
    { key: "k", header: "Score", render: (s) => <code>{s.scoreKey}</code> },
    { key: "v", header: "Valeur", numeric: true, render: (s) => s.value },
    { key: "lv", header: "Niveau de risque", render: (s) => <Badge tone={s.level === "high" ? "error" : s.level === "medium" ? "warning" : "success"}>{s.level}</Badge> },
  ];
  return (
    <Card>
      <SectionLabel>Scoring explicable (§35) — chaque score montre ses variables</SectionLabel>
      {scores.error && <div className="nx-error-text">{scores.error}</div>}
      <DataTable columns={cols} rows={scores.data ?? []} rowKey={(s) => `${s.entityType}:${s.entityId}:${s.scoreKey}`} empty="Aucun score calculé pour le moment." />
    </Card>
  );
}

// --- §39 Gouvernance -----------------------------------------------------------

export function ModelsSection() {
  const models = useAsync(listModels);
  const cols: Array<Column<IntelligenceModel>> = [
    { key: "k", header: "Clé", render: (m) => <code>{m.key}</code> },
    { key: "t", header: "Type", render: (m) => <Badge>{m.kind === "rule" ? "règle" : "score"}</Badge> },
    { key: "n", header: "Nom", render: (m) => m.name },
    { key: "d", header: "Description", render: (m) => <span style={{ color: "var(--nx-text-2)" }}>{m.description}</span> },
    { key: "o", header: "Propriétaire", render: (m) => m.owner },
    { key: "v", header: "Version", numeric: true, render: (m) => m.version },
  ];
  return (
    <Card>
      <SectionLabel>Gouvernance (§39) — catalogue des règles et scores en production</SectionLabel>
      {models.error && <div className="nx-error-text">{models.error}</div>}
      <DataTable columns={cols} rows={models.data ?? []} rowKey={(m) => m.key} empty="—" />
    </Card>
  );
}

// --- §34 Règles configurables ----------------------------------------------------

export function RulesSection({ canManage }: { canManage: boolean }) {
  const rules = useAsync(listRules);
  const form = useFormDialog();
  async function override() {
    const system = (rules.data ?? []).filter((r) => r.isSystemDefault);
    const v = await form.ask({
      title: "Surcharger une règle", confirmLabel: "Enregistrer la surcharge",
      intro: "La surcharge s'applique à cette entreprise ; la règle système reste intacte et revient si la surcharge est retirée.",
      fields: [
        { name: "key", label: "Règle", type: "select", required: true, options: system.map((r) => ({ value: r.key, label: r.label })) },
        { name: "label", label: "Libellé pour l'entreprise", hint: "Vide : libellé de la règle système." },
        { name: "defaultPriority", label: "Priorité", type: "select", defaultValue: "medium",
          options: [{ value: "high", label: "Haute" }, { value: "medium", label: "Moyenne" }, { value: "low", label: "Basse" }] },
        { name: "enabled", label: "État", type: "select", options: [{ value: "true", label: "Activée" }, { value: "false", label: "Désactivée" }] },
      ],
      onSubmit: (f) => {
        const label = f.label || system.find((r) => r.key === f.key)?.label || f.key;
        return saveRule({
          key: f.key, category: "custom", label, threshold: {}, defaultPriority: f.defaultPriority, actionLabel: label, enabled: f.enabled === "true",
        });
      },
    });
    if (v) rules.load();
  }
  const cols: Array<Column<IntelligenceRule>> = [
    { key: "k", header: "Clé", render: (r) => <code>{r.key}</code> },
    { key: "l", header: "Libellé", render: (r) => r.label },
    { key: "s", header: "Portée", render: (r) => <Badge tone={r.isSystemDefault ? undefined : "info"}>{r.isSystemDefault ? "système" : "entreprise"}</Badge> },
    { key: "p", header: "Priorité", render: (r) => r.defaultPriority },
    { key: "e", header: "État", render: (r) => <Badge tone={r.enabled ? "success" : undefined}>{r.enabled ? "activée" : "désactivée"}</Badge> },
    { key: "a", header: "", render: (r) => canManage && !r.isSystemDefault && (
      <Button size="sm" variant="ghost" onClick={() => rules.run(() => deleteRule(r.id))}>Retirer la surcharge</Button>
    ) },
  ];
  return (
    <Card>
      <SectionLabel action={canManage && (
        <Button size="sm" variant="ghost" disabled={rules.busy || !rules.data} onClick={() => void override()}>Surcharger une règle</Button>
      )}>Règles configurables (§34)</SectionLabel>
      {rules.error && <div className="nx-error-text">{rules.error}</div>}
      {form.dialog}
      <DataTable columns={cols} rows={rules.data ?? []} rowKey={(r) => r.id} empty="—" />
    </Card>
  );
}

// --- §31 Tâches ---------------------------------------------------------------

export function TasksSection() {
  const tasks = useAsync(() => listTasks());
  const cols: Array<Column<IntelligenceTask>> = [
    { key: "t", header: "Tâche", render: (t) => t.title },
    { key: "p", header: "Priorité", render: (t) => <Badge tone={t.priority === "urgent" ? "error" : t.priority === "high" ? "warning" : undefined}>{t.priority}</Badge> },
    { key: "s", header: "Statut", render: (t) => <Badge tone={t.status === "done" ? "success" : undefined}>{t.status}</Badge> },
    { key: "d", header: "Échéance", render: (t) => t.dueAt ? new Date(t.dueAt).toLocaleDateString() : "—" },
    { key: "a", header: "", render: (t) => t.status !== "done" && (
      <Button size="sm" variant="ghost" onClick={() => tasks.run(() => completeTask(t.id))}>Marquer terminée</Button>
    ) },
  ];
  return (
    <Card>
      <SectionLabel>Tâches issues des recommandations (§31)</SectionLabel>
      {tasks.error && <div className="nx-error-text">{tasks.error}</div>}
      <DataTable columns={cols} rows={tasks.data ?? []} rowKey={(t) => t.id} empty="Aucune tâche." />
    </Card>
  );
}

// --- §6-10, §28-30 Assistant ---------------------------------------------------

const ROLES = [
  { id: "direction", label: "Direction" },
  { id: "manager", label: "Manager" },
  { id: "comptable", label: "Comptable" },
  { id: "finance", label: "Finance" },
  { id: "caissier", label: "Caissier" },
];

export function AssistantSection() {
  const [role, setRole] = useState("direction");
  const [briefing, setBriefing] = useState<AssistantBriefing | null>(null);
  const [error, setError] = useState<string | null>(null);

  const loadBriefing = useCallback((r: string) => {
    setError(null);
    getBriefing(r).then(setBriefing).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);
  useEffect(() => loadBriefing(role), [role, loadBriefing]);

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Assistant par rôle (§6-10)</SectionLabel>
        <div style={{ display: "flex", gap: 6, marginBottom: 12, flexWrap: "wrap" }}>
          {ROLES.map((r) => (
            <Button key={r.id} size="sm" variant={role === r.id ? "primary" : "ghost"} onClick={() => setRole(r.id)}>{r.label}</Button>
          ))}
        </div>
        {error && <div className="nx-error-text">{error}</div>}
        {briefing && (
          <>
            <p style={{ fontWeight: 600 }}>{briefing.headline}</p>
            {briefing.recommendations.length > 0 && (
              <div style={{ marginTop: 8 }}>
                <SectionLabel>Actions recommandées</SectionLabel>
                <ul style={{ margin: "6px 0 0", paddingLeft: 18 }}>
                  {briefing.recommendations.map((r) => <li key={r.id}>{r.title}</li>)}
                </ul>
              </div>
            )}
            {briefing.insights.length > 0 && (
              <div style={{ marginTop: 8 }}>
                <SectionLabel>Points d'attention</SectionLabel>
                <ul style={{ margin: "6px 0 0", paddingLeft: 18 }}>
                  {briefing.insights.map((i) => <li key={i.id}>{i.title}</li>)}
                </ul>
              </div>
            )}
          </>
        )}
      </Card>

      <Card style={{ padding: 0, overflow: "hidden" }}>
        {/* NEXORA AI (NEXORA ONE §8) — même conversation que le panneau global (Ctrl+J). */}
        <AiConversation />
      </Card>
    </div>
  );
}
