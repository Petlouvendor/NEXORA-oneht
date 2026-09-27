import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import {
  databaseMaintenance, deadLetters, decideDataCorrection, deleteDeadLetter, deleteFeatureFlag,
  diagnostics, executePurge, invalidateCache, listDataCorrections, listFeatureFlags, listJobs,
  listMaintenanceWindows, listRetentionPolicies, pauseJob, previewPurge, purgeHistory, queueStats,
  replayDeadLetter, requestDataCorrection, resumeJob, runJobNow, saveFeatureFlag, scheduleMaintenance,
  storageStats, transitionMaintenance,
  type DataCorrection, type DeadLetterItem, type DiagnosticCheck,
  type FeatureFlag, type JobStatus, type MaintenanceWindow, type PurgeRun, type QueueStats,
  type RetentionPolicy,
} from "@/operations/operationsApi";

// Administration opérationnelle & maintenance (Phase 41 §7-9, §10-12, §13-15, §16-19, §23-24, §27, §44).

const td: React.CSSProperties = { padding: "6px 8px", whiteSpace: "nowrap" };
const th: React.CSSProperties = { padding: "4px 8px", textAlign: "left", color: "var(--nx-text-2)" };
const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };
const TONE: Record<string, string> = {
  ok: "var(--nx-success)", succeeded: "var(--nx-success)", applied: "var(--nx-success)", executed: "var(--nx-success)",
  warn: "var(--nx-warning)", running: "var(--nx-warning)", requested: "var(--nx-warning)",
  fail: "var(--nx-error)", failed: "var(--nx-error)", rejected: "var(--nx-error)",
  skipped: "var(--nx-text-2)",
};
const tone = (s: string) => TONE[s] ?? "var(--nx-text-2)";
const rel = (iso: string | null) => {
  if (!iso) return "—";
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? "à l'instant" : m < 60 ? `il y a ${m} min` : m < 1440 ? `il y a ${Math.round(m / 60)} h` : `il y a ${Math.round(m / 1440)} j`;
};
const bytes = (n: number) => {
  const u = ["o", "Ko", "Mo", "Go", "To"]; let v = n, i = 0;
  while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
  return `${v.toFixed(1)} ${u[i]}`;
};

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

// --- §7-9 : registre des jobs planifiés ------------------------------------

export function JobsSection({ canManage }: { canManage: boolean }) {
  const { data: jobs, error, busy, run } = useAsync(listJobs);
  return (
    <section className="nx-card" style={{ display: "grid", gap: 8 }}>
      <div style={label}>Jobs planifiés (§7-9)</div>
      {error && <div className="nx-error-text">{error}</div>}
      <div style={{ overflowX: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Job", "Catégorie", "Dernière exécution", "Statut", "Réussite (30j)", "État", canManage ? "" : undefined].filter(Boolean).map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {(jobs ?? []).map((j: JobStatus) => (
              <tr key={j.key} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td} title={j.description}>{j.name}<div style={{ color: "var(--nx-text-2)", fontSize: 11 }}>{j.key}</div></td>
                <td style={{ ...td, color: "var(--nx-text-2)" }}>{j.category === "business" ? "métier" : "technique"}</td>
                <td style={td}>{rel(j.lastRunAt)}</td>
                <td style={{ ...td, color: tone(j.lastStatus ?? "") }}>{j.lastStatus ?? "jamais exécuté"}{j.lastError ? ` — ${j.lastError}` : ""}</td>
                <td style={td}>{j.successRatePct == null ? "—" : `${j.successRatePct}% (${j.runsLast30Days})`}</td>
                <td style={{ ...td, color: j.enabled ? "var(--nx-success)" : "var(--nx-warning)" }}>{j.enabled ? "actif" : "en pause"}</td>
                {canManage && (
                  <td style={td}>
                    <div style={{ display: "flex", gap: 4 }}>
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                              onClick={() => void run(() => runJobNow(j.key))}>Exécuter</button>
                      {j.enabled
                        ? <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy} onClick={() => void run(() => pauseJob(j.key))}>Pause</button>
                        : <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy} onClick={() => void run(() => resumeJob(j.key))}>Reprendre</button>}
                    </div>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

// --- §10-12 : files d'attente & dead-letter --------------------------------

export function QueuesSection({ canManage }: { canManage: boolean }) {
  const q = useAsync(queueStats);
  const dl = useAsync(deadLetters);
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={label}>Files d'attente (§10)</div>
        {q.error && <div className="nx-error-text">{q.error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))", gap: 10 }}>
          {(q.data ?? []).map((s: QueueStats) => (
            <div key={s.queue} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: "8px 10px", display: "grid", gap: 2 }}>
              <div style={{ fontWeight: 700, textTransform: "capitalize" }}>{s.queue}</div>
              <div style={{ fontSize: 13 }}>{s.pending} en attente · {s.failed} en échec · <span style={{ color: s.dead > 0 ? "var(--nx-error)" : undefined }}>{s.dead} mort(s)</span></div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{s.deliveredLastHour} remis / h · plus ancien : {s.oldestPendingAgeMinutes == null ? "—" : `${Math.round(s.oldestPendingAgeMinutes)} min`}</div>
            </div>
          ))}
        </div>
      </section>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={label}>Dead-letter (§11-12)</div>
        {dl.error && <div className="nx-error-text">{dl.error}</div>}
        <div style={{ overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["File", "Sujet", "Cause", "Tentatives", "Créé", canManage ? "" : undefined].filter(Boolean).map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {(dl.data ?? []).map((d: DeadLetterItem) => (
                <tr key={d.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{d.queue}</td>
                  <td style={td}>{d.subject}</td>
                  <td style={{ ...td, color: "var(--nx-text-2)", whiteSpace: "normal" }}>{d.cause ?? "—"}</td>
                  <td style={td}>{d.attempts}</td>
                  <td style={td}>{rel(d.createdAt)}</td>
                  {canManage && (
                    <td style={td}>
                      <div style={{ display: "flex", gap: 4 }}>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={dl.busy}
                                onClick={() => void dl.run(() => replayDeadLetter(d.id))}>Rejouer</button>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={dl.busy}
                                onClick={() => void dl.run(() => deleteDeadLetter(d.id))}>Supprimer</button>
                      </div>
                    </td>
                  )}
                </tr>
              ))}
              {(dl.data ?? []).length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={6}>Aucun message mort.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}

// --- §5-6, §13-15 : diagnostics, base de données & stockage ----------------

export function DiagnosticsSection({ canManage }: { canManage: boolean }) {
  const diag = useAsync(diagnostics);
  const store = useAsync(storageStats);
  const dbm = useAsync(databaseMaintenance);
  const [cacheMsg, setCacheMsg] = useState<string | null>(null);
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={label}>Diagnostics (§5-6)</div>
        {diag.error && <div className="nx-error-text">{diag.error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))", gap: 10 }}>
          {(diag.data ?? []).map((c: DiagnosticCheck) => (
            <div key={c.key} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: "8px 10px", display: "grid", gap: 2 }}>
              <div style={{ fontWeight: 700 }}><span style={{ color: tone(c.status) }}>●</span> {c.label}</div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{c.detail}</div>
            </div>
          ))}
        </div>
      </section>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <div style={label}>Base de données & stockage (§13-15)</div>
          {canManage && (
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }}
                    onClick={() => { void invalidateCache().then(() => setCacheMsg("Cache purgé.")); }}>
              Purger le cache technique
            </button>
          )}
        </div>
        {cacheMsg && <div style={{ color: "var(--nx-success)", fontSize: 13 }}>{cacheMsg}</div>}
        {store.data && dbm.data && (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))", gap: 10, fontSize: 14 }}>
            <div>Base : <strong>{bytes(store.data.databaseSizeBytes)}</strong></div>
            <div>Sauvegardes : <strong>{store.data.backupCount}</strong> ({bytes(store.data.backupTotalSizeBytes)})</div>
            <div>Documents émis : <strong>{store.data.documentCount}</strong></div>
            <div>Connexions actives : <strong>{dbm.data.activeConnections}</strong></div>
            <div>Transactions longues : <strong>{dbm.data.longRunningTransactions}</strong></div>
            <div>Verrous : <strong>{dbm.data.locks}</strong></div>
          </div>
        )}
        {dbm.data && dbm.data.topTables.length > 0 && (
          <div style={{ overflowX: "auto" }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
              <thead><tr>{["Table", "Taille"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
              <tbody>
                {dbm.data.topTables.map((t) => (
                  <tr key={t.table} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={td}>{t.table}</td><td style={td}>{bytes(t.sizeBytes)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </section>
    </div>
  );
}

// --- §27 : bascules de fonctionnalité ---------------------------------------

export function FeatureFlagsSection({ canManage }: { canManage: boolean }) {
  const flags = useAsync(listFeatureFlags);
  return (
    <section className="nx-card" style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <div style={label}>Bascules de fonctionnalité (§27)</div>
        {canManage && (
          <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={flags.busy}
                  onClick={() => {
                    const key = prompt("Clé de la bascule :"); if (!key) return;
                    void flags.run(() => saveFeatureFlag({ key, scope: "global", enabled: true }));
                  }}>
            Nouvelle bascule globale
          </button>
        )}
      </div>
      {flags.error && <div className="nx-error-text">{flags.error}</div>}
      <div style={{ overflowX: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Clé", "Portée", "État", "Expire", canManage ? "" : undefined].filter(Boolean).map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {(flags.data ?? []).map((f: FeatureFlag) => (
              <tr key={f.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td}>{f.key}</td>
                <td style={{ ...td, color: "var(--nx-text-2)" }}>{f.scope}</td>
                <td style={{ ...td, color: f.enabled ? "var(--nx-success)" : "var(--nx-error)" }}>{f.enabled ? "activée" : "désactivée (kill switch)"}</td>
                <td style={td}>{f.expiresAt ? rel(f.expiresAt) : "—"}</td>
                {canManage && (
                  <td style={td}>
                    <div style={{ display: "flex", gap: 4 }}>
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={flags.busy}
                              onClick={() => void flags.run(() => saveFeatureFlag({ key: f.key, scope: f.scope, companyId: f.companyId, userId: f.userId, enabled: !f.enabled }))}>
                        {f.enabled ? "Désactiver" : "Activer"}
                      </button>
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={flags.busy}
                              onClick={() => void flags.run(() => deleteFeatureFlag(f.id))}>Supprimer</button>
                    </div>
                  </td>
                )}
              </tr>
            ))}
            {(flags.data ?? []).length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={5}>Aucune bascule.</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}

// --- §23-24 : maintenance planifiée -----------------------------------------

const NEXT_STATUS: Record<string, string[]> = {
  planned: ["notified", "canceled"], notified: ["backed_up", "canceled"], backed_up: ["in_progress", "canceled"],
  in_progress: ["verifying"], verifying: ["closed", "in_progress"], closed: [], canceled: [],
};

export function MaintenanceWindowsSection({ canManage }: { canManage: boolean }) {
  const mw = useAsync(listMaintenanceWindows);
  return (
    <section className="nx-card" style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <div style={label}>Maintenance planifiée (§23-24)</div>
        {canManage && (
          <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={mw.busy}
                  onClick={() => {
                    const impact = prompt("Impact de la maintenance :"); if (!impact) return;
                    void mw.run(() => scheduleMaintenance({
                      scheduledAt: new Date(Date.now() + 24 * 3600_000).toISOString(),
                      durationMinutes: 60, impact, services: ["api", "database"],
                    }));
                  }}>
            Planifier (+24h, 60 min)
          </button>
        )}
      </div>
      {mw.error && <div className="nx-error-text">{mw.error}</div>}
      <div style={{ overflowX: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Prévue", "Durée", "Impact", "Services", "Statut", canManage ? "" : undefined].filter(Boolean).map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {(mw.data ?? []).map((w: MaintenanceWindow) => (
              <tr key={w.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td}>{new Date(w.scheduledAt).toLocaleString("fr-FR")}</td>
                <td style={td}>{w.durationMinutes} min</td>
                <td style={{ ...td, whiteSpace: "normal" }}>{w.impact}</td>
                <td style={{ ...td, color: "var(--nx-text-2)" }}>{w.services.join(", ")}</td>
                <td style={{ ...td, color: w.status === "in_progress" ? "var(--nx-warning)" : tone(w.status) }}>{w.status}</td>
                {canManage && (
                  <td style={td}>
                    <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
                      {(NEXT_STATUS[w.status] ?? []).map((next) => (
                        <button key={next} className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={mw.busy}
                                onClick={() => void mw.run(() => transitionMaintenance(w.id, next))}>
                          → {next}
                        </button>
                      ))}
                    </div>
                  </td>
                )}
              </tr>
            ))}
            {(mw.data ?? []).length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={6}>Aucune maintenance planifiée.</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}

// --- §16-19 : rétention & purge technique -----------------------------------

export function RetentionSection({ canManage }: { canManage: boolean }) {
  const policies = useAsync(listRetentionPolicies);
  const history = useAsync(purgeHistory);
  const [preview, setPreview] = useState<PurgeRun | null>(null);
  const [busyType, setBusyType] = useState<string | null>(null);

  async function doPreview(dataType: string) {
    setBusyType(dataType);
    try { setPreview(await previewPurge(dataType)); } catch { setPreview(null); }
    finally { setBusyType(null); }
  }
  async function doExecute(dataType: string) {
    setBusyType(dataType);
    try { await executePurge(dataType); setPreview(null); history.load(); } finally { setBusyType(null); }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={label}>Politiques de rétention (§17, §22)</div>
        {policies.error && <div className="nx-error-text">{policies.error}</div>}
        <div style={{ overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Type de donnée", "Entreprise", "Durée", "Action", canManage ? "" : undefined].filter(Boolean).map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {(policies.data ?? []).map((p: RetentionPolicy) => (
                <tr key={p.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{p.dataType}</td>
                  <td style={{ ...td, color: "var(--nx-text-2)" }}>{p.companyId ? "spécifique" : "défaut système"}</td>
                  <td style={td}>{p.durationDays} j</td>
                  <td style={td}>{p.action === "delete" ? "suppression" : "archivage"}</td>
                  {canManage && (
                    <td style={td}>
                      <div style={{ display: "flex", gap: 4 }}>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busyType === p.dataType}
                                onClick={() => void doPreview(p.dataType)}>Aperçu</button>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-warning)" }} disabled={busyType === p.dataType}
                                onClick={() => { if (confirm(`Purger « ${p.dataType} » maintenant ?`)) void doExecute(p.dataType); }}>Purger</button>
                      </div>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {preview && (
          <div className="nx-card" style={{ background: "var(--nx-surface-2)", fontSize: 13 }}>
            Aperçu « {preview.dataType} » : <strong>{preview.objectCount}</strong> objet(s) purgeable(s),{" "}
            <strong>{preview.protectedExcludedCount}</strong> protégé(s) et exclu(s) — aucune écriture effectuée.
          </div>
        )}
      </section>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={label}>Journal des purges (§18)</div>
        {history.error && <div className="nx-error-text">{history.error}</div>}
        <div style={{ overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
            <thead><tr>{["Type", "Aperçu / exécuté", "Objets", "Protégés", "Statut"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {(history.data ?? []).slice(0, 20).map((h: PurgeRun) => (
                <tr key={h.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{h.dataType}</td>
                  <td style={td}>{h.dryRun ? "aperçu" : "exécuté"}</td>
                  <td style={td}>{h.objectCount}</td>
                  <td style={td}>{h.protectedExcludedCount}</td>
                  <td style={{ ...td, color: tone(h.status) }}>{h.status}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </div>
  );
}

// --- §44 : correction de donnée contrôlée -----------------------------------

const CORRECTABLE: Record<string, string[]> = {
  customer: ["name", "phone", "email", "address", "tax_identifier"],
  supplier: ["name", "phone", "email", "address", "tax_identifier"],
  product: ["name", "short_name", "description", "barcode"],
  app_user: ["full_name", "username"],
};

export function DataCorrectionsSection({ canManage, canDecide }: { canManage: boolean; canDecide: boolean }) {
  const corr = useAsync(() => listDataCorrections());
  return (
    <section className="nx-card" style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <div style={label}>Correction de donnée contrôlée (§44)</div>
        {canManage && (
          <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={corr.busy}
                  onClick={() => {
                    const tableName = prompt(`Table (${Object.keys(CORRECTABLE).join(", ")}) :`); if (!tableName || !CORRECTABLE[tableName]) return;
                    const recordId = prompt("Identifiant de l'enregistrement :"); if (!recordId) return;
                    const fieldName = prompt(`Champ (${CORRECTABLE[tableName].join(", ")}) :`); if (!fieldName) return;
                    const newValue = prompt("Nouvelle valeur :"); if (newValue == null) return;
                    const reason = prompt("Motif (obligatoire) :"); if (!reason) return;
                    void corr.run(() => requestDataCorrection({ tableName, recordId, fieldName, newValue, reason }));
                  }}>
            Demander une correction
          </button>
        )}
      </div>
      {corr.error && <div className="nx-error-text">{corr.error}</div>}
      <div style={{ overflowX: "auto" }}>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Table / champ", "Avant → après", "Motif", "Statut", canDecide ? "" : undefined].filter(Boolean).map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {(corr.data ?? []).map((c: DataCorrection) => (
              <tr key={c.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td}>{c.tableName}.{c.fieldName}</td>
                <td style={{ ...td, whiteSpace: "normal" }}>{c.oldValue ?? "∅"} → {c.newValue}</td>
                <td style={{ ...td, color: "var(--nx-text-2)", whiteSpace: "normal" }}>{c.reason}</td>
                <td style={{ ...td, color: tone(c.status) }}>{c.status}</td>
                {canDecide && (
                  <td style={td}>
                    {c.status === "requested" && (
                      <div style={{ display: "flex", gap: 4 }}>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-success)" }} disabled={corr.busy}
                                onClick={() => void corr.run(() => decideDataCorrection(c.id, true))}>Approuver</button>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={corr.busy}
                                onClick={() => { const n = prompt("Motif du refus :") ?? undefined; void corr.run(() => decideDataCorrection(c.id, false, n)); }}>Refuser</button>
                      </div>
                    )}
                  </td>
                )}
              </tr>
            ))}
            {(corr.data ?? []).length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={5}>Aucune demande de correction.</td></tr>}
          </tbody>
        </table>
      </div>
    </section>
  );
}
