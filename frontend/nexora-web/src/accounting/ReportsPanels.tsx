import { useState } from "react";
import { useAsync } from "@/shared/useAsync";
import { dateShort, money, todayIso } from "@/shared/format";
import { Badge, Card, DataTable, Field, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  ACCOUNT_TYPES, getFinancialStatements, getGeneralLedger, getTrialBalance,
  type ChartAccount, type LedgerLine, type StatementSection,
} from "@/accounting/accountingApi";
import { yearToDate } from "@/accounting/entryDraft";

const row = { display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end", marginBottom: 10 } as const;
const note = { fontSize: 12, color: "var(--nx-text-2)" } as const;

/** Grand livre d'un compte : solde d'ouverture, mouvements validés avec solde progressif, solde de clôture. */
export function GeneralLedgerPanel({ accounts, functionalCurrency }: { accounts: ChartAccount[]; functionalCurrency: string }) {
  const ytd = yearToDate(todayIso());
  const postable = accounts.filter((a) => a.isPostable);
  const [account, setAccount] = useState("");
  const [from, setFrom] = useState(ytd.from);
  const [to, setTo] = useState(ytd.to);
  const ledger = useAsync(() => (account && from && to ? getGeneralLedger(account, from, to) : Promise.resolve(null)), [account, from, to]);
  const g = ledger.data;
  const opening: LedgerLine | null = g ? { date: g.from, entryNumber: "", journal: "", description: "Solde d'ouverture", debit: 0, credit: 0, runningBalance: g.openingBalance } : null;

  return (
    <Card>
      <SectionLabel>Grand livre</SectionLabel>
      <div style={row}>
        <Field label="Compte">
          <Select value={account} onChange={(e) => setAccount(e.target.value)}>
            <option value="">— Choisir un compte —</option>
            {postable.map((a) => <option key={a.id} value={a.code}>{a.code} {a.name}</option>)}
          </Select>
        </Field>
        <Field label="Du"><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field label="Au"><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
      </div>
      {from > to && <div className="nx-error-text">La date de début est postérieure à la date de fin.</div>}
      {ledger.error && <div role="alert" className="nx-error-text">{ledger.error}</div>}
      {!account && <div style={note}>Choisissez un compte pour afficher ses mouvements.</div>}
      {g && opening && (
        <DataTable exportTitle={`Grand livre ${g.accountCode} ${g.accountName}`}
          columns={[
            { key: "d", header: "Date", render: (l) => dateShort(l.date) },
            { key: "j", header: "Journal", render: (l) => l.journal },
            { key: "n", header: "N°", render: (l) => l.entryNumber },
            { key: "l", header: "Libellé", render: (l) => l.description ?? "" },
            { key: "de", header: "Débit", numeric: true, render: (l) => (l.debit ? money(l.debit, functionalCurrency) : "") },
            { key: "c", header: "Crédit", numeric: true, render: (l) => (l.credit ? money(l.credit, functionalCurrency) : "") },
            { key: "s", header: "Solde", numeric: true, render: (l) => money(l.runningBalance, functionalCurrency) },
          ]}
          rows={[opening, ...g.lines]} rowKey={(l) => `${l.entryNumber}-${l.date}-${l.runningBalance}-${l.debit}-${l.credit}`}
          footer={<><td colSpan={4}><strong>Solde au {dateShort(g.to)}</strong></td>
            <td className="nx-num"><strong>{money(g.lines.reduce((s, l) => s + l.debit, 0), functionalCurrency)}</strong></td>
            <td className="nx-num"><strong>{money(g.lines.reduce((s, l) => s + l.credit, 0), functionalCurrency)}</strong></td>
            <td className="nx-num"><strong>{money(g.closingBalance, functionalCurrency)}</strong></td></>} />
      )}
    </Card>
  );
}

/** Balance générale à une date : contrôle débit = crédit. */
export function TrialBalancePanel() {
  const [asOf, setAsOf] = useState(todayIso());
  const trial = useAsync(() => (asOf ? getTrialBalance(asOf) : Promise.resolve(null)), [asOf]);
  const t = trial.data;
  const balanced = !!t && Math.round(t.totalDebit * 100) === Math.round(t.totalCredit * 100);
  return (
    <Card>
      <SectionLabel>Balance générale</SectionLabel>
      <div style={row}>
        <Field label="Au"><TextInput type="date" value={asOf} onChange={(e) => setAsOf(e.target.value)} /></Field>
        {t && <Badge tone={balanced ? "success" : "error"}>{balanced ? "Équilibrée" : "Déséquilibrée"}</Badge>}
      </div>
      {trial.error && <div role="alert" className="nx-error-text">{trial.error}</div>}
      <DataTable exportTitle={`Balance au ${asOf}`}
        columns={[
          { key: "c", header: "Compte", render: (r) => `${r.accountCode} ${r.accountName}` },
          { key: "t", header: "Type", render: (r) => ACCOUNT_TYPES[r.accountType] ?? r.accountType },
          { key: "d", header: "Débit", numeric: true, render: (r) => money(r.debit, t?.currency) },
          { key: "cr", header: "Crédit", numeric: true, render: (r) => money(r.credit, t?.currency) },
        ]}
        rows={t?.rows ?? []} rowKey={(r) => r.accountCode}
        empty={trial.loading ? "Chargement…" : "Aucun mouvement validé à cette date."}
        footer={t && <><td colSpan={2}><strong>Totaux</strong></td>
          <td className="nx-num"><strong>{money(t.totalDebit, t.currency)}</strong></td>
          <td className="nx-num"><strong>{money(t.totalCredit, t.currency)}</strong></td></>} />
    </Card>
  );
}

function Section({ s, currency, title }: { s: StatementSection; currency: string; title: string }) {
  return (
    <DataTable exportTitle={title}
      columns={[
        { key: "l", header: s.title, render: (l) => l.label },
        { key: "a", header: "Montant", numeric: true, render: (l) => money(l.amount, currency) },
      ]}
      rows={s.lines} rowKey={(l) => l.label} empty="—"
      footer={<><td><strong>Total {s.title.toLowerCase()}</strong></td><td className="nx-num"><strong>{money(s.subtotal, currency)}</strong></td></>} />
  );
}

/** Compte de résultat (période) et bilan (à la date de fin). */
export function StatementsPanel() {
  const ytd = yearToDate(todayIso());
  const [from, setFrom] = useState(ytd.from);
  const [to, setTo] = useState(ytd.to);
  const st = useAsync(() => (from && to && from <= to ? getFinancialStatements(from, to) : Promise.resolve(null)), [from, to]);
  const is = st.data?.incomeStatement;
  const bs = st.data?.balanceSheet;
  return (
    <div style={{ display: "grid", gap: 12 }}>
      <Card>
        <div style={row}>
          <Field label="Du"><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
          <Field label="Au"><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
        </div>
        {from > to && <div className="nx-error-text">La date de début est postérieure à la date de fin.</div>}
        {st.error && <div role="alert" className="nx-error-text">{st.error}</div>}
        <div style={note}>Établis à partir des seules écritures validées, en {is?.currency ?? "devise de l'entreprise"}.</div>
      </Card>
      {is && (
        <Card>
          <SectionLabel>Compte de résultat — du {dateShort(is.from)} au {dateShort(is.to)}</SectionLabel>
          <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(300px, 1fr))" }}>
            <Section s={is.revenue} currency={is.currency} title="Compte de résultat — produits" />
            <Section s={is.expenses} currency={is.currency} title="Compte de résultat — charges" />
          </div>
          <div style={{ marginTop: 10, fontSize: 18 }}>
            Résultat net : <strong style={{ color: is.netResult < 0 ? "var(--nx-neg)" : undefined }}>{money(is.netResult, is.currency)}</strong>
            {" "}{is.netResult >= 0 ? "(bénéfice)" : "(perte)"}
          </div>
        </Card>
      )}
      {bs && (
        <Card>
          <SectionLabel action={<Badge tone={bs.isBalanced ? "success" : "error"}>{bs.isBalanced ? "Actif = Passif" : "Bilan déséquilibré"}</Badge>}>
            Bilan au {dateShort(bs.asOf)}
          </SectionLabel>
          <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(300px, 1fr))" }}>
            <Section s={bs.assets} currency={bs.currency} title="Bilan — actif" />
            <div style={{ display: "grid", gap: 12 }}>
              <Section s={bs.liabilities} currency={bs.currency} title="Bilan — passif" />
              <Section s={bs.equity} currency={bs.currency} title="Bilan — capitaux propres" />
              <div>Résultat de l'exercice : <strong>{money(bs.result, bs.currency)}</strong></div>
              <div><strong>Total passif : {money(bs.liabilities.subtotal + bs.equity.subtotal + bs.result, bs.currency)}</strong></div>
            </div>
          </div>
        </Card>
      )}
    </div>
  );
}
