import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { PaymentsSection } from "@/integrations/PaymentsSection";
import {
  autoMatchStatement,
  createApiKey,
  deliverDue,
  disableApp,
  getCenter,
  installApp,
  listApiKeys,
  listApps,
  listDeliveries,
  listDeveloperUsage,
  listInstalledApps,
  listOutbound,
  listStatements,
  listWebhookEvents,
  retryDelivery,
  revokeApiKey,
  rotateApiKey,
  uninstallApp,
  type ApiKey,
  type AppInstallation,
  type BankStatement,
  type DeveloperKeyUsage,
  type IntegrationCenter,
  type MarketplaceApp,
  type OutboundDelivery,
  type OutboundWebhook,
  type WebhookEvent,
} from "@/integrations/integrationsApi";

const rel = (iso: string | null) => {
  if (!iso) return "—";
  const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (mins < 1) return "à l'instant";
  if (mins < 60) return `il y a ${mins} min`;
  if (mins < 1440) return `il y a ${Math.round(mins / 60)} h`;
  return `il y a ${Math.round(mins / 1440)} j`;
};

const tone = (status: string): string | undefined => {
  if (["error", "failed", "exhausted", "amount_mismatch", "expired", "cancelled"].includes(status)) return "var(--nx-error)";
  if (["pending", "reconciling", "duplicate", "partially_refunded"].includes(status)) return "var(--nx-warning)";
  if (["active", "confirmed", "processed", "delivered", "reconciled", "refunded"].includes(status)) return "var(--nx-success)";
  return undefined;
};

/** Centre des intégrations — observabilité transverse (Phase 12 §39-40). */
export function IntegrationsCenterPage() {
  const { activeCompanyId, hasPermission } = useAuth();
  const [center, setCenter] = useState<IntegrationCenter | null>(null);
  const [events, setEvents] = useState<WebhookEvent[]>([]);
  const [outbound, setOutbound] = useState<OutboundWebhook[]>([]);
  const [deliveries, setDeliveries] = useState<OutboundDelivery[]>([]);
  const [statements, setStatements] = useState<BankStatement[]>([]);
  const [apiKeys, setApiKeys] = useState<ApiKey[]>([]);
  const [newSecret, setNewSecret] = useState<string | null>(null);
  const [keyDraft, setKeyDraft] = useState({ name: "", scopes: "", expiresInDays: "" });
  const [apps, setApps] = useState<MarketplaceApp[]>([]);
  const [installed, setInstalled] = useState<AppInstallation[]>([]);
  const [usage, setUsage] = useState<DeveloperKeyUsage[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canWebhooks = hasPermission("integrations.webhooks");
  const canBank = hasPermission("integrations.bank");
  const canApiKeys = hasPermission("integrations.manage_api_keys");
  const canManageApps = hasPermission("integrations.manage_apps");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      getCenter(),
      canWebhooks ? listWebhookEvents() : Promise.resolve([]),
      canWebhooks ? listOutbound() : Promise.resolve([]),
      canWebhooks ? listDeliveries() : Promise.resolve([]),
      canBank ? listStatements() : Promise.resolve([]),
      canApiKeys ? listApiKeys() : Promise.resolve([]),
      listApps(),
      canManageApps ? listInstalledApps() : Promise.resolve([]),
      canApiKeys ? listDeveloperUsage() : Promise.resolve([]),
    ])
      .then(([c, ev, ob, dl, st, ak, mp, inst, us]) => {
        setCenter(c); setEvents(ev);
        setOutbound(ob); setDeliveries(dl); setStatements(st); setApiKeys(ak);
        setApps(mp.items); setInstalled(inst); setUsage(us);
      })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, [canWebhooks, canBank, canApiKeys, canManageApps]);

  useEffect(load, [load, activeCompanyId]);

  async function run(fn: () => Promise<unknown>, msg?: string) {
    setBusy(true); setError(null); setNotice(null);
    try { const r = await fn(); if (msg) setNotice(typeof r === "string" ? r : msg); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("integrations.view")) {
    return <div className="nx-card">Le centre des intégrations n'est pas accessible avec vos permissions.</div>;
  }

  const th: React.CSSProperties = { padding: "4px 8px", whiteSpace: "nowrap", textAlign: "left" };
  const td: React.CSSProperties = { padding: "6px 8px", whiteSpace: "nowrap" };
  const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <h1 style={{ margin: 0 }}>Centre des intégrations</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {notice && <div style={{ color: "var(--nx-success)" }}>{notice}</div>}

      {center && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(170px, 1fr))", gap: 12 }}>
            {[
              ["Connecteurs actifs", center.activeConnectors, false],
              ["Connecteurs en erreur", center.errorConnectors, center.errorConnectors > 0],
              ["Paiements en attente", center.pendingPayments, center.pendingPayments > 0],
              ["Webhooks en échec", center.failedWebhooks, center.failedWebhooks > 0],
              ["Livraisons sortantes dues", center.pendingOutboundDeliveries, center.pendingOutboundDeliveries > 0],
              ["Lignes bancaires non rapprochées", center.openBankExceptions, center.openBankExceptions > 0],
            ].map(([l, v, warn]) => (
              <div key={l as string} className="nx-card" style={{ display: "grid", gap: 4 }}>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>{l}</div>
                <div className="nx-numeric" style={{ fontWeight: 700, fontSize: 18, color: warn ? "var(--nx-warning)" : undefined }}>{v as number}</div>
              </div>
            ))}
          </div>

          {center.recommendedActions.length > 0 && (
            <div className="nx-card" style={{ borderColor: "var(--nx-warning)" }}>
              <div style={label}>Actions recommandées</div>
              <ul style={{ margin: 0, paddingLeft: 18, color: "var(--nx-warning)" }}>
                {center.recommendedActions.map((a, i) => <li key={i}>{a}</li>)}
              </ul>
            </div>
          )}
        </>
      )}

      <PaymentsSection onChanged={load} />

      <section className="nx-card">
        <div style={label}>Marketplace — apps partenaires (§41-44)</div>
        <div style={{ overflowX: "auto" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr style={{ color: "var(--nx-text-2)" }}>
              {["Nom", "Éditeur", "Catégorie", "Certification", "Prix", "Statut installation", ""].map((h) => <th key={h} style={th}>{h}</th>)}
            </tr></thead>
            <tbody>
              {apps.map((a) => {
                const inst = installed.find((i) => i.marketplaceAppId === a.id);
                return (
                  <tr key={a.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={td}>{a.name}</td>
                    <td style={{ ...td, color: "var(--nx-text-2)" }}>{a.publisherName}</td>
                    <td style={{ ...td, color: "var(--nx-text-2)" }}>{a.category}</td>
                    <td style={td}>{a.certification === "official" ? "🏅 officielle" : a.certification === "certified" ? "✔ certifiée" : "communauté"}</td>
                    <td style={td}>{a.pricingModel === "free" ? "gratuit" : `${a.priceAmount?.toLocaleString("fr-FR")} ${a.priceCurrencyCode}/${a.pricingModel}`}</td>
                    <td style={{ ...td, color: inst?.status === "installed" ? "var(--nx-success)" : "var(--nx-text-2)" }}>
                      {inst?.status ?? "non installée"}
                    </td>
                    <td style={td}>
                      {canManageApps && (
                        <span style={{ display: "flex", gap: 6 }}>
                          {inst?.status === "installed" && (
                            <>
                              <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12 }} disabled={busy}
                                      onClick={() => run(() => disableApp(a.id), "Application désactivée")}>Désactiver</button>
                              <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={busy}
                                      onClick={() => run(() => uninstallApp(a.id), "Application désinstallée")}>Désinstaller</button>
                            </>
                          )}
                          {inst?.status !== "installed" && (
                            <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12 }} disabled={busy}
                                    onClick={() => run(() => installApp(a.id, a.requestedScopes), "Application installée")}>
                              {inst ? "Réactiver" : "Installer"} (scopes : {a.requestedScopes.join(", ") || "aucun"})
                            </button>
                          )}
                        </span>
                      )}
                    </td>
                  </tr>
                );
              })}
              {apps.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={7}>Aucune application publiée pour le moment.</td></tr>}
            </tbody>
          </table>
        </div>
        <div style={{ color: "var(--nx-text-2)", fontSize: 12, marginTop: 6 }}>
          Installer une app accorde par défaut l'intégralité de son périmètre demandé — désinstaller révoque
          immédiatement toute clé d'API émise pour elle (§46).
        </div>
      </section>

      {canWebhooks && (
        <>
          <section className="nx-card">
            <div style={label}>Webhooks entrants (§12-13)</div>
            <div style={{ overflowX: "auto" }}>
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
                <thead><tr style={{ color: "var(--nx-text-2)" }}>
                  {["Reçu", "Type", "Event ID", "Signature", "Statut"].map((h) => <th key={h} style={th}>{h}</th>)}
                </tr></thead>
                <tbody>
                  {events.slice(0, 20).map((e) => (
                    <tr key={e.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                      <td style={td}>{rel(e.receivedAt)}</td>
                      <td style={td}>{e.eventType}</td>
                      <td style={{ ...td, fontFamily: "monospace", fontSize: 12 }}>{e.providerEventId}</td>
                      <td style={{ ...td, color: e.signatureValid ? "var(--nx-success)" : "var(--nx-error)" }}>{e.signatureValid ? "valide" : "invalide"}</td>
                      <td style={{ ...td, color: tone(e.status) }}>{e.status}{e.error ? ` · ${e.error}` : ""}</td>
                    </tr>
                  ))}
                  {events.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={5}>Aucun webhook reçu.</td></tr>}
                </tbody>
              </table>
            </div>
          </section>

          <section className="nx-card">
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
              <div style={label}>Webhooks sortants (§32)</div>
              <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 10px", fontSize: 12 }} disabled={busy}
                      onClick={() => run(() => deliverDue().then((r) => `${r.attempted} tentative(s)`), "Livraisons rejouées")}>
                Rejouer les livraisons dues
              </button>
            </div>
            <div style={{ display: "grid", gap: 6, marginBottom: 10 }}>
              {outbound.map((w) => (
                <div key={w.id} style={{ fontSize: 13, color: "var(--nx-text-2)" }}>
                  <span style={{ color: tone(w.status) }}>●</span> {w.name} → <span style={{ fontFamily: "monospace" }}>{w.targetUrl}</span>
                  {" "}· {w.subscribedEvents.join(", ")}
                  {w.consecutiveFailures > 0 && <span style={{ color: "var(--nx-warning)" }}> · {w.consecutiveFailures} échec(s) consécutif(s)</span>}
                </div>
              ))}
              {outbound.length === 0 && <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>Aucun webhook sortant enregistré.</div>}
            </div>
            <div style={{ overflowX: "auto" }}>
              <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
                <thead><tr style={{ color: "var(--nx-text-2)" }}>
                  {["Événement", "Tentatives", "Statut", "Code", "Prochaine", "Erreur", ""].map((h) => <th key={h} style={th}>{h}</th>)}
                </tr></thead>
                <tbody>
                  {deliveries.slice(0, 20).map((d) => (
                    <tr key={d.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                      <td style={td}>{d.eventType}</td>
                      <td style={td} className="nx-numeric">{d.attempts}/{d.maxAttempts}</td>
                      <td style={{ ...td, color: tone(d.status) }}>{d.status}</td>
                      <td style={td} className="nx-numeric">{d.responseCode ?? "—"}</td>
                      <td style={td}>{d.status === "failed" ? rel(d.nextAttemptAt) : "—"}</td>
                      <td style={{ ...td, color: "var(--nx-text-2)", maxWidth: 220, overflow: "hidden", textOverflow: "ellipsis" }}>{d.lastError ?? "—"}</td>
                      <td style={td}>
                        {d.status !== "delivered" && (
                          <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12 }} disabled={busy}
                                  onClick={() => run(() => retryDelivery(d.id), "Livraison rejouée")}>Rejouer</button>
                        )}
                      </td>
                    </tr>
                  ))}
                  {deliveries.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={7}>Aucune livraison.</td></tr>}
                </tbody>
              </table>
            </div>
          </section>
        </>
      )}

      {canBank && (
        <section className="nx-card">
          <div style={label}>Relevés bancaires (§16-17)</div>
          <div style={{ overflowX: "auto" }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
              <thead><tr style={{ color: "var(--nx-text-2)" }}>
                {["Date", "Numéro", "Solde clôture", "Lignes", "Rapprochées", "Statut", ""].map((h) => <th key={h} style={th}>{h}</th>)}
              </tr></thead>
              <tbody>
                {statements.map((s) => (
                  <tr key={s.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={td}>{s.statementDate}</td>
                    <td style={td}>{s.statementNumber ?? "—"}</td>
                    <td style={td} className="nx-numeric">{s.closingBalance.toLocaleString("fr-FR")}</td>
                    <td style={td} className="nx-numeric">{s.lineCount}</td>
                    <td style={{ ...td, color: s.matchedCount < s.lineCount ? "var(--nx-warning)" : "var(--nx-success)" }} className="nx-numeric">{s.matchedCount}</td>
                    <td style={{ ...td, color: tone(s.status) }}>{s.status}</td>
                    <td style={td}>
                      {s.status !== "reconciled" && (
                        <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12 }} disabled={busy}
                                onClick={() => run(() => autoMatchStatement(s.id).then((n) => `${n} ligne(s) rapprochée(s)`), "Rapprochement lancé")}>
                          Rapprocher
                        </button>
                      )}
                    </td>
                  </tr>
                ))}
                {statements.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={7}>Aucun relevé importé.</td></tr>}
              </tbody>
            </table>
          </div>
        </section>
      )}

      {canApiKeys && (
        <section className="nx-card">
          <div style={label}>Clés d'API — intégrations serveur (§13-14)</div>

          {newSecret && (
            <div style={{ border: "1px solid var(--nx-warning)", borderRadius: 8, padding: 12, marginBottom: 12, display: "grid", gap: 6 }}>
              <strong>Secret de la clé — copiez-le maintenant, il ne sera plus affiché :</strong>
              <code style={{ background: "var(--nx-surface-2)", padding: "6px 10px", borderRadius: 6, wordBreak: "break-all" }}>{newSecret}</code>
              <button className="nx-btn nx-btn--ghost" style={{ justifySelf: "start", padding: "2px 10px", fontSize: 12 }}
                      onClick={() => setNewSecret(null)}>J'ai copié la clé</button>
            </div>
          )}

          <div style={{ overflowX: "auto" }}>
            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
              <thead><tr style={{ color: "var(--nx-text-2)" }}>
                {["Nom", "Préfixe", "Env.", "App liée", "Scopes", "Quota/min", "Dernier usage", "Statut", ""].map((h) => <th key={h} style={th}>{h}</th>)}
              </tr></thead>
              <tbody>
                {apiKeys.map((k) => (
                  <tr key={k.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={td}>{k.name}</td>
                    <td style={td} className="nx-numeric">{k.keyPrefix}</td>
                    <td style={td}>{k.environment}</td>
                    <td style={{ ...td, color: "var(--nx-text-2)" }}>
                      {k.appInstallationId ? (installed.find((i) => i.id === k.appInstallationId)?.appName ?? "app désinstallée") : "—"}
                    </td>
                    <td style={{ ...td, whiteSpace: "normal", maxWidth: 260, color: "var(--nx-text-2)" }}>{k.scopes.join(", ")}</td>
                    <td style={td} className="nx-numeric">{k.rateLimitPerMinute}</td>
                    <td style={td}>{rel(k.lastUsedAt)}{k.lastUsedIp ? ` · ${k.lastUsedIp}` : ""}</td>
                    <td style={{ ...td, color: k.status === "active" ? "var(--nx-success)" : "var(--nx-error)" }}>{k.status}</td>
                    <td style={td}>
                      {k.status === "active" && (
                        <span style={{ display: "flex", gap: 6 }}>
                          <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12 }} disabled={busy}
                                  onClick={() => run(() => rotateApiKey(k.id).then((r) => { setNewSecret(r.secret); }), "Clé pivotée")}>
                            Pivoter
                          </button>
                          <button className="nx-btn nx-btn--ghost" style={{ padding: "2px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={busy}
                                  onClick={() => run(() => revokeApiKey(k.id), "Clé révoquée")}>
                            Révoquer
                          </button>
                        </span>
                      )}
                    </td>
                  </tr>
                ))}
                {apiKeys.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={8}>Aucune clé d'API.</td></tr>}
              </tbody>
            </table>
          </div>

          <form
            style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center", marginTop: 12 }}
            onSubmit={(e) => {
              e.preventDefault();
              const scopes = keyDraft.scopes.split(/[,\s]+/).map((s) => s.trim()).filter(Boolean);
              if (!keyDraft.name || scopes.length === 0) return;
              const expiresInDays = keyDraft.expiresInDays ? Number(keyDraft.expiresInDays) : null;
              void run(async () => {
                const r = await createApiKey({ name: keyDraft.name, scopes, expiresInDays });
                setNewSecret(r.secret);
                setKeyDraft({ name: "", scopes: "", expiresInDays: "" });
              }, "Clé créée");
            }}
          >
            <input className="nx-input" style={{ maxWidth: 180 }} placeholder="nom de la clé"
                   value={keyDraft.name} onChange={(e) => setKeyDraft({ ...keyDraft, name: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 320 }} placeholder="scopes (ex. catalog.view, orders.view)"
                   value={keyDraft.scopes} onChange={(e) => setKeyDraft({ ...keyDraft, scopes: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 130 }} type="number" min={1} placeholder="expire (jours)"
                   value={keyDraft.expiresInDays} onChange={(e) => setKeyDraft({ ...keyDraft, expiresInDays: e.target.value })} />
            <button className="nx-btn" disabled={busy}>Créer une clé</button>
          </form>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, marginTop: 6 }}>
            Le périmètre d'une clé ne peut pas dépasser vos propres permissions. Envoi : en-tête <code>X-Api-Key</code>.
          </div>

          <div style={{ marginTop: 16 }}>
            <div style={label}>Portail développeur — usage quotidien, 30 derniers jours (§47-48)</div>
            {usage.length === 0 && <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>Aucun appel enregistré.</div>}
            {usage.map((u) => {
              const total = u.daily.reduce((s, d) => s + d.requestCount, 0);
              const limited = u.daily.reduce((s, d) => s + d.rateLimitedCount, 0);
              return (
                <div key={u.apiKeyId} style={{ fontSize: 13, color: "var(--nx-text-2)", marginBottom: 4 }}>
                  <strong style={{ color: "var(--nx-text-1, inherit)" }}>{u.keyName}</strong> — {total} appel(s)
                  {limited > 0 && <span style={{ color: "var(--nx-warning)" }}> · {limited} bloqué(s) par le quota</span>}
                </div>
              );
            })}
          </div>
        </section>
      )}
    </div>
  );
}
