import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { listUsers } from "@/admin/adminApi";
import { listAccounts } from "@/accounting/accountingApi";
import { useFormDialog } from "@/shared/FormDialog";
import { usePrompt } from "@/shared/PromptDialog";
import { Badge, Button, Card, DataTable, Money, SectionLabel, type Column } from "@/shared/ui";
import {
  approveBudget, assignInsight, closeBudget, createBudget, deleteObjective, deleteReport,
  deleteSchedule, dismissInsight, generateForecast, getBudgetLines, getObjectiveScore,
  listBudgets, listExecutions, listForecasts, listInsights, listKpiDefinitions, listObjectives,
  listReports, listSchedules, reviseBudget, runReport, saveReport, saveSchedule, setBudgetLines,
  setObjective, submitBudget,
  type Budget, type BudgetLine, type Forecast, type Insight, type KpiDefinition,
  type Objective, type ObjectiveScore, type ReportCatalogEntry, type ReportExecution,
  type ReportSchedule, type SavedReport,
} from "@/reports/biApi";

// Business Intelligence, Reporting & Pilotage (Phase 42 §33-41, §48-50, §55).

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

const rel = (iso: string | null) => {
  if (!iso) return "—";
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? "à l'instant" : m < 60 ? `il y a ${m} min` : m < 1440 ? `il y a ${Math.round(m / 60)} h` : `il y a ${Math.round(m / 1440)} j`;
};

// --- §48, §50 : catalogue KPI gouverné --------------------------------------

export function KpiCatalogSection() {
  const { data, error } = useAsync(listKpiDefinitions);
  const cols: Array<Column<KpiDefinition>> = [
    { key: "k", header: "Clé", render: (k) => <code>{k.key}</code> },
    { key: "l", header: "Libellé", render: (k) => k.label },
    { key: "cat", header: "Catégorie", render: (k) => <Badge>{k.category}</Badge> },
    { key: "f", header: "Formule", render: (k) => <span style={{ color: "var(--nx-text-2)" }}>{k.formula}</span> },
    { key: "s", header: "Source", render: (k) => k.source },
    { key: "u", header: "Unité", numeric: true, render: (k) => k.unit },
    { key: "o", header: "Propriétaire", render: (k) => k.owner },
    { key: "v", header: "Version", numeric: true, render: (k) => k.version },
  ];
  return (
    <Card>
      <SectionLabel>Catalogue KPI gouverné (§48, §50)</SectionLabel>
      {error && <div className="nx-error-text">{error}</div>}
      <DataTable columns={cols} rows={data ?? []} rowKey={(k) => k.key} empty="—" />
    </Card>
  );
}

// --- §39-40 : objectifs & score de performance ------------------------------

export function ObjectivesSection({ canManage }: { canManage: boolean }) {
  const objectives = useAsync(listObjectives);
  const score = useAsync(getObjectiveScore);
  const form = useFormDialog();
  async function newObjective() {
    const kpis = await listKpiDefinitions().catch(() => []);
    const v = await form.ask({
      title: "Nouvel objectif", confirmLabel: "Enregistrer",
      fields: [
        { name: "kpiKey", label: "Indicateur", type: "select", required: true,
          options: kpis.map((k) => ({ value: k.key, label: `${k.label} (${k.unit})` })) },
        { name: "periodType", label: "Période", type: "select", defaultValue: "month", options: PERIODS },
        { name: "targetValue", label: "Valeur cible", type: "number", required: true, min: 0 },
        { name: "direction", label: "Sens", type: "select", options: [
          { value: "min", label: "À atteindre (au moins la cible)" }, { value: "max", label: "Plafond (au plus la cible)" }] },
      ],
      onSubmit: (f) => setObjective({ kpiKey: f.kpiKey, periodType: f.periodType, targetValue: Number(f.targetValue), direction: f.direction }),
    });
    if (v) objectives.load();
  }

  const cols: Array<Column<Objective>> = [
    { key: "k", header: "KPI", render: (o) => <code>{o.kpiKey}</code> },
    { key: "p", header: "Période", render: (o) => o.periodType },
    { key: "t", header: "Cible", numeric: true, render: (o) => <Money value={o.targetValue} /> },
    { key: "d", header: "Sens", render: (o) => <Badge>{o.direction === "min" ? "à atteindre" : "plafond"}</Badge> },
    { key: "w", header: "Poids", numeric: true, render: (o) => o.weight },
    { key: "s", header: "", render: (o) => canManage && <Button size="sm" variant="ghost" onClick={() => objectives.run(() => deleteObjective(o.id))}>Supprimer</Button> },
  ];
  const scoreCols: Array<Column<ObjectiveScore["items"][number]>> = [
    { key: "k", header: "KPI", render: (i) => i.label },
    { key: "t", header: "Cible", numeric: true, render: (i) => <Money value={i.target} /> },
    { key: "a", header: "Réel", numeric: true, render: (i) => <Money value={i.actual} /> },
    { key: "r", header: "Atteinte", numeric: true, render: (i) => <Badge tone={i.ratioPct >= 100 ? "success" : i.ratioPct >= 80 ? "warning" : "error"}>{i.ratioPct}%</Badge> },
    { key: "w", header: "Poids", numeric: true, render: (i) => i.weight },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Score de performance (§40)</SectionLabel>
        {score.error && <div className="nx-error-text">{score.error}</div>}
        {score.data && (
          <>
            <div style={{ display: "flex", alignItems: "baseline", gap: 12, marginBottom: 10 }}>
              <Badge tone={score.data.score >= 100 ? "success" : score.data.score >= 70 ? "warning" : "error"}>{score.data.grade}</Badge>
              <strong style={{ fontSize: 20 }}>{score.data.score}%</strong>
              <span style={{ color: "var(--nx-text-2)", fontSize: 12 }}>évalué {rel(score.data.evaluatedAt)}</span>
            </div>
            <DataTable columns={scoreCols} rows={score.data.items} rowKey={(i) => i.kpiKey} empty="Aucun objectif actif." />
          </>
        )}
      </Card>
      <Card>
        <SectionLabel action={canManage && (
          <Button size="sm" variant="ghost" disabled={objectives.busy} onClick={() => void newObjective()}>Nouvel objectif</Button>
        )}>Objectifs (§39)</SectionLabel>
        {objectives.error && <div className="nx-error-text">{objectives.error}</div>}
        {form.dialog}
        <DataTable columns={cols} rows={objectives.data ?? []} rowKey={(o) => o.id} empty="Aucun objectif." />
      </Card>
    </div>
  );
}

// --- §48-49, §41 : catalogue de rapports, rapports enregistrés & planifiés --

export function ReportsSection({ canSchedule }: { canSchedule: boolean }) {
  const catalog = useAsync(listReports);
  const schedules = useAsync(listSchedules);
  const executions = useAsync(listExecutions);
  const form = useFormDialog();
  const reportOptions = () => (catalog.data?.standard ?? []).map((r) => ({ value: r.key, label: `${r.name} — ${r.category}` }));
  async function newSavedReport() {
    const v = await form.ask({
      title: "Enregistrer un rapport", confirmLabel: "Enregistrer",
      fields: [
        { name: "name", label: "Nom", required: true },
        { name: "reportKey", label: "Rapport standard", type: "select", required: true, options: reportOptions() },
      ],
      onSubmit: (f) => saveReport({ name: f.name, reportKey: f.reportKey, filter: {} }),
    });
    if (v) catalog.load();
  }
  async function newSchedule() {
    const v = await form.ask({
      title: "Planifier un rapport", confirmLabel: "Planifier",
      fields: [
        { name: "name", label: "Nom", required: true },
        { name: "reportKey", label: "Rapport standard", type: "select", required: true, options: reportOptions() },
        { name: "frequency", label: "Fréquence", type: "select", defaultValue: "weekly", options: FREQUENCIES },
      ],
      onSubmit: (f) => saveSchedule({ name: f.name, reportKey: f.reportKey, frequency: f.frequency, recipients: [] }),
    });
    if (v) schedules.load();
  }
  const [result, setResult] = useState<{ key: string; json: string } | null>(null);

  const standardCols: Array<Column<ReportCatalogEntry>> = [
    { key: "n", header: "Rapport", render: (r) => r.name },
    { key: "cat", header: "Catégorie", render: (r) => <Badge>{r.category}</Badge> },
    { key: "d", header: "Description", render: (r) => <span style={{ color: "var(--nx-text-2)" }}>{r.description}</span> },
    { key: "e", header: "Accès", render: (r) => <code>{r.endpoint}</code> },
  ];
  const savedCols: Array<Column<SavedReport>> = [
    { key: "n", header: "Nom", render: (r) => r.name },
    { key: "k", header: "Rapport", render: (r) => <code>{r.reportKey}</code> },
    { key: "s", header: "Partage", render: (r) => <Badge tone={r.isShared ? "info" : undefined}>{r.isShared ? "partagé" : "privé"}</Badge> },
    { key: "a", header: "", render: (r) => (
      <div style={{ display: "flex", gap: 6 }}>
        <Button size="sm" variant="ghost" onClick={async () => setResult({ key: r.reportKey, json: JSON.stringify((await runReport(r.id)).result, null, 2) })}>Exécuter</Button>
        {r.mine && <Button size="sm" variant="ghost" onClick={() => catalog.run(() => deleteReport(r.id))}>Supprimer</Button>}
      </div>
    ) },
  ];
  const scheduleCols: Array<Column<ReportSchedule>> = [
    { key: "n", header: "Nom", render: (s) => s.name },
    { key: "k", header: "Rapport", render: (s) => <code>{s.reportKey}</code> },
    { key: "f", header: "Fréquence", render: (s) => s.frequency },
    { key: "st", header: "Statut", render: (s) => <Badge tone={s.status === "active" ? "success" : undefined}>{s.status}</Badge> },
    { key: "nx", header: "Prochaine", render: (s) => rel(s.nextRunAt) },
    { key: "a", header: "", render: (s) => <Button size="sm" variant="ghost" onClick={() => schedules.run(() => deleteSchedule(s.id))}>Supprimer</Button> },
  ];
  const execCols: Array<Column<ReportExecution>> = [
    { key: "k", header: "Rapport", render: (e) => <code>{e.reportKey}</code> },
    { key: "s", header: "Statut", render: (e) => <Badge tone={e.status === "succeeded" ? "success" : e.status === "failed" ? "error" : undefined}>{e.status}</Badge> },
    { key: "n", header: "Lignes", numeric: true, render: (e) => e.rowCount ?? "—" },
    { key: "d", header: "Quand", render: (e) => rel(e.startedAt) },
    { key: "err", header: "Erreur", render: (e) => e.error ?? "—" },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Catalogue de rapports standards (§48)</SectionLabel>
        {catalog.error && <div className="nx-error-text">{catalog.error}</div>}
        {form.dialog}
        <DataTable columns={standardCols} rows={catalog.data?.standard ?? []} rowKey={(r) => r.key} empty="—" />
      </Card>
      <Card>
        <SectionLabel action={
          <Button size="sm" variant="ghost" disabled={catalog.busy || !catalog.data} onClick={() => void newSavedReport()}>Enregistrer un rapport</Button>
        }>Mes rapports enregistrés (§49)</SectionLabel>
        <DataTable columns={savedCols} rows={catalog.data?.saved ?? []} rowKey={(r) => r.id} empty="Aucun rapport enregistré." />
        {result && (
          <div className="nx-card" style={{ marginTop: 10, background: "var(--nx-surface-2)" }}>
            <SectionLabel action={<Button size="sm" variant="ghost" onClick={() => setResult(null)}>Fermer</Button>}>Résultat — {result.key}</SectionLabel>
            <pre style={{ maxHeight: 300, overflow: "auto", fontSize: 12 }}>{result.json}</pre>
          </div>
        )}
      </Card>
      {canSchedule && (
        <Card>
          <SectionLabel action={
            <Button size="sm" variant="ghost" disabled={schedules.busy || !catalog.data} onClick={() => void newSchedule()}>Planifier un rapport</Button>
          }>Rapports planifiés (§41)</SectionLabel>
          {schedules.error && <div className="nx-error-text">{schedules.error}</div>}
          <DataTable columns={scheduleCols} rows={schedules.data ?? []} rowKey={(s) => s.id} empty="Aucun rapport planifié." />
        </Card>
      )}
      {canSchedule && (
        <Card>
          <SectionLabel>Journal des exécutions</SectionLabel>
          {executions.error && <div className="nx-error-text">{executions.error}</div>}
          <DataTable columns={execCols} rows={(executions.data ?? []).slice(0, 20)} rowKey={(e) => e.id} empty="Aucune exécution." />
        </Card>
      )}
    </div>
  );
}

// --- §35-36 : prévisions -----------------------------------------------------

const METRICS = ["revenue", "cash", "stock", "receivables", "expenses"];
// Finalisation phase 20 : listes de choix des formulaires (avant : clés techniques à taper dans window.prompt).
const METRIC_LABEL: Record<string, string> = {
  revenue: "Chiffre d'affaires", cash: "Trésorerie", stock: "Valeur du stock", receivables: "Créances clients", expenses: "Dépenses",
};
const PERIODS = [
  { value: "day", label: "Jour" }, { value: "week", label: "Semaine" }, { value: "month", label: "Mois" },
  { value: "quarter", label: "Trimestre" }, { value: "year", label: "Année" },
];
const FREQUENCIES = [
  { value: "daily", label: "Chaque jour" }, { value: "weekly", label: "Chaque semaine" },
  { value: "monthly", label: "Chaque mois" }, { value: "on_close", label: "À chaque clôture" },
];
const SCENARIOS = [
  { value: "base", label: "Tendance actuelle" }, { value: "optimistic", label: "Optimiste" },
  { value: "prudent", label: "Prudent" }, { value: "custom", label: "Personnalisé" },
];
const MONTHS = ["Janvier", "Février", "Mars", "Avril", "Mai", "Juin", "Juillet", "Août", "Septembre", "Octobre", "Novembre", "Décembre"];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function ForecastsSection({ canGenerate }: { canGenerate: boolean }) {
  const forecasts = useAsync(() => listForecasts());
  const form = useFormDialog();
  async function newForecast() {
    const v = await form.ask({
      title: "Générer une prévision", confirmLabel: "Générer",
      intro: "Une prévision est une estimation fondée sur l'historique récent — jamais une certitude.",
      fields: [
        { name: "metric", label: "Indicateur", type: "select", options: METRICS.map((m) => ({ value: m, label: METRIC_LABEL[m] ?? m })) },
        { name: "horizonDays", label: "Horizon (jours)", type: "number", defaultValue: "30", required: true, min: 1, max: 365, integer: true },
        { name: "scenario", label: "Scénario", type: "select", defaultValue: "base", options: SCENARIOS },
      ],
      onSubmit: (f) => generateForecast({ metric: f.metric, horizonDays: Number(f.horizonDays), scenario: f.scenario }),
    });
    if (v) forecasts.load();
  }
  const cols: Array<Column<Forecast>> = [
    { key: "m", header: "Indicateur", render: (f) => <Badge>{f.metric}</Badge> },
    { key: "s", header: "Scénario", render: (f) => f.scenario },
    { key: "h", header: "Horizon", numeric: true, render: (f) => `${f.horizonDays} j` },
    { key: "c", header: "Confiance", render: (f) => <Badge tone={f.confidenceLevel === "high" ? "success" : f.confidenceLevel === "low" ? "warning" : undefined}>{f.confidenceLevel}</Badge> },
    { key: "d", header: "Généré", render: (f) => rel(f.generatedAt) },
    { key: "a", header: "Hypothèses", render: (f) => <span style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{f.assumptions}</span> },
  ];

  return (
    <Card>
      <SectionLabel action={canGenerate && (
        <Button size="sm" variant="ghost" disabled={forecasts.busy} onClick={() => void newForecast()}>Générer une prévision</Button>
      )}>Prévisions — toujours une estimation (§35-36)</SectionLabel>
      {forecasts.error && <div className="nx-error-text">{forecasts.error}</div>}
      {form.dialog}
      <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginBottom: 8 }}>
        Une prévision est une estimation fondée sur l'historique récent — jamais une certitude (§35).
      </div>
      <DataTable columns={cols} rows={forecasts.data ?? []} rowKey={(f) => f.id} empty="Aucune prévision générée." />
    </Card>
  );
}

// --- §37-38, §58 : insights --------------------------------------------------

export function InsightsSection({ canManage }: { canManage: boolean }) {
  const insights = useAsync(() => listInsights());
  const form = useFormDialog();
  const prompt = usePrompt();
  async function assign(insight: Insight) {
    // Liste des utilisateurs si le rôle peut la lire ; sinon saisie de l'identifiant, contrôlée.
    const users = await listUsers().catch(() => null);
    const v = await form.ask({
      title: `Assigner « ${insight.title} »`, confirmLabel: "Assigner",
      fields: [users
        ? { name: "userId", label: "Responsable", type: "select", required: true,
            options: users.filter((u) => u.status === "active").map((u) => ({ value: u.id, label: u.fullName ? `${u.fullName} — ${u.email}` : u.email })) }
        : { name: "userId", label: "Identifiant de l'utilisateur", required: true, hint: "Identifiant (UUID) visible dans Administration → Utilisateurs.",
            validate: (x) => (UUID.test(x) ? null : "Identifiant (UUID) invalide.") }],
      onSubmit: (f) => assignInsight(insight.id, f.userId),
    });
    if (v) insights.load();
  }
  async function dismiss(insight: Insight) {
    const reason = await prompt.ask({ title: `Ignorer « ${insight.title} »`, label: "Motif", multiline: true, confirmLabel: "Ignorer",
      hint: "Facultatif, mais utile au suivi : pourquoi cette anomalie n'appelle pas d'action ?",
      onSubmit: (r) => dismissInsight(insight.id, r || undefined) });
    if (reason !== null) insights.load();
  }
  const cols: Array<Column<Insight>> = [
    { key: "s", header: "", render: (i) => <Badge tone={i.severity === "critical" ? "error" : i.severity === "warning" ? "warning" : "info"}>{i.severity}</Badge> },
    { key: "t", header: "Insight", render: (i) => <><strong>{i.title}</strong><div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{i.detail}</div></> },
    { key: "act", header: "Action suggérée", render: (i) => <span style={{ color: "var(--nx-text-2)" }}>{i.suggestedAction ?? "—"}</span> },
    { key: "st", header: "Statut", render: (i) => <Badge>{i.status}</Badge> },
    { key: "a", header: "", render: (i) => canManage && i.status === "open" && (
      <div style={{ display: "flex", gap: 6 }}>
        <Button size="sm" variant="ghost" onClick={() => void assign(i)}>Assigner</Button>
        <Button size="sm" variant="ghost" onClick={() => void dismiss(i)}>Ignorer</Button>
      </div>
    ) },
  ];
  return (
    <Card>
      <SectionLabel>Insights — anomalies expliquées (§37-38)</SectionLabel>
      {insights.error && <div className="nx-error-text">{insights.error}</div>}
      {form.dialog}
      {prompt.dialog}
      <DataTable columns={cols} rows={insights.data ?? []} rowKey={(i) => i.id} empty="Aucune anomalie détectée." />
    </Card>
  );
}

// --- §33-34 : budgets ---------------------------------------------------------

const BUDGET_ACTIONS: Record<string, Array<{ label: string; fn: (id: string) => Promise<unknown> }>> = {
  draft: [{ label: "Soumettre", fn: submitBudget }],
  pending_approval: [{ label: "Approuver", fn: approveBudget }],
  active: [{ label: "Clore", fn: closeBudget }],
  closed: [{ label: "Réviser", fn: reviseBudget }],
};

export function BudgetsSection({ canManage }: { canManage: boolean }) {
  const budgets = useAsync(listBudgets);
  const [lines, setLines] = useState<{ budgetId: string; rows: BudgetLine[] } | null>(null);
  const form = useFormDialog();
  async function newBudget() {
    const year = new Date().getFullYear();
    const v = await form.ask({
      title: "Nouveau budget", confirmLabel: "Créer",
      fields: [
        { name: "name", label: "Nom", required: true },
        { name: "fiscalYear", label: "Exercice", type: "number", defaultValue: String(year), required: true, integer: true, min: year - 5, max: year + 5 },
        { name: "currencyCode", label: "Devise", type: "select", defaultValue: "HTG",
          options: ["HTG", "USD", "EUR", "CAD"].map((c) => ({ value: c, label: c })) },
      ],
      onSubmit: (f) => createBudget({ name: f.name, fiscalYear: Number(f.fiscalYear), currencyCode: f.currencyCode }),
    });
    if (v) budgets.load();
  }
  async function addLine(b: Budget) {
    const accounts = await listAccounts().catch(() => []);
    const v = await form.ask({
      title: `Ligne du budget « ${b.name} »`, confirmLabel: "Ajouter",
      fields: [
        { name: "accountId", label: "Compte", type: "select",
          options: [{ value: "", label: "— sans compte (enveloppe globale) —" }, ...accounts.map((a) => ({ value: a.id, label: `${a.code} — ${a.name}` }))] },
        { name: "periodMonth", label: "Période", type: "select", defaultValue: "",
          options: [{ value: "", label: "Toute l'année" }, ...MONTHS.map((m, i) => ({ value: String(i + 1), label: m }))] },
        { name: "amount", label: `Montant (${b.currencyCode})`, type: "number", required: true, min: 0 },
      ],
      onSubmit: async (f) => {
        const existing = lines?.budgetId === b.id ? lines.rows : await getBudgetLines(b.id);
        await setBudgetLines(b.id, [
          ...existing.map((l) => ({ accountId: l.accountId, costCenterId: l.costCenterId, establishmentId: l.establishmentId, periodMonth: l.periodMonth, amount: l.amount })),
          { accountId: f.accountId || null, periodMonth: f.periodMonth ? Number(f.periodMonth) : null, amount: Number(f.amount) },
        ]);
      },
    });
    if (v) budgets.load();
  }

  const cols: Array<Column<Budget>> = [
    { key: "n", header: "Nom", render: (b) => b.name },
    { key: "y", header: "Exercice", numeric: true, render: (b) => b.fiscalYear },
    { key: "c", header: "Devise", render: (b) => b.currencyCode },
    { key: "s", header: "Statut", render: (b) => <Badge tone={b.status === "active" ? "success" : b.status === "closed" ? undefined : "warning"}>{b.status}</Badge> },
    { key: "v", header: "", render: (b) => canManage && (
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        <Button size="sm" variant="ghost" onClick={async () => setLines({ budgetId: b.id, rows: await getBudgetLines(b.id) })}>Lignes</Button>
        {b.status === "draft" && (
          <Button size="sm" variant="ghost" onClick={() => void addLine(b)}>+ Ligne</Button>
        )}
        {(BUDGET_ACTIONS[b.status] ?? []).map((a) => (
          <Button key={a.label} size="sm" variant="ghost" onClick={() => budgets.run(() => a.fn(b.id))}>{a.label}</Button>
        ))}
      </div>
    ) },
  ];

  return (
    <Card>
      <SectionLabel action={canManage && (
        <Button size="sm" variant="ghost" disabled={budgets.busy} onClick={() => void newBudget()}>Nouveau budget</Button>
      )}>Budgets (§33-34)</SectionLabel>
      {budgets.error && <div className="nx-error-text">{budgets.error}</div>}
      {form.dialog}
      <DataTable columns={cols} rows={budgets.data ?? []} rowKey={(b) => b.id} empty="Aucun budget." />
      {lines && (
        <div className="nx-card" style={{ marginTop: 10, background: "var(--nx-surface-2)" }}>
          <SectionLabel action={<Button size="sm" variant="ghost" onClick={() => setLines(null)}>Fermer</Button>}>Lignes du budget</SectionLabel>
          {lines.rows.length === 0
            ? <div style={{ color: "var(--nx-text-2)" }}>Aucune ligne.</div>
            : <ul style={{ margin: 0, paddingLeft: 18 }}>{lines.rows.map((l) => <li key={l.id}>{l.periodMonth ? `Mois ${l.periodMonth}` : "Annuel"} — <Money value={l.amount} /></li>)}</ul>}
        </div>
      )}
    </Card>
  );
}
