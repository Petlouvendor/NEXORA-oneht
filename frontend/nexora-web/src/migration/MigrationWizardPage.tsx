import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  cancelImport,
  completeOnboardingTask,
  createImport,
  executeImport,
  onboardingChecklist,
  profileImport,
  reconcileImport,
  setMapping,
  simulateImport,
  suggestMapping,
  targetFields,
  validateImport,
  type ColumnMapping,
  type ImportJob,
  type ImportKind,
  type OnboardingChecklist,
  type TargetField,
} from "@/migration/migrationApi";

const KINDS: { value: ImportKind; label: string }[] = [
  { value: "customers", label: "Clients" },
  { value: "suppliers", label: "Fournisseurs" },
  { value: "products", label: "Produits" },
  { value: "opening_stock", label: "Stock d'ouverture" },
];
const TRANSFORMS = ["trim", "lower", "upper", "email", "phone", "date", "decimal", "integer", "boolean", "none"];
const STEPS = ["Source", "Profilage", "Mapping", "Validation", "Simulation", "Import", "Rapprochement"];

const stepOf = (s: string) =>
  ({ created: 1, profiled: 2, mapped: 3, validated: 4, simulated: 5, executing: 6, paused: 6, completed: 7, failed: 6, canceled: 1 } as Record<string, number>)[s] ?? 1;

/** Assistant de migration & checklist d'onboarding (Phase 39 §5, §32). */
export function MigrationWizardPage() {
  const { hasPermission } = useAuth();
  const canManage = hasPermission("migration.manage");

  const [kind, setKind] = useState<ImportKind>("customers");
  const [mode, setMode] = useState("upsert");
  const [content, setContent] = useState("");
  const [job, setJob] = useState<ImportJob | null>(null);
  const [fields, setFields] = useState<TargetField[]>([]);
  const [mapping, setMappingState] = useState<ColumnMapping[]>([]);
  const [checklist, setChecklist] = useState<OnboardingChecklist | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const loadChecklist = useCallback(() => {
    onboardingChecklist().then(setChecklist).catch(() => {});
  }, []);
  useEffect(loadChecklist, [loadChecklist]);

  async function run<T>(fn: () => Promise<T>, msg?: string): Promise<T | undefined> {
    setBusy(true); setError(null); setNotice(null);
    try { const r = await fn(); if (msg) setNotice(msg); return r; }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); return undefined; }
    finally { setBusy(false); }
  }

  async function start() {
    const created = await run(() => createImport({ kind, sourceType: "csv", content, mode, fileName: "import.csv" }), "Import créé");
    if (!created) return;
    const profiled = await run(() => profileImport(created.id));
    if (!profiled) { setJob(created); return; }
    setJob(profiled);
    const tf = await targetFields(kind);
    setFields(tf);
    const cols = profiled.profile?.columns ?? [];
    const suggested = await suggestMapping(kind, cols);
    setMappingState(tf.map((f) => suggested.find((x) => x.targetField === f.field) ?? { targetField: f.field, sourceColumn: null, transform: "trim" }));
  }

  async function applyMapping() {
    const j = await run(() => setMapping(job!.id, mapping.filter((m) => m.sourceColumn || m.defaultValue)), "Mapping enregistré");
    if (j) setJob(j);
  }
  async function step(fn: (id: string) => Promise<ImportJob>, msg: string) {
    const j = await run(() => fn(job!.id), msg);
    if (j) { setJob(j); loadChecklist(); }
  }
  async function reconcile() {
    const r = await run(() => reconcileImport(job!.id), "Rapprochement effectué");
    if (r) setJob((j) => (j ? { ...j, reconciliation: r } : j));
  }

  if (!hasPermission("migration.view")) {
    return <div className="nx-card">La migration n'est pas accessible avec vos permissions.</div>;
  }

  const th: React.CSSProperties = { padding: "4px 8px", textAlign: "left", color: "var(--nx-text-2)" };
  const td: React.CSSProperties = { padding: "6px 8px" };
  const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };
  const cur = job ? stepOf(job.status) : 0;
  const cols = job?.profile?.columns ?? [];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Migration & onboarding</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {notice && <div style={{ color: "var(--nx-success)" }}>{notice}</div>}

      {checklist && (
        <section className="nx-card" style={{ display: "grid", gap: 10 }}>
          <div style={{ display: "flex", alignItems: "baseline", gap: 12, flexWrap: "wrap" }}>
            <span style={label}>Checklist d'onboarding (§32)</span>
            <span style={{ fontWeight: 800 }}>{checklist.progressPct}%</span>
            <span style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{checklist.done}/{checklist.total} étapes</span>
            <span style={{ fontSize: 12 }}>
              Score migration — données {checklist.score.data} · mapping {checklist.score.mapping} · qualité {checklist.score.quality} · compta {checklist.score.accounting} ·{" "}
              <b style={{ color: checklist.score.ready ? "var(--nx-success)" : "var(--nx-warning)" }}>PRÊT : {checklist.score.ready ? "OUI" : "NON"}</b>
            </span>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))", gap: 6 }}>
            {checklist.items.map((i) => (
              <div key={i.key} style={{ display: "flex", justifyContent: "space-between", alignItems: "center", fontSize: 13, gap: 8 }}>
                <span style={{ color: i.status === "done" ? "var(--nx-success)" : i.status === "skipped" ? "var(--nx-text-2)" : undefined }}>
                  {i.status === "done" ? "✓" : i.status === "skipped" ? "–" : "○"} {i.label}
                </span>
                {canManage && i.source === "manual" && i.status !== "done" && (
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "0 6px", fontSize: 11 }} disabled={busy}
                          onClick={() => run(() => completeOnboardingTask(i.key).then(setChecklist), "Étape validée")}>ok</button>
                )}
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="nx-card" style={{ display: "grid", gap: 10 }}>
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {STEPS.map((s, idx) => (
            <span key={s} style={{
              fontSize: 12, padding: "2px 8px", borderRadius: 999,
              background: idx + 1 <= cur ? "var(--nx-accent)" : "var(--nx-surface-2)",
              color: idx + 1 <= cur ? "#04231f" : "var(--nx-text-2)",
            }}>{idx + 1}. {s}</span>
          ))}
        </div>

        {!job || job.status === "canceled" ? (
          <div style={{ display: "grid", gap: 8 }}>
            <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
              <label style={{ fontSize: 13 }}>Type&nbsp;
                <select className="nx-input" value={kind} onChange={(e) => setKind(e.target.value as ImportKind)}>
                  {KINDS.map((k) => <option key={k.value} value={k.value}>{k.label}</option>)}
                </select>
              </label>
              <label style={{ fontSize: 13 }}>Mode&nbsp;
                <select className="nx-input" value={mode} onChange={(e) => setMode(e.target.value)}>
                  {["create_only", "update_only", "upsert", "skip_duplicates", "reject_duplicates"].map((m) => <option key={m} value={m}>{m}</option>)}
                </select>
              </label>
            </div>
            <textarea className="nx-input" rows={8} placeholder="Collez ici un CSV (UTF-8) ou un JSON — §44"
                      value={content} onChange={(e) => setContent(e.target.value)} style={{ fontFamily: "monospace", fontSize: 12 }} />
            <button className="nx-btn" disabled={busy || !canManage || content.trim().length < 3} onClick={start}>Démarrer l'assistant</button>
          </div>
        ) : (
          <div style={{ display: "grid", gap: 12 }}>
            <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>
              Import <b>{job.kind}</b> · {job.totalRows} ligne(s) · statut <b>{job.status}</b>
              {job.errorMessage && <span style={{ color: "var(--nx-error)" }}> · {job.errorMessage}</span>}
            </div>

            {job.status === "profiled" && (
              <div style={{ overflowX: "auto" }}>
                <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
                  <thead><tr>{["Champ NEXORA", "Colonne source", "Transformation"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
                  <tbody>
                    {fields.map((f, i) => (
                      <tr key={f.field} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                        <td style={td}>{f.field}{f.required && <span style={{ color: "var(--nx-error)" }}> *</span>}
                          <div style={{ fontSize: 11, color: "var(--nx-text-2)" }}>{f.description}</div></td>
                        <td style={td}>
                          <select className="nx-input" value={mapping[i]?.sourceColumn ?? ""}
                                  onChange={(e) => setMappingState((m) => m.map((x, j) => j === i ? { ...x, sourceColumn: e.target.value || null } : x))}>
                            <option value="">—</option>
                            {cols.map((c) => <option key={c} value={c}>{c}</option>)}
                          </select>
                        </td>
                        <td style={td}>
                          <select className="nx-input" value={mapping[i]?.transform ?? "trim"}
                                  onChange={(e) => setMappingState((m) => m.map((x, j) => j === i ? { ...x, transform: e.target.value } : x))}>
                            {TRANSFORMS.map((t) => <option key={t} value={t}>{t}</option>)}
                          </select>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
                <button className="nx-btn" style={{ marginTop: 8 }} disabled={busy} onClick={applyMapping}>Enregistrer le mapping</button>
              </div>
            )}

            {(job.status === "mapped" || job.status === "validated") && (
              <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                {job.status === "validated" && (
                  <span style={{ fontSize: 13 }}>
                    ✓ {job.okRows} · ▲ {job.warningRows} · ✕ {job.errorRows} · doublons {job.duplicateRows}
                  </span>
                )}
                <button className="nx-btn nx-btn--ghost" disabled={busy} onClick={() => step(validateImport, "Validation terminée")}>Valider (§25)</button>
                {job.status === "validated" && (
                  <button className="nx-btn nx-btn--ghost" disabled={busy} onClick={() => step(simulateImport, "Simulation terminée — aucune écriture (§23)")}>Simuler (§23)</button>
                )}
              </div>
            )}

            {job.simulation && (
              <div className="nx-card" style={{ background: "var(--nx-surface-2)" }}>
                <div style={label}>Prévisualisation (§24)</div>
                <div style={{ fontSize: 14 }}>
                  {job.simulation.wouldCreate} création(s) · {job.simulation.wouldUpdate} mise(s) à jour · {job.simulation.wouldReject} rejet(s) ·{" "}
                  {job.simulation.errors} erreur(s) · {job.simulation.duplicates} doublon(s)
                </div>
                {job.status === "simulated" && canManage && (
                  <button className="nx-btn" style={{ marginTop: 8 }} disabled={busy} onClick={() => step(executeImport, "Import exécuté")}>Lancer l'import final</button>
                )}
              </div>
            )}

            {job.status === "completed" && (
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <span style={{ fontSize: 14, color: "var(--nx-success)" }}>
                  ✓ {job.createdCount} créé(s) · {job.updatedCount} mis à jour · {job.skippedCount} ignoré(s) · {job.rejectedCount} rejeté(s)
                </span>
                <button className="nx-btn nx-btn--ghost" disabled={busy} onClick={reconcile}>Rapprocher (§35)</button>
                {canManage && <button className="nx-btn nx-btn--ghost" style={{ color: "var(--nx-warning)" }} disabled={busy}
                        onClick={() => step(cancelImport, "Import annulé — créations défaites")}>Annuler (rollback §37)</button>}
              </div>
            )}

            {job.reconciliation && (
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
                <tbody>
                  {job.reconciliation.lines.map((l) => (
                    <tr key={l.metric} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                      <td style={td}>{l.metric}</td>
                      <td style={td} className="nx-numeric">{l.sourceValue}</td>
                      <td style={td} className="nx-numeric">{l.nexoraValue}</td>
                      <td style={{ ...td, color: l.ok ? "var(--nx-success)" : "var(--nx-error)" }}>{l.ok ? "OK" : `écart ${l.delta}`}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}

            <button className="nx-btn nx-btn--ghost" style={{ justifySelf: "start", fontSize: 12 }}
                    onClick={() => { setJob(null); setContent(""); setMappingState([]); }}>Nouvel import</button>
          </div>
        )}
      </section>
    </div>
  );
}
