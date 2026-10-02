import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { money, todayIso } from "@/shared/format";
import { Card, KpiCard, Tabs } from "@/shared/ui";
import {
  getTrialBalance, listAccountingCurrencies, listChartAccounts, listJournals, listPeriods, previewCloseYear, searchEntries,
} from "@/accounting/accountingApi";
import { EntriesPanel } from "@/accounting/EntriesPanel";
import { ChartPanel } from "@/accounting/ChartPanel";
import { GeneralLedgerPanel, StatementsPanel, TrialBalancePanel } from "@/accounting/ReportsPanels";
import { PeriodsPanel } from "@/accounting/PeriodsPanel";

const TABS = [
  { id: "entries", label: "Écritures" },
  { id: "chart", label: "Plan comptable" },
  { id: "ledger", label: "Grand livre" },
  { id: "balance", label: "Balance" },
  { id: "statements", label: "États financiers" },
  { id: "periods", label: "Périodes & clôture" },
];

/** Comptabilité générale : écritures, plan comptable, grand livre, balance, états financiers, périodes et clôture. */
export function LedgerPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("entries");
  const [version, setVersion] = useState(0);
  const changed = () => setVersion((v) => v + 1);
  const journals = useAsync(listJournals, []);
  const accounts = useAsync(listChartAccounts, [version]);
  const periods = useAsync(listPeriods, [version]);
  const currencies = useAsync(listAccountingCurrencies, []);
  const drafts = useAsync(() => searchEntries({ status: "draft", limit: 500 }), [version]);
  const trial = useAsync(() => getTrialBalance(todayIso()), [version]);
  const openPeriod = (periods.data ?? []).find((p) => p.status === "open");
  const close = useAsync(() => (openPeriod ? previewCloseYear(openPeriod.id) : Promise.resolve(null)), [openPeriod?.id, version]);

  if (!hasPermission("accounting.view")) {
    return <Card>La comptabilité n'est pas accessible avec vos permissions.</Card>;
  }

  const fc = currencies.data?.find((c) => c.isCompanyDefault)?.code ?? trial.data?.currency ?? "HTG";
  const t = trial.data;
  const balanced = !!t && Math.round(t.totalDebit * 100) === Math.round(t.totalCredit * 100);
  const c = close.data;
  const draftCount = drafts.data?.length ?? 0;

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Comptabilité générale</h1>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        <KpiCard label={`Résultat ${openPeriod?.name ?? "de l'exercice"}`} value={c ? money(c.netResult, c.currency) : "—"} tone={c && c.netResult < 0 ? "warning" : undefined} />
        <KpiCard label="Produits" value={c ? money(c.revenue, c.currency) : "—"} />
        <KpiCard label="Charges" value={c ? money(c.expenses, c.currency) : "—"} />
        <KpiCard label="Balance du jour" value={t ? (balanced ? "Équilibrée" : "Déséquilibrée") : "—"} tone={t && !balanced ? "warning" : undefined} />
        <KpiCard label="Brouillons à valider" value={String(draftCount)} tone={draftCount ? "warning" : undefined} />
      </div>
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      {tab === "entries" && <EntriesPanel journals={journals.data ?? []} accounts={accounts.data ?? []} onChanged={changed} />}
      {tab === "chart" && <ChartPanel accounts={accounts.data ?? []} loading={accounts.loading} error={accounts.error}
                                       functionalCurrency={fc} onChanged={changed} />}
      {tab === "ledger" && <GeneralLedgerPanel key={version} accounts={accounts.data ?? []} functionalCurrency={fc} />}
      {tab === "balance" && <TrialBalancePanel key={version} />}
      {tab === "statements" && <StatementsPanel key={version} />}
      {tab === "periods" && <PeriodsPanel periods={periods.data ?? []} onChanged={changed} />}
    </div>
  );
}
