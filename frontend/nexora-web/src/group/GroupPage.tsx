import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  executeRun,
  getControlCenter,
  listRuns,
  listTasks,
  publishRun,
  validateRun,
  type ConsolidationRun,
  type GroupControlCenter,
  type GroupTask,
} from "@/group/groupApi";

const STATUS_COLOR: Record<string, string> = {
  draft: "var(--nx-text-2)",
  preparing: "var(--nx-text-2)",
  reconciling: "var(--nx-warning)",
  ready: "var(--nx-brand)",
  validated: "var(--nx-success)",
  published: "var(--nx-success)",
  reopened: "var(--nx-error)",
};

const money = (v: number) => v.toLocaleString("fr-FR", { maximumFractionDigits: 2 });

/** Groupe & Consolidation (Phase 9 §9, §17-20, §24). */
export function GroupPage() {
  const { user, hasPermission } = useAuth();
  const groupId = useMemo(
    () => user?.companies.find((c) => c.groupId)?.groupId ?? null,
    [user],
  );

  const [cc, setCc] = useState<GroupControlCenter | null>(null);
  const [runs, setRuns] = useState<ConsolidationRun[]>([]);
  const [tasks, setTasks] = useState<GroupTask[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);

  const load = useCallback(() => {
    if (!groupId) return;
    setError(null);
    Promise.all([getControlCenter(groupId), listRuns(groupId), listTasks(groupId).catch(() => [])])
      .then(([c, r, t]) => { setCc(c); setRuns(r); setTasks(t); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, [groupId]);

  useEffect(load, [load]);

  async function act(runId: string, fn: () => Promise<unknown>) {
    setBusy(runId);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(null); }
  }

  if (!hasPermission("group.view")) {
    return <div className="nx-card">La vue groupe n'est pas accessible avec vos permissions.</div>;
  }
  if (!groupId) {
    return <div className="nx-card">Cette installation ne contient pas de groupe multi-entreprises.</div>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Groupe &amp; Consolidation</h1>
      {error && <div className="nx-error-text">{error}</div>}

      {cc && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))", gap: 12 }}>
            {[
              ["Entreprises", cc.companies.length.toString()],
              ["Intercompany non rapproché", cc.unreconciledIntercompany.toString()],
              ["Consolidations à valider", cc.runsReadyForValidation.toString()],
              ["Tâches ouvertes", cc.openTasks.toString()],
              ["Créances groupe", `${money(cc.groupReceivables)} ${cc.currency}`],
              ["Dettes groupe", `${money(cc.groupPayables)} ${cc.currency}`],
            ].map(([label, val]) => (
              <div key={label} className="nx-card" style={{ display: "grid", gap: 4 }}>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>{label}</div>
                <div className="nx-numeric" style={{ fontWeight: 700, fontSize: 18 }}>{val}</div>
              </div>
            ))}
          </div>

          <section className="nx-card">
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 }}>
              Centre de contrôle — comptable en chef (§24)
            </div>
            <div style={{ overflowX: "auto" }}>
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
                <thead>
                  <tr style={{ color: "var(--nx-text-2)", textAlign: "left" }}>
                    {["Entreprise", "Secteur", "Statut", "Période", "Comptes non mappés", "Intercompany", "Créances", "Dettes"].map((h) => (
                      <th key={h} style={{ padding: "4px 8px", whiteSpace: "nowrap" }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {cc.companies.map((c) => (
                    <tr key={c.companyId} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                      <td style={{ padding: "6px 8px" }}>{c.companyName}</td>
                      <td style={{ padding: "6px 8px", color: "var(--nx-text-2)" }}>{c.sector ?? "—"}</td>
                      <td style={{ padding: "6px 8px" }}>{c.status}</td>
                      <td style={{ padding: "6px 8px" }}>{c.latestPeriod} · {c.latestPeriodStatus}</td>
                      <td style={{ padding: "6px 8px", color: c.unmappedAccounts > 0 ? "var(--nx-warning)" : undefined }}>{c.unmappedAccounts}</td>
                      <td style={{ padding: "6px 8px", color: c.unreconciledIntercompany > 0 ? "var(--nx-warning)" : undefined }}>{c.unreconciledIntercompany}</td>
                      <td className="nx-numeric" style={{ padding: "6px 8px" }}>{money(c.receivables)}</td>
                      <td className="nx-numeric" style={{ padding: "6px 8px" }}>{money(c.payables)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>
        </>
      )}

      <section className="nx-card">
        <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 }}>
          Consolidations (§17-20)
        </div>
        {runs.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucune consolidation.</div>}
        <div style={{ display: "grid", gap: 8 }}>
          {runs.map((r) => (
            <div key={r.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 12, display: "grid", gap: 6 }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", flexWrap: "wrap", gap: 8 }}>
                <div style={{ fontWeight: 600 }}>
                  {r.periodLabel} <span style={{ color: "var(--nx-text-2)", fontWeight: 400 }}>({r.fromDate} → {r.toDate})</span>
                </div>
                <span style={{ color: STATUS_COLOR[r.status] ?? "var(--nx-text-2)", fontWeight: 600, textTransform: "uppercase", fontSize: 12 }}>
                  {r.status}{r.status === "ready" && !r.coherenceOk ? " ⚠" : ""}
                </span>
              </div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 13 }} className="nx-numeric">
                D {money(r.totalDebit)} / C {money(r.totalCredit)} · éliminations {money(r.eliminationTotal)} {r.currency}
              </div>
              {r.diagnostics.length > 0 && (
                <ul style={{ margin: 0, paddingLeft: 18, color: "var(--nx-warning)", fontSize: 12 }}>
                  {r.diagnostics.map((d, i) => <li key={i}>{d}</li>)}
                </ul>
              )}
              {hasPermission("consolidation.run") && (
                <div style={{ display: "flex", gap: 6 }}>
                  {["draft", "preparing", "reconciling", "reopened"].includes(r.status) && (
                    <button className="nx-btn" style={{ padding: "4px 10px", fontSize: 13 }} disabled={busy === r.id}
                            onClick={() => act(r.id, () => executeRun(groupId, r.id))}>Exécuter</button>
                  )}
                  {r.status === "ready" && r.coherenceOk && hasPermission("consolidation.validate") && (
                    <button className="nx-btn" style={{ padding: "4px 10px", fontSize: 13 }} disabled={busy === r.id}
                            onClick={() => act(r.id, () => validateRun(groupId, r.id))}>Valider</button>
                  )}
                  {r.status === "validated" && hasPermission("consolidation.publish") && (
                    <button className="nx-btn" style={{ padding: "4px 10px", fontSize: 13 }} disabled={busy === r.id}
                            onClick={() => act(r.id, () => publishRun(groupId, r.id))}>Publier</button>
                  )}
                </div>
              )}
            </div>
          ))}
        </div>
      </section>

      {tasks.length > 0 && (
        <section className="nx-card">
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 }}>
            Tâches de contrôle (§25)
          </div>
          <ul style={{ margin: 0, padding: 0, listStyle: "none", display: "grid", gap: 6 }}>
            {tasks.map((t) => (
              <li key={t.id} style={{ display: "flex", justifyContent: "space-between", fontSize: 14 }}>
                <span>{t.title}{t.companyName ? ` · ${t.companyName}` : ""}</span>
                <span style={{ color: "var(--nx-text-2)" }}>{t.priority} · {t.status}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
