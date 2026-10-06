import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { Tabs } from "@/shared/ui";
import { usePrompt } from "@/shared/PromptDialog";
import {
  goLiveReadiness,
  listDeployments,
  listSignOffs,
  recordDeployment,
  rollbackDeployment,
  signOff,
  systemStatus,
  type Deployment,
  type GoLiveReadiness,
  type GoLiveSignOff,
  type SystemStatus,
} from "@/operations/operationsApi";
import {
  DataCorrectionsSection, DiagnosticsSection, FeatureFlagsSection, JobsSection,
  MaintenanceWindowsSection, QueuesSection, RetentionSection,
} from "@/operations/MaintenanceSections";
import { ProductionSection } from "@/operations/ProductionSection";

const rel = (iso: string | null) => {
  if (!iso) return "—";
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? "à l'instant" : m < 60 ? `il y a ${m} min` : m < 1440 ? `il y a ${Math.round(m / 60)} h` : `il y a ${Math.round(m / 1440)} j`;
};

const STATUS_TONE: Record<string, string> = {
  ok: "var(--nx-success)", pass: "var(--nx-success)",
  degraded: "var(--nx-warning)", warn: "var(--nx-warning)", manual: "var(--nx-text-2)",
  down: "var(--nx-error)", fail: "var(--nx-error)",
};
const STATUS_MARK: Record<string, string> = { ok: "●", pass: "✓", degraded: "▲", warn: "▲", manual: "○", down: "✕", fail: "✕" };

/** Qualité, exploitation & préparation production (Phase 37 §29, §40, §54, §56). */
export function OperationsPage() {
  const { activeCompanyId, hasPermission } = useAuth();
  const [sys, setSys] = useState<SystemStatus | null>(null);
  const [gl, setGl] = useState<GoLiveReadiness | null>(null);
  const [signoffs, setSignoffs] = useState<GoLiveSignOff[]>([]);
  const [deployments, setDeployments] = useState<Deployment[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canManage = hasPermission("operations.manage");
  const canDecide = hasPermission("operations.correct_data");
  const [tab, setTab] = useState("overview");
  const prompt = usePrompt();

  const load = useCallback(() => {
    setError(null);
    Promise.all([systemStatus(), goLiveReadiness(), listSignOffs(), listDeployments()])
      .then(([s, g, so, d]) => { setSys(s); setGl(g); setSignoffs(so); setDeployments(d); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);
  useEffect(load, [load, activeCompanyId]);

  async function run(fn: () => Promise<unknown>, msg?: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await fn(); if (msg) setNotice(msg); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("operations.view")) {
    return <div className="nx-card">L'exploitation n'est pas accessible avec vos permissions.</div>;
  }

  const td: React.CSSProperties = { padding: "6px 8px", whiteSpace: "nowrap" };
  const th: React.CSSProperties = { padding: "4px 8px", textAlign: "left", color: "var(--nx-text-2)" };
  const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <h1 style={{ margin: 0 }}>Exploitation & maintenance</h1>
      <Tabs
        active={tab} onChange={setTab}
        tabs={[
          { id: "overview", label: "Vue d'ensemble" },
          { id: "production", label: "Production" },
          { id: "jobs", label: "Jobs" },
          { id: "queues", label: "Files & dead-letter" },
          { id: "diagnostics", label: "Diagnostics" },
          { id: "flags", label: "Bascules" },
          { id: "maintenance", label: "Maintenance planifiée" },
          { id: "retention", label: "Rétention & purge" },
          { id: "corrections", label: "Corrections" },
        ]}
      />
      {error && <div className="nx-error-text">{error}</div>}
      {notice && <div style={{ color: "var(--nx-success)" }}>{notice}</div>}

      {tab === "production" && <ProductionSection />}
      {tab === "jobs" && <JobsSection canManage={canManage} />}
      {tab === "queues" && <QueuesSection canManage={canManage} />}
      {tab === "diagnostics" && <DiagnosticsSection canManage={canManage} />}
      {tab === "flags" && <FeatureFlagsSection canManage={canManage} />}
      {tab === "maintenance" && <MaintenanceWindowsSection canManage={canManage} />}
      {tab === "retention" && <RetentionSection canManage={canManage} />}
      {tab === "corrections" && <DataCorrectionsSection canManage={canManage} canDecide={canDecide} />}

      {tab === "overview" && sys && (
        <section className="nx-card" style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
            <span style={label}>État système (§29, §56)</span>
            <span style={{ fontWeight: 800, textTransform: "uppercase", color: STATUS_TONE[sys.overall] }}>{sys.overall}</span>
            <span style={{ fontSize: 12, color: "var(--nx-text-2)" }}>vérifié {rel(sys.checkedAt)}</span>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(240px, 1fr))", gap: 10 }}>
            {sys.components.map((c) => (
              <div key={c.component} style={{ display: "grid", gap: 2, border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: "8px 10px" }}>
                <div style={{ fontWeight: 700 }}>
                  <span style={{ color: STATUS_TONE[c.status] }}>{STATUS_MARK[c.status]}</span> {c.component}
                </div>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{c.detail}</div>
              </div>
            ))}
          </div>
        </section>
      )}

      {tab === "overview" && gl && (
        <section className="nx-card" style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", flexWrap: "wrap", gap: 8 }}>
            <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
              <span style={label}>Checklist Go-Live (§40, §54)</span>
              <span style={{ fontWeight: 800, color: gl.canGoLive ? "var(--nx-success)" : "var(--nx-error)" }}>
                {gl.canGoLive ? "GO possible" : "NO-GO"}
              </span>
              <span style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
                {gl.passed} ok · {gl.warnings} alerte(s) · {gl.failed} échec(s) · {gl.manual} à attester
              </span>
            </div>
            {canManage && (
              <div style={{ display: "flex", gap: 6 }}>
                <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                        onClick={() => run(() => signOff({ environment: "production", verdict: "go_with_reservations", notes: "validé depuis la console" }), "Go-Live signé (avec réserves)")}>
                  Signer avec réserves
                </button>
                <button className="nx-btn" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy || !gl.canGoLive}
                        onClick={() => run(() => signOff({ environment: "production", verdict: "go" }), "Go-Live signé")}>
                  Signer le Go-Live
                </button>
              </div>
            )}
          </div>
          <div style={{ overflowX: "auto" }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
              <thead><tr>{["", "Contrôle", "Catégorie", "Source", "Détail"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
              <tbody>
                {gl.checks.map((c) => (
                  <tr key={c.key} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={{ ...td, color: STATUS_TONE[c.status], fontWeight: 700 }}>{STATUS_MARK[c.status]}</td>
                    <td style={td}>{c.label}</td>
                    <td style={{ ...td, color: "var(--nx-text-2)" }}>{c.category}</td>
                    <td style={{ ...td, color: "var(--nx-text-2)" }}>{c.source}</td>
                    <td style={{ padding: "6px 8px", color: "var(--nx-text-2)", whiteSpace: "normal" }}>{c.detail}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {tab === "overview" && (
      <section className="nx-card">
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <div style={label}>Journal des déploiements (§34, §38-39)</div>
          {canManage && (
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                    onClick={async () => {
                      const v = await prompt.ask({ title: "Journaliser un déploiement", label: "Version déployée", required: true, minLength: 1,
                        hint: "Version de l'image (ex. 1.18.0) ; le schéma de base courant est enregistré avec.", confirmLabel: "Journaliser" });
                      if (v) void run(() => recordDeployment({ version: v, environment: "production" }), "Déploiement journalisé");
                    }}>
              Journaliser un déploiement
            </button>
          )}
        </div>
        <div style={{ overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Version", "Environnement", "Schéma", "Statut", "Quand", ""].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {deployments.map((d) => (
                <tr key={d.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{d.version}{d.rolledBackFrom && <span style={{ color: "var(--nx-text-2)" }}> ↩</span>}</td>
                  <td style={td}>{d.environment}</td>
                  <td style={td}>{d.schemaVersion ?? "—"}</td>
                  <td style={{ ...td, color: d.status === "succeeded" ? "var(--nx-success)" : d.status === "rolled_back" ? "var(--nx-warning)" : "var(--nx-error)" }}>{d.status}</td>
                  <td style={td}>{rel(d.startedAt)}</td>
                  <td style={td}>
                    {canManage && d.status === "succeeded" && !d.rolledBackFrom && (
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-warning)" }} disabled={busy}
                              onClick={async () => {
                                const r = await prompt.ask({ title: `Retour arrière de ${d.version}`, label: "Motif du retour arrière", required: true,
                                  multiline: true, danger: true, confirmLabel: "Journaliser le retour arrière" });
                                if (r) void run(() => rollbackDeployment(d.id, { reason: r }), "Retour arrière journalisé");
                              }}>
                        Rollback
                      </button>
                    )}
                  </td>
                </tr>
              ))}
              {deployments.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={6}>Aucun déploiement journalisé.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
      )}

      {prompt.dialog}
      {tab === "overview" && signoffs.length > 0 && (
        <section className="nx-card">
          <div style={label}>Validations Go-Live (§54)</div>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Environnement", "Verdict", "Prêt", "Forcé", "Quand"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {signoffs.map((s) => (
                <tr key={s.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{s.environment}</td>
                  <td style={{ ...td, color: s.verdict === "go" ? "var(--nx-success)" : s.verdict === "no_go" ? "var(--nx-error)" : "var(--nx-warning)" }}>{s.verdict}</td>
                  <td style={td}>{s.canGoLive ? "✓" : "✕"}</td>
                  <td style={td}>{s.forced ? `oui — ${s.forceReason ?? ""}` : "—"}</td>
                  <td style={td}>{rel(s.signedAt)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </div>
  );
}
