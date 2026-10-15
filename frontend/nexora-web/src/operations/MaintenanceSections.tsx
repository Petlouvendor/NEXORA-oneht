import { useCallback, useEffect, useState, type FormEvent } from "react";
import { ApiError } from "@/core/apiClient";
import { usePrompt } from "@/shared/PromptDialog";
import { TopLayer } from "@/shared/TopLayer";
import { Button, ConfirmDialog, Field, Modal, Select, TextInput } from "@/shared/ui";
import {
  databaseMaintenance, deadLetters, decideDataCorrection, deleteDeadLetter,
  diagnostics, executePurge, invalidateCache, listDataCorrections, listJobs,
  listMaintenanceWindows, listRetentionPolicies, pauseJob, previewPurge, purgeHistory, queueStats,
  replayDeadLetter, requestDataCorrection, resumeJob, runJobNow, scheduleMaintenance,
  storageStats, transitionMaintenance,
  type DataCorrection, type DeadLetterItem, type DiagnosticCheck,
  type JobStatus, type MaintenanceWindow, type PurgeRun, type QueueStats,
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


// --- §23-24 : maintenance planifiée -----------------------------------------

const NEXT_STATUS: Record<string, string[]> = {
  planned: ["notified", "canceled"], notified: ["backed_up", "canceled"], backed_up: ["in_progress", "canceled"],
  in_progress: ["verifying"], verifying: ["closed", "in_progress"], closed: [], canceled: [],
};

export function MaintenanceWindowsSection({ canManage }: { canManage: boolean }) {
  const mw = useAsync(listMaintenanceWindows);
  const [planning, setPlanning] = useState(false);
  return (
    <section className="nx-card" style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <div style={label}>Maintenance planifiée (§23-24)</div>
        {canManage && (
          <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={mw.busy}
                  onClick={() => setPlanning(true)}>
            Planifier une maintenance
          </button>
        )}
      </div>
      {mw.error && <div className="nx-error-text">{mw.error}</div>}
      {planning && <ScheduleMaintenanceDialog onClose={() => setPlanning(false)} onSaved={() => { setPlanning(false); mw.load(); }} />}
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
  const [confirmPurge, setConfirmPurge] = useState<PurgeRun | null>(null);
  const [purgeError, setPurgeError] = useState<string | null>(null);

  async function doPreview(dataType: string) {
    setBusyType(dataType);
    try { setPreview(await previewPurge(dataType)); } catch { setPreview(null); }
    finally { setBusyType(null); }
  }
  /** Aperçu d'abord (aucune écriture), puis confirmation avec le nombre réel d'objets concernés. */
  async function askPurge(dataType: string) {
    setBusyType(dataType); setPurgeError(null);
    try { setConfirmPurge(await previewPurge(dataType)); }
    catch (e) { setPurgeError(e instanceof ApiError ? e.message : "Aperçu impossible"); }
    finally { setBusyType(null); }
  }
  async function doExecute(dataType: string) {
    setConfirmPurge(null); setBusyType(dataType); setPurgeError(null);
    try { await executePurge(dataType); setPreview(null); history.load(); }
    catch (e) { setPurgeError(e instanceof ApiError ? e.message : "Purge impossible"); }
    finally { setBusyType(null); }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <section className="nx-card" style={{ display: "grid", gap: 8 }}>
        <div style={label}>Politiques de rétention (§17, §22)</div>
        {policies.error && <div className="nx-error-text">{policies.error}</div>}
        {purgeError && <div role="alert" className="nx-error-text">{purgeError}</div>}
        {confirmPurge && (
          <TopLayer>
            <ConfirmDialog danger title={`Purger « ${confirmPurge.dataType} »`} confirmLabel="Purger définitivement"
              message={`${confirmPurge.objectCount} objet(s) concerné(s), traités selon la politique (suppression ou archivage) ; ${confirmPurge.protectedExcludedCount} objet(s) protégé(s) restent exclus.`}
              impact="Action irréversible et journalisée ; une confirmation d'identité peut être demandée."
              onConfirm={() => void doExecute(confirmPurge.dataType)} onCancel={() => setConfirmPurge(null)} />
          </TopLayer>
        )}
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
                                onClick={() => void askPurge(p.dataType)}>Purger</button>
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
  const [requesting, setRequesting] = useState(false);
  const prompt = usePrompt();
  return (
    <section className="nx-card" style={{ display: "grid", gap: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <div style={label}>Correction de donnée contrôlée (§44)</div>
        {canManage && (
          <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={corr.busy}
                  onClick={() => setRequesting(true)}>
            Demander une correction
          </button>
        )}
      </div>
      {corr.error && <div className="nx-error-text">{corr.error}</div>}
      {prompt.dialog}
      {requesting && <DataCorrectionDialog onClose={() => setRequesting(false)} onSaved={() => { setRequesting(false); corr.load(); }} />}
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
                                onClick={async () => {
                                  const n = await prompt.ask({ title: "Refuser la correction", label: "Motif du refus", required: true,
                                    multiline: true, danger: true, confirmLabel: "Refuser",
                                    onSubmit: (reason) => decideDataCorrection(c.id, false, reason) });
                                  if (n) corr.load();
                                }}>Refuser</button>
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

// --- Finalisation phase 18 : fenêtres de saisie (remplacent les boîtes natives) ------------------------------

const SERVICES: Array<{ value: string; label: string }> = [
  { value: "api", label: "API" }, { value: "database", label: "Base de données" }, { value: "pos", label: "Caisse" },
  { value: "sync", label: "Synchronisation hors ligne" }, { value: "notifications", label: "Notifications" },
  { value: "integrations", label: "Intégrations et paiements" },
];

/** Valeur d'un champ date-heure local (AAAA-MM-JJTHH:MM) pour un instant donné. */
function localInput(d: Date): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}T${p(d.getHours())}:${p(d.getMinutes())}`;
}

function ScheduleMaintenanceDialog({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [at, setAt] = useState(() => localInput(new Date(Date.now() + 24 * 3600_000)));
  const [duration, setDuration] = useState("60");
  const [impact, setImpact] = useState("");
  const [services, setServices] = useState<string[]>(["api", "database"]);
  const [responsible, setResponsible] = useState("");
  const [rollbackPlan, setRollbackPlan] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const next: Record<string, string> = {};
    const when = new Date(at);
    const minutes = Number(duration);
    if (!at || Number.isNaN(when.getTime())) next.at = "Date et heure requises.";
    else if (when.getTime() < Date.now()) next.at = "La maintenance doit être planifiée dans le futur.";
    if (!Number.isInteger(minutes) || minutes < 5 || minutes > 1440) next.duration = "Entre 5 et 1 440 minutes.";
    if (impact.trim().length < 5) next.impact = "Décrivez l'impact pour les utilisateurs (5 caractères au moins).";
    if (services.length === 0) next.services = "Au moins un service concerné.";
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    setBusy(true); setError(null);
    try {
      await scheduleMaintenance({ scheduledAt: when.toISOString(), durationMinutes: minutes, impact: impact.trim(), services,
        responsible: responsible.trim() || null, rollbackPlan: rollbackPlan.trim() || null });
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Planification impossible");
    } finally {
      setBusy(false);
    }
  }

  return (
    <TopLayer>
      <Modal title="Planifier une maintenance" onClose={onClose}
        footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => void save()}>Planifier</Button></>}>
        <form noValidate onSubmit={save} style={{ display: "grid", gap: 10 }}>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(min(200px, 100%), 1fr))", gap: 10 }}>
            <Field label="Début" required error={errors.at}>
              <TextInput type="datetime-local" value={at} onChange={(e) => setAt(e.target.value)} />
            </Field>
            <Field label="Durée (minutes)" required error={errors.duration}>
              <TextInput inputMode="numeric" value={duration} onChange={(e) => setDuration(e.target.value)} />
            </Field>
          </div>
          <Field label="Impact pour les utilisateurs" required error={errors.impact} hint="Affiché avant et pendant la maintenance.">
            <textarea className="nx-textarea" rows={2} value={impact} onChange={(e) => setImpact(e.target.value)} />
          </Field>
          <Field label="Services concernés" required error={errors.services}>
            <div style={{ display: "flex", flexWrap: "wrap", gap: "4px 14px" }}>
              {SERVICES.map((s) => (
                <label key={s.value} style={{ display: "flex", gap: 6, alignItems: "center" }}>
                  <input type="checkbox" checked={services.includes(s.value)}
                    onChange={(e) => setServices((cur) => e.target.checked ? [...cur, s.value] : cur.filter((v) => v !== s.value))} />
                  {s.label}
                </label>
              ))}
            </div>
          </Field>
          <Field label="Responsable">
            <TextInput value={responsible} onChange={(e) => setResponsible(e.target.value)} />
          </Field>
          <Field label="Plan de retour arrière" hint="Que faire si la maintenance échoue.">
            <textarea className="nx-textarea" rows={2} value={rollbackPlan} onChange={(e) => setRollbackPlan(e.target.value)} />
          </Field>
          <button type="submit" hidden />
        </form>
      </Modal>
    </TopLayer>
  );
}

const TABLE_LABEL: Record<string, string> = { customer: "Client", supplier: "Fournisseur", product: "Produit", app_user: "Utilisateur" };
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function DataCorrectionDialog({ onClose, onSaved }: { onClose: () => void; onSaved: () => void }) {
  const [tableName, setTableName] = useState("customer");
  const [recordId, setRecordId] = useState("");
  const [fieldName, setFieldName] = useState(CORRECTABLE.customer[0]);
  const [newValue, setNewValue] = useState("");
  const [reason, setReason] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const next: Record<string, string> = {};
    if (!UUID.test(recordId.trim())) next.recordId = "Identifiant (UUID) de l'enregistrement, tel qu'affiché dans sa fiche.";
    if (!newValue.trim()) next.newValue = "Nouvelle valeur requise.";
    if (reason.trim().length < 5) next.reason = "Motif obligatoire (5 caractères au moins).";
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    setBusy(true); setError(null);
    try {
      await requestDataCorrection({ tableName, recordId: recordId.trim(), fieldName, newValue: newValue.trim(), reason: reason.trim() });
      onSaved();
    } catch (err) {
      setError(err instanceof ApiError ? err.message : "Demande impossible");
    } finally {
      setBusy(false);
    }
  }

  return (
    <TopLayer>
      <Modal title="Demander une correction de donnée" onClose={onClose}
        footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => void save()}>Demander</Button></>}>
        <form noValidate onSubmit={save} style={{ display: "grid", gap: 10 }}>
          <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
            La correction n'est appliquée qu'après approbation par une autre personne (quatre yeux), avec l'ancienne valeur conservée.
          </div>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(min(200px, 100%), 1fr))", gap: 10 }}>
            <Field label="Type d'enregistrement" required>
              <Select value={tableName} onChange={(e) => { setTableName(e.target.value); setFieldName(CORRECTABLE[e.target.value][0]); }}>
                {Object.keys(CORRECTABLE).map((t) => <option key={t} value={t}>{TABLE_LABEL[t] ?? t}</option>)}
              </Select>
            </Field>
            <Field label="Champ" required>
              <Select value={fieldName} onChange={(e) => setFieldName(e.target.value)}>
                {CORRECTABLE[tableName].map((f) => <option key={f} value={f}>{f}</option>)}
              </Select>
            </Field>
          </div>
          <Field label="Identifiant de l'enregistrement" required error={errors.recordId}>
            <TextInput value={recordId} onChange={(e) => setRecordId(e.target.value)} placeholder="00000000-0000-0000-0000-000000000000" />
          </Field>
          <Field label="Nouvelle valeur" required error={errors.newValue}>
            <TextInput value={newValue} onChange={(e) => setNewValue(e.target.value)} />
          </Field>
          <Field label="Motif" required error={errors.reason}>
            <textarea className="nx-textarea" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} />
          </Field>
          <button type="submit" hidden />
        </form>
      </Modal>
    </TopLayer>
  );
}
