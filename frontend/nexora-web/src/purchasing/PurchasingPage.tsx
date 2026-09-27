import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, KpiCard, Money, SectionLabel, Select, StatePill, Tabs,
  type Column,
} from "@/shared/ui";
import {
  approvePurchaseOrder, listPayables, listPurchaseOrders, listSuppliers, matchInvoice,
  sendPurchaseOrder,
  type Payable, type PurchaseOrderSummary, type SupplierSummary, type ThreeWayMatchReport,
} from "@/purchasing/purchasingApi";
import { SupplierDocumentsDialog } from "@/purchasing/SupplierDocuments";
import { PayPayableDialog } from "@/purchasing/PayPayableDialog";
import { PrintMenu } from "@/shared/ExportMenu";
import { money } from "@/shared/format";

/** Achats — fournisseurs, commandes, rapprochement 3-way & dettes (Phase 26 §9-20, §24-27, §36). */
export function PurchasingPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("payables");
  const [suppliers, setSuppliers] = useState<SupplierSummary[]>([]);
  const [orders, setOrders] = useState<PurchaseOrderSummary[]>([]);
  const [payables, setPayables] = useState<Payable[]>([]);
  const [report, setReport] = useState<ThreeWayMatchReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  // Dernier paiement fournisseur enregistré : son reçu (reste à payer) est proposé à l'impression.
  const [lastPayment, setLastPayment] = useState<{ id: string; supplier: string; amount: number; currency: string } | null>(null);
  const [docsFor, setDocsFor] = useState<SupplierSummary | null>(null);
  const [paying, setPaying] = useState<Payable | null>(null);
  const [matchInvoiceId, setMatchInvoiceId] = useState("");

  const canPay = hasPermission("purchasing.pay");
  const canMatch = hasPermission("purchasing.match");
  const canApprove = hasPermission("purchasing.approve_order");
  const canViewPayables = hasPermission("purchasing.view_payables");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      listSuppliers().catch(() => []),
      listPurchaseOrders().catch(() => []),
      listPayables(true).catch(() => []),
    ])
      .then(([s, o, p]) => { setSuppliers(s); setOrders(o); setPayables(p); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, []);

  useEffect(load, [load]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  async function doMatch(invoiceId: string) {
    setBusy(true);
    setError(null);
    try { setReport(await matchInvoice(invoiceId)); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Rapprochement impossible"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("purchasing.view")) {
    return <Card>Le module Achats n'est pas accessible avec vos permissions.</Card>;
  }

  const overdue = payables.filter((p) => p.daysOverdue > 0);
  const invoices = payables.filter((p) => p.supplierInvoiceId);
  const totalDue = payables.reduce((a, p) => a + p.remainingAmount, 0);
  const currency = payables[0]?.currency ?? suppliers[0]?.currency ?? "HTG";

  const supplierCols: Array<Column<SupplierSummary>> = [
    { key: "n", header: "N°", render: (s) => <code>{s.supplierNumber}</code> },
    { key: "name", header: "Fournisseur", render: (s) => s.name },
    { key: "s", header: "Statut", render: (s) => <StatePill status={s.status} /> },
    { key: "d", header: "Dettes ouvertes", numeric: true, render: (s) => <Money value={s.openPayables} currency={s.currency} /> },
    { key: "x", header: "", render: (s) => <Button size="sm" variant="ghost" onClick={() => setDocsFor(s)}>Pièces & relevé</Button> },
  ];

  const orderCols: Array<Column<PurchaseOrderSummary>> = [
    { key: "n", header: "N°", render: (o) => <code>{o.documentNumber}</code> },
    { key: "sup", header: "Fournisseur", render: (o) => o.supplierName },
    { key: "s", header: "Statut", render: (o) => <StatePill status={o.status} /> },
    { key: "r", header: "Reçu", numeric: true, render: (o) => `${Math.round(o.receivedRatio * 100)} %` },
    { key: "t", header: "Total", numeric: true, render: (o) => <Money value={o.total} currency={o.currency} /> },
    {
      key: "a", header: "", render: (o) => (
        <div style={{ display: "flex", gap: 4, alignItems: "center" }}>
          {canApprove && o.status === "submitted"
            && <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(() => approvePurchaseOrder(o.id))}>Approuver</Button>}
          {canApprove && o.status === "approved"
            && <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(() => sendPurchaseOrder(o.id))}>Envoyer</Button>}
          <PrintMenu kind="purchase-order" id={o.id} iconOnly label="Imprimer le bon de commande" />
        </div>
      ),
    },
  ];

  const payableCols: Array<Column<Payable>> = [
    { key: "sup", header: "Fournisseur", render: (p) => p.supplierName },
    { key: "inv", header: "Facture", render: (p) => <>{p.invoiceNumber ?? "—"}{p.supplierReference && <span style={{ color: "var(--nx-text-2)" }}> · {p.supplierReference}</span>}</> },
    { key: "due", header: "Échéance", render: (p) => p.dueDate ?? "—" },
    { key: "od", header: "Retard", numeric: true, render: (p) => (p.daysOverdue > 0 ? <Badge tone="error">{p.daysOverdue} j</Badge> : "—") },
    { key: "rem", header: "Solde", numeric: true, render: (p) => <Money value={p.remainingAmount} currency={p.currency} /> },
    { key: "st", header: "Statut", render: (p) => (p.paymentBlocked ? <Badge tone="error">bloqué</Badge> : <StatePill status={p.status} />) },
    {
      key: "a", header: "", render: (p) => canPay && !p.paymentBlocked && p.remainingAmount > 0 ? (
        <Button size="sm" disabled={busy} onClick={() => setPaying(p)}>Payer</Button>
      ) : p.paymentBlocked ? <span style={{ fontSize: 12, color: "var(--nx-neg)" }}>{p.blockReason}</span> : null,
    },
    { key: "p", header: "", render: (p) => (p.supplierInvoiceId && canViewPayables
      ? <PrintMenu kind="supplier-invoice" id={p.supplierInvoiceId} iconOnly label="Imprimer la facture" /> : null) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Achats & fournisseurs</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {lastPayment && (
        <Card style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap", background: "var(--nx-success-bg)", borderColor: "transparent" }}>
          <span>Paiement de <b>{money(lastPayment.amount, lastPayment.currency)}</b> à <b>{lastPayment.supplier}</b> enregistré.</span>
          <PrintMenu kind="supplier-payment" id={lastPayment.id} ticket label="Imprimer le reçu" />
          <Button size="sm" variant="ghost" onClick={() => setLastPayment(null)}>Masquer</Button>
        </Card>
      )}
      {paying && (
        <PayPayableDialog payable={paying} onClose={() => setPaying(null)}
                          onPaid={(paymentId, amount) => {
                            if (paymentId) setLastPayment({ id: paymentId, supplier: paying.supplierName, amount, currency: paying.currency });
                            setPaying(null);
                            load();
                          }} />
      )}
      {docsFor && (
        <SupplierDocumentsDialog supplierId={docsFor.id} supplierName={docsFor.name}
                                 canStatement={canViewPayables} onClose={() => setDocsFor(null)} />
      )}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        <KpiCard label="Dettes ouvertes" value={<Money value={totalDue} currency={currency} />} />
        <KpiCard label="Dettes en retard" value={String(overdue.length)} tone={overdue.length ? "warning" : undefined} tag={overdue.length ? "!" : undefined} />
        <KpiCard label="Commandes en cours" value={String(orders.filter((o) => !["received", "closed", "cancelled"].includes(o.status)).length)} />
        <KpiCard label="Fournisseurs" value={String(suppliers.length)} />
      </div>

      <Card>
        <Tabs
          tabs={[
            { id: "payables", label: `Dettes (${payables.length})` },
            { id: "orders", label: `Commandes (${orders.length})` },
            { id: "suppliers", label: `Fournisseurs (${suppliers.length})` },
          ]}
          active={tab}
          onChange={setTab}
        />
        <div style={{ marginTop: 12 }}>
          {tab === "payables" && <DataTable columns={payableCols} rows={payables} rowKey={(p) => p.id} empty="Aucune dette ouverte." />}
          {tab === "orders" && <DataTable columns={orderCols} rows={orders} rowKey={(o) => o.id} empty="Aucune commande." />}
          {tab === "suppliers" && <DataTable columns={supplierCols} rows={suppliers} rowKey={(s) => s.id} empty="Aucun fournisseur." />}
        </div>
      </Card>

      {canMatch && (
        <Card>
          <SectionLabel>Rapprochement 3-way (§20)</SectionLabel>
          <p style={{ fontSize: 13, color: "var(--nx-text-2)", marginTop: 4 }}>
            Choisir une facture fournisseur ouverte pour comparer
            <strong> commande ↕ réception ↕ facture</strong>. Un écart hors tolérance bloque le paiement.
          </p>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <Select value={matchInvoiceId} onChange={(e) => setMatchInvoiceId(e.target.value)} aria-label="Facture à rapprocher"
                    style={{ maxWidth: 420 }}>
              <option value="">{invoices.length ? "Facture à rapprocher…" : "Aucune facture ouverte"}</option>
              {invoices.map((p) => (
                <option key={p.supplierInvoiceId!} value={p.supplierInvoiceId!}>
                  {p.invoiceNumber ?? "—"} — {p.supplierName} — {money(p.originalAmount, p.currency)}{p.paymentBlocked ? " (bloquée)" : ""}
                </option>
              ))}
            </Select>
            <Button variant="subtle" disabled={busy || !matchInvoiceId} onClick={() => doMatch(matchInvoiceId)}>
              Lancer le rapprochement
            </Button>
          </div>
          {report && (
            <div style={{ marginTop: 12 }}>
              <div style={{ marginBottom: 8 }}>
                Facture {report.invoiceNumber} —{" "}
                {report.paymentBlocked
                  ? <Badge tone="error">écart · paiement bloqué</Badge>
                  : <Badge tone="success">rapprochée</Badge>}
              </div>
              <DataTable
                columns={[
                  { key: "d", header: "Article", render: (l) => l.description },
                  { key: "o", header: "Commandé", numeric: true, render: (l) => `${l.orderedQuantity} × ${l.orderedUnitPrice}` },
                  { key: "r", header: "Reçu", numeric: true, render: (l) => l.receivedQuantity },
                  { key: "i", header: "Facturé", numeric: true, render: (l) => `${l.invoicedQuantity} × ${l.invoicedUnitPrice}` },
                  { key: "vq", header: "Écart qté", numeric: true, render: (l) => `${l.quantityVariancePercent} %` },
                  { key: "vp", header: "Écart prix", numeric: true, render: (l) => `${l.priceVariancePercent} %` },
                  { key: "s", header: "", render: (l) => (l.status === "matched" ? <Badge tone="success">ok</Badge> : <Badge tone="error">{l.detail}</Badge>) },
                ]}
                rows={report.lines.map((l, i) => ({ ...l, _k: i }))}
                rowKey={(l) => String(l._k)}
              />
            </div>
          )}
        </Card>
      )}
    </div>
  );
}
