import { useEffect, useMemo, useState, type ReactNode } from "react";
import { Link, useNavigate } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { usePreferences } from "@/shared/PreferencesContext";
import { Badge, Button, Card, KpiCard, Skeleton } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { dateLong, money, number as fmtNumber, percent } from "@/shared/format";
import {
  ActivityTimeline, BarList, ForecastChart, HeroKpi, RecommendationCard, SectionHeader, SignalCount,
} from "@/shared/intel";
import { OnboardingChecklist } from "@/dashboard/OnboardingChecklist";
import { PendingApprovalsCard, RoleHome } from "@/dashboard/RoleHome";
import {
  getCommandCenter, getSalesBreakdown, getTopProducts,
  type CommandCenterResponse, type KpiValue, type ReportFilter,
} from "@/dashboard/dashboardApi";
import { classifyRisks, firstName, greeting, pctChange, previousWindow } from "@/dashboard/homeModel";
import {
  acceptRecommendation, dismissRecommendation, getIntelligenceDashboard, listRecommendations,
  type IntelligenceDashboard, type Recommendation,
} from "@/intelligence/intelligenceApi";
import { generateForecast, listForecasts, type Forecast } from "@/reports/biApi";
import { getActivity, type ActivityEvent } from "@/app/experienceApi";
import { openNexoraAi } from "@/ai/nexoraAi";

const PRESETS: Array<{ key: NonNullable<ReportFilter["preset"]>; label: string }> = [
  { key: "today", label: "Aujourd'hui" },
  { key: "week", label: "Semaine" },
  { key: "month", label: "Mois" },
  { key: "year", label: "Année" },
];

const QUICK_ACTIONS = [
  { to: "/pos", label: "Nouvelle vente", icon: <Icon.Pos size={16} />, perm: "pos.sell" },
  { to: "/purchasing", label: "Réception", icon: <Icon.Truck size={16} />, perm: "purchasing.receive" },
  { to: "/finance", label: "Encaissement", icon: <Icon.Wallet size={16} />, perm: "finance.pay" },
  { to: "/reports", label: "Rapports", icon: <Icon.Chart size={16} />, perm: "reporting.view" },
];

const errText = (e: unknown) => (e instanceof ApiError ? e.message : "Erreur de chargement");

function fmtKpi(v: KpiValue, currency: string): string {
  if (v.unit === "%") return percent(v.value);
  if (v.unit === "n") return fmtNumber(v.value);
  return money(v.value, currency);
}

/** Charge une source indépendante : l'échec d'un bloc n'efface jamais le reste du dashboard. */
function useSection<T>(enabled: boolean, loader: () => Promise<T>, deps: unknown[]) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tick, setTick] = useState(0);
  useEffect(() => {
    if (!enabled) { setData(null); return; }
    let alive = true;
    setError(null);
    loader().then((d) => alive && setData(d)).catch((e) => alive && setError(errText(e)));
    return () => { alive = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabled, tick, ...deps]);
  return { data, error, reload: () => setTick((t) => t + 1) };
}

/**
 * Dashboard NEXORA ONE (brief §6) — répond d'abord à : que se passe-t-il ? qu'est-ce qui est important ?
 * que dois-je faire ? qu'est-ce qui risque d'arriver ? Chaque bloc n'interroge que l'API autorisée
 * (guide §18, §27) ; un bloc sans permission disparaît au lieu d'afficher un zéro trompeur.
 */
export function DashboardPage() {
  const { user, activeCompanyId, hasPermission } = useAuth();
  const { prefs, update, branding } = usePreferences();
  const navigate = useNavigate();
  const detailed = prefs.dashboardMode === "detailed";
  const [preset, setPreset] = useState<NonNullable<ReportFilter["preset"]>>("month");
  const [perfTab, setPerfTab] = usePerfTab();

  const canPilot = hasPermission("reporting.view");
  const canSales = hasPermission("reporting.view_sales");
  const canFinancial = hasPermission("reporting.view_financial");
  const canIntel = hasPermission("intelligence.view");
  const canAct = hasPermission("intelligence.act_on_recommendation");
  const canForecast = hasPermission("reporting.forecast");

  // Finalisation phase 20 (§49) : rien n'est chargé avant que l'entreprise active soit connue — sinon chaque section
  // partait deux fois au démarrage (sans entreprise, puis avec).
  const ready = Boolean(activeCompanyId);
  const company = user?.companies.find((c) => c.companyId === activeCompanyId);
  const hello = greeting(new Date(), firstName(user?.fullName, user?.email));
  const today = dateLong();

  // Que se passe-t-il ? — période courante + période précédente de même durée (variations).
  const cc = useSection<{ cur: CommandCenterResponse; prev: CommandCenterResponse | null }>(canPilot && ready, async () => {
    const cur = await getCommandCenter({ preset });
    const w = previousWindow(cur.from, cur.to);
    const prev = await getCommandCenter({ from: w.from, to: w.to }).catch(() => null);
    return { cur, prev };
  }, [preset, activeCompanyId]);

  // Que dois-je faire ? — bandeau Intelligence + recommandations ouvertes (aussi utilisées par les risques).
  const intel = useSection<IntelligenceDashboard>(canIntel && ready, getIntelligenceDashboard, [activeCompanyId]);
  const openRecs = useSection<Recommendation[]>(canIntel && ready, () => listRecommendations("open"), [activeCompanyId]);

  const top = useSection(canSales && ready, async () => {
    const [products, customers, days] = await Promise.all([
      getTopProducts({ preset }, 5),
      getSalesBreakdown("customer", { preset }),
      getSalesBreakdown("day", { preset }),
    ]);
    return { products, customers, days };
  }, [preset, activeCompanyId]);

  const activity = useSection<ActivityEvent[]>(ready, () => getActivity(10), [activeCompanyId]);

  // Qu'est-ce qui risque d'arriver ? — dernières prévisions CA & trésorerie (jamais présentées comme certaines).
  const forecasts = useSection<Forecast[]>(canFinancial && ready, async () => {
    const [rev, cash] = await Promise.all([listForecasts("revenue"), listForecasts("cash")]);
    const latest = (l: Forecast[]) => [...l].sort((a, b) => b.generatedAt.localeCompare(a.generatedAt))[0];
    return [latest(rev), latest(cash)].filter((f): f is Forecast => Boolean(f));
  }, [activeCompanyId]);

  const [busy, setBusy] = useState(false);
  async function act(fn: () => Promise<unknown>) {
    setBusy(true);
    try { await fn(); intel.reload(); openRecs.reload(); } finally { setBusy(false); }
  }

  const risks = useMemo(
    () => classifyRisks(cc.data?.cur.criticalAlerts ?? [], openRecs.data ?? []),
    [cc.data, openRecs.data],
  );

  const header = (
    <header className="nx-greeting" style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 16, flexWrap: "wrap" }}>
      <div>
        <div className="nx-eyebrow" style={{ textTransform: "none", letterSpacing: "0.02em" }}>
          {today.charAt(0).toUpperCase() + today.slice(1)} · {branding?.displayName || company?.companyName || "NEXORA ONE"}
        </div>
        <h1>{hello}</h1>
        <p>Voici ce qui mérite votre attention aujourd'hui.</p>
      </div>
      <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
        {canPilot && (
          <div style={{ display: "flex", gap: 3 }} role="group" aria-label="Période">
            {PRESETS.map((p) => (
              <Button key={p.key} size="sm" variant={p.key === preset ? "primary" : "ghost"} onClick={() => setPreset(p.key)}>{p.label}</Button>
            ))}
          </div>
        )}
        {hasPermission("intelligence.use_assistant") && (
          <Button size="sm" variant="ghost" onClick={() => openNexoraAi()}><Icon.Sparkles size={15} /> Demander à NEXORA AI</Button>
        )}
      </div>
    </header>
  );

  const quickActions = QUICK_ACTIONS.filter((a) => hasPermission(a.perm));

  if (!canPilot) {
    return (
      <div style={{ display: "grid", gap: 20 }}>
        {header}
        <OnboardingChecklist />
        <RoleHome />
        {activity.data && activity.data.length > 0 && (
          <Card>
            <SectionHeader eyebrow="Activité" title="Mes dernières opérations" />
            <ActivityTimeline events={toTimeline(activity.data)} />
          </Card>
        )}
      </div>
    );
  }

  const cur = cc.data?.cur;
  const prev = cc.data?.prev ?? null;
  const k = (r: CommandCenterResponse | null | undefined, key: string) => r?.kpis.find((x) => x.key === key)?.value;
  const currency = cur?.currency ?? "HTG";
  const cashFlow = (r: CommandCenterResponse | null | undefined) => {
    const i = k(r, "cash_inflows"); const o = k(r, "cash_outflows");
    return i === undefined || o === undefined ? undefined : i - o;
  };
  const periodLabel = prev ? `vs ${prev.from.slice(5).split("-").reverse().join("/")} → ${prev.to.slice(5).split("-").reverse().join("/")}` : undefined;

  const hero: ReactNode[] = [];
  if (cur) {
    const rev = k(cur, "revenue");
    if (rev !== undefined) hero.push(<HeroKpi key="rev" label="Chiffre d'affaires" value={money(rev, currency)}
      delta={pctChange(rev, k(prev, "revenue"))} deltaLabel={periodLabel} trend={cur.revenueTrend.map((p) => p.value)} />);
    const profit = k(cur, "profit");
    if (profit !== undefined) hero.push(<HeroKpi key="profit" label="Profit" value={money(profit, currency)}
      delta={pctChange(profit, k(prev, "profit"))} deltaLabel={periodLabel} />);
    const flow = cashFlow(cur);
    if (flow !== undefined) hero.push(<HeroKpi key="cash" label="Cash flow net" value={money(flow, currency)}
      delta={pctChange(flow, cashFlow(prev))}
      hint={k(cur, "cash") !== undefined ? `Trésorerie ${money(k(cur, "cash")!, currency)}` : undefined} />);
    const stock = k(cur, "stock_value");
    if (stock !== undefined) hero.push(<HeroKpi key="stock" label="Valeur du stock" value={money(stock, currency)}
      hint="Au coût moyen, à date" />);
    // Profil sans KPI financier (ex. responsable stock) : les KPI autorisés restent visibles.
    if (hero.length === 0) cur.kpis.slice(0, 4).forEach((x) => hero.push(<HeroKpi key={x.key} label={x.label} value={fmtKpi(x, currency)} />));
  }

  const d = intel.data;
  const topRecs = [...(d?.topActions ?? []), ...(d?.topOpportunities ?? [])].slice(0, 3);
  const alertCount = (cur?.criticalAlerts.length ?? 0) + (d?.openAnomalies ?? 0);

  return (
    <div style={{ display: "grid", gap: 28 }}>
      {header}

      <OnboardingChecklist />
      <PendingApprovalsCard />

      {quickActions.length > 0 && (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginTop: -12 }}>
          {quickActions.map((a) => (
            <Link key={a.to} to={a.to}><Button size="sm" variant="subtle">{a.icon} {a.label}</Button></Link>
          ))}
        </div>
      )}

      {/* 1. Que se passe-t-il ? */}
      <section aria-label="Indicateurs clés">
        {cc.error && <div className="nx-error-text" role="alert">{cc.error}</div>}
        {!cur && !cc.error && <Card><Skeleton rows={3} /></Card>}
        {cur && (
          <div className="nx-grid">
            {hero.map((h, i) => <div key={i} className="nx-col-3">{h}</div>)}
          </div>
        )}
        {cur && detailed && (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 12, marginTop: 12 }}>
            {cur.kpis.map((x) => (
              <KpiCard key={x.key} label={x.label} value={fmtKpi(x, currency)}
                       tag={x.kind === "forecast" ? "prévision" : x.kind === "estimated" ? "estimation" : undefined}
                       tone={x.kind !== "actual" ? "warning" : undefined} />
            ))}
          </div>
        )}
        {cur && (
          <div style={{ display: "flex", justifyContent: "space-between", marginTop: 8, color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>
            <span>Du {cur.from} au {cur.to} · {cur.currency} · dérivé des données validées</span>
            <button type="button" className="nx-linkbtn" style={{ fontSize: "var(--nx-fs-xs)" }}
                    onClick={() => update({ dashboardMode: detailed ? "summary" : "detailed" })}>
              {detailed ? "Vue résumé" : "Tous les indicateurs"}
            </button>
          </div>
        )}
      </section>

      {/* 2-3. Qu'est-ce qui est important ? Que dois-je faire ? */}
      {canIntel && (
        <section aria-label="NEXORA Intelligence">
          <SectionHeader brand eyebrow="NEXORA Intelligence" title="Ce que NEXORA a détecté pour vous"
            action={<Link to="/intelligence"><Button size="sm" variant="ghost">Tout voir <Icon.ArrowRight size={14} /></Button></Link>} />
          {intel.error && <div className="nx-error-text">{intel.error}</div>}
          <div className="nx-grid" style={{ marginBottom: 12 }}>
            <div className="nx-col-4"><SignalCount count={d ? d.criticalCount + d.importantCount + d.watchCount : null} label="recommandations à décider" tone="info" icon={<Icon.Bulb size={20} />} /></div>
            <div className="nx-col-4"><SignalCount count={d ? alertCount : null} label="alertes & anomalies" tone="warning" icon={<Icon.Alert size={20} />} /></div>
            <div className="nx-col-4"><SignalCount count={d ? d.opportunityCount : null} label="opportunités" tone="success" icon={<Icon.Trend size={20} />} /></div>
          </div>
          {d && topRecs.length === 0 && (
            <Card><div style={{ display: "flex", gap: 8, alignItems: "center", color: "var(--nx-text-2)" }}>
              <Icon.Check size={16} /> Rien à décider pour le moment : aucune recommandation ouverte.
            </div></Card>
          )}
          {topRecs.length > 0 && (
            <div className="nx-grid">
              {topRecs.map((r) => (
                <div key={r.id} className="nx-col-4">
                  <RecommendationCard compact rec={r} busy={busy}
                    onAnalyse={() => navigate("/intelligence")}
                    onAct={canAct ? () => void act(() => acceptRecommendation(r.id)) : undefined}
                    onDismiss={canAct ? () => void act(() => dismissRecommendation(r.id)) : undefined} />
                </div>
              ))}
            </div>
          )}
        </section>
      )}

      <div className="nx-grid">
        {/* TOP PERFORMANCE */}
        {canSales && (
          <section className="nx-col-6 nx-card" aria-label="Top performance">
            <SectionHeader eyebrow="Top performance" title="Ce qui marche le mieux"
              action={
                <div style={{ display: "flex", gap: 4 }}>
                  {(["products", "customers", "days"] as const).map((t) => (
                    <button key={t} type="button" className="nx-chip" aria-pressed={perfTab === t} onClick={() => setPerfTab(t)}>
                      {t === "products" ? "Produits" : t === "customers" ? "Clients" : "Ventes"}
                    </button>
                  ))}
                </div>
              } />
            {top.error && <div className="nx-error-text">{top.error}</div>}
            {!top.data && !top.error && <Skeleton rows={4} />}
            {top.data && (() => {
              const rows = perfTab === "products"
                ? top.data.products.map((p) => ({ key: p.productId, label: p.name, value: p.revenue, display: money(p.revenue, currency), hint: `${fmtNumber(p.quantity, 2)} vendus · marge ${money(p.margin, currency)}` }))
                : perfTab === "customers"
                  ? top.data.customers.rows.slice(0, 5).map((c) => ({ key: c.label, label: c.label, value: c.revenue, display: money(c.revenue, currency), hint: `${c.transactions} vente(s) · marge ${percent(c.marginRate)}` }))
                  : [...top.data.days.rows].sort((a, b) => b.revenue - a.revenue).slice(0, 5).map((r) => ({ key: r.label, label: dateLong(new Date(`${r.label}T12:00:00`)), value: r.revenue, display: money(r.revenue, currency), hint: `${r.transactions} vente(s)` }));
              return rows.length === 0
                ? <div style={{ color: "var(--nx-text-2)" }}>Aucune vente sur la période.</div>
                : <BarList rows={rows} />;
            })()}
          </section>
        )}

        {/* RISKS */}
        <section className={`${canSales ? "nx-col-6" : "nx-col-12"} nx-card`} aria-label="Risques">
          <SectionHeader eyebrow="Risques" title="Ce qui pourrait mal tourner" />
          <div className="nx-grid" style={{ gap: 10 }}>
            {risks.map((g) => {
              const I = Icon[g.icon];
              const worst = g.items[0]?.tone;
              return (
                <div key={g.domain} className="nx-col-6 nx-risk">
                  <div className="nx-risk__head">
                    <span style={{ display: "inline-flex", gap: 6, alignItems: "center" }}><I size={15} /> {g.label}</span>
                    {g.items.length === 0
                      ? <Badge tone="success"><Icon.Check size={11} /> RAS</Badge>
                      : <Badge tone={worst === "error" ? "error" : worst === "warning" ? "warning" : "info"}>{g.items.length}</Badge>}
                  </div>
                  {g.items.length === 0
                    ? <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-sm)" }}>Aucun risque détecté.</div>
                    : g.items.slice(0, 2).map((it) => (
                      <Link key={it.key} to={it.to} style={{ color: "var(--nx-text)", fontSize: "var(--nx-fs-sm)" }}>
                        <div style={{ fontWeight: 600 }}>{it.title}</div>
                        <div style={{ color: "var(--nx-text-2)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{it.detail}</div>
                      </Link>
                    ))}
                </div>
              );
            })}
          </div>
        </section>

        {/* ACTIVITY */}
        <section className={`${canFinancial ? "nx-col-6" : "nx-col-12"} nx-card`} aria-label="Activité">
          <SectionHeader eyebrow="Activité" title="Dernières opérations importantes" />
          {activity.error && <div className="nx-error-text">{activity.error}</div>}
          {!activity.data && !activity.error ? <Skeleton rows={5} /> : <ActivityTimeline events={toTimeline(activity.data ?? [])} />}
        </section>

        {/* FORECAST */}
        {canFinancial && (
          <section className="nx-col-6 nx-card" aria-label="Prévisions">
            <SectionHeader eyebrow="Prévisions" title="Ce qui risque d'arriver"
              action={canForecast && (
                <Button size="sm" variant="ghost" disabled={busy}
                        onClick={() => void act(async () => {
                          await generateForecast({ metric: "revenue", horizonDays: 30 });
                          await generateForecast({ metric: "cash", horizonDays: 30 });
                          forecasts.reload();
                        })}>
                  Actualiser
                </Button>
              )} />
            {forecasts.error && <div className="nx-error-text">{forecasts.error}</div>}
            {forecasts.data && forecasts.data.length === 0 && (
              <div style={{ color: "var(--nx-text-2)" }}>
                Aucune prévision calculée.{canForecast ? " Cliquez sur « Actualiser » pour projeter les 30 prochains jours." : ""}
              </div>
            )}
            {forecasts.data?.map((f) => {
              const pts = f.points.map((p) => p.value);
              const total = f.metric === "revenue" ? pts.reduce((a, b) => a + b, 0) : pts[pts.length - 1] ?? 0;
              return (
                <div key={f.id} style={{ display: "grid", gap: 6, marginBottom: 12 }}>
                  <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", gap: 8 }}>
                    <div>
                      <div style={{ fontWeight: 650 }}>{f.metric === "revenue" ? `Chiffre d'affaires projeté · ${f.horizonDays} j` : `Trésorerie à J+${f.horizonDays}`}</div>
                      <div className="nx-numeric" style={{ fontSize: "var(--nx-fs-xl)", fontWeight: 750 }}>{money(total, currency)}</div>
                    </div>
                    <Badge tone={f.confidenceLevel === "high" ? "success" : f.confidenceLevel === "medium" ? "info" : "warning"}>
                      Confiance {f.confidenceLevel === "high" ? "élevée" : f.confidenceLevel === "medium" ? "moyenne" : "faible"}
                    </Badge>
                  </div>
                  <ForecastChart label={`Prévision ${f.metric}`} height={90}
                    history={f.metric === "revenue" ? (cur?.revenueTrend ?? []).map((p) => p.value).slice(-30) : []}
                    forecast={pts} />
                  <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>{f.assumptions}</div>
                </div>
              );
            })}
          </section>
        )}
      </div>
    </div>
  );
}

// Onglet de TOP PERFORMANCE mémorisé pour la session de l'écran.
let perfTabMemory: "products" | "customers" | "days" = "products";
function usePerfTab(): ["products" | "customers" | "days", (t: "products" | "customers" | "days") => void] {
  const [t, setT] = useState(perfTabMemory);
  return [t, (v) => { perfTabMemory = v; setT(v); }];
}

function toTimeline(events: ActivityEvent[]) {
  return events.map((e, i) => ({
    key: `${e.kind}-${e.occurredAt}-${i}`, title: e.title, detail: e.detail, at: e.occurredAt, tone: e.tone,
    amount: e.amount === null ? undefined : e.currency ? money(e.amount, e.currency) : fmtNumber(e.amount, 2),
    to: e.route,
  }));
}
