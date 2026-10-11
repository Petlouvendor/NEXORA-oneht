import { useCallback, useEffect, useState } from "react";
import { ExportMenu } from "@/shared/ExportMenu";
import { Link } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Badge, Button, ConfirmDialog, DataTable, EmptyState, Field, Select, Spinner, TextInput, type Column } from "@/shared/ui";
import { dateShort, money, todayIso } from "@/shared/format";
import {
  approveExpense, approveRefund, EXPENSE_CATEGORIES, exportCustomerReport, listCustomerReports, listPendingExpenses,
  listPendingRefunds, rejectExpense, rejectRefund, runCustomerReport,
  type CustomerExpense, type CustomerRefund, type CustomerReport, type ReportDefinition,
} from "@/customers/customersApi";
import { errorText } from "@/customers/CustomerDialogs";

/**
 * Validations en attente (module clients §10, §11, §19) : dépenses imputées et remboursements d'avoir
 * de toute l'entreprise. Le serveur refuse qu'un utilisateur valide sa propre saisie.
 */
export function CustomerApprovalsPanel() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [expenses, setExpenses] = useState<CustomerExpense[] | null>(null);
  const [refunds, setRefunds] = useState<CustomerRefund[]>([]);
  const [rejecting, setRejecting] = useState<{ kind: "expense" | "refund"; id: string; label: string } | null>(null);
  const canExpense = hasPermission("customers.approve_expense");
  const canRefund = hasPermission("credit.refund");

  const load = useCallback(() => {
    listPendingExpenses().then(setExpenses).catch(() => setExpenses([]));
    listPendingRefunds().then(setRefunds).catch(() => setRefunds([]));
  }, []);
  useEffect(load, [load]);
  const act = (p: Promise<unknown>, ok: string) => p.then(() => { toast.success(ok); load(); }).catch((e) => toast.error(errorText(e)));

  if (!expenses) return <Spinner />;
  const expenseCols: Array<Column<CustomerExpense>> = [
    { key: "n", header: "Dépense", render: (e) => e.expenseNumber },
    { key: "c", header: "Client", render: (e) => <Link to={`/customers/${e.customerId}?tab=expenses`}>{e.customerName}</Link> },
    { key: "k", header: "Catégorie", render: (e) => EXPENSE_CATEGORIES[e.category] ?? e.category },
    { key: "d", header: "Description", render: (e) => e.description },
    { key: "a", header: "Montant", numeric: true, render: (e) => money(e.amount, e.currency) },
    { key: "b", header: "Refacturée", render: (e) => (e.billable ? "oui" : "non") },
    { key: "u", header: "Saisie par", render: (e) => `${e.createdByName ?? "—"} · ${dateShort(e.createdAt)}` },
    { key: "x", header: "", render: (e) => canExpense ? (
      <div style={{ display: "flex", gap: 4 }}>
        <Button size="sm" onClick={() => void act(approveExpense(e.id), `${e.expenseNumber} validée.`)}>Valider</Button>
        <Button size="sm" variant="ghost" onClick={() => setRejecting({ kind: "expense", id: e.id, label: e.expenseNumber })}>Rejeter</Button>
      </div>
    ) : null },
  ];
  const refundCols: Array<Column<CustomerRefund>> = [
    { key: "n", header: "Remboursement", render: (r) => r.refundNumber },
    { key: "c", header: "Client", render: (r) => <Link to={`/customers/${r.customerId}?tab=returns`}>{r.customerName}</Link> },
    { key: "m", header: "Avoir", render: (r) => r.memoNumber },
    { key: "a", header: "Montant", numeric: true, render: (r) => money(r.amount, r.currency) },
    { key: "r", header: "Motif", render: (r) => r.reason },
    { key: "u", header: "Demandé par", render: (r) => `${r.requestedByName ?? "—"} · ${dateShort(r.requestedAt)}` },
    { key: "x", header: "", render: (r) => canRefund ? (
      <div style={{ display: "flex", gap: 4 }}>
        <Button size="sm" onClick={() => void act(approveRefund(r.id), `${r.refundNumber} approuvé.`)}>Approuver</Button>
        <Button size="sm" variant="ghost" onClick={() => setRejecting({ kind: "refund", id: r.id, label: r.refundNumber })}>Refuser</Button>
      </div>
    ) : null },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div>
        <h3 style={{ margin: "0 0 8px" }}>Dépenses imputées à valider <Badge tone={expenses.length ? "warning" : undefined}>{expenses.length}</Badge></h3>
        <DataTable columns={expenseCols} rows={expenses} rowKey={(e) => e.id} empty="Aucune dépense en attente." />
      </div>
      <div>
        <h3 style={{ margin: "0 0 8px" }}>Remboursements d'avoir à approuver <Badge tone={refunds.length ? "warning" : undefined}>{refunds.length}</Badge></h3>
        <DataTable columns={refundCols} rows={refunds} rowKey={(r) => r.id} empty="Aucun remboursement en attente." />
      </div>
      {rejecting && (
        <ConfirmDialog title={`Refuser ${rejecting.label}`} message="Le demandeur est notifié du motif." requireReason confirmLabel="Refuser"
                       onCancel={() => setRejecting(null)}
                       onConfirm={(note) => {
                         void act(rejecting.kind === "expense" ? rejectExpense(rejecting.id, note) : rejectRefund(rejecting.id, note), "Refus enregistré.");
                         setRejecting(null);
                       }} />
      )}
    </div>
  );
}

const iso = (d: Date) => todayIso(d);

/** Rapports clients (§26) : tableau calculé par le serveur, export CSV selon `customers.export`. */
export function CustomerReportsPanel() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [catalog, setCatalog] = useState<ReportDefinition[]>([]);
  const [key, setKey] = useState("balances");
  const [from, setFrom] = useState(() => iso(new Date(Date.now() - 30 * 86400000)));
  const [to, setTo] = useState(() => iso(new Date()));
  const [report, setReport] = useState<CustomerReport | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => { listCustomerReports().then(setCatalog).catch(() => setCatalog([])); }, []);
  const run = useCallback(() => {
    setBusy(true);
    runCustomerReport(key, from, to).then(setReport).catch((e) => toast.error(errorText(e))).finally(() => setBusy(false));
  }, [key, from, to, toast]);
  useEffect(run, [run]);

  const def = catalog.find((c) => c.key === key);
  return (
    <div style={{ display: "grid", gap: 12 }}>
      <div style={{ display: "flex", gap: 10, flexWrap: "wrap", alignItems: "end" }}>
        <Field label="Rapport">
          <Select value={key} onChange={(e) => setKey(e.target.value)}>
            {catalog.map((c) => <option key={c.key} value={c.key}>{c.title}</option>)}
          </Select>
        </Field>
        <Field label="Du"><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field label="Au"><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
        {hasPermission("customers.export") && (
          <ExportMenu label="Exporter" run={(format) => exportCustomerReport(key, from, to, format)} />
        )}
      </div>
      {def && <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>{def.description}{report && !report.from ? " — situation à date, période ignorée." : ""}</div>}
      {busy && !report ? <Spinner /> : !report ? <EmptyState title="Aucun résultat" /> : (
        <>
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr>{report.columns.map((c) => <th key={c}>{c}</th>)}</tr></thead>
              <tbody>
                {report.rows.length === 0
                  ? <tr><td colSpan={report.columns.length} style={{ textAlign: "center", color: "var(--nx-text-2)", padding: 20 }}>Rien à afficher.</td></tr>
                  : report.rows.map((r, i) => <tr key={i}>{r.map((cell, j) => <td key={j}>{cell ?? ""}</td>)}</tr>)}
              </tbody>
            </table>
          </div>
          {Object.keys(report.totals).length > 0 && (
            <div style={{ display: "flex", gap: 16, flexWrap: "wrap", fontSize: "var(--nx-fs-sm)" }}>
              {Object.entries(report.totals).map(([k, v]) => <span key={k}>{k} : <b className="nx-numeric">{v.toLocaleString("fr-FR", { minimumFractionDigits: 2 })}</b></span>)}
            </div>
          )}
        </>
      )}
    </div>
  );
}
