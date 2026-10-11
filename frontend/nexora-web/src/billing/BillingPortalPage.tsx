import { useCallback, useEffect, useState } from "react";
import { usePrompt } from "@/shared/PromptDialog";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  issueLicenseToken,
  myInvoices,
  myLicenses,
  mySubscription,
  myUsage,
  publicPlans,
  selfCancel,
  selfDowngrade,
  selfReactivate,
  selfUpgrade,
  type Invoice,
  type License,
  type Plan,
  type QuotaReport,
  type Subscription,
} from "@/billing/billingApi";

const money = (n: number, c: string) => `${n.toLocaleString(undefined, { maximumFractionDigits: 2 })} ${c}`;
const day = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString() : "—");

/** Portail client — abonnement, factures, usage, licences (Phase 38 §45-46). */
export function BillingPortalPage() {
  const { hasPermission } = useAuth();
  const [sub, setSub] = useState<Subscription | undefined>();
  const [plans, setPlans] = useState<Plan[]>([]);
  const [invoices, setInvoices] = useState<Invoice[]>([]);
  const [usage, setUsage] = useState<QuotaReport | null>(null);
  const [licenses, setLicenses] = useState<License[]>([]);
  const [token, setToken] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setError(null);
    Promise.all([mySubscription(), publicPlans(), myInvoices().catch(() => []), myUsage().catch(() => null), myLicenses().catch(() => [])])
      .then(([s, p, i, u, l]) => { setSub(s); setPlans(p); setInvoices(i); setUsage(u); setLicenses(l); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);
  useEffect(load, [load]);

  const prompt = usePrompt();
  async function askCancel(subscriptionId: string) {
    const reason = await prompt.ask({ title: "Résilier l'abonnement", label: "Pourquoi souhaitez-vous résilier ?", multiline: true,
      danger: true, confirmLabel: "Résilier en fin de période",
      hint: "L'accès reste ouvert jusqu'à la fin de la période payée ; vous pouvez réactiver d'ici là.",
      onSubmit: (r) => selfCancel(subscriptionId, false, r || undefined) });
    if (reason !== null) saved("Résiliation en fin de période enregistrée");
  }

  /** Après un enregistrement fait dans une fenêtre (qui affiche elle-même un refus) : message et rechargement. */
  const saved = (msg: string) => void run(async () => undefined, msg);

  async function run(fn: () => Promise<unknown>, msg?: string) {
    setBusy(true); setError(null); setNotice(null);
    try { await fn(); if (msg) setNotice(msg); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("platform.portal")) {
    return <div className="nx-card">Le portail d'abonnement n'est pas accessible avec vos permissions.</div>;
  }

  const th: React.CSSProperties = { padding: "4px 8px", textAlign: "left", color: "var(--nx-text-2)" };
  const td: React.CSSProperties = { padding: "6px 8px", whiteSpace: "nowrap" };
  const label: React.CSSProperties = { color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase", marginBottom: 8 };

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {prompt.dialog}
      <h1 style={{ margin: 0 }}>Mon abonnement NEXORA</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {notice && <div style={{ color: "var(--nx-success)" }}>{notice}</div>}

      <section className="nx-card" style={{ display: "grid", gap: 10 }}>
        <div style={label}>Abonnement &amp; plan (§45)</div>
        {sub ? (
          <>
            <div style={{ display: "flex", alignItems: "baseline", gap: 12, flexWrap: "wrap" }}>
              <span style={{ fontWeight: 800, fontSize: 20 }}>{sub.planName}</span>
              <span style={{ fontWeight: 700, color: sub.status === "active" ? "var(--nx-success)" : sub.status === "suspended" ? "var(--nx-error)" : "var(--nx-warning)" }}>{sub.status}</span>
              <span style={{ color: "var(--nx-text-2)" }}>
                {money(sub.priceAmount, sub.currencyCode)} / {sub.period === "annual" ? "an" : "mois"} · période jusqu'au {day(sub.currentPeriodEnd)}
              </span>
            </div>
            {sub.trialEndsAt && <div style={{ color: "var(--nx-accent)" }}>Essai gratuit jusqu'au {day(sub.trialEndsAt)}.</div>}
            {sub.cancelAtPeriodEnd && <div style={{ color: "var(--nx-warning)" }}>Résiliation programmée à la fin de la période.</div>}
            {sub.status === "past_due" && <div style={{ color: "var(--nx-warning)" }}>Paiement en attente — réglez la dernière facture avant le {day(sub.graceEndsAt)}.</div>}

            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
              <select className="nx-input" style={{ padding: "3px 8px", fontSize: 13 }} defaultValue=""
                      onChange={(e) => {
                        const pid = e.target.value; if (!pid) return;
                        const target = plans.find((p) => p.id === pid)!;
                        const up = target.priceAmount >= sub.priceAmount;
                        void run(() => (up ? selfUpgrade(sub.id, pid) : selfDowngrade(sub.id, pid)),
                          up ? "Passage au plan supérieur — prorata facturé (§14)" : "Passage au plan inférieur — avoir au prorata, données conservées (§18)");
                        e.target.value = "";
                      }}>
                <option value="">Changer de plan…</option>
                {plans.filter((p) => p.id !== sub.planId && p.status === "active" && p.isPublic).map((p) => (
                  <option key={p.id} value={p.id}>{p.name} — {money(p.priceAmount, p.currencyCode)}/{p.period === "annual" ? "an" : "mois"}</option>
                ))}
              </select>
              {sub.cancelAtPeriodEnd || sub.status === "suspended"
                ? <button className="nx-btn nx-btn--ghost" style={{ padding: "3px 12px", fontSize: 13 }} disabled={busy}
                          onClick={() => run(() => selfReactivate(sub.id), "Réactivation demandée")}>Réactiver</button>
                : <button className="nx-btn nx-btn--ghost" style={{ padding: "3px 12px", fontSize: 13, color: "var(--nx-error)" }} disabled={busy}
                          onClick={() => void askCancel(sub.id)}>Résilier</button>}
            </div>
          </>
        ) : (
          <div style={{ color: "var(--nx-text-2)" }}>Aucun abonnement actif. Contactez votre administrateur.</div>
        )}
      </section>

      {usage && (
        <section className="nx-card">
          <div style={label}>Usage &amp; quotas (§22)</div>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(230px, 1fr))", gap: 10 }}>
            {usage.lines.filter((l) => l.limit > 0).map((l) => (
              <div key={l.resource} style={{ display: "grid", gap: 3 }}>
                <div style={{ display: "flex", justifyContent: "space-between", fontSize: 13 }}>
                  <span>{l.resource}</span>
                  <span style={{ color: l.exceeded ? "var(--nx-error)" : "var(--nx-text-2)" }}>{l.used} / {l.limit}</span>
                </div>
                <div style={{ height: 6, background: "var(--nx-surface-2)", borderRadius: 3 }}>
                  <div style={{ width: `${Math.min(100, l.percent)}%`, height: "100%", borderRadius: 3, background: l.percent >= 90 ? "var(--nx-error)" : l.percent >= 80 ? "var(--nx-warning)" : "var(--nx-accent)" }} />
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="nx-card">
        <div style={label}>Factures NEXORA (§13, §45)</div>
        <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
          <thead><tr>{["Numéro", "Type", "Émise", "Échéance", "Total", "Statut"].map((h) => <th key={h} style={th}>{h}</th>)}</tr></thead>
          <tbody>
            {invoices.map((i) => (
              <tr key={i.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                <td style={td}>{i.number}</td>
                <td style={td}>{i.kind === "credit_note" ? "avoir" : i.kind === "receipt" ? "reçu" : "facture"}</td>
                <td style={td}>{i.issueDate}</td>
                <td style={td}>{i.dueDate}</td>
                <td style={td} className="nx-numeric">{money(i.totalAmount, i.currencyCode)}</td>
                <td style={{ ...td, color: i.status === "paid" ? "var(--nx-success)" : i.status === "overdue" ? "var(--nx-error)" : "var(--nx-warning)" }}>{i.status}</td>
              </tr>
            ))}
            {invoices.length === 0 && <tr><td style={{ ...td, color: "var(--nx-text-2)" }} colSpan={6}>Aucune facture.</td></tr>}
          </tbody>
        </table>
      </section>

      {licenses.length > 0 && (
        <section className="nx-card" style={{ display: "grid", gap: 8 }}>
          <div style={label}>Licences (§27-28)</div>
          {licenses.map((l) => (
            <div key={l.id} style={{ display: "flex", alignItems: "center", gap: 10, flexWrap: "wrap", fontSize: 14 }}>
              <span style={{ fontWeight: 600 }}>{l.scope}</span>
              <span style={{ color: l.status === "active" ? "var(--nx-success)" : "var(--nx-error)" }}>{l.status}</span>
              <span style={{ color: "var(--nx-text-2)" }}>expire le {day(l.expiresAt)} (+{l.graceDays} j de grâce offline)</span>
              {l.status === "active" && (
                <button className="nx-btn nx-btn--ghost" style={{ padding: "1px 8px", fontSize: 12 }} disabled={busy}
                        onClick={() => run(async () => { const t = await issueLicenseToken(l.id); setToken(t.token); }, "Jeton offline émis — copiez-le dans votre installation")}>
                  Générer un jeton offline
                </button>
              )}
            </div>
          ))}
          {token && (
            <textarea readOnly value={token} rows={3}
                      style={{ width: "100%", fontFamily: "monospace", fontSize: 11, background: "var(--nx-surface-2)", border: "1px solid var(--nx-surface-2)", borderRadius: 6, padding: 8 }} />
          )}
        </section>
      )}
    </div>
  );
}
