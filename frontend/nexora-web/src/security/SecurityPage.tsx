import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { usePrompt } from "@/shared/PromptDialog";
import { SecurityPolicyPanel } from "@/security/SecurityPolicyPanel";
import {
  beginMfaEnrollment,
  confirmMfaEnrollment,
  decideApproval,
  decideEmergencyAccess,
  disableMfa,
  getMfaStatus,
  getSecurityDashboard,
  listApprovals,
  listCertificates,
  listComplianceControls,
  listEmergencyAccess,
  listFindings,
  listSessions,
  listSignals,
  queryAudit,
  registerCertificate,
  reportFinding,
  requestEmergencyAccess,
  revokeAllSessions,
  revokeEmergencyAccess,
  revokeSession,
  reviewSignal,
  scanFraud,
  trustDevice,
  updateFindingStatus,
  type ApprovalRequest,
  type AuditEvent,
  type ComplianceControl,
  type EmergencyAccessGrant,
  type FraudSignal,
  type MfaEnrollment,
  type MfaStatus,
  type SecurityCertificate,
  type SecurityDashboard,
  type SecurityFinding,
  type UserSession,
} from "@/security/securityApi";

const SEV: Record<string, string> = {
  critical: "var(--nx-error)",
  high: "var(--nx-warning)",
  medium: "var(--nx-brand)",
  low: "var(--nx-text-2)",
};

const rel = (iso: string) => {
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? "à l'instant" : m < 60 ? `il y a ${m} min` : `il y a ${Math.round(m / 60)} h`;
};

/** Sécurité, Audit & Anti-fraude (Phase 11) + IAM & Conformité (Phase 47). */
export function SecurityPage() {
  const { activeCompanyId, hasPermission } = useAuth();
  const [tab, setTab] = useState<"mfa" | "fraud" | "approvals" | "audit" | "sessions" | "emergency" | "overview" | "registries" | "policy">("mfa");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [signals, setSignals] = useState<FraudSignal[]>([]);
  const [approvals, setApprovals] = useState<ApprovalRequest[]>([]);
  const [audit, setAudit] = useState<AuditEvent[]>([]);
  const [sessions, setSessions] = useState<UserSession[]>([]);
  const [emergencyGrants, setEmergencyGrants] = useState<EmergencyAccessGrant[]>([]);
  const [dashboard, setDashboard] = useState<SecurityDashboard | null>(null);
  const [findings, setFindings] = useState<SecurityFinding[]>([]);
  const [certificates, setCertificates] = useState<SecurityCertificate[]>([]);
  const [controls, setControls] = useState<ComplianceControl[]>([]);
  const [emergencyDraft, setEmergencyDraft] = useState({ roleName: "", reason: "", hours: "2" });
  const [certDraft, setCertDraft] = useState({ name: "", usageType: "tls", notAfter: "" });

  const [mfa, setMfa] = useState<MfaStatus | null>(null);
  const [enrollment, setEnrollment] = useState<MfaEnrollment | null>(null);
  const [otp, setOtp] = useState("");
  const [recoveryCodes, setRecoveryCodes] = useState<string[] | null>(null);

  const canPlatformSecurity = hasPermission("platform.security");

  const load = useCallback(() => {
    setError(null);
    if (tab === "mfa") getMfaStatus().then(setMfa).catch(fail);
    if (tab === "fraud" && hasPermission("security.fraud_review")) listSignals().then(setSignals).catch(fail);
    if (tab === "approvals" && hasPermission("security.approvals")) listApprovals("pending").then(setApprovals).catch(fail);
    if (tab === "audit" && hasPermission("security.audit_view")) queryAudit({ pageSize: "50" }).then((p) => setAudit(p.events)).catch(fail);
    if (tab === "sessions") listSessions().then(setSessions).catch(fail);
    if (tab === "emergency") listEmergencyAccess().then(setEmergencyGrants).catch(fail);
    if (tab === "overview" && hasPermission("security.configure")) getSecurityDashboard().then(setDashboard).catch(fail);
    if (tab === "registries" && canPlatformSecurity) {
      listFindings().then(setFindings).catch(fail);
      listCertificates().then(setCertificates).catch(fail);
      listComplianceControls().then(setControls).catch(fail);
    }
    function fail(e: unknown) { setError(e instanceof ApiError ? e.message : "Erreur"); }
  }, [tab, hasPermission, canPlatformSecurity]);

  useEffect(load, [load, activeCompanyId]);

  const { ask, dialog } = usePrompt();

  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  const tabs = [
    ["mfa", "Mon authentification", null],
    ["overview", "Vue d'ensemble", "security.configure"],
    ["policy", "Politique", "security.configure"],
    ["fraud", "Anti-fraude", "security.fraud_review"],
    ["approvals", "Approbations", "security.approvals"],
    ["emergency", "Accès d'urgence", "security.request_emergency_access"],
    ["audit", "Journal d'audit", "security.audit_view"],
    ["sessions", "Sessions & appareils", null],
    ["registries", "Vulnérabilités & conformité", "platform.security"],
  ] as const;

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {dialog}
      <h1 style={{ margin: 0 }}>Sécurité</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
        {tabs.filter(([, , perm]) => perm === null || hasPermission(perm)).map(([key, label]) => (
          <button key={key} type="button" onClick={() => setTab(key)}
                  className={tab === key ? "nx-btn" : "nx-btn nx-btn--ghost"} style={{ padding: "6px 12px" }}>
            {label}
          </button>
        ))}
      </div>

      {tab === "policy" && <SecurityPolicyPanel />}

      {tab === "mfa" && (
        <section className="nx-card" style={{ display: "grid", gap: 12, maxWidth: 640 }}>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
            Authentification à deux facteurs (TOTP) — §8
          </div>

          {mfa === null && <div style={{ color: "var(--nx-text-2)" }}>Chargement…</div>}

          {mfa?.enabled && (
            <>
              <div style={{ fontSize: 14 }}>
                ✓ Activée{mfa.enrolledAt ? ` le ${new Date(mfa.enrolledAt).toLocaleDateString()}` : ""} ·{" "}
                {mfa.recoveryCodesRemaining} code(s) de récupération restant(s).
              </div>
              <form
                style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}
                onSubmit={(e) => {
                  e.preventDefault();
                  if (!otp) return;
                  void run(async () => { await disableMfa(otp); setOtp(""); setRecoveryCodes(null); });
                }}
              >
                <input className="nx-input" style={{ maxWidth: 160 }} placeholder="code TOTP ou récupération"
                       value={otp} onChange={(e) => setOtp(e.target.value)} />
                <button className="nx-btn nx-btn--ghost" disabled={busy}>Désactiver</button>
              </form>
            </>
          )}

          {mfa && !mfa.enabled && (
            <>
              {mfa.policyRequires && (
                <div className="nx-error-text">
                  La politique de votre entreprise impose la MFA pour votre rôle (§37) — activez-la maintenant.
                </div>
              )}
              {!enrollment && (
                <button className="nx-btn" style={{ justifySelf: "start" }} disabled={busy}
                        onClick={() => run(async () => { setEnrollment(await beginMfaEnrollment()); setRecoveryCodes(null); })}>
                  Activer la MFA
                </button>
              )}
              {enrollment && (
                <div style={{ display: "grid", gap: 8 }}>
                  <div style={{ fontSize: 14 }}>
                    1. Ajoutez ce compte à votre application d'authentification :
                  </div>
                  <code style={{ background: "var(--nx-surface-2)", padding: "6px 10px", borderRadius: 6, wordBreak: "break-all", fontSize: 13 }}>
                    {enrollment.secret}
                  </code>
                  <div style={{ color: "var(--nx-text-2)", fontSize: 12, wordBreak: "break-all" }}>{enrollment.otpauthUri}</div>
                  <form
                    style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}
                    onSubmit={(e) => {
                      e.preventDefault();
                      if (!otp) return;
                      void run(async () => {
                        const res = await confirmMfaEnrollment(otp);
                        setRecoveryCodes(res.recoveryCodes);
                        setEnrollment(null);
                        setOtp("");
                      });
                    }}
                  >
                    <input className="nx-input" style={{ maxWidth: 140 }} placeholder="code à 6 chiffres"
                           value={otp} onChange={(e) => setOtp(e.target.value)} inputMode="numeric" />
                    <button className="nx-btn" disabled={busy}>2. Confirmer</button>
                  </form>
                </div>
              )}
            </>
          )}

          {recoveryCodes && (
            <div style={{ border: "1px solid var(--nx-warning)", borderRadius: 8, padding: 12, display: "grid", gap: 6 }}>
              <strong>Codes de récupération — notez-les, ils ne seront plus affichés :</strong>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(2, 1fr)", gap: 4, fontFamily: "monospace" }}>
                {recoveryCodes.map((c) => <span key={c}>{c}</span>)}
              </div>
            </div>
          )}
        </section>
      )}

      {tab === "fraud" && (
        <section className="nx-card" style={{ display: "grid", gap: 10 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>Signaux — l'alerte ne remplace pas le jugement humain (§1)</div>
            <button className="nx-btn" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                    onClick={() => run(() => scanFraud())}>Lancer un balayage</button>
          </div>
          {signals.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucun signal ouvert.</div>}
          {signals.map((sig) => (
            <div key={sig.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
                <span style={{ fontWeight: 600 }}>{sig.ruleName}</span>
                <span className="nx-numeric" style={{ color: SEV[sig.severity], fontWeight: 700 }}>
                  {sig.severity} · score {sig.riskScore}
                </span>
              </div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
                {sig.detail} {sig.subjectLabel ? `· ${sig.subjectLabel}` : ""} · {rel(sig.detectedAt)} · statut {sig.status}
              </div>
              {hasPermission("security.fraud_review") && sig.status === "open" && (
                <div style={{ display: "flex", gap: 6 }}>
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                          onClick={() => run(() => reviewSignal(sig.id, "reviewing"))}>Prendre en revue</button>
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                          onClick={async () => { const note = await ask({ title: "Confirmer le signal", label: "Note de revue", required: true, multiline: true }); if (note !== null) run(() => reviewSignal(sig.id, "confirmed", note)); }}>Confirmer</button>
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                          onClick={async () => { const note = await ask({ title: "Faux positif", label: "Note", required: true, defaultValue: "faux positif" }); if (note !== null) run(() => reviewSignal(sig.id, "false_positive", note)); }}>Faux positif</button>
                </div>
              )}
            </div>
          ))}
        </section>
      )}

      {tab === "approvals" && (
        <section className="nx-card" style={{ display: "grid", gap: 10 }}>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>Demandes en attente — demandeur ≠ approbateur (§5)</div>
          {approvals.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucune demande en attente.</div>}
          {approvals.map((req) => (
            <div key={req.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
              <div style={{ fontWeight: 600 }}>
                {req.operationType} {req.amount != null ? `· ${req.amount.toLocaleString("fr-FR")}` : ""}
                <span style={{ color: "var(--nx-text-2)", fontWeight: 400 }}> · niveau {req.currentLevel}/{req.requiredLevels}</span>
              </div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>{rel(req.requestedAt)} · {req.actions.filter((a) => a.action === "approve").length} approbation(s)</div>
              <div style={{ display: "flex", gap: 6 }}>
                <button className="nx-btn" style={{ padding: "2px 12px", fontSize: 12 }} disabled={busy}
                        onClick={async () => { const c = await ask({ title: "Approuver la demande", label: "Commentaire (facultatif)" }); if (c !== null) run(() => decideApproval(req.id, true, c || undefined)); }}>Approuver</button>
                <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 12px", fontSize: 12 }} disabled={busy}
                        onClick={async () => { const m = await ask({ title: "Refuser la demande", label: "Motif du refus", required: true, danger: true, confirmLabel: "Refuser" }); if (m !== null) run(() => decideApproval(req.id, false, m)); }}>Refuser</button>
              </div>
            </div>
          ))}
        </section>
      )}

      {tab === "audit" && (
        <section className="nx-card">
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 }}>
            Journal d'audit — immuable (§12), borné à votre périmètre (§9)
          </div>
          <div style={{ overflowX: "auto" }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
              <thead>
                <tr style={{ color: "var(--nx-text-2)", textAlign: "left" }}>
                  {["Quand", "Utilisateur", "Action", "Ressource", "Changements"].map((h) => (
                    <th key={h} style={{ padding: "4px 8px", whiteSpace: "nowrap" }}>{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {audit.map((e) => (
                  <tr key={e.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={{ padding: "6px 8px", whiteSpace: "nowrap" }}>{rel(e.occurredAt)}</td>
                    <td style={{ padding: "6px 8px" }} className="nx-numeric">{e.userId?.slice(0, 8) ?? "système"}</td>
                    <td style={{ padding: "6px 8px" }}>{e.action}</td>
                    <td style={{ padding: "6px 8px" }}>{e.entityType}</td>
                    <td style={{ padding: "6px 8px", color: "var(--nx-text-2)" }}>
                      {e.changes ? Object.entries(e.changes).slice(0, 3).map(([k, v]) => `${k}: ${v}`).join(" · ") : "—"}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {tab === "sessions" && (
        <section className="nx-card" style={{ display: "grid", gap: 10 }}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Mes sessions & appareils (§6, §11, §30) — un appareil est une session ouverte
            </div>
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                    onClick={() => run(() => revokeAllSessions("Déconnexion globale demandée"))}>Déconnexion globale</button>
          </div>
          {sessions.map((sess) => (
            <div key={sess.id} style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", fontSize: 14, borderTop: "1px solid var(--nx-surface-2)", paddingTop: 6 }}>
              <span>
                {sess.trusted ? "🛡 " : ""}{sess.deviceLabel ?? "Appareil inconnu"} · {sess.deviceType}
                {sess.current ? " · (session courante)" : ""}
                <span style={{ color: "var(--nx-text-2)" }}> · {sess.ipAddress ?? "—"} · ouverte {rel(sess.issuedAt)}</span>
              </span>
              {sess.revokedAt ? (
                <span style={{ color: "var(--nx-error)" }}>révoquée</span>
              ) : (
                <span style={{ display: "flex", gap: 6 }}>
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                          onClick={() => run(() => trustDevice(sess.id, !sess.trusted))}>
                    {sess.trusted ? "Retirer la confiance" : "Marquer de confiance"}
                  </button>
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                          onClick={() => run(() => revokeSession(sess.id, "Révoquée depuis le centre de sécurité"))}>Révoquer</button>
                </span>
              )}
            </div>
          ))}
        </section>
      )}

      {tab === "overview" && (
        <section className="nx-card" style={{ display: "grid", gap: 12 }}>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>Score & tableau de bord (§56-57)</div>
          {!dashboard && <div style={{ color: "var(--nx-text-2)" }}>Chargement…</div>}
          {dashboard && (
            <>
              <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
                <span className="nx-numeric" style={{ fontSize: 32, fontWeight: 700 }}>{dashboard.score.score}/100</span>
                <span style={{ fontSize: 20, fontWeight: 700, color: dashboard.score.grade <= "B" ? "var(--nx-success)" : dashboard.score.grade === "C" ? "var(--nx-warning)" : "var(--nx-error)" }}>
                  Note {dashboard.score.grade}
                </span>
              </div>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 10 }}>
                {dashboard.score.factors.map((f) => (
                  <div key={f.key} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10 }}>
                    <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{f.label}</div>
                    <div className="nx-numeric" style={{ fontWeight: 700 }}>{f.score}/{f.maxScore}</div>
                    <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{f.explanation}</div>
                  </div>
                ))}
              </div>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(160px, 1fr))", gap: 10 }}>
                {[
                  ["Sessions actives", dashboard.activeSessions], ["Appareils de confiance", dashboard.trustedDevices],
                  ["Signaux anti-fraude", dashboard.openFraudSignals], ["Approbations en attente", dashboard.pendingApprovals],
                  ["Vulnérabilités critiques", dashboard.openFindingsCritical], ["Vulnérabilités élevées", dashboard.openFindingsHigh],
                  ["Certificats à échéance", dashboard.certificatesExpiringSoon], ["Incidents de sécurité ouverts", dashboard.openSecurityIncidents],
                  ["Accès d'urgence en attente", dashboard.pendingEmergencyAccessRequests],
                  ["Couverture MFA", `${dashboard.mfaEnrolledUsers}/${dashboard.totalUsers}`],
                ].map(([l, v]) => (
                  <div key={l as string} style={{ fontSize: 13 }}>
                    <span style={{ color: "var(--nx-text-2)" }}>{l} : </span><span className="nx-numeric" style={{ fontWeight: 600 }}>{v}</span>
                  </div>
                ))}
              </div>
            </>
          )}
        </section>
      )}

      {tab === "emergency" && (
        <section className="nx-card" style={{ display: "grid", gap: 10 }}>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
            Accès d'urgence « break-glass » (§12) — jamais auto-approuvé, réservé aux situations exceptionnelles
          </div>
          <form
            style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}
            onSubmit={(e) => {
              e.preventDefault();
              if (!emergencyDraft.roleName || emergencyDraft.reason.length < 10) return;
              void run(async () => {
                await requestEmergencyAccess(emergencyDraft.roleName, emergencyDraft.reason, Number(emergencyDraft.hours));
                setEmergencyDraft({ roleName: "", reason: "", hours: "2" });
              });
            }}
          >
            <input className="nx-input" style={{ maxWidth: 140 }} placeholder="rôle demandé"
                   value={emergencyDraft.roleName} onChange={(e) => setEmergencyDraft({ ...emergencyDraft, roleName: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 340 }} placeholder="motif (≥ 10 caractères, obligatoire)"
                   value={emergencyDraft.reason} onChange={(e) => setEmergencyDraft({ ...emergencyDraft, reason: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 100 }} type="number" min={1} max={8} placeholder="heures"
                   value={emergencyDraft.hours} onChange={(e) => setEmergencyDraft({ ...emergencyDraft, hours: e.target.value })} />
            <button className="nx-btn" disabled={busy}>Demander</button>
          </form>
          {emergencyGrants.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucune demande.</div>}
          {emergencyGrants.map((g) => (
            <div key={g.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
              <div style={{ fontWeight: 600 }}>
                Rôle {g.roleName} · {g.requestedDurationHours} h
                <span style={{ color: "var(--nx-text-2)", fontWeight: 400 }}> · {g.status}{g.isActive ? " · actif" : ""}</span>
              </div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>{g.reason}</div>
              {g.status === "pending" && hasPermission("security.approve_emergency_access") && (
                <div style={{ display: "flex", gap: 6 }}>
                  <button className="nx-btn" style={{ padding: "2px 12px", fontSize: 12 }} disabled={busy}
                          onClick={async () => { const n = await ask({ title: "Approuver l'accès d'urgence", label: "Note d'approbation", required: true }); if (n !== null) run(() => decideEmergencyAccess(g.id, true, n)); }}>Approuver</button>
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 12px", fontSize: 12 }} disabled={busy}
                          onClick={async () => { const m = await ask({ title: "Refuser l'accès d'urgence", label: "Motif du refus", required: true, danger: true, confirmLabel: "Refuser" }); if (m !== null) run(() => decideEmergencyAccess(g.id, false, m)); }}>Refuser</button>
                </div>
              )}
              {g.isActive && hasPermission("security.approve_emergency_access") && (
                <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 12px", fontSize: 12, justifySelf: "start", color: "var(--nx-error)" }} disabled={busy}
                        onClick={async () => { const m = await ask({ title: "Révoquer l'accès d'urgence", label: "Motif de révocation", required: true, danger: true, confirmLabel: "Révoquer" }); if (m !== null) run(() => revokeEmergencyAccess(g.id, m)); }}>
                  Révoquer maintenant
                </button>
              )}
            </div>
          ))}
        </section>
      )}

      {tab === "registries" && (
        <div style={{ display: "grid", gap: 16 }}>
          <section className="nx-card" style={{ display: "grid", gap: 10 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Vulnérabilités — SAST/DAST/SCA/pentest (§42-44)
            </div>
            {findings.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucun constat ouvert.</div>}
            {findings.map((f) => (
              <div key={f.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
                <div style={{ display: "flex", justifyContent: "space-between" }}>
                  <span style={{ fontWeight: 600 }}>{f.title} <span style={{ color: "var(--nx-text-2)", fontWeight: 400 }}>({f.source})</span></span>
                  <span style={{ color: SEV[f.severity] ?? "var(--nx-text-2)", fontWeight: 700 }}>{f.severity}</span>
                </div>
                <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
                  statut {f.status} {f.cveId ? `· ${f.cveId}` : ""} {f.slaDueAt ? `· SLA ${new Date(f.slaDueAt).toLocaleDateString()}` : ""}
                </div>
                {f.status !== "resolved" && f.status !== "false_positive" && (
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12, justifySelf: "start" }} disabled={busy}
                          onClick={async () => { const n = await ask({ title: "Constat résolu", label: "Notes de remédiation", required: true, multiline: true }); if (n !== null) run(() => updateFindingStatus(f.id, "resolved", n)); }}>
                    Marquer résolu
                  </button>
                )}
              </div>
            ))}
            <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13, justifySelf: "start" }} disabled={busy}
                    onClick={async () => {
                      const title = await ask({ title: "Constat manuel", label: "Titre du constat", required: true });
                      if (title === null) return;
                      const severity = await ask({ title: "Constat manuel", label: "Sévérité", choices: [
                        { value: "critical", label: "Critique" }, { value: "high", label: "Haute" }, { value: "medium", label: "Moyenne" },
                        { value: "low", label: "Basse" }, { value: "info", label: "Information" }] });
                      if (severity === null) return;
                      run(() => reportFinding({ source: "manual", severity, title }));
                    }}>
              Déclarer un constat manuel
            </button>
          </section>

          <section className="nx-card" style={{ display: "grid", gap: 10 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>Certificats (§53)</div>
            {certificates.map((c) => (
              <div key={c.id} style={{ display: "flex", justifyContent: "space-between", fontSize: 14, borderTop: "1px solid var(--nx-surface-2)", paddingTop: 6 }}>
                <span>{c.name} · {c.usageType} <span style={{ color: "var(--nx-text-2)" }}>· échéance {c.notAfter}</span></span>
                <span style={{ color: c.status === "expired" ? "var(--nx-error)" : c.status === "expiring_soon" ? "var(--nx-warning)" : "var(--nx-text-2)" }}>
                  {c.status}
                </span>
              </div>
            ))}
            <form
              style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center", marginTop: 6 }}
              onSubmit={(e) => {
                e.preventDefault();
                if (!certDraft.name || !certDraft.notAfter) return;
                void run(async () => {
                  await registerCertificate({ name: certDraft.name, usageType: certDraft.usageType, notAfter: certDraft.notAfter });
                  setCertDraft({ name: "", usageType: "tls", notAfter: "" });
                });
              }}
            >
              <input className="nx-input" style={{ maxWidth: 220 }} placeholder="nom du certificat"
                     value={certDraft.name} onChange={(e) => setCertDraft({ ...certDraft, name: e.target.value })} />
              <select className="nx-input" style={{ maxWidth: 140 }} value={certDraft.usageType}
                      onChange={(e) => setCertDraft({ ...certDraft, usageType: e.target.value })}>
                <option value="tls">TLS</option><option value="signing">Signature</option>
                <option value="webhook">Webhook</option><option value="other">Autre</option>
              </select>
              <input className="nx-input" style={{ maxWidth: 160 }} type="date"
                     value={certDraft.notAfter} onChange={(e) => setCertDraft({ ...certDraft, notAfter: e.target.value })} />
              <button className="nx-btn" disabled={busy}>Enregistrer</button>
            </form>
          </section>

          <section className="nx-card" style={{ display: "grid", gap: 10 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Conformité — auto-évaluation honnête (§35), jamais une certification externe
            </div>
            {controls.map((c) => (
              <div key={c.id} style={{ borderTop: "1px solid var(--nx-surface-2)", paddingTop: 6, display: "grid", gap: 4 }}>
                <div style={{ display: "flex", justifyContent: "space-between", fontSize: 14 }}>
                  <span style={{ fontWeight: 600 }}>{c.title} <span style={{ color: "var(--nx-text-2)", fontWeight: 400 }}>({c.domain})</span></span>
                  <span style={{
                    color: c.status === "met" ? "var(--nx-success)" : c.status === "partial" ? "var(--nx-warning)"
                      : c.status === "not_met" ? "var(--nx-error)" : "var(--nx-text-2)",
                  }}>
                    {c.status}
                  </span>
                </div>
                {c.description && <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{c.description}</div>}
                {c.evidenceRef && <div style={{ color: "var(--nx-text-2)", fontSize: 12, fontFamily: "monospace" }}>{c.evidenceRef}</div>}
              </div>
            ))}
          </section>
        </div>
      )}
    </div>
  );
}
