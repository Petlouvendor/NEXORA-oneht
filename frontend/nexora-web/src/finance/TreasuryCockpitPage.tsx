import { useCallback, useEffect, useState, type FormEvent } from "react";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { num, useSubmit } from "@/shared/forms";
import { dateShort, money, todayIso } from "@/shared/format";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, Field, KpiCard, Modal, Money, SectionLabel, Select, StatePill, Tabs, TextInput,
  type Column,
} from "@/shared/ui";
import { errorMessage } from "@/shared/useAsync";
import {
  completeFinancialClose, getForecast, getLiquidity, getPosition, listPeriods, listSuspense,
  openSuspense, previewFinancialClose, resolveSuspense,
  type AccountingPeriod, type CashflowForecast, type FinancialCloseReport, type Liquidity,
  type MaturityBucket, type SuspenseItem, type TreasuryPosition, type FinancialAccount,
} from "@/finance/financeApi";
import { AccountsPanel } from "@/finance/AccountsPanel";
import { ExpensesPanel } from "@/finance/ExpensesPanel";
import { ReconciliationPanel } from "@/finance/ReconciliationPanel";
import { ExchangeRatesPanel } from "@/finance/ExchangeRatesPanel";
import { ACCOUNT_TYPE } from "@/finance/treasuryApi";

const SUSPENSE_KIND: Record<string, string> = {
  unidentified: "Encaissement non identifié", payment_without_invoice: "Paiement sans facture", unallocated_fee: "Frais non affectés",
  bank_discrepancy: "Écart bancaire", mobile_money_discrepancy: "Écart mobile money", cash_variance: "Écart de caisse", other: "Autre",
};

/** Finance & trésorerie : position, comptes, dépenses, rapprochement bancaire, prévision, suspens, clôture. */
export function TreasuryCockpitPage() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") ?? "position";
  const [position, setPosition] = useState<TreasuryPosition | null>(null);
  const [liquidity, setLiquidity] = useState<Liquidity | null>(null);
  const [forecast, setForecast] = useState<CashflowForecast | null>(null);
  const [suspense, setSuspense] = useState<SuspenseItem[]>([]);
  const [periods, setPeriods] = useState<AccountingPeriod[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      getPosition().catch(() => null),
      getLiquidity().catch(() => null),
      getForecast().catch(() => null),
      listSuspense().catch(() => [] as SuspenseItem[]),
      listPeriods().catch(() => [] as AccountingPeriod[]),
    ])
      .then(([p, l, f, s, per]) => { setPosition(p); setLiquidity(l); setForecast(f); setSuspense(s); setPeriods(per); })
      .catch((e) => setError(errorMessage(e, "Erreur de chargement")));
  }, []);

  useEffect(load, [load]);

  if (!hasPermission("finance.view")) {
    return <Card>La trésorerie n'est pas accessible avec vos permissions.</Card>;
  }

  const cur = position?.currency ?? "HTG";
  const gap = liquidity?.projectedGap ?? 0;
  const accountCols: Array<Column<FinancialAccount>> = [
    { key: "n", header: "Compte", render: (a) => <>{a.name} {a.status !== "active" && <Badge tone="warning">{a.status}</Badge>}</> },
    { key: "t", header: "Type", render: (a) => ACCOUNT_TYPE[a.accountType] ?? a.accountType },
    { key: "b", header: "Solde", numeric: true, render: (a) => <Money value={a.systemBalance} currency={a.currency} /> },
    { key: "bb", header: `Solde (${cur})`, numeric: true, render: (a) => <Money value={a.baseBalance} currency={cur} /> },
  ];
  const forecastCols: Array<Column<MaturityBucket>> = [
    { key: "l", header: "Maturité", render: (b) => b.label },
    { key: "in", header: "Entrées", numeric: true, render: (b) => <Money value={b.inflow} currency={cur} /> },
    { key: "out", header: "Sorties", numeric: true, render: (b) => <Money value={b.outflow} currency={cur} /> },
    { key: "net", header: "Net", numeric: true, render: (b) => <Money value={b.net} currency={cur} /> },
    { key: "bal", header: "Solde projeté", numeric: true, render: (b) => <Money value={b.projectedBalance} currency={cur} /> },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Finance & trésorerie</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Trésorerie disponible" value={<Money value={position?.available ?? 0} currency={cur} />} />
        <KpiCard label="Banques" value={<Money value={position?.bank ?? 0} currency={cur} />} />
        <KpiCard label="Caisses" value={<Money value={position?.cash ?? 0} currency={cur} />} />
        <KpiCard label="Mobile Money" value={<Money value={position?.mobileMoney ?? 0} currency={cur} />} />
        <KpiCard label={`Gap ${liquidity?.horizonDays ?? 30} j`} value={<Money value={gap} currency={cur} />}
                 tone={gap < 0 ? "warning" : undefined} tag={gap < 0 ? "!" : undefined} />
        <KpiCard label="Suspens" value={<Money value={position?.suspenseExposure ?? 0} currency={cur} />}
                 tag={suspense.some((s) => s.overdue) ? "!" : undefined} tone={suspense.some((s) => s.overdue) ? "warning" : undefined} />
      </div>

      {liquidity && liquidity.alerts.length > 0 && (
        <Card>
          <SectionLabel>Alertes de liquidité</SectionLabel>
          <ul style={{ margin: "8px 0 0", paddingLeft: 18 }}>
            {liquidity.alerts.map((a) => <li key={a} style={{ color: "var(--nx-warning, #b45309)" }}>{a}</li>)}
          </ul>
        </Card>
      )}

      <Card>
        <Tabs
          tabs={[
            { id: "position", label: "Position" },
            { id: "accounts", label: "Comptes" },
            { id: "expenses", label: "Dépenses" },
            { id: "reconciliation", label: "Rapprochement bancaire" },
            { id: "rates", label: "Taux de change" },
            { id: "forecast", label: "Prévision" },
            { id: "suspense", label: `Suspens (${suspense.filter((s) => s.status === "open").length})` },
            { id: "close", label: "Clôture" },
          ]}
          active={tab}
          onChange={(t) => setParams({ tab: t })}
        />
      </Card>

      {tab === "position" && (
        <Card><DataTable columns={accountCols} rows={position?.accounts ?? []} rowKey={(a) => a.accountId} empty="Aucun compte financier." /></Card>
      )}
      {tab === "accounts" && <AccountsPanel onChanged={load} />}
      {tab === "expenses" && <ExpensesPanel onChanged={load} />}
      {tab === "reconciliation" && <ReconciliationPanel />}
      {tab === "rates" && <ExchangeRatesPanel />}
      {tab === "forecast" && (
        <Card>
          {forecast ? (
            <>
              <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginBottom: 8 }}>
                Solde d'ouverture <Money value={forecast.openingBalance} currency={cur} /> — au {dateShort(forecast.asOf)}
              </div>
              <DataTable columns={forecastCols} rows={forecast.buckets} rowKey={(b) => b.label} empty="—" />
            </>
          ) : <div>Prévision indisponible.</div>}
        </Card>
      )}
      {tab === "suspense" && <SuspensePanel items={suspense} currency={cur} onChanged={load} />}
      {tab === "close" && <ClosePanel periods={periods} onChanged={load} />}
    </div>
  );
}

function SuspensePanel({ items, currency, onChanged }: { items: SuspenseItem[]; currency: string; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canManage = hasPermission("finance.manage_suspense");
  const [opening, setOpening] = useState(false);
  const [resolving, setResolving] = useState<SuspenseItem | null>(null);

  return (
    <Card>
      <SectionLabel action={canManage ? <Button size="sm" onClick={() => setOpening(true)}>Ouvrir un suspens</Button> : undefined}>Suspens de trésorerie</SectionLabel>
      <DataTable exportTitle="Suspens de trésorerie"
        columns={[
          { key: "k", header: "Nature", render: (i) => <Badge tone={i.overdue ? "error" : undefined}>{SUSPENSE_KIND[i.kind] ?? i.kind}</Badge> },
          { key: "a", header: "Montant", numeric: true, render: (i) => <Money value={i.amount} currency={i.currency} /> },
          { key: "c", header: "Compte", render: (i) => i.accountName ?? "—" },
          { key: "r", header: "Motif", render: (i) => i.reason },
          { key: "ref", header: "Réf.", render: (i) => i.reference ?? "—" },
          { key: "d", header: "Échéance", render: (i) => (i.resolutionDueDate ? <>{dateShort(i.resolutionDueDate)} {i.overdue && <Badge tone="error">en retard</Badge>}</> : "—") },
          { key: "s", header: "Statut", render: (i) => <StatePill status={i.status} /> },
          { key: "x", header: "", render: (i) => (canManage && i.status === "open"
            ? <Button size="sm" variant="subtle" onClick={() => setResolving(i)}>Résoudre</Button> : i.resolution ?? null) },
        ]}
        rows={items} rowKey={(i) => i.id} empty="Aucun suspens." />
      {opening && <OpenSuspenseDialog currency={currency} onClose={() => setOpening(false)} onDone={() => { setOpening(false); toast.success("Suspens ouvert."); onChanged(); }} />}
      {resolving && <ResolveSuspenseDialog item={resolving} onClose={() => setResolving(null)} onDone={() => { setResolving(null); toast.success("Suspens clos."); onChanged(); }} />}
    </Card>
  );
}

function OpenSuspenseDialog({ currency, onClose, onDone }: { currency: string; onClose: () => void; onDone: () => void }) {
  const [kind, setKind] = useState("unidentified");
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const [reference, setReference] = useState("");
  const [due, setDue] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "finance.suspense_amount": "amount", "finance.suspense_reason": "reason" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    if (a == null || a === 0) return fail("amount", "Montant non nul requis (+ entrée, − sortie).");
    if (reason.trim().length < 3) return fail("reason", "Motif obligatoire.");
    const ok = await submit(async () => {
      await openSuspense({ kind, amount: a, currency, reason: reason.trim(), reference: reference.trim() || null, resolutionDueDate: due || null });
      return true;
    });
    if (ok) onDone();
  }

  return (
    <Modal title="Ouvrir un suspens" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Ouvrir</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nature">
          <Select value={kind} onChange={(e) => setKind(e.target.value)}>
            {Object.entries(SUSPENSE_KIND).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label={`Montant (${currency})`} required error={fieldErrors.amount} hint="Positif = entrée d'argent inexpliquée ; négatif = sortie.">
          <TextInput type="number" step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        <Field label="Motif" required error={fieldErrors.reason}><TextInput value={reason} maxLength={300} onChange={(e) => setReason(e.target.value)} /></Field>
        <Field label="Référence"><TextInput value={reference} maxLength={100} onChange={(e) => setReference(e.target.value)} /></Field>
        <Field label="À résoudre avant le"><TextInput type="date" min={todayIso()} value={due} onChange={(e) => setDue(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function ResolveSuspenseDialog({ item, onClose, onDone }: { item: SuspenseItem; onClose: () => void; onDone: () => void }) {
  const [outcome, setOutcome] = useState("resolved");
  const [note, setNote] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit();

  async function save() {
    if (note.trim().length < 3) return fail("note", "Expliquez comment le suspens est réglé.");
    const ok = await submit(async () => { await resolveSuspense(item.id, outcome, note.trim()); return true; });
    if (ok) onDone();
  }

  return (
    <Modal title="Résoudre le suspens" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => void save()}>Enregistrer</Button></>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div>{SUSPENSE_KIND[item.kind] ?? item.kind} — {money(item.amount, item.currency)} — {item.reason}</div>
        <Field label="Issue">
          <Select value={outcome} onChange={(e) => setOutcome(e.target.value)}>
            <option value="resolved">Résolu (opération identifiée et enregistrée)</option>
            <option value="written_off">Passé en perte / profit</option>
            <option value="cancelled">Annulé (ouvert par erreur)</option>
          </Select>
        </Field>
        <Field label="Note de résolution" required error={fieldErrors.note}>
          <textarea className="nx-textarea" rows={3} value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
      </div>
    </Modal>
  );
}

function ClosePanel({ periods, onChanged }: { periods: AccountingPeriod[]; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canClose = hasPermission("finance.close_period");
  const [periodId, setPeriodId] = useState(periods[periods.length - 1]?.id ?? "");
  const [report, setReport] = useState<FinancialCloseReport | null>(null);
  const [forcing, setForcing] = useState(false);
  const { busy, error, submit, setError } = useSubmit();

  const loadReport = useCallback((id: string) => {
    if (!id) return;
    previewFinancialClose(id).then(setReport).catch((e) => setError(errorMessage(e, "Contrôles indisponibles")));
  }, [setError]);
  useEffect(() => { if (!periodId && periods.length) setPeriodId(periods[periods.length - 1].id); }, [periods, periodId]);
  useEffect(() => loadReport(periodId), [periodId, loadReport]);

  async function close(force: boolean, note?: string) {
    const ok = await submit(async () => { await completeFinancialClose(periodId, force, note); return true; });
    if (ok) { toast.success(force ? "Clôture forcée enregistrée (motif tracé)." : "Clôture financière enregistrée."); loadReport(periodId); onChanged(); }
  }

  return (
    <Card>
      <div style={{ display: "grid", gap: 12 }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ maxWidth: 260 }}>
          <Field label="Période">
            <Select value={periodId} onChange={(e) => setPeriodId(e.target.value)}>
              {periods.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
            </Select>
          </Field>
        </div>
        {report && (
          <>
            <DataTable
              columns={[
                { key: "l", header: "Contrôle", render: (c: FinancialCloseReport["controls"][number]) => <>{c.label} {c.blocking && <Badge>bloquant</Badge>}</> },
                { key: "s", header: "État", render: (c) => <Badge tone={c.status === "pass" ? "success" : c.status === "warn" ? "warning" : "error"}>{c.status === "pass" ? "OK" : c.status === "warn" ? "À vérifier" : "Échec"}</Badge> },
                { key: "d", header: "Détail", render: (c) => c.detail ?? "—" },
              ] as Array<Column<FinancialCloseReport["controls"][number]>>}
              rows={report.controls} rowKey={(c) => c.key} empty="—" />
            <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
              {report.status === "completed"
                ? <Badge tone="success">Clôturée au {dateShort(report.asOf)}</Badge>
                : canClose && (
                  <>
                    <Button disabled={busy || !report.canClose} onClick={() => void close(false)}>Clôturer au {dateShort(report.asOf)}</Button>
                    {!report.canClose && <Button variant="danger" disabled={busy} onClick={() => setForcing(true)}>Forcer la clôture…</Button>}
                  </>
                )}
            </div>
          </>
        )}
      </div>
      {forcing && (
        <ConfirmDialog title="Forcer la clôture financière" danger requireReason confirmLabel="Forcer la clôture"
          message="Des contrôles bloquants ne sont pas satisfaits. La clôture forcée est tracée avec son motif et son auteur."
          onCancel={() => setForcing(false)} onConfirm={(reason) => { setForcing(false); void close(true, reason); }} />
      )}
    </Card>
  );
}
