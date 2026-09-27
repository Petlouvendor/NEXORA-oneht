import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  deletePreference,
  listNotifications,
  listPreferences,
  markAllRead,
  markRead,
  setArchived,
  setPinned,
  upsertPreference,
  type Notification,
  type NotificationPreference,
} from "@/notifications/notificationsApi";

const SEV: Record<string, string> = {
  info: "var(--nx-text-2)",
  success: "var(--nx-success)",
  warning: "var(--nx-warning)",
  error: "var(--nx-error)",
  critical: "var(--nx-error)",
};

const rel = (iso: string) => {
  const m = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  return m < 1 ? "à l'instant" : m < 60 ? `il y a ${m} min` : m < 1440 ? `il y a ${Math.round(m / 60)} h` : `il y a ${Math.round(m / 1440)} j`;
};

/** Centre de notifications & préférences (Phase 34 §4-5). */
export function NotificationsPage() {
  const { activeCompanyId } = useAuth();
  const [tab, setTab] = useState<"unread" | "all" | "pinned" | "archived" | "preferences">("unread");
  const [items, setItems] = useState<Notification[]>([]);
  const [prefs, setPrefs] = useState<NotificationPreference[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setError(null);
    if (tab === "preferences") {
      listPreferences().then(setPrefs).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
    } else {
      listNotifications(tab).then((p) => setItems(p.items)).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
    }
  }, [tab]);

  useEffect(load, [load, activeCompanyId]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  const tabs = [
    ["unread", "Non lues"],
    ["all", "Toutes"],
    ["pinned", "Épinglées"],
    ["archived", "Archivées"],
    ["preferences", "Préférences"],
  ] as const;

  return (
    <div style={{ display: "grid", gap: 16, maxWidth: 860 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
        <h1 style={{ margin: 0 }}>Notifications</h1>
        {tab !== "preferences" && (
          <button className="nx-btn nx-btn--ghost" style={{ padding: "4px 12px", fontSize: 13 }} disabled={busy}
                  onClick={() => run(() => markAllRead())}>Tout marquer lu</button>
        )}
      </div>

      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
        {tabs.map(([key, label]) => (
          <button key={key} type="button" onClick={() => setTab(key)}
                  className={tab === key ? "nx-btn" : "nx-btn nx-btn--ghost"} style={{ padding: "6px 12px" }}>
            {label}
          </button>
        ))}
      </div>

      {tab !== "preferences" && (
        <section className="nx-card" style={{ display: "grid", gap: 8 }}>
          {items.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucune notification.</div>}
          {items.map((n) => (
            <div key={n.id} style={{
              display: "grid", gap: 4, padding: "8px 4px",
              borderTop: "1px solid var(--nx-surface-2)",
              opacity: n.readAt ? 0.62 : 1,
            }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
                <span style={{ fontWeight: 600 }}>
                  {n.pinnedAt ? "📌 " : ""}{n.title}
                </span>
                <span style={{ color: SEV[n.severity] ?? "var(--nx-text-2)", fontSize: 12, whiteSpace: "nowrap" }}>
                  {n.category} · P{n.priority} · {rel(n.createdAt)}
                </span>
              </div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 14 }}>{n.message}</div>
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {!n.readAt && (
                  <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                          onClick={() => run(() => markRead(n.id, true))}>Marquer lu</button>
                )}
                <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                        onClick={() => run(() => setPinned(n.id, !n.pinnedAt))}>{n.pinnedAt ? "Désépingler" : "Épingler"}</button>
                <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                        onClick={() => run(() => setArchived(n.id, !n.archivedAt))}>{n.archivedAt ? "Désarchiver" : "Archiver"}</button>
                {n.actionUrl && <a className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} href={n.actionUrl}>Ouvrir</a>}
              </div>
            </div>
          ))}
        </section>
      )}

      {tab === "preferences" && (
        <PreferencesPanel prefs={prefs} busy={busy} activeCompanyId={activeCompanyId}
          onSave={(p) => run(() => upsertPreference(p))}
          onDelete={(id) => run(() => deletePreference(id))} />
      )}
    </div>
  );
}

const CATEGORIES = ["", "dunning", "low_stock", "expiring_lot", "delivery_failed", "security"];
const DIGESTS = ["instant", "daily", "weekly", "off"];

function PreferencesPanel({
  prefs, busy, activeCompanyId, onSave, onDelete,
}: {
  prefs: NotificationPreference[];
  busy: boolean;
  activeCompanyId: string | null;
  onSave: (p: Omit<NotificationPreference, "id">) => void;
  onDelete: (id: string) => void;
}) {
  const [draft, setDraft] = useState<Omit<NotificationPreference, "id">>({
    companyId: null, category: null, inApp: true, email: false, sms: false, webhook: false,
    minPriority: 5, digest: "instant", quietFrom: null, quietTo: null,
  });

  return (
    <section className="nx-card" style={{ display: "grid", gap: 12 }}>
      <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
        Routage par catégorie — priorité mini, heures calmes, résumé (§5)
      </div>

      <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 13 }}>
        <thead>
          <tr style={{ textAlign: "left", color: "var(--nx-text-2)" }}>
            <th style={{ padding: "4px 6px" }}>Catégorie</th>
            <th style={{ padding: "4px 6px" }}>Interne</th>
            <th style={{ padding: "4px 6px" }}>Email</th>
            <th style={{ padding: "4px 6px" }}>SMS</th>
            <th style={{ padding: "4px 6px" }}>Prio. min.</th>
            <th style={{ padding: "4px 6px" }}>Résumé</th>
            <th style={{ padding: "4px 6px" }}></th>
          </tr>
        </thead>
        <tbody>
          {prefs.length === 0 && <tr><td colSpan={7} style={{ padding: 8, color: "var(--nx-text-2)" }}>Aucune préférence — le défaut est : interne seulement, toutes priorités.</td></tr>}
          {prefs.map((p) => (
            <tr key={p.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
              <td style={{ padding: "6px" }}>{p.category ?? "(toutes)"}{p.companyId ? "" : " · global"}</td>
              <td style={{ padding: "6px" }}>{p.inApp ? "✓" : "—"}</td>
              <td style={{ padding: "6px" }}>{p.email ? "✓" : "—"}</td>
              <td style={{ padding: "6px" }}>{p.sms ? "✓" : "—"}</td>
              <td style={{ padding: "6px" }} className="nx-numeric">P{p.minPriority}</td>
              <td style={{ padding: "6px" }}>{p.digest}</td>
              <td style={{ padding: "6px" }}>
                <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                        onClick={() => onDelete(p.id)}>Supprimer</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>

      <form
        style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}
        onSubmit={(e) => { e.preventDefault(); onSave({ ...draft, companyId: draft.companyId ? activeCompanyId : null }); }}
      >
        <select className="nx-input" style={{ maxWidth: 150 }} value={draft.category ?? ""}
                onChange={(e) => setDraft({ ...draft, category: e.target.value || null })}>
          {CATEGORIES.map((c) => <option key={c} value={c}>{c || "(toutes)"}</option>)}
        </select>
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: 13 }}>
          <input type="checkbox" checked={draft.inApp} onChange={(e) => setDraft({ ...draft, inApp: e.target.checked })} /> interne
        </label>
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: 13 }}>
          <input type="checkbox" checked={draft.email} onChange={(e) => setDraft({ ...draft, email: e.target.checked })} /> email
        </label>
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: 13 }}>
          <input type="checkbox" checked={draft.sms} onChange={(e) => setDraft({ ...draft, sms: e.target.checked })} /> SMS
        </label>
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: 13 }}>
          prio. min.
          <select className="nx-input" style={{ width: 60 }} value={draft.minPriority}
                  onChange={(e) => setDraft({ ...draft, minPriority: Number(e.target.value) })}>
            {[1, 2, 3, 4, 5].map((n) => <option key={n} value={n}>{n}</option>)}
          </select>
        </label>
        <select className="nx-input" style={{ maxWidth: 110 }} value={draft.digest}
                onChange={(e) => setDraft({ ...draft, digest: e.target.value })}>
          {DIGESTS.map((d) => <option key={d} value={d}>{d}</option>)}
        </select>
        <button className="nx-btn" disabled={busy}>Enregistrer</button>
      </form>
    </section>
  );
}
