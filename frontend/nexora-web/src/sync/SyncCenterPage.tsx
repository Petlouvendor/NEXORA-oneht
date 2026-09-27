import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  getOfflinePolicy,
  getSyncCenter,
  resolveConflict,
  revokeDevice,
  setOfflinePolicy,
  type OfflinePolicy,
  type SyncCenter,
} from "@/sync/syncApi";

const rel = (iso: string | null) => {
  if (!iso) return "jamais";
  const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (mins < 1) return "à l'instant";
  if (mins < 60) return `il y a ${mins} min`;
  return `il y a ${Math.round(mins / 60)} h`;
};

/** Centre de synchronisation (Phase 10 §27, §30). */
export function SyncCenterPage() {
  const { activeCompanyId, hasPermission } = useAuth();
  const [center, setCenter] = useState<SyncCenter | null>(null);
  const [policy, setPolicy] = useState<OfflinePolicy | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setError(null);
    Promise.all([getSyncCenter(), getOfflinePolicy().catch(() => null)])
      .then(([c, p]) => { setCenter(c); setPolicy(p); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);

  useEffect(load, [load, activeCompanyId]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("sync.view")) {
    return <div className="nx-card">Le centre de synchronisation n'est pas accessible avec vos permissions.</div>;
  }

  const canManage = hasPermission("sync.manage");
  const canDevices = hasPermission("sync.devices");
  const canConfigure = hasPermission("offline.configure");

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Centre de synchronisation</h1>
      {error && <div className="nx-error-text">{error}</div>}

      {center && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 12 }}>
            {[
              ["En attente", center.pendingOperations.toString(), center.pendingThresholdExceeded],
              ["Synchronisées", center.syncedOperations.toString(), false],
              ["Échecs", center.failedOperations.toString(), center.failedOperations > 0],
              ["Conflits ouverts", center.openConflicts.toString(), center.openConflicts > 0],
              ["Dernière synchro", rel(center.lastSyncAt), (center.minutesSinceLastSync ?? 0) > 120],
            ].map(([label, val, warn]) => (
              <div key={label as string} className="nx-card" style={{ display: "grid", gap: 4 }}>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>{label}</div>
                <div className="nx-numeric" style={{ fontWeight: 700, fontSize: 18, color: warn ? "var(--nx-warning)" : undefined }}>{val}</div>
              </div>
            ))}
          </div>

          {center.recommendedActions.length > 0 && (
            <div className="nx-card" style={{ borderColor: "var(--nx-warning)" }}>
              <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 6 }}>Actions recommandées</div>
              <ul style={{ margin: 0, paddingLeft: 18, color: "var(--nx-warning)" }}>
                {center.recommendedActions.map((a, i) => <li key={i}>{a}</li>)}
              </ul>
            </div>
          )}

          <section className="nx-card">
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 }}>Appareils</div>
            <div style={{ overflowX: "auto" }}>
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
                <thead>
                  <tr style={{ color: "var(--nx-text-2)", textAlign: "left" }}>
                    {["Appareil", "Type", "Statut", "Dernière synchro", "Vu", "En attente", ""].map((h) => (
                      <th key={h} style={{ padding: "4px 8px", whiteSpace: "nowrap" }}>{h}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  {center.devices.map((d) => (
                    <tr key={d.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                      <td style={{ padding: "6px 8px" }}>{d.name}</td>
                      <td style={{ padding: "6px 8px", color: "var(--nx-text-2)" }}>{d.deviceType}</td>
                      <td style={{ padding: "6px 8px", color: d.status === "revoked" ? "var(--nx-error)" : undefined }}>{d.status}</td>
                      <td style={{ padding: "6px 8px" }}>{rel(d.lastSyncAt)}</td>
                      <td style={{ padding: "6px 8px" }}>{rel(d.lastSeenAt)}</td>
                      <td style={{ padding: "6px 8px", color: d.pendingOperations > 0 ? "var(--nx-warning)" : undefined }}>{d.pendingOperations}</td>
                      <td style={{ padding: "6px 8px" }}>
                        {canDevices && d.status !== "revoked" && (
                          <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12 }} disabled={busy}
                                  onClick={() => run(() => revokeDevice(d.id, "Révoqué depuis le centre de synchronisation"))}>Révoquer</button>
                        )}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          {center.conflicts.length > 0 && (
            <section className="nx-card">
              <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 }}>Conflits (§15-17)</div>
              <div style={{ display: "grid", gap: 8 }}>
                {center.conflicts.map((c) => (
                  <div key={c.id} style={{ border: "1px solid var(--nx-surface-2)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
                    <div style={{ fontWeight: 600 }}>{c.conflictType} · {c.entityType}</div>
                    <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>{c.detail}</div>
                    {c.recommendedAction && <div style={{ color: "var(--nx-brand)", fontSize: 12 }}>→ {c.recommendedAction}</div>}
                    {canManage && (
                      <div style={{ display: "flex", gap: 6 }}>
                        {["server_wins", "client_wins", "manual"].map((res) => (
                          <button key={res} className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                                  onClick={() => run(() => resolveConflict(c.id, res))}>{res}</button>
                        ))}
                      </div>
                    )}
                  </div>
                ))}
              </div>
            </section>
          )}
        </>
      )}

      {policy && (
        <section className="nx-card" style={{ display: "grid", gap: 10 }}>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
            Politique offline (§10, §17){canConfigure ? "" : " — lecture seule"}
          </div>
          {(
            [
              ["allowOfflineSales", "Ventes hors ligne"],
              ["allowOfflineCredit", "Crédit hors ligne"],
              ["allowOfflineDiscount", "Remises hors ligne"],
              ["allowOfflineRefund", "Remboursements hors ligne"],
              ["allowOfflineCustomerPayment", "Versements clients hors ligne"],
              ["autoSync", "Synchronisation automatique"],
            ] as const
          ).map(([key, label]) => (
            <label key={key} style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 14 }}>
              <input
                type="checkbox"
                checked={policy[key]}
                disabled={!canConfigure || busy}
                onChange={(e) => run(() => setOfflinePolicy({ [key]: e.target.checked }))}
              />
              {label}
            </label>
          ))}
          <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
            Plafond crédit offline : <span className="nx-numeric">{policy.offlineCreditLimit.toLocaleString("fr-FR")}</span> ·
            durée offline max : {policy.maxOfflineMinutes} min ·
            seuil d'alerte file : {policy.pendingAlertThreshold} ·
            moyens autorisés : {policy.allowedPaymentMethods.join(", ")}
          </div>
        </section>
      )}
    </div>
  );
}
