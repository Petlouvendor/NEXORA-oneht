import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, KpiCard, Money, SectionLabel, Tabs,
  type Column,
} from "@/shared/ui";
import {
  createSnapshot, getDataQuality, getReconciliation, listSnapshots,
  type AnalyticsSnapshot, type DataQualityCheck, type DataQualityReport,
  type ReconciliationLine, type ReconciliationReport,
} from "@/reports/analyticsApi";
import {
  BudgetsSection, ForecastsSection, InsightsSection, KpiCatalogSection, ObjectivesSection, ReportsSection,
} from "@/reports/BiSections";
import { ExportMenu } from "@/shared/ExportMenu";
import { exportReport } from "@/shared/printApi";

/** Rapports, Business Intelligence & Pilotage (Phase 7, Phase 31 §44-46, Phase 42 §33-50). */
export function AnalyticsPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("kpis");
  const [recon, setRecon] = useState<ReconciliationReport | null>(null);
  const [dq, setDq] = useState<DataQualityReport | null>(null);
  const [snaps, setSnaps] = useState<AnalyticsSnapshot[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canView = hasPermission("reporting.view");
  const canDataQuality = hasPermission("reporting.data_quality");
  const canManageBudgets = hasPermission("reporting.manage_budgets");
  const canForecast = hasPermission("reporting.forecast");
  const canManageInsights = hasPermission("reporting.manage_insights");
  const canSchedule = hasPermission("reporting.schedule");
  const canManageTargets = hasPermission("reporting.manage_targets");

  const load = useCallback(() => {
    if (!canDataQuality) return;
    setError(null);
    Promise.all([
      getReconciliation().catch(() => null),
      getDataQuality().catch(() => null),
      listSnapshots().catch(() => [] as AnalyticsSnapshot[]),
    ])
      .then(([r, q, s]) => { setRecon(r); setDq(q); setSnaps(s); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, [canDataQuality]);

  useEffect(load, [load]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  if (!canView) {
    return <Card>Le reporting requiert la permission <code>reporting.view</code>.</Card>;
  }

  const cur = recon?.currency ?? "HTG";

  const reconCols: Array<Column<ReconciliationLine>> = [
    { key: "l", header: "Indicateur", render: (l) => <>{l.label}</> },
    { key: "op", header: "Opérationnel", numeric: true, render: (l) => <Money value={l.operationalValue} currency={cur} /> },
    { key: "ac", header: "Comptable", numeric: true, render: (l) => <Money value={l.accountingValue} currency={cur} /> },
    { key: "d", header: "Écart", numeric: true, render: (l) => <Money value={l.difference} currency={cur} /> },
    { key: "s", header: "", render: (l) => <Badge tone={l.reconciled ? "success" : "error"}>{l.reconciled ? "réconcilié" : "écart"}</Badge> },
  ];

  const dqCols: Array<Column<DataQualityCheck>> = [
    { key: "l", header: "Contrôle", render: (c) => <>{c.label}</> },
    { key: "cat", header: "Catégorie", render: (c) => <Badge>{c.category}</Badge> },
    { key: "n", header: "Occurrences", numeric: true, render: (c) => (c.count > 0 ? <Badge tone={c.severity === "error" ? "error" : c.severity === "warn" ? "warning" : "info"}>{c.count}</Badge> : "—") },
    { key: "det", header: "Détail", render: (c) => c.detail ?? (c.count === 0 ? "OK" : "—") },
  ];

  const snapCols: Array<Column<AnalyticsSnapshot>> = [
    { key: "k", header: "Type", render: (s) => <Badge>{s.kind}</Badge> },
    { key: "l", header: "Libellé", render: (s) => s.label ?? "—" },
    { key: "d", header: "Date", render: (s) => s.asOfDate },
    { key: "sc", header: "Score", render: (s) => (s.score != null ? String(s.score) : "—") },
    { key: "c", header: "Créé", render: (s) => new Date(s.createdAt).toLocaleString() },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Rapports & Pilotage</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {/* Journal des ventes, stock et balances âgées sont des rapports financiers (serveur : reporting.export_financial). */}
      {hasPermission("reporting.export_financial") && <FullExports />}

      {canDataQuality && (
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
          <KpiCard
            label="Réconciliation"
            value={recon ? `${recon.reconciledCount}/${recon.totalCount}` : "—"}
            tone={recon && !recon.allReconciled ? "warning" : undefined}
            tag={recon && !recon.allReconciled ? "!" : undefined}
          />
          <KpiCard label="Score qualité" value={dq ? `${dq.score} (${dq.grade})` : "—"}
            tone={dq && dq.score < 75 ? "warning" : undefined} />
          <KpiCard label="Contrôles en échec" value={dq ? String(dq.checksFailed) : "—"} />
          <KpiCard label="Snapshots" value={String(snaps.length)} />
        </div>
      )}

      <Tabs
        tabs={[
          { id: "kpis", label: "Catalogue KPI" },
          { id: "objectives", label: "Objectifs & score" },
          { id: "reports", label: "Rapports" },
          { id: "forecasts", label: "Prévisions" },
          { id: "insights", label: "Insights" },
          { id: "budgets", label: "Budgets" },
          { id: "reconciliation", label: "Réconciliation KPI" },
          { id: "quality", label: "Qualité des données" },
          { id: "snapshots", label: `Snapshots (${snaps.length})` },
        ]}
        active={tab}
        onChange={setTab}
      />

      {tab === "kpis" && <KpiCatalogSection />}
      {tab === "objectives" && <ObjectivesSection canManage={canManageTargets} />}
      {tab === "reports" && <ReportsSection canSchedule={canSchedule} />}
      {tab === "forecasts" && <ForecastsSection canGenerate={canForecast} />}
      {tab === "insights" && <InsightsSection canManage={canManageInsights} />}
      {tab === "budgets" && <BudgetsSection canManage={canManageBudgets} />}

      {["reconciliation", "quality", "snapshots"].includes(tab) && (
      <Card>
        {!canDataQuality && (
          <div style={{ color: "var(--nx-text-2)" }}>
            La couche de confiance du reporting requiert la permission <code>reporting.data_quality</code>.
          </div>
        )}
        <div style={{ marginTop: 12 }}>
          {tab === "reconciliation" && recon && (
            <>
              <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginBottom: 8 }}>
                Période {recon.from} → {recon.to} · tolérance {recon.tolerance} {cur}
              </div>
              <DataTable columns={reconCols} rows={recon.lines} rowKey={(l) => l.metric} empty="—" />
              <div style={{ marginTop: 10 }}>
                <SectionLabel>Définitions (§2)</SectionLabel>
                <ul style={{ margin: "6px 0 0", paddingLeft: 18, fontSize: 12, color: "var(--nx-text-2)" }}>
                  {recon.lines.map((l) => <li key={l.metric}><strong>{l.label}</strong> — {l.definition}</li>)}
                </ul>
              </div>
              <div style={{ marginTop: 12 }}>
                <Button size="sm" disabled={busy} onClick={() => run(() => createSnapshot("reconciliation", "Contrôle " + new Date().toLocaleDateString()))}>
                  Figer un snapshot
                </Button>
              </div>
            </>
          )}

          {tab === "quality" && dq && (
            <>
              <div style={{ display: "flex", gap: 12, alignItems: "center", marginBottom: 8 }}>
                <Badge tone={dq.score >= 90 ? "success" : dq.score >= 60 ? "warning" : "error"}>{dq.grade}</Badge>
                <span>Score {dq.score}/100 · {dq.checksFailed}/{dq.checksRun} contrôles en échec</span>
              </div>
              <DataTable columns={dqCols} rows={dq.checks} rowKey={(c) => c.key} empty="—" />
              <div style={{ marginTop: 12 }}>
                <Button size="sm" disabled={busy} onClick={() => run(() => createSnapshot("data_quality", "Qualité " + new Date().toLocaleDateString()))}>
                  Figer un snapshot
                </Button>
              </div>
            </>
          )}

          {tab === "snapshots" && (
            <DataTable columns={snapCols} rows={snaps} rowKey={(s) => s.id} empty="Aucun snapshot figé." />
          )}
        </div>
      </Card>
      )}
    </div>
  );
}

/**
 * Exports complets calculés par le serveur (toutes les lignes, filtres appliqués), en vrai Excel, PDF ou
 * CSV. Les rapports financiers exigent en plus reporting.export_financial (contrôlé par le serveur).
 * Tout autre tableau de l'application s'exporte depuis son propre bouton « Exporter ce tableau ».
 */
function FullExports() {
  const [preset, setPreset] = useState("month");
  const reports: Array<{ key: string; label: string; filter?: Record<string, unknown> }> = [
    { key: "sales_journal", label: "Journal des ventes", filter: { preset } },
    { key: "stock_valuation", label: "Valorisation du stock" },
    { key: "aging_receivables", label: "Balance âgée clients" },
    { key: "aging_payables", label: "Balance âgée fournisseurs" },
  ];
  return (
    <Card>
      <SectionLabel>Exports complets (Excel, PDF)</SectionLabel>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center", marginTop: 8 }}>
        <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: "var(--nx-fs-sm)" }}>
          Période des ventes
          <select className="nx-input" value={preset} onChange={(e) => setPreset(e.target.value)} style={{ width: "auto" }}>
            <option value="today">Aujourd'hui</option>
            <option value="week">Cette semaine</option>
            <option value="month">Ce mois</option>
            <option value="year">Cette année</option>
          </select>
        </label>
        {reports.map((r) => (
          <ExportMenu key={r.key} label={r.label} run={(format) => exportReport(r.key, format, r.filter ?? {})} />
        ))}
      </div>
    </Card>
  );
}
