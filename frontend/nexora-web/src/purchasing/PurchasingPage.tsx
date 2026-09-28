import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { Badge, Button, Card, DataTable, KpiCard, Money, SectionLabel, StatePill, Tabs, type Column } from "@/shared/ui";
import { listPayables, listPurchaseOrders, type Payable, type SupplierSummary } from "@/purchasing/purchasingApi";
import { SupplierDocumentsDialog } from "@/purchasing/SupplierDocuments";
import { PayPayableDialog } from "@/purchasing/PayPayableDialog";
import { OrdersPanel } from "@/purchasing/OrdersPanel";
import { ReceiptsPanel } from "@/purchasing/ReceiptsPanel";
import { InvoicesPanel } from "@/purchasing/InvoicesPanel";
import { ReturnsPanel } from "@/purchasing/ReturnsPanel";
import { SupplierIdentityDialog, SupplierSheet } from "@/purchasing/SupplierSheet";
import { usePurchasingRefs } from "@/purchasing/purchasingShared";
import { PrintMenu } from "@/shared/ExportMenu";
import { dateShort, money } from "@/shared/format";

/**
 * Achats — cycle complet : commande (brouillon → approbation → envoi) → réception (contrôle, refus) →
 * facture (rapprochement 3-way) → dette → paiement ; retours fournisseur ; fiche et conditions fournisseur.
 */
export function PurchasingPage() {
  const { hasPermission, activeCompanyId } = useAuth();
  const [tab, setTab] = useState("orders");
  const refs = usePurchasingRefs();
  const canViewPayables = hasPermission("purchasing.view_payables");
  const summary = useAsync(async () => {
    const [orders, payables] = await Promise.all([
      listPurchaseOrders().catch(() => []),
      canViewPayables ? listPayables(true).catch(() => [] as Payable[]) : Promise.resolve([] as Payable[]),
    ]);
    return { orders, payables };
  }, [activeCompanyId, canViewPayables]);
  const [lastPayment, setLastPayment] = useState<{ id: string; supplier: string; amount: number; currency: string } | null>(null);
  const [docsFor, setDocsFor] = useState<SupplierSummary | null>(null);
  const [sheetFor, setSheetFor] = useState<string | null>(null);
  const [creatingSupplier, setCreatingSupplier] = useState(false);
  const [paying, setPaying] = useState<Payable | null>(null);

  if (!hasPermission("purchasing.view")) {
    return <Card>Le module Achats n'est pas accessible avec vos permissions.</Card>;
  }

  const refresh = () => { summary.reload(); refs.reload(); };
  const payables = summary.data?.payables ?? [];
  const orders = summary.data?.orders ?? [];
  const overdue = payables.filter((p) => p.daysOverdue > 0);
  const totalDue = payables.reduce((a, p) => a + p.remainingAmount, 0);
  const currency = payables[0]?.currency ?? refs.defaultCurrency;
  const canPay = hasPermission("purchasing.pay");

  const supplierCols: Array<Column<SupplierSummary>> = [
    { key: "n", header: "N°", render: (s) => <code>{s.supplierNumber}</code> },
    { key: "name", header: "Fournisseur", render: (s) => (
      <button type="button" onClick={() => setSheetFor(s.id)} style={{ appearance: "none", background: "transparent", border: "none", padding: 0,
        cursor: "pointer", color: "var(--nx-brand)", font: "inherit" }}>{s.name}</button>) },
    { key: "s", header: "Statut", render: (s) => <StatePill status={s.status} /> },
    { key: "d", header: "Dettes ouvertes", numeric: true, render: (s) => <Money value={s.openPayables} currency={s.currency} /> },
    { key: "x", header: "", render: (s) => <Button size="sm" variant="ghost" onClick={() => setDocsFor(s)}>Pièces & relevé</Button> },
  ];

  const payableCols: Array<Column<Payable>> = [
    { key: "sup", header: "Fournisseur", render: (p) => p.supplierName },
    { key: "inv", header: "Facture", render: (p) => <>{p.invoiceNumber ?? "—"}{p.supplierReference && <span style={{ color: "var(--nx-text-2)" }}> · {p.supplierReference}</span>}</> },
    { key: "due", header: "Échéance", render: (p) => (p.dueDate ? dateShort(p.dueDate) : "—") },
    { key: "od", header: "Retard", numeric: true, render: (p) => (p.daysOverdue > 0 ? <Badge tone="error">{p.daysOverdue} j</Badge> : "—") },
    { key: "rem", header: "Solde", numeric: true, render: (p) => <Money value={p.remainingAmount} currency={p.currency} /> },
    { key: "st", header: "Statut", render: (p) => (p.paymentBlocked ? <Badge tone="error">bloqué</Badge> : <StatePill status={p.status} />) },
    {
      key: "a", header: "", render: (p) => canPay && !p.paymentBlocked && p.remainingAmount > 0 ? (
        <Button size="sm" onClick={() => setPaying(p)}>Payer</Button>
      ) : p.paymentBlocked ? <span style={{ fontSize: 12, color: "var(--nx-neg)" }}>{p.blockReason}</span> : null,
    },
    { key: "p", header: "", render: (p) => (p.supplierInvoiceId ? <PrintMenu kind="supplier-invoice" id={p.supplierInvoiceId} iconOnly label="Imprimer la facture" /> : null) },
  ];

  const tabs = [
    { id: "orders", label: "Commandes" },
    { id: "receipts", label: "Réceptions" },
    ...(canViewPayables ? [{ id: "invoices", label: "Factures" }, { id: "payables", label: `Dettes (${payables.length})` }] : []),
    { id: "returns", label: "Retours" },
    { id: "suppliers", label: `Fournisseurs (${refs.suppliers.length})` },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Achats & fournisseurs</h1>
      {(summary.error || refs.error) && <div role="alert" className="nx-error-text">{summary.error ?? refs.error}</div>}
      {lastPayment && (
        <Card style={{ display: "flex", gap: 12, alignItems: "center", flexWrap: "wrap", background: "var(--nx-success-bg)", borderColor: "transparent" }}>
          <span>Paiement de <b>{money(lastPayment.amount, lastPayment.currency)}</b> à <b>{lastPayment.supplier}</b> enregistré.</span>
          <PrintMenu kind="supplier-payment" id={lastPayment.id} ticket label="Imprimer le reçu" />
          <Button size="sm" variant="ghost" onClick={() => setLastPayment(null)}>Masquer</Button>
        </Card>
      )}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        {canViewPayables && <KpiCard label="Dettes ouvertes" value={<Money value={totalDue} currency={currency} />} />}
        {canViewPayables && <KpiCard label="Dettes en retard" value={String(overdue.length)} tone={overdue.length ? "warning" : undefined} tag={overdue.length ? "!" : undefined} />}
        <KpiCard label="Commandes en cours" value={String(orders.filter((o) => !["received", "invoiced", "closed", "cancelled"].includes(o.status)).length)} />
        <KpiCard label="À approuver" value={String(orders.filter((o) => o.status === "submitted").length)} />
      </div>

      <Card>
        <Tabs tabs={tabs} active={tab} onChange={setTab} />
      </Card>
      {tab === "orders" && <OrdersPanel refs={refs} onChanged={refresh} />}
      {tab === "receipts" && <ReceiptsPanel refs={refs} onChanged={refresh} />}
      {tab === "invoices" && canViewPayables && <InvoicesPanel refs={refs} onChanged={refresh} />}
      {tab === "returns" && <ReturnsPanel refs={refs} onChanged={refresh} />}
      {tab === "payables" && canViewPayables && (
        <Card>
          <SectionLabel>Dettes fournisseur ouvertes</SectionLabel>
          <DataTable exportTitle="Dettes fournisseur" columns={payableCols} rows={payables} rowKey={(p) => p.id}
                     empty={summary.loading ? "Chargement…" : "Aucune dette ouverte."} />
        </Card>
      )}
      {tab === "suppliers" && (
        <Card>
          <SectionLabel action={hasPermission("suppliers.create") ? <Button size="sm" onClick={() => setCreatingSupplier(true)}>Nouveau fournisseur</Button> : undefined}>
            Fournisseurs
          </SectionLabel>
          <DataTable exportTitle="Fournisseurs" columns={supplierCols} rows={refs.suppliers} rowKey={(s) => s.id}
                     empty={refs.loading ? "Chargement…" : "Aucun fournisseur."} />
        </Card>
      )}

      {paying && (
        <PayPayableDialog payable={paying} onClose={() => setPaying(null)}
                          onPaid={(paymentId, amount) => {
                            if (paymentId) setLastPayment({ id: paymentId, supplier: paying.supplierName, amount, currency: paying.currency });
                            setPaying(null);
                            refresh();
                          }} />
      )}
      {docsFor && (
        <SupplierDocumentsDialog supplierId={docsFor.id} supplierName={docsFor.name}
                                 canStatement={canViewPayables} onClose={() => setDocsFor(null)} />
      )}
      {sheetFor && <SupplierSheet key={sheetFor} supplierId={sheetFor} onClose={() => setSheetFor(null)} onChanged={refresh} />}
      {creatingSupplier && <SupplierIdentityDialog supplier={null} onClose={() => setCreatingSupplier(false)}
                                                   onSaved={() => { setCreatingSupplier(false); refresh(); }} />}
    </div>
  );
}
