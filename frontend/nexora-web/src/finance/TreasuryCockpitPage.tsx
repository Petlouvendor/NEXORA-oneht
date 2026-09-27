import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, KpiCard, Money, SectionLabel, StatePill, Tabs,
  type Column,
} from "@/shared/ui";
import {
  completeFinancialClose, getForecast, getLiquidity, getPosition, listPeriods, listSuspense,
  openSuspense, previewFinancialClose, resolveSuspense,
  type AccountingPeriod, type CashflowForecast, type FinancialCloseReport, type Liquidity,
  type MaturityBucket, type SuspenseItem, type TreasuryPosition, type FinancialAccount,
} from "@/finance/financeApi";

/** Finance & trésorerie — cockpit de pilotage (Phase 28 §27-28, §22-23, §32, §38). */
export function TreasuryCockpitPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("position");
  const [position, setPosition] = useState<TreasuryPosition | null>(null);
  const [liquidity, setLiquidity] = useState<Liquidity | null>(null);
  const [forecast, setForecast] = useState<CashflowForecast | null>(null);
  const [suspense, setSuspense] = useState<SuspenseItem[]>([]);
  const [periods, setPeriods] = useState<AccountingPeriod[]>([]);
  const [periodId, setPeriodId] = useState<string>("");
  const [closeReport, setCloseReport] = useState<FinancialCloseReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canManageSuspense = hasPermission("finance.manage_suspense");
  const canClose = hasPermission("finance.close_period");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      getPosition().catch(() => null),
      getLiquidity().catch(() => null),
      getForecast().catch(() => null),
      listSuspense().catch(() => [] as SuspenseItem[]),
      listPeriods().catch(() => [] as AccountingPeriod[]),
    ])
      .then(([p, l, f, s, per]) => {
        setPosition(p); setLiquidity(l); setForecast(f); setSuspense(s); setPeriods(per);
        if (per.length && !periodId) setPeriodId(per[per.length - 1].id);
      })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, [periodId]);

  useEffect(load, [load]);

  const loadClose = useCallback((id: string) => {
    if (!id) return;
    previewFinancialClose(id)
      .then(setCloseReport)
      .catch((e) => setError(e instanceof ApiError ? e.message : "Contrôles indisponibles"));
  }, []);

  useEffect(() => { loadClose(periodId); }, [periodId, loadClose]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); loadClose(periodId); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("finance.view")) {
    return <Card>La trésorerie n'est pas accessible avec vos permissions.</Card>;
  }

  const cur = position?.currency ?? "HTG";

  const accountCols: Array<Column<FinancialAccount>> = [
    { key: "n", header: "Compte", render: (a) => <>{a.name} {a.status !== "active" && <Badge tone="warning">{a.status}</Badge>}</> },
    { key: "t", header: "Type", render: (a) => <Badge>{a.accountType}</Badge> },
    { key: "b", header: "Solde", numeric: true, render: (a) => <Money value={a.systemBalance} currency={a.currency} /> },
    { key: "bb", header: `Solde (${cur})`, numeric: true, render: (a) => <Money value={a.baseBalance} currency={cur} /> },
  ];

  const suspenseCols: Array<Column<SuspenseItem>> = [
    { key: "k", header: "Nature", render: (i) => <Badge tone={i.overdue ? "error" : undefined}>{i.kind}</Badge> },
    { key: "a", header: "Montant", numeric: true, render: (i) => <Money value={i.amount} currency={i.currency} /> },
    { key: "r", header: "Motif", render: (i) => i.reason },
    { key: "ref", header: "Réf.", render: (i) => i.reference ?? "—" },
    { key: "d", header: "Échéance", render: (i) => (i.resolutionDueDate ? <>{i.resolutionDueDate} {i.overdue && <Badge tone="error">en retard</Badge>}</> : "—") },
    { key: "s", header: "Statut", render: (i) => <StatePill status={i.status} /> },
    {
      key: "act", header: "", render: (i) => canManageSuspense && i.status === "open"
        ? (
          <Button size="sm" variant="subtle" disabled={busy}
            onClick={() => { const r = window.prompt("Note de résolution ?"); if (r) run(() => resolveSuspense(i.id, "resolved", r)); }}>
            Résoudre
          </Button>
        ) : null,
    },
  ];

  const forecastCols: Array<Column<MaturityBucket>> = [
    { key: "l", header: "Maturité", render: (b) => b.label },
    { key: "in", header: "Entrées", numeric: true, render: (b) => <Money value={b.inflow} currency={cur} /> },
    { key: "out", header: "Sorties", numeric: true, render: (b) => <Money value={b.outflow} currency={cur} /> },
    { key: "net", header: "Net", numeric: true, render: (b) => <Money value={b.net} currency={cur} /> },
    { key: "bal", header: "Solde projeté", numeric: true, render: (b) => <Money value={b.projectedBalance} currency={cur} /> },
  ];

  const gap = liquidity?.projectedGap ?? 0;

  async function forceClose() {
    const note = window.prompt("Motif de la clôture forcée ?");
    if (!note) return;
    await run(() => completeFinancialClose(periodId, true, note));
  }

  async function doClose() {
    await run(() => completeFinancialClose(periodId, false));
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Finance & trésorerie</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Trésorerie disponible" value={<Money value={position?.available ?? 0} currency={cur} />} />
        <KpiCard label="Banques" value={<Money value={position?.bank ?? 0} currency={cur} />} />
        <KpiCard label="Caisses" value={<Money value={position?.cash ?? 0} currency={cur} />} />
        <KpiCard label="Mobile Money" value={<Money value={position?.mobileMoney ?? 0} currency={cur} />} />
        <KpiCard
          label={`Gap ${liquidity?.horizonDays ?? 30} j`}
          value={<Money value={gap} currency={cur} />}
          tone={gap < 0 ? "warning" : undefined}
          tag={gap < 0 ? "!" : undefined}
        />
        <KpiCard label="Suspens" value={<Money value={position?.suspenseExposure ?? 0} currency={cur} />} tag={suspense.some((s) => s.overdue) ? "!" : undefined} tone={suspense.some((s) => s.overdue) ? "warning" : undefined} />
      </div>

      {liquidity && liquidity.alerts.length > 0 && (
        <Card>
          <SectionLabel>Alertes de liquidité (§28)</SectionLabel>
          <ul style={{ margin: "8px 0 0", paddingLeft: 18 }}>
            {liquidity.alerts.map((a) => <li key={a} style={{ color: "var(--nx-warning, #b45309)" }}>{a}</li>)}
          </ul>
        </Card>
      )}

      <Card>
        <Tabs
          tabs={[
            { id: "position", label: `Position (${position?.accounts.length ?? 0})` },
            { id: "forecast", label: "Prévision" },
            { id: "suspense", label: `Suspens (${suspense.length})` },
            { id: "close", label: "Clôture" },
          ]}
          active={tab}
          onChange={setTab}
        />
        <div style={{ marginTop: 12 }}>
          {tab === "position" && (
            <DataTable columns={accountCols} rows={position?.accounts ?? []} rowKey={(a) => a.accountId} empty="Aucun compte financier." />
          )}

          {tab === "forecast" && forecast && (
            <>
              <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginBottom: 8 }}>
                Solde d'ouverture <Money value={forecast.openingBalance} currency={cur} /> — au {forecast.asOf}
              </div>
              <DataTable columns={forecastCols} rows={forecast.buckets} rowKey={(b) => b.label} empty="—" />
            </>
          )}

          {tab === "suspense" && (
            <>
              {canManageSuspense && (
                <div style={{ marginBottom: 10 }}>
                  <Button
                    size="sm"
                    disabled={busy}
                    onClick={() => {
                      const amount = Number(window.prompt("Montant (signé : + entrée, − sortie) ?") ?? "");
                      if (!amount) return;
                      const reason = window.prompt("Motif ?") ?? "";
                      if (!reason) return;
                      run(() => openSuspense({ kind: "unidentified", amount, currency: cur, reason }));
                    }}
                  >
                    Ouvrir un suspens
                  </Button>
                </div>
              )}
              <DataTable columns={suspenseCols} rows={suspense} rowKey={(i) => i.id} empty="Aucun suspens." />
            </>
          )}

          {tab === "close" && (
            <div style={{ display: "grid", gap: 12 }}>
              <label style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 13 }}>
                Période
                <select value={periodId} onChange={(e) => setPeriodId(e.target.value)}>
                  {periods.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
                </select>
              </label>

              {closeReport && (
                <>
                  <DataTable
                    columns={[
                      { key: "l", header: "Contrôle", render: (c: FinancialCloseReport["controls"][number]) => <>{c.label} {c.blocking && <Badge>bloquant</Badge>}</> },
                      { key: "s", header: "État", render: (c) => <Badge tone={c.status === "pass" ? "success" : c.status === "warn" ? "warning" : "error"}>{c.status}</Badge> },
                      { key: "d", header: "Détail", render: (c) => c.detail ?? "—" },
                    ] as Array<Column<FinancialCloseReport["controls"][number]>>}
                    rows={closeReport.controls}
                    rowKey={(c) => c.key}
                    empty="—"
                  />
                  <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
                    {closeReport.status === "completed"
                      ? <Badge tone="success">Clôturée au {closeReport.asOf}</Badge>
                      : canClose && (
                        <>
                          <Button disabled={busy || !closeReport.canClose} onClick={doClose}>
                            Clôturer au {closeReport.asOf}
                          </Button>
                          {!closeReport.canClose && (
                            <Button variant="danger" disabled={busy} onClick={forceClose}>Forcer (motif requis)</Button>
                          )}
                        </>
                      )}
                  </div>
                </>
              )}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
