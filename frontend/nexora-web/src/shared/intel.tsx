import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { Badge, Button } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { number as fmtNumber, percent, relative } from "@/shared/format";

/* =============================================================================
   NEXORA ONE — composants d'intelligence du design system (brief §4, §6-8).
   DATA → INFORMATION → INSIGHT → RECOMMENDATION → ACTION : ces composants
   portent les trois derniers maillons. Réutilisés par le Dashboard, NEXORA
   INTELLIGENCE et NEXORA AI — aucun écran ne redessine sa propre carte.
   ========================================================================== */

type Tone = "success" | "warning" | "error" | "info";

/** En-tête de section : surtitre (catégorie) + titre + action — hiérarchie visuelle forte (§6). */
export function SectionHeader({ eyebrow, title, action, brand }: {
  eyebrow: string; title?: ReactNode; action?: ReactNode; brand?: boolean;
}) {
  return (
    <div className="nx-section-head">
      <div>
        <div className={`nx-eyebrow${brand ? " nx-eyebrow--brand" : ""}`}>{eyebrow}</div>
        {title && <h2 className="nx-section-title">{title}</h2>}
      </div>
      {action}
    </div>
  );
}

/** Micro-tendance SVG : forme de la série, jamais d'axe ni de valeur inventée. */
export function Sparkline({ points, width = 120, height = 36, tone = "brand", label, fluid }: {
  points: number[]; width?: number; height?: number; tone?: "brand" | "pos" | "neg"; label?: string; fluid?: boolean;
}) {
  if (points.length < 2) return null;
  const min = Math.min(...points);
  const max = Math.max(...points);
  const span = max - min || 1;
  const step = width / (points.length - 1);
  const xy = points.map((v, i) => [i * step, height - 3 - ((v - min) / span) * (height - 6)] as const);
  const line = xy.map(([x, y], i) => `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`).join(" ");
  const color = tone === "pos" ? "var(--nx-pos)" : tone === "neg" ? "var(--nx-neg)" : "var(--nx-brand)";
  const id = `nx-spark-${Math.round(points.reduce((a, b) => a + b, 0) * 7) % 100000}-${points.length}`;
  return (
    <svg width={fluid ? "100%" : width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label ?? "Tendance"}
         preserveAspectRatio={fluid ? "none" : undefined} style={{ display: "block" }}>
      <defs>
        <linearGradient id={id} x1="0" x2="0" y1="0" y2="1">
          <stop offset="0" stopColor={color} stopOpacity="0.28" />
          <stop offset="1" stopColor={color} stopOpacity="0" />
        </linearGradient>
      </defs>
      <path d={`${line} L${width},${height} L0,${height} Z`} fill={`url(#${id})`} />
      <path d={line} fill="none" stroke={color} strokeWidth="1.6" strokeLinejoin="round" strokeLinecap="round" />
    </svg>
  );
}

/** Variation signée : flèche + couleur + valeur (la couleur ne porte jamais seule le sens). */
export function Delta({ value, suffix = " %", invert, label }: {
  value: number | null; suffix?: string; invert?: boolean; label?: string;
}) {
  if (value === null || !Number.isFinite(value)) return <span className="nx-delta nx-delta--flat">—</span>;
  const good = invert ? value < 0 : value > 0;
  const cls = value === 0 ? "nx-delta--flat" : good ? "nx-delta--up" : "nx-delta--down";
  return (
    <span className={`nx-delta ${cls}`} title={label}>
      {value > 0 ? "▲" : value < 0 ? "▼" : "■"} {fmtNumber(Math.abs(value), 1)}{suffix}
    </span>
  );
}

/** KPI héros du dashboard (§6 : CA, PROFIT, CASH FLOW, STOCK VALUE). */
export function HeroKpi({ label, value, delta, deltaLabel, trend, hint, tag, invert }: {
  label: string; value: ReactNode; delta?: number | null; deltaLabel?: string; trend?: number[];
  hint?: ReactNode; tag?: string; invert?: boolean;
}) {
  return (
    <div className="nx-card nx-hero-kpi">
      <div className="nx-eyebrow">
        {label}
        {tag && <Badge tone="warning">{tag}</Badge>}
      </div>
      <div className="nx-hero-kpi__value nx-numeric">{value}</div>
      <div className="nx-hero-kpi__foot">
        {delta !== undefined && <Delta value={delta} invert={invert} label={deltaLabel} />}
        {deltaLabel && delta !== undefined && <span>{deltaLabel}</span>}
        {hint && <span>{hint}</span>}
      </div>
      {trend && trend.length > 1 && (
        <div className="nx-hero-kpi__spark">
          <Sparkline fluid width={240} height={30} points={trend} tone={delta !== undefined && delta !== null && delta < 0 ? "neg" : "brand"} label={`Tendance ${label}`} />
        </div>
      )}
    </div>
  );
}

/** Compteur de signaux (bandeau INTELLIGENCE §6 : « 3 recommandations, 2 alertes, 1 opportunité »). */
export function SignalCount({ count, label, tone, icon }: { count: number | null; label: string; tone: Tone; icon: ReactNode }) {
  return (
    <div className="nx-signal">
      <div className="nx-signal__count" style={{ color: count ? `var(--nx-${tone})` : "var(--nx-text-2)" }}>
        {icon}{count ?? "—"}
      </div>
      <div className="nx-signal__label">{label}</div>
    </div>
  );
}

/* --------------------------------------------------------------- Recommandations */

export type PriorityLevel = "critical" | "important" | "watch" | "opportunity";

export const PRIORITY: Record<PriorityLevel, { label: string; tone: Tone; headline: string }> = {
  critical: { label: "Critique", tone: "error", headline: "RISQUE CRITIQUE" },
  important: { label: "Important", tone: "warning", headline: "ATTENTION REQUISE" },
  watch: { label: "À surveiller", tone: "info", headline: "À SURVEILLER" },
  opportunity: { label: "Opportunité", tone: "success", headline: "OPPORTUNITÉ DÉTECTÉE" },
};

/** Libellés métier des preuves produites par les règles (Phase 43/54) — jamais une clé technique brute. */
const EVIDENCE_LABELS: Record<string, { label: string; unit?: "pct" | "days" | "qty" | "money" | "date" }> = {
  onHand: { label: "Stock actuel", unit: "qty" },
  velocityPerDay: { label: "Ventes moyennes / jour", unit: "qty" },
  reorderPoint: { label: "Point de commande", unit: "qty" },
  leadTimeDays: { label: "Délai fournisseur", unit: "days" },
  suggestedQuantity: { label: "Quantité suggérée", unit: "qty" },
  growthPct: { label: "Tendance", unit: "pct" },
  declinePct: { label: "Baisse", unit: "pct" },
  increasePct: { label: "Hausse", unit: "pct" },
  recentDailyAvg: { label: "Moyenne récente / jour", unit: "qty" },
  priorDailyAvg: { label: "Moyenne antérieure / jour", unit: "qty" },
  recentDays: { label: "Fenêtre récente", unit: "days" },
  priorDays: { label: "Fenêtre antérieure", unit: "days" },
  historyDays: { label: "Historique analysé", unit: "days" },
  periodDays: { label: "Période analysée", unit: "days" },
  bucket1To30: { label: "Échu 1-30 j", unit: "money" },
  bucket31To60: { label: "Échu 31-60 j", unit: "money" },
  bucket61To90: { label: "Échu 61-90 j", unit: "money" },
  bucket90Plus: { label: "Échu > 90 j", unit: "money" },
  daysSinceLastSale: { label: "Sans vente depuis", unit: "days" },
  lastSaleAt: { label: "Dernière vente", unit: "date" },
  avgCost: { label: "Coût moyen", unit: "money" },
  estimatedTiedUpValue: { label: "Capital immobilisé", unit: "money" },
  totalOutstanding: { label: "Encours", unit: "money" },
  potentialRecovery: { label: "Récupérable", unit: "money" },
  oldestOverdueDays: { label: "Retard le plus ancien", unit: "days" },
  openPromises: { label: "Promesses ouvertes" },
  openDisputes: { label: "Litiges ouverts" },
  projectedShortfall: { label: "Manque projeté", unit: "money" },
  estimatedShortfall: { label: "Manque estimé", unit: "money" },
  expectedReceivables: { label: "Encaissements attendus", unit: "money" },
  commitments: { label: "Engagements", unit: "money" },
  horizonDays: { label: "Horizon", unit: "days" },
  gap: { label: "Écart", unit: "money" },
  budget: { label: "Budget", unit: "money" },
  actualToDate: { label: "Réalisé à date", unit: "money" },
  projectedYearEnd: { label: "Projection fin d'année", unit: "money" },
  overrunPct: { label: "Dépassement", unit: "pct" },
  fractionElapsedPct: { label: "Année écoulée", unit: "pct" },
  lifetimeValue: { label: "Valeur client", unit: "money" },
  inactiveThresholdDays: { label: "Seuil d'inactivité", unit: "days" },
  totalOrders: { label: "Commandes", unit: "qty" },
  priorAvgPrice: { label: "Prix antérieur", unit: "money" },
  recentAvgPrice: { label: "Prix récent", unit: "money" },
  marginRatePct: { label: "Taux de marge", unit: "pct" },
};

/** Transforme une preuve JSON en métriques lisibles (valeurs scalaires seulement, ordre du service). */
export function evidenceMetrics(evidence: Record<string, unknown> | null | undefined, currency?: string) {
  return Object.entries(evidence ?? {})
    .filter(([, v]) => typeof v === "number" || (typeof v === "string" && v.length <= 40))
    .map(([key, raw]) => {
      const def = EVIDENCE_LABELS[key];
      const label = def?.label ?? key.replace(/([A-Z])/g, " $1").replace(/^./, (c) => c.toUpperCase());
      let value: string;
      if (typeof raw === "number") {
        value = def?.unit === "pct" ? percent(raw)
          : def?.unit === "days" ? `${fmtNumber(raw, 1)} j`
          : def?.unit === "money" ? `${fmtNumber(raw, 2)}${currency ? ` ${currency}` : ""}`
          : fmtNumber(raw, 2);
      } else {
        const text = String(raw);
        value = def?.unit === "date" && !Number.isNaN(Date.parse(text)) ? new Date(text).toLocaleDateString() : text;
      }
      return { key, label, value };
    });
}

export interface InsightCardData {
  id: string; title: string; why: string; proposedAction: string; expectedResult?: string;
  priorityLevel: PriorityLevel; category: string; evidence: Record<string, unknown>;
  impactEstimate: Record<string, unknown>; confidenceLevel: string; confidenceReason?: string;
}

const CONFIDENCE: Record<string, string> = {
  high: "Confiance élevée", medium: "Confiance moyenne", low: "Confiance faible", insufficient_data: "Données insuffisantes",
};

/** Libellé du bouton d'action principal selon la nature de la recommandation (§2 « [COMMANDER] »). */
export function primaryActionLabel(category: string): string {
  switch (category) {
    case "stock_reorder": case "sales_spike": return "Commander";
    case "receivable_priority": case "customer_inactive": return "Relancer";
    case "supplier_price_increase": return "Renégocier";
    case "stock_dormant": return "Écouler";
    case "budget_overrun": return "Réviser";
    case "sales_decline": return "Analyser";
    case "cashflow_risk": return "Sécuriser";
    default: return "Appliquer";
  }
}

/**
 * Carte de recommandation (§2, §7) : QUOI (titre + métriques) / POURQUOI / IMPACT / ACTION, puis
 * [VOIR L'ANALYSE] [MODIFIER] [ACTION] [IGNORER]. Les callbacks absents masquent leur bouton : la carte
 * ne propose jamais une action que l'utilisateur n'a pas le droit d'exécuter.
 */
export function RecommendationCard({ rec, onAnalyse, onModify, onAct, onDismiss, busy, compact }: {
  rec: InsightCardData; onAnalyse?: () => void; onModify?: () => void; onAct?: () => void; onDismiss?: () => void;
  busy?: boolean; compact?: boolean;
}) {
  const p = PRIORITY[rec.priorityLevel] ?? PRIORITY.watch;
  const currency = typeof rec.impactEstimate?.currency === "string" ? rec.impactEstimate.currency : undefined;
  const metrics = evidenceMetrics(rec.evidence, currency).slice(0, compact ? 4 : 8);
  const impact = evidenceMetrics(rec.impactEstimate, currency).filter((m) => m.key !== "currency");
  return (
    <article className="nx-insight" data-tone={p.tone} aria-label={`${p.headline} — ${rec.title}`}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
        <span className="nx-eyebrow" style={{ color: `var(--nx-${p.tone})` }}>
          <Icon.Sparkles size={13} /> {p.headline}
        </span>
        <Badge tone={rec.confidenceLevel === "high" ? "success" : rec.confidenceLevel === "medium" ? "info" : "warning"}>
          {CONFIDENCE[rec.confidenceLevel] ?? rec.confidenceLevel}
        </Badge>
      </div>
      <h3 className="nx-insight__title">{rec.title}</h3>
      {metrics.length > 0 && (
        <div className="nx-insight__metrics">
          {metrics.map((m) => (
            <div key={m.key} className="nx-metric">
              <div className="nx-metric__label">{m.label}</div>
              <div className="nx-metric__value">{m.value}</div>
            </div>
          ))}
        </div>
      )}
      <div className="nx-insight__block"><b>Pourquoi ?</b><span>{rec.why}</span></div>
      {!compact && impact.length > 0 && (
        <div className="nx-insight__block"><b>Impact</b>
          <span>{impact.map((m) => `${m.label} : ${m.value}`).join(" · ")}</span>
        </div>
      )}
      <div className="nx-insight__action">
        <Icon.ArrowRight size={16} /> <span><span style={{ color: "var(--nx-text-2)" }}>NEXORA recommande : </span>{rec.proposedAction}</span>
      </div>
      {(onAnalyse || onModify || onAct || onDismiss) && (
        <div className="nx-insight__buttons">
          {onAnalyse && <Button size="sm" variant="ghost" onClick={onAnalyse}>Voir l'analyse</Button>}
          {onModify && <Button size="sm" variant="ghost" onClick={onModify} disabled={busy}>Modifier</Button>}
          {onAct && <Button size="sm" onClick={onAct} disabled={busy}>{primaryActionLabel(rec.category)}</Button>}
          {onDismiss && <Button size="sm" variant="subtle" onClick={onDismiss} disabled={busy}>Ignorer</Button>}
        </div>
      )}
    </article>
  );
}

/* --------------------------------------------------------------- Timeline */

export interface TimelineEvent {
  key: string; title: string; detail?: string; at: string; tone?: Tone; amount?: string; to?: string | null;
}

/** Timeline d'activité (§6 « ACTIVITY ») : quoi, quand, combien — cliquable vers l'objet source. */
export function ActivityTimeline({ events, empty = "Aucune opération récente." }: { events: TimelineEvent[]; empty?: string }) {
  if (events.length === 0) return <div style={{ color: "var(--nx-text-2)" }}>{empty}</div>;
  return (
    <ul className="nx-timeline">
      {events.map((e) => {
        const title = e.to ? <Link to={e.to} style={{ color: "var(--nx-text)", fontWeight: 600 }}>{e.title}</Link>
          : <span style={{ fontWeight: 600 }}>{e.title}</span>;
        return (
          <li key={e.key}>
            <span className="nx-timeline__dot" style={{ ["--_tone" as string]: e.tone ? `var(--nx-${e.tone})` : undefined }} />
            <div style={{ minWidth: 0 }}>
              {title}
              {e.detail && <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{e.detail}</div>}
            </div>
            <div className="nx-timeline__meta">
              {e.amount && <div className="nx-numeric" style={{ color: "var(--nx-text)", fontWeight: 600 }}>{e.amount}</div>}
              <span title={new Date(e.at).toLocaleString()}><Icon.Clock size={11} /> {relative(e.at)}</span>
            </div>
          </li>
        );
      })}
    </ul>
  );
}

/* --------------------------------------------------------------- Visualisation */

/** Classement en barres horizontales (top performance, faits de NEXORA AI). Valeurs négatives en rouge. */
export function BarList({ rows, max }: { rows: Array<{ key: string; label: string; value: number; display: string; hint?: string }>; max?: number }) {
  const top = max ?? Math.max(1, ...rows.map((r) => Math.abs(r.value)));
  return (
    <div className="nx-barlist" role="list">
      {rows.map((r) => (
        <div key={r.key} className="nx-barlist__row" role="listitem">
          <div className="nx-barlist__head">
            <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{r.label}</span>
            <span className="nx-numeric" style={{ fontWeight: 650 }}>{r.display}</span>
          </div>
          <div className="nx-barlist__track">
            <div className={`nx-barlist__fill${r.value < 0 ? " nx-barlist__fill--neg" : ""}`}
                 style={{ width: `${Math.max(2, (Math.abs(r.value) / top) * 100)}%` }} />
          </div>
          {r.hint && <div style={{ fontSize: "var(--nx-fs-xs)", color: "var(--nx-text-3)" }}>{r.hint}</div>}
        </div>
      ))}
    </div>
  );
}

/**
 * Courbe de prévision (§6 « FORECAST ») : historique plein, projection pointillée. Une prévision n'est
 * jamais présentée comme une certitude — le niveau de confiance accompagne toujours la courbe.
 */
export function ForecastChart({ history, forecast, height = 140, label }: {
  history: number[]; forecast: number[]; height?: number; label: string;
}) {
  const all = [...history, ...forecast];
  if (all.length < 2) return null;
  const width = 560;
  const min = Math.min(0, ...all);
  const max = Math.max(...all) || 1;
  const step = width / (all.length - 1);
  const y = (v: number) => height - 8 - ((v - min) / (max - min || 1)) * (height - 16);
  const path = (vals: number[], offset: number) =>
    vals.map((v, i) => `${i === 0 ? "M" : "L"}${((i + offset) * step).toFixed(1)},${y(v).toFixed(1)}`).join(" ");
  const pivot = Math.max(0, history.length - 1);
  return (
    <svg viewBox={`0 0 ${width} ${height}`} width="100%" height={height} role="img" aria-label={label} preserveAspectRatio="none">
      <line x1={pivot * step} x2={pivot * step} y1={0} y2={height} stroke="var(--nx-border-strong)" strokeDasharray="3 4" />
      {history.length > 1 && <path d={path(history, 0)} fill="none" stroke="var(--nx-text-2)" strokeWidth="1.6" />}
      <path d={path(history.length ? [history[history.length - 1], ...forecast] : forecast, pivot)}
            fill="none" stroke="var(--nx-brand)" strokeWidth="2" strokeDasharray="6 5" strokeLinecap="round" />
    </svg>
  );
}
