import { useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { Button, Card, DataTable, EmptyState, Money, SectionLabel, Skeleton, StatePill, type Column } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { dateTime } from "@/shared/format";
import { printDocument } from "@/shared/printApi";
import { getSale, type SaleDetail, type SaleLine, type SalePayment } from "@/sales/ordersApi";
import { Badge } from "@/shared/ui";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { DOCUMENT_TYPE_LABEL, issueDocument, openDocumentPdf, searchDocuments } from "@/documents/documentsApi";

const SALE_TYPE: Record<string, string> = { cash: "Comptant", credit: "À crédit", mixed: "Mixte", quote: "Devis", layaway: "Mise de côté" };

/** Détail d'une vente — ouvert depuis la recherche Ctrl+K, le fil d'activité ou une commande client. La facture
 * s'émet d'ici (document figé, numéroté, vérifiable). */
export function SaleDetailPage() {
  const { id = "" } = useParams();
  const { hasPermission } = useAuth();
  const [sale, setSale] = useState<SaleDetail | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setSale(null);
    setError(null);
    getSale(id).then(setSale).catch((e) => setError(e instanceof ApiError && e.problem.status === 404 ? "Vente introuvable." : e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, [id]);

  if (error) return <Card><EmptyState title={error} action={<Link to="/"><Button variant="ghost">Retour au dashboard</Button></Link>} /></Card>;
  if (!sale) return <Card><Skeleton rows={5} /></Card>;

  const lineCols: Array<Column<SaleLine>> = [
    { key: "d", header: "Article", render: (l) => <>{l.description}{l.lotNumber && <small style={{ color: "var(--nx-text-3)" }}> · lot {l.lotNumber}</small>}</> },
    { key: "q", header: "Qté", numeric: true, render: (l) => l.quantity },
    { key: "p", header: "Prix unitaire", numeric: true, render: (l) => <Money value={l.unitPrice} currency={sale.currency} /> },
    { key: "r", header: "Remise", numeric: true, render: (l) => (l.discount ? <Money value={l.discount} currency={sale.currency} /> : "—") },
    { key: "t", header: "Total", numeric: true, render: (l) => <Money value={l.lineTotal} currency={sale.currency} /> },
  ];
  const payCols: Array<Column<SalePayment>> = [
    { key: "m", header: "Mode", render: (p) => p.method },
    { key: "d", header: "Date", render: (p) => dateTime(p.receivedAt) },
    { key: "a", header: "Montant", numeric: true, render: (p) => <Money value={p.amount} currency={sale.currency} /> },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div className="nx-eyebrow">Vente · {SALE_TYPE[sale.saleType] ?? sale.saleType}</div>
          <h1 style={{ margin: "2px 0 0" }}>{sale.documentNumber} <StatePill status={sale.status} /></h1>
          <div style={{ color: "var(--nx-text-2)" }}>{dateTime(sale.soldAt)}</div>
        </div>
        <div style={{ display: "flex", gap: 8 }}>
          {sale.customerId && hasPermission("customers.view") && (
            <Link to={`/customers/${sale.customerId}`}><Button variant="ghost"><Icon.Users size={16} /> Compte client</Button></Link>
          )}
          <Button variant="ghost" onClick={() => void printDocument("sale", sale.id, "ticket")}><Icon.Printer size={16} /> Reçu</Button>
        </div>
      </header>

      <div className="nx-grid">
        {[
          ["Total", sale.total], ["Payé", sale.amountPaid], ["Reste dû", sale.amountDue], ["Taxes", sale.taxTotal],
        ].map(([label, value]) => (
          <div key={label as string} className="nx-col-3 nx-card nx-kpi">
            <div className="nx-kpi__label">{label}</div>
            <div className="nx-kpi__value nx-numeric"><Money value={value as number} currency={sale.currency} /></div>
          </div>
        ))}
      </div>

      <Card>
        <SectionLabel>Lignes ({sale.lines.length})</SectionLabel>
        <DataTable columns={lineCols} rows={sale.lines} rowKey={(l) => l.id} empty="Aucune ligne." />
      </Card>
      <Card>
        <SectionLabel>Paiements</SectionLabel>
        <DataTable columns={payCols} rows={sale.payments} rowKey={(p) => p.paymentId} empty="Aucun paiement enregistré." />
      </Card>
      {hasPermission("documents.view") && <SaleDocuments saleId={sale.id} status={sale.status} />}
    </div>
  );
}

/** Documents émis pour la vente (facture…) ; émission de la facture si elle n'existe pas encore. */
function SaleDocuments({ saleId, status }: { saleId: string; status: string }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const docs = useAsync(() => searchDocuments({ sourceType: "sale", sourceId: saleId }), [saleId]);
  const { busy, error, submit } = useSubmit();
  const valid = (docs.data ?? []).filter((d) => d.status !== "cancelled" && d.status !== "replaced");
  const hasInvoice = valid.some((d) => d.documentType === "invoice");
  const canIssue = hasPermission("documents.issue") && !hasInvoice && status !== "void" && status !== "draft" && status !== "parked";

  async function issue() {
    const d = await submit(() => issueDocument("invoice", "sale", saleId));
    if (d) { toast.success(`Facture ${d.documentNumber} émise.`); docs.reload(); void openDocumentPdf(d.id); }
  }

  return (
    <Card>
      <SectionLabel action={canIssue ? <Button size="sm" disabled={busy} onClick={() => void issue()}>Émettre la facture</Button> : undefined}>
        Documents
      </SectionLabel>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {docs.error && <div role="alert" className="nx-error-text">{docs.error}</div>}
      {(docs.data ?? []).length === 0
        ? <div style={{ color: "var(--nx-text-2)" }}>{docs.loading ? "Chargement…" : "Aucun document émis pour cette vente."}</div>
        : (docs.data ?? []).map((d) => (
          <div key={d.id} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap", padding: "4px 0" }}>
            <Badge>{DOCUMENT_TYPE_LABEL[d.documentType] ?? d.documentType}</Badge><code>{d.documentNumber}</code>
            {d.status === "cancelled" && <Badge tone="error">annulée</Badge>}
            <Button size="sm" variant="ghost" onClick={() => void openDocumentPdf(d.id)}><Icon.Printer size={14} /> PDF</Button>
          </div>
        ))}
    </Card>
  );
}
