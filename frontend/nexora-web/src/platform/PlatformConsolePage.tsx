import { useCallback, useEffect, useState } from "react";
import { usePrompt } from "@/shared/PromptDialog";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  confirmPayment,
  customerInvoices,
  customerQuotas,
  customerSubscriptions,
  listCustomers,
  listPlans,
  paymentsForReview,
  platformDowngrade,
  platformReactivate,
  platformSuspend,
  platformUpgrade,
  saasMetrics,
  type Customer,
  type Invoice,
  type Payment,
  type Plan,
  type QuotaReport,
  type SaasMetrics,
  type Subscription,
} from "@/platform/platformApi";
import { listAllApps, reviewApp, submitApp, type MarketplaceApp } from "@/integrations/integrationsApi";

const money = (n: number, c: string) => `${n.toLocaleString(undefined, { minimumFractionDigits: 0, maximumFractionDigits: 2 })} ${c}`;
const rel = (iso: string | null) => {
  if (!iso) return "—";
  const d = Math.round((new Date(iso).getTime() - Date.now()) / 86_400_000);
  return d === 0 ? "aujourd'hui" : d > 0 ? `dans ${d} j` : `il y a ${-d} j`;
};
const STATUS_TONE: Record<string, string> = {
  active: "var(--nx-success)", trialing: "var(--nx-accent)", past_due: "var(--nx-warning)",
  grace: "var(--nx-warning)", suspended: "var(--nx-error)", canceled: "var(--nx-text-2)", expired: "var(--nx-text-2)",
};

/** Console d'administration de la plateforme NEXORA (Phase 38 §34). */
export function PlatformConsolePage() {
  const { hasPermission } = useAuth();
  const [metrics, setMetrics] = useState<SaasMetrics | null>(null);
  const [plans, setPlans] = useState<Plan[]>([]);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [review, setReview] = useState<Payment[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [subs, setSubs] = useState<Subscription[]>([]);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [quota, setQuota] = useState<QuotaReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canManageSubs = hasPermission("platform.manage_subscriptions");
  const canRecordPayment = hasPermission("platform.record_payment");
  const canManageMarketplace = hasPermission("platform.manage_marketplace");
  const [apps, setApps] = useState<MarketplaceApp[]>([]);
  const [appDraft, setAppDraft] = useState({ slug: "", name: "", publisherName: "", category: "other", version: "1.0", scopes: "" });

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      saasMetrics(), listPlans(), listCustomers(), paymentsForReview().catch(() => []),
      canManageMarketplace ? listAllApps("pending_review") : Promise.resolve({ items: [], nextCursor: null }),
    ])
      .then(([m, p, c, r, mp]) => { setMetrics(m); setPlans(p); setCustomers(c); setReview(r); setApps(mp.items); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, [canManageMarketplace]);
  useEffect(load, [load]);

  const loadCustomer = useCallback((id: string) => {
    setSelected(id);
    Promise.all([customerSubscriptions(id), customerInvoices(id), customerQuotas(id).catch(() => null)])
      .then(([s, i, q]) => { setSubs(s); setInvoices(i); setQuota(q); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);

  const prompt = usePrompt();
  async function askSuspend(subscriptionId: string) {
    const reason = await prompt.ask({ title: "Suspendre l'abonnement", label: "Motif de suspension", required: true, multiline: true,
      danger: true, confirmLabel: "Suspendre", hint: "L'entreprise perd l'accès jusqu'à la réactivation ; le motif est journalisé.",
      onSubmit: (r) => platformSuspend(subscriptionId, r) });
    if (reason) saved("Suspendu");
  }
  async function askReject(appId: string) {
    const notes = await prompt.ask({ title: "Refuser l'application", label: "Motif du refus", required: true, multiline: true,
      danger: true, confirmLabel: "Refuser", hint: "Transmis à l'éditeur de l'application.",
      onSubmit: (n) => reviewApp(appId, "reject", n) });
    if (notes) saved("Application refusée");
  }

  /** Après un enregistrement fait dans une fenêtre (qui affiche elle-même un refus) : message et rechargement. */
  const saved = (msg: string) => void run(async () => undefined, msg);

  async function run(fn: () => Promise<unknown>, msg?: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await fn(); if (msg) setNotice(msg); load(); if (selected) loadCustomer(selected); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("platform.view")) {
    return <div className="nx-card">La console plateforme est réservée aux rôles NEXORA.</div>;
  }

  const th: React.CSSProperties = { padding: "4px 8px", textAlign: "left", color: "var(--nx-text-2)" };
  const td: React.CSSProperties = { padding: "6px 8px", whiteSpace: "nowrap" };
  const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };
  const live = subs.find((s) => ["trialing", "active", "past_due", "grace", "suspended"].includes(s.status));

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {prompt.dialog}
      <h1 style={{ margin: 0 }}>Plateforme NEXORA</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {notice && <div style={{ color: "var(--nx-success)" }}>{notice}</div>}

      {metrics && (
        <section className="nx-card">
          <div style={label}>Métriques SaaS (§55) — distinctes des KPI des entreprises clientes</div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 12 }}>
            {[
              ["MRR", money(metrics.mrr, metrics.currencyCode)],
              ["ARR", money(metrics.arr, metrics.currencyCode)],
              ["ARPU", money(metrics.arpu, metrics.currencyCode)],
              ["Abonnements actifs", metrics.activeSubscriptions],
              ["En essai", metrics.trialingSubscriptions],
              ["En retard", metrics.pastDueSubscriptions],
              ["Suspendus", metrics.suspendedSubscriptions],
              ["Churn 30 j", `${metrics.churnRatePct} %`],
              ["Conversion essai", `${metrics.trialConversionPct} %`],
              ["Taux paiement OK", `${metrics.paymentSuccessRatePct} %`],
              ["Tickets ouverts", metrics.openTickets],
              ["Paiements à revoir", metrics.paymentsAwaitingReview],
            ].map(([l, v]) => (
              <div key={l as string} style={{ display: "grid", gap: 2 }}>
                <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{l}</div>
                <div style={{ fontWeight: 700, fontSize: 18 }}>{v}</div>
              </div>
            ))}
          </div>
        </section>
      )}

      {review.length > 0 && (
        <section className="nx-card" style={{ borderLeft: "3px solid var(--nx-warning)" }}>
          <div style={label}>Paiements en attente de revue (§54) — jamais de suspension automatique</div>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <tbody>
              {review.map((p) => (
                <tr key={p.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{money(p.amount, p.currencyCode)}</td>
                  <td style={td}>{p.provider ?? "—"}</td>
                  <td style={td}>{rel(p.receivedAt)}</td>
                  <td style={td}>{p.customerId ? "rattaché" : "non rattaché"}</td>
                  <td style={td}>
                    {canRecordPayment && p.customerId && (
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                              onClick={() => run(() => confirmPayment(p.id), "Paiement confirmé")}>Confirmer</button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}

      <section className="nx-card">
        <div style={label}>Catalogue — plans (§4-6)</div>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Code", "Palier", "Prix", "Essai", "Sociétés", "Utilisateurs", "Statut"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {plans.map((p) => (
              <tr key={p.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td}>{p.code}</td>
                <td style={td}>{p.tier}</td>
                <td style={td} className="nx-numeric">{money(p.priceAmount, p.currencyCode)}/{p.period === "annual" ? "an" : "mois"}</td>
                <td style={td}>{p.trialDays ? `${p.trialDays} j` : "—"}</td>
                <td style={td} className="nx-numeric">{p.quotas.companies || "∞"}</td>
                <td style={td} className="nx-numeric">{p.quotas.users || "∞"}</td>
                <td style={td}>{p.status}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section className="nx-card">
        <div style={label}>Comptes clients (§7-9)</div>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Référence", "Raison sociale", "Pays", "Devise", "Entreprises", "Statut"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {customers.map((c) => (
              <tr key={c.id} onClick={() => loadCustomer(c.id)}
                  style={{ borderTop: "1px solid var(--nx-surface-2)", cursor: "pointer", background: selected === c.id ? "var(--nx-surface-2)" : undefined }}>
                <td style={td}>{c.customerRef}</td>
                <td style={td}>{c.legalName}</td>
                <td style={td}>{c.countryCode}</td>
                <td style={td}>{c.billingCurrency}</td>
                <td style={td} className="nx-numeric">{c.companyIds.length}</td>
                <td style={{ ...td, color: c.status === "active" ? "var(--nx-success)" : "var(--nx-warning)" }}>{c.status}</td>
              </tr>
            ))}
            {customers.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={6}>Aucun client.</td></tr>}
          </tbody>
        </table>
      </section>

      {selected && (
        <section className="nx-card" style={{ display: "grid", gap: 12 }}>
          <div style={label}>Client sélectionné — abonnement, quotas, factures</div>

          {live ? (
            <div style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
              <span style={{ fontWeight: 700 }}>{live.planCode}</span>
              <span style={{ color: STATUS_TONE[live.status] ?? "var(--nx-text-2)", fontWeight: 700 }}>{live.status}</span>
              <span style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
                {money(live.priceAmount, live.currencyCode)}/{live.period === "annual" ? "an" : "mois"} · période jusqu'à {rel(live.currentPeriodEnd)}
                {live.cancelAtPeriodEnd && " · annulation programmée"}
              </span>
              {canManageSubs && (
                <span style={{ display: "flex", gap: 6 }}>
                  <select className="nx-input" style={{ padding: "2px 6px", fontSize: 12 }} defaultValue=""
                          onChange={(e) => { const pid = e.target.value; if (!pid) return;
                            const target = plans.find((p) => p.id === pid)!;
                            void run(() => target.priceAmount >= live.priceAmount ? platformUpgrade(live.id, pid) : platformDowngrade(live.id, pid),
                              "Changement de plan appliqué"); e.target.value = ""; }}>
                    <option value="">Changer de plan…</option>
                    {plans.filter((p) => p.id !== live.planId && p.status === "active").map((p) => (
                      <option key={p.id} value={p.id}>{p.code} — {money(p.priceAmount, p.currencyCode)}</option>
                    ))}
                  </select>
                  {live.status === "suspended"
                    ? <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                              onClick={() => run(() => platformReactivate(live.id), "Réactivé")}>Réactiver</button>
                    : <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={busy}
                              onClick={() => void askSuspend(live.id)}>Suspendre</button>}
                </span>
              )}
            </div>
          ) : <div style={{ color: "var(--nx-text-2)" }}>Aucun abonnement en cours.</div>}

          {quota && (
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(220px, 1fr))", gap: 8 }}>
              {quota.lines.filter((l) => l.limit > 0).map((l) => (
                <div key={l.resource} style={{ display: "grid", gap: 2 }}>
                  <div style={{ display: "flex", justifyContent: "space-between", fontSize: 12 }}>
                    <span>{l.resource}</span><span style={{ color: l.exceeded ? "var(--nx-error)" : "var(--nx-text-2)" }}>{l.used}/{l.limit}</span>
                  </div>
                  <div style={{ height: 4, background: "var(--nx-surface-2)", borderRadius: 2 }}>
                    <div style={{ width: `${Math.min(100, l.percent)}%`, height: "100%", borderRadius: 2, background: l.percent >= 90 ? "var(--nx-error)" : l.percent >= 80 ? "var(--nx-warning)" : "var(--nx-accent)" }} />
                  </div>
                </div>
              ))}
            </div>
          )}

          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Facture", "Type", "Émise", "Total", "Statut"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {invoices.map((i) => (
                <tr key={i.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{i.number}</td>
                  <td style={td}>{i.kind}</td>
                  <td style={td}>{i.issueDate}</td>
                  <td style={td} className="nx-numeric">{money(i.totalAmount, i.currencyCode)}</td>
                  <td style={{ ...td, color: i.status === "paid" ? "var(--nx-success)" : i.status === "overdue" ? "var(--nx-error)" : "var(--nx-warning)" }}>{i.status}</td>
                </tr>
              ))}
              {invoices.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={5}>Aucune facture.</td></tr>}
            </tbody>
          </table>
        </section>
      )}

      {canManageMarketplace && (
        <section className="nx-card">
          <div style={label}>Marketplace — curation & revue (§41-42, §45)</div>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
            <thead><tr>{["Nom", "Éditeur", "Catégorie", "Scopes demandés", "Soumise", ""].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
            <tbody>
              {apps.map((a) => (
                <tr key={a.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                  <td style={td}>{a.name}</td>
                  <td style={td}>{a.publisherName}</td>
                  <td style={td}>{a.category}</td>
                  <td style={{ ...td, whiteSpace: "normal", maxWidth: 260, color: "var(--nx-text-2)" }}>{a.requestedScopes.join(", ")}</td>
                  <td style={{ ...td, color: "var(--nx-text-2)" }}>{a.status}</td>
                  <td style={td}>
                    <span style={{ display: "flex", gap: 6 }}>
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                              onClick={() => run(() => reviewApp(a.id, "approve"), "Application approuvée")}>Approuver</button>
                      <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12, color: "var(--nx-error)" }} disabled={busy}
                              onClick={() => void askReject(a.id)}>
                        Refuser
                      </button>
                    </span>
                  </td>
                </tr>
              ))}
              {apps.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={6}>Aucune soumission en attente de revue.</td></tr>}
            </tbody>
          </table>

          <form
            style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center", marginTop: 12 }}
            onSubmit={(e) => {
              e.preventDefault();
              const scopes = appDraft.scopes.split(/[,\s]+/).map((s) => s.trim()).filter(Boolean);
              if (!appDraft.slug || !appDraft.name || !appDraft.publisherName) return;
              void run(async () => {
                await submitApp({ ...appDraft, requestedScopes: scopes });
                setAppDraft({ slug: "", name: "", publisherName: "", category: "other", version: "1.0", scopes: "" });
              }, "Application soumise à la revue");
            }}
          >
            <input className="nx-input" style={{ maxWidth: 160 }} placeholder="identifiant (slug)"
                   value={appDraft.slug} onChange={(e) => setAppDraft({ ...appDraft, slug: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 160 }} placeholder="nom de l'app"
                   value={appDraft.name} onChange={(e) => setAppDraft({ ...appDraft, name: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 160 }} placeholder="éditeur"
                   value={appDraft.publisherName} onChange={(e) => setAppDraft({ ...appDraft, publisherName: e.target.value })} />
            <input className="nx-input" style={{ maxWidth: 260 }} placeholder="scopes demandés (ex. catalog.view, pos.sell)"
                   value={appDraft.scopes} onChange={(e) => setAppDraft({ ...appDraft, scopes: e.target.value })} />
            <button className="nx-btn" disabled={busy}>Soumettre pour revue</button>
          </form>
          <div style={{ color: "var(--nx-text-2)", fontSize: 12, marginTop: 6 }}>
            Le soumissionnaire ne peut jamais réviser sa propre soumission (§45).
          </div>
        </section>
      )}
    </div>
  );
}
