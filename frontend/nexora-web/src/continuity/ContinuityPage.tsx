import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  advanceIncident,
  approveRestore,
  drStatus,
  listBackups,
  listDrTests,
  listIncidents,
  listRestores,
  listRunbooks,
  lockDownIncident,
  openIncident,
  recordDrTestResult,
  registerBackup,
  requestRestore,
  resolveIncident,
  restoreTest,
  scheduleDrTest,
  statusPage,
  verifyBackup,
  type Backup,
  type ContinuityIncident,
  type DisasterRecoveryStatus,
  type DrTest,
  type RestoreRun,
  type Runbook,
  type StatusPage as StatusPageDto,
} from "@/continuity/continuityApi";

const rel = (iso: string | null) => {
  if (!iso) return "—";
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? "à l'instant" : m < 60 ? `il y a ${m} min` : m < 1440 ? `il y a ${Math.round(m / 60)} h` : `il y a ${Math.round(m / 1440)} j`;
};
const GRADE_TONE: Record<string, string> = { A: "var(--nx-success)", B: "var(--nx-success)", C: "var(--nx-warning)", D: "var(--nx-error)", E: "var(--nx-error)" };
const RUNBOOK = ["", "Détecter", "Protéger", "Isoler", "Évaluer", "Restaurer / Basculer", "Vérifier", "Rouvrir", "Analyser", "Améliorer"];
const OVERALL_TONE: Record<string, string> = { operational: "var(--nx-success)", degraded: "var(--nx-warning)", incident: "var(--nx-error)", maintenance: "var(--nx-warning)" };
const OVERALL_LABEL: Record<string, string> = { operational: "Opérationnel", degraded: "Dégradé", incident: "Incident en cours", maintenance: "Maintenance" };
const DR_TEST_TYPES = ["tabletop", "restore", "failover_db", "failover_app", "network_outage", "server_outage", "storage_outage", "ransomware_simulation", "site_unavailable", "return_to_normal"];

/** Continuité, sauvegarde & reprise (Phase 36). */
export function ContinuityPage() {
  const { activeCompanyId, hasPermission } = useAuth();
  const [dr, setDr] = useState<DisasterRecoveryStatus | null>(null);
  const [backups, setBackups] = useState<Backup[]>([]);
  const [restores, setRestores] = useState<RestoreRun[]>([]);
  const [incidents, setIncidents] = useState<ContinuityIncident[]>([]);
  const [status, setStatus] = useState<StatusPageDto | null>(null);
  const [drTests, setDrTests] = useState<DrTest[]>([]);
  const [runbooks, setRunbooks] = useState<Runbook[]>([]);
  const [openRunbook, setOpenRunbook] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canManage = hasPermission("continuity.manage");
  const canRestore = hasPermission("continuity.restore");

  const load = useCallback(() => {
    setError(null);
    Promise.all([drStatus(), listBackups(), listRestores(), listIncidents(), statusPage(), listDrTests(), listRunbooks()])
      .then(([d, b, r, i, sp, dt, rb]) => { setDr(d); setBackups(b); setRestores(r); setIncidents(i); setStatus(sp); setDrTests(dt); setRunbooks(rb); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);
  useEffect(load, [load, activeCompanyId]);

  async function run(fn: () => Promise<unknown>, msg?: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await fn(); if (msg) setNotice(msg); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("continuity.view")) {
    return <div className="nx-card">La continuité n'est pas accessible avec vos permissions.</div>;
  }

  const td: React.CSSProperties = { padding: "6px 8px", whiteSpace: "nowrap" };
  const th: React.CSSProperties = { padding: "4px 8px", textAlign: "left", color: "var(--nx-text-2)" };
  const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Continuité & reprise</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {notice && <div style={{ color: "var(--nx-success)" }}>{notice}</div>}

      {status && (
        <section className="nx-card" style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
            <span style={label}>Page de statut (§7, §39)</span>
            <span style={{ fontWeight: 800, color: OVERALL_TONE[status.overallStatus] ?? "var(--nx-text-2)" }}>
              {OVERALL_LABEL[status.overallStatus] ?? status.overallStatus}
            </span>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(160px, 1fr))", gap: 10 }}>
            {status.components.map((c) => (
              <div key={c.key} style={{ display: "grid", gap: 2 }}>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{c.label} · {c.criticality}</div>
                <div style={{ fontWeight: 700, color: c.status === "ok" ? "var(--nx-success)" : c.status === "fail" ? "var(--nx-error)" : c.status === "warn" ? "var(--nx-warning)" : "var(--nx-text-2)" }}>
                  {c.status}{c.detail ? ` — ${c.detail}` : ""}
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

      {dr && (
        <section className="nx-card" style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
            <span style={label}>État de reprise après sinistre</span>
            <span style={{ fontWeight: 800, fontSize: 22, color: GRADE_TONE[dr.grade] ?? "var(--nx-text-2)" }}>{dr.grade}</span>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 10 }}>
            {[
              ["Dernière sauvegarde saine", dr.lastGoodBackupAt ? `${rel(dr.lastGoodBackupAt)}` : "aucune", dr.backupOverdue],
              ["Point de reprise (RPO)", `cible ${dr.rpoTargetMinutes} min`, dr.rpoBreached],
              ["Durée de reprise (RTO)", `cible ${dr.rtoTargetMinutes} min`, false],
              ["Dernière sauvegarde vérifiée", rel(dr.lastVerifiedBackupAt), dr.lastVerifiedBackupAt === null],
              ["À vérifier", dr.backupsAwaitingVerification, dr.backupsAwaitingVerification > 0],
              ["Incidents ouverts", `${dr.unresolvedIncidents}${dr.openP1Incidents ? ` · ${dr.openP1Incidents} P1` : ""}`, dr.unresolvedIncidents > 0],
            ].map(([l, v, warn]) => (
              <div key={l as string} style={{ display: "grid", gap: 2 }}>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{l}</div>
                <div style={{ fontWeight: 700, color: warn ? "var(--nx-warning)" : undefined }}>{v as string}</div>
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="nx-card">
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <div style={label}>Catalogue des sauvegardes (§6, §21)</div>
          {canManage && (
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                    onClick={() => run(() => registerBackup({
                      kind: "full",
                      coversUpTo: new Date().toISOString(),
                      objectCount: null,
                      locationRef: "manuelle",
                      checksum: null,
                    }), "Sauvegarde enregistrée (à compléter par l'agent)")}>
              Enregistrer une sauvegarde
            </button>
          )}
        </div>
        <div style={{ overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Type", "Point de couverture", "Taille", "Empreinte", "Vérifiée", "Expire", "Statut", ""].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {backups.map((b) => (
                <tr key={b.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{b.kind}</td>
                  <td style={td}>{new Date(b.coversUpTo).toLocaleString()}</td>
                  <td style={td} className="nx-numeric">{b.sizeBytes ? `${Math.round(b.sizeBytes / 1024)} Ko` : "—"}</td>
                  <td style={td}>{b.checksum ? "✓" : "—"}</td>
                  <td style={td}>{b.verifiedAt ? rel(b.verifiedAt) : "—"}</td>
                  <td style={td}>{b.expiresAt ? new Date(b.expiresAt).toLocaleDateString() : "—"}</td>
                  <td style={{ ...td, color: b.status === "verified" ? "var(--nx-success)" : b.status === "failed" ? "var(--nx-error)" : undefined }}>{b.status}</td>
                  <td style={td}>
                    {canManage && b.status !== "failed" && (
                      <span style={{ display: "flex", gap: 6 }}>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                                onClick={() => run(() => verifyBackup(b.id).then((v) => setNotice(v.ok ? "Contrôles passés" : `Échec : ${v.detail}`)))}>Vérifier</button>
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                                onClick={() => run(() => restoreTest(b.id), "Test de restauration exécuté")}>Test resto</button>
                        {canRestore && (
                          <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-warning)" }} disabled={busy}
                                  onClick={() => { const t = prompt("Cible de restauration en production :"); if (t) void run(() => requestRestore(b.id, t), "Demande de restauration créée — en attente d'un second approbateur"); }}>
                            Restaurer
                          </button>
                        )}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
              {backups.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={8}>Aucune sauvegarde enregistrée.</td></tr>}
            </tbody>
          </table>
        </div>
      </section>

      {restores.length > 0 && (
        <section className="nx-card">
          <div style={label}>Restaurations & tests (§18-21, §39)</div>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Mode", "Cible", "Objets (attendu/observé)", "Statut", ""].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {restores.map((r) => (
                <tr key={r.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{r.mode}</td>
                  <td style={td}>{r.target ?? "—"}</td>
                  <td style={td} className="nx-numeric">{r.objectCountExpected ?? "—"} / {r.objectCountActual ?? "—"}</td>
                  <td style={{ ...td, color: r.status === "passed" ? "var(--nx-success)" : r.status === "failed" ? "var(--nx-error)" : "var(--nx-warning)" }}>{r.status}</td>
                  <td style={td}>
                    {canRestore && r.status === "pending_approval" && (
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                              onClick={() => run(() => approveRestore(r.id), "Restauration approuvée")}>Approuver</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      <section className="nx-card" style={{ display: "grid", gap: 10 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <div style={label}>Incidents & runbook (§43-45)</div>
          {canManage && (
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                    onClick={() => { const t = prompt("Titre de l'incident :"); if (t) void run(() => openIncident({ severity: "p2", category: "other", title: t }), "Incident ouvert"); }}>
              Ouvrir un incident
            </button>
          )}
        </div>
        {incidents.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucun incident.</div>}
        {incidents.map((i) => (
          <div key={i.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
              <span style={{ fontWeight: 600 }}>{i.severity.toUpperCase()} · {i.title}</span>
              <span style={{ fontSize: 12, color: i.status === "resolved" ? "var(--nx-success)" : "var(--nx-warning)" }}>{i.status} · {rel(i.openedAt)}</span>
            </div>
            <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
              {i.domain} · {i.category} · détecté via {i.detectedVia} · runbook {i.runbookStep}/9 — {i.runbookLabel}
            </div>
            {i.containmentActions && i.containmentActions.length > 0 && (
              <div style={{ color: "var(--nx-warning)", fontSize: 12 }}>
                Confinement : {i.containmentActions.map((a) => `${a.action} (${rel(a.at)})`).join(", ")}
              </div>
            )}
            {canManage && i.status !== "resolved" && (
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {i.runbookStep < 9 && (
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                          onClick={() => run(() => advanceIncident(i.id, i.runbookStep + 1), `Étape ${i.runbookStep + 1} : ${RUNBOOK[i.runbookStep + 1]}`)}>
                    Avancer → {RUNBOOK[i.runbookStep + 1]}
                  </button>
                )}
                {canRestore && i.domain === "security" && (
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={busy}
                          onClick={() => {
                            if (!confirm("Verrouiller l'entreprise : révoque toutes les sessions et clés d'API actives. Confirmer ?")) return;
                            void run(() => lockDownIncident(i.id).then((r) =>
                              setNotice(`Verrouillage effectué : ${r.sessionsRevoked} session(s), ${r.apiKeysRevoked} clé(s) d'API, ${r.usersAffected} utilisateur(s) touché(s)`)));
                          }}>
                    Verrouillage d'urgence (§32)
                  </button>
                )}
                <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                        onClick={() => {
                          const cause = prompt("Post-mortem — cause :"); if (!cause) return;
                          void run(() => resolveIncident(i.id, {
                            rpoActualMinutes: Number(prompt("RPO observé (min) :") ?? 0) || null,
                            rtoActualMinutes: Number(prompt("RTO observé (min) :") ?? 0) || null,
                            postMortem: {
                              cause,
                              impact: prompt("Impact :") ?? "",
                              actions: prompt("Actions correctives :") ?? "",
                              prevention: prompt("Prévention :") ?? "",
                              owner: prompt("Responsable :") ?? "",
                            },
                          }), "Incident résolu");
                        }}>
                  Résoudre
                </button>
              </div>
            )}
            {i.postMortem && (
              <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>
                Cause : {i.postMortem.cause} · Prévention : {i.postMortem.prevention} · Responsable : {i.postMortem.owner}
              </div>
            )}
          </div>
        ))}
      </section>

      <section className="nx-card" style={{ display: "grid", gap: 10 }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <div style={label}>Exercices de reprise (§44-45)</div>
          {canManage && (
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                    onClick={() => {
                      const testType = prompt(`Type d'exercice (${DR_TEST_TYPES.join(", ")}) :`, "tabletop");
                      if (!testType) return;
                      const title = prompt("Titre de l'exercice :"); if (!title) return;
                      void run(() => scheduleDrTest({ testType, title }), "Exercice programmé");
                    }}>
              Programmer un exercice
            </button>
          )}
        </div>
        {drTests.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucun exercice programmé.</div>}
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Type", "Titre", "Prévu le", "Statut", "Prochain dû", ""].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {drTests.map((t) => (
              <tr key={t.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td}>{t.testType}</td>
                <td style={td}>{t.title}</td>
                <td style={td}>{t.scheduledFor ?? "—"}</td>
                <td style={{ ...td, color: t.status === "passed" ? "var(--nx-success)" : t.status === "failed" ? "var(--nx-error)" : "var(--nx-warning)" }}>{t.status}</td>
                <td style={td}>{t.nextTestDue ?? "—"}</td>
                <td style={td}>
                  {canManage && t.status === "scheduled" && (
                    <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                            onClick={() => {
                              const passed = confirm("L'exercice a-t-il réussi ? (OK = réussi, Annuler = échoué)");
                              void run(() => recordDrTestResult(t.id, {
                                passed,
                                findings: prompt("Constats :") ?? undefined,
                                correctiveActions: prompt("Actions correctives :") ?? undefined,
                              }), "Résultat consigné");
                            }}>
                    Consigner le résultat
                    </button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section className="nx-card" style={{ display: "grid", gap: 10 }}>
        <div style={label}>Runbooks de référence (§37)</div>
        <div style={{ display: "grid", gap: 6 }}>
          {runbooks.map((r) => (
            <div key={r.key} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10 }}>
              <button className="nx-btn nx-btn--ghost" style={{ padding: 0, border: "none", fontWeight: 600, textAlign: "left", width: "100%" }}
                      onClick={() => setOpenRunbook(openRunbook === r.key ? null : r.key)}>
                {r.title}
              </button>
              <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>{r.description}</div>
              {openRunbook === r.key && (
                <ol style={{ margin: "8px 0 0", paddingLeft: 20, display: "grid", gap: 4 }}>
                  {r.steps.map((s) => (
                    <li key={s.step} style={{ fontSize: 13 }}>
                      <strong>{s.title}</strong> — {s.guidance}
                    </li>
                  ))}
                </ol>
              )}
            </div>
          ))}
        </div>
      </section>
    </div>
  );
}
