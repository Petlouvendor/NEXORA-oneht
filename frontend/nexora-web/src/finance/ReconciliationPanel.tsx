import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money, todayIso } from "@/shared/format";
import { Badge, Button, Card, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  autoMatch, importStatement, lineCandidates, listBankStatements, listManagedAccounts, matchLine, parseStatementCsv,
  statementLines, unmatchLine, validateStatement, type BankStatementSummary, type ManagedAccount, type StatementImport, type StatementLine,
} from "@/finance/treasuryApi";

const STATUS: Record<string, string> = { imported: "Importé", reconciling: "En cours", reconciled: "Rapproché" };
const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Rapprochement bancaire : import du relevé, rapprochement automatique puis manuel, validation quand tout concorde. */
export function ReconciliationPanel() {
  const { hasPermission } = useAuth();
  const statements = useAsync(listBankStatements, []);
  const accounts = useAsync(listManagedAccounts, []);
  const [importing, setImporting] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);
  const accountName = (id: string) => accounts.data?.find((a) => a.id === id)?.name ?? "—";
  const currencyOf = (id: string) => accounts.data?.find((a) => a.id === id)?.currency ?? "HTG";
  const opened = (statements.data ?? []).find((s) => s.id === openId) ?? null;

  if (!hasPermission("integrations.bank"))
    return <Card>Le rapprochement bancaire exige la permission « integrations.bank ».</Card>;

  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setImporting(true)}>Importer un relevé</Button>}>Relevés bancaires</SectionLabel>
      {statements.error && <div role="alert" className="nx-error-text">{statements.error}</div>}
      <DataTable exportTitle="Relevés bancaires"
        columns={[
          { key: "n", header: "Relevé", render: (s) => <button type="button" style={linkStyle} onClick={() => setOpenId(s.id)}>{s.statementNumber ?? "—"}</button> },
          { key: "d", header: "Date", render: (s) => dateShort(s.statementDate) },
          { key: "a", header: "Compte", render: (s) => accountName(s.financialAccountId) },
          { key: "o", header: "Ouverture", numeric: true, render: (s) => money(s.openingBalance, currencyOf(s.financialAccountId)) },
          { key: "c", header: "Clôture", numeric: true, render: (s) => money(s.closingBalance, currencyOf(s.financialAccountId)) },
          { key: "m", header: "Rapprochées", numeric: true, render: (s) => `${s.matchedCount} / ${s.lineCount}` },
          { key: "s", header: "Statut", render: (s) => <Badge tone={s.status === "reconciled" ? "success" : "warning"}>{STATUS[s.status] ?? s.status}</Badge> },
        ]}
        rows={statements.data ?? []} rowKey={(s) => s.id} empty={statements.loading ? "Chargement…" : "Aucun relevé importé."} />
      {importing && <ImportDialog accounts={(accounts.data ?? []).filter((a) => a.status === "active" && a.accountType !== "cash")}
                                  onClose={() => setImporting(false)} onDone={(id) => { setImporting(false); statements.reload(); setOpenId(id); }} />}
      {opened && <StatementDrawer key={opened.id} statement={opened} currency={currencyOf(opened.financialAccountId)}
                                  accountName={accountName(opened.financialAccountId)} onClose={() => setOpenId(null)} onChanged={statements.reload} />}
    </Card>
  );
}

function ImportDialog({ accounts, onClose, onDone }: { accounts: ManagedAccount[]; onClose: () => void; onDone: (id: string) => void }) {
  const toast = useToast();
  const [account, setAccount] = useState("");
  const [number, setNumber] = useState("");
  const [date, setDate] = useState(todayIso());
  const [opening, setOpening] = useState("");
  const [closing, setClosing] = useState("");
  const [parsed, setParsed] = useState<{ lines: StatementImport["lines"]; errors: string[] } | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "integrations.statement_unbalanced": "closing", "integrations.statement_empty": "file" });
  const sum = Math.round((parsed?.lines ?? []).reduce((a, l) => a + l.amount, 0) * 100) / 100;
  const expectedClosing = (num(opening) ?? 0) + sum;

  async function readFile(file: File | undefined) {
    if (!file) return;
    setParsed(parseStatementCsv(await file.text()));
  }

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const o = num(opening), c = num(closing);
    if (!account) return fail("account", "Choisissez le compte bancaire.");
    if (!number.trim()) return fail("number", "N° du relevé obligatoire.");
    if (!parsed || parsed.lines.length === 0) return fail("file", "Chargez un fichier de relevé lisible.");
    if (o == null || c == null) return fail("closing", "Soldes d'ouverture et de clôture obligatoires.");
    if (Math.abs(o + sum - c) > 0.01) return fail("closing", `Incohérent : ${o} + ${sum} = ${Math.round((o + sum) * 100) / 100}, pas ${c}.`);
    const r = await submit(() => importStatement({ financialAccountId: account, statementNumber: number.trim(), statementDate: date,
      openingBalance: o, closingBalance: c, lines: parsed.lines }));
    if (r) {
      toast.success(`${r.linesImported} ligne(s) importée(s), ${r.autoMatched} rapprochée(s) automatiquement${r.duplicatesSkipped ? `, ${r.duplicatesSkipped} doublon(s) ignoré(s)` : ""}.`);
      onDone(r.statementId);
    }
  }

  return (
    <Modal title="Importer un relevé bancaire" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Importer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(560px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 10 }}>
          <Field label="Compte" required error={fieldErrors.account}>
            <Select value={account} onChange={(e) => setAccount(e.target.value)}>
              <option value="">—</option>
              {accounts.map((a) => <option key={a.id} value={a.id}>{a.name} ({a.currency})</option>)}
            </Select>
          </Field>
          <Field label="N° du relevé" required error={fieldErrors.number}><TextInput value={number} maxLength={60} onChange={(e) => setNumber(e.target.value)} /></Field>
          <Field label="Date du relevé" required><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
          <Field label="Solde d'ouverture" required><TextInput type="number" step="0.01" value={opening} onChange={(e) => setOpening(e.target.value)} /></Field>
          <Field label="Solde de clôture" required error={fieldErrors.closing}><TextInput type="number" step="0.01" value={closing} onChange={(e) => setClosing(e.target.value)} /></Field>
        </div>
        <Field label="Fichier du relevé (CSV)" error={fieldErrors.file}
               hint="Colonnes : date ; montant (signé) ou débit / crédit ; libellé ; référence. Export CSV de la banque.">
          <input type="file" accept=".csv,.txt,text/csv" onChange={(e) => void readFile(e.target.files?.[0])} />
        </Field>
        {parsed && (
          <div style={{ display: "grid", gap: 4 }}>
            <div>{parsed.lines.length} ligne(s) lue(s), total des mouvements <b>{money(sum, "")}</b>
              {num(opening) != null && <> — clôture attendue <b>{money(expectedClosing, "")}</b></>}</div>
            {parsed.errors.slice(0, 5).map((m) => <div key={m} className="nx-err">{m}</div>)}
            {parsed.errors.length > 5 && <div className="nx-err">… et {parsed.errors.length - 5} autre(s) ligne(s) rejetée(s).</div>}
          </div>
        )}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function StatementDrawer({ statement, currency, accountName, onClose, onChanged }: {
  statement: BankStatementSummary; currency: string; accountName: string; onClose: () => void; onChanged: () => void;
}) {
  const toast = useToast();
  const lines = useAsync(() => statementLines(statement.id), [statement.id]);
  const [matching, setMatching] = useState<StatementLine | null>(null);
  const { busy, error, submit } = useSubmit();
  const refresh = () => { lines.reload(); onChanged(); };
  const locked = statement.status === "reconciled";
  const unmatched = (lines.data ?? []).filter((l) => l.status === "unmatched").length;

  async function act(fn: () => Promise<unknown>, done: string) {
    const ok = await submit(async () => { await fn(); return true; });
    if (ok) { toast.success(done); refresh(); }
  }

  return (
    <Drawer title={`Relevé ${statement.statementNumber ?? ""}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div>{accountName} · {dateShort(statement.statementDate)} · ouverture {money(statement.openingBalance, currency)} → clôture {money(statement.closingBalance, currency)}</div>
        {!locked && (
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <Button size="sm" variant="subtle" disabled={busy} onClick={() => act(() => autoMatch(statement.id), "Rapprochement automatique effectué.")}>Rapprocher automatiquement</Button>
            <Button size="sm" disabled={busy || unmatched > 0} onClick={() => act(() => validateStatement(statement.id), "Relevé validé.")}>
              Valider le relevé
            </Button>
          </div>
        )}
        {!locked && unmatched > 0 && (
          <div style={{ fontSize: 12, color: "var(--nx-warning)" }}>
            {unmatched} ligne(s) sans mouvement correspondant : enregistrez l'opération manquante (frais bancaires, transfert…) dans « Comptes », puis rapprochez-la.
          </div>
        )}
        {locked && <Badge tone="success">Relevé rapproché et validé</Badge>}
        <DataTable exportTitle={`Relevé ${statement.statementNumber ?? ""}`}
          columns={[
            { key: "d", header: "Date", render: (l) => dateShort(l.valueDate) },
            { key: "l", header: "Libellé", render: (l) => <>{l.description ?? "—"}{l.reference ? <span style={{ color: "var(--nx-text-2)" }}> · {l.reference}</span> : null}</> },
            { key: "a", header: "Montant", numeric: true, render: (l) => money(l.amount, currency) },
            { key: "s", header: "État", render: (l) => <Badge tone={l.status === "matched" ? "success" : "warning"}>{l.status === "matched" ? "rapprochée" : "à rapprocher"}</Badge> },
            { key: "x", header: "", render: (l) => locked ? null : l.status === "matched"
              ? <Button size="sm" variant="ghost" disabled={busy} onClick={() => act(() => unmatchLine(l.id), "Rapprochement défait.")}>Défaire</Button>
              : <Button size="sm" variant="subtle" onClick={() => setMatching(l)}>Rapprocher…</Button> },
          ]}
          rows={lines.data ?? []} rowKey={(l) => l.id} empty={lines.loading ? "Chargement…" : "Aucune ligne."} />
      </div>
      {matching && <MatchDialog line={matching} currency={currency} onClose={() => setMatching(null)} onDone={() => { setMatching(null); refresh(); }} />}
    </Drawer>
  );
}

function MatchDialog({ line, currency, onClose, onDone }: { line: StatementLine; currency: string; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const candidates = useAsync(() => lineCandidates(line.id), [line.id]);
  const { busy, error, submit } = useSubmit();

  async function pick(transactionId: string) {
    const ok = await submit(async () => { await matchLine(line.id, transactionId); return true; });
    if (ok) { toast.success("Ligne rapprochée."); onDone(); }
  }

  return (
    <Modal title={`Rapprocher ${money(line.amount, currency)} du ${dateShort(line.valueDate)}`} onClose={onClose}
      footer={<Button variant="ghost" onClick={onClose}>Fermer</Button>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(560px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {candidates.error && <div role="alert" className="nx-error-text">{candidates.error}</div>}
        <div style={{ color: "var(--nx-text-2)" }}>Mouvements du même compte, du même montant, pas encore rapprochés :</div>
        <DataTable
          columns={[
            { key: "d", header: "Date", render: (c) => dateTime(c.occurredAt) },
            { key: "l", header: "Opération", render: (c) => c.label ?? c.transactionType },
            { key: "a", header: "Montant", numeric: true, render: (c) => money(c.amount, currency) },
            { key: "x", header: "", render: (c) => <Button size="sm" disabled={busy} onClick={() => void pick(c.transactionId)}>Choisir</Button> },
          ]}
          rows={candidates.data ?? []} rowKey={(c) => c.transactionId}
          empty={candidates.loading ? "Recherche…" : "Aucun mouvement correspondant : enregistrez d'abord l'opération (frais, transfert…)."} />
      </div>
    </Modal>
  );
}
