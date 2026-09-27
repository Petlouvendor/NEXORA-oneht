import { useEffect, useState } from "react";
import { Badge, DataTable, EmptyState, Modal, Spinner, type Column } from "@/shared/ui";
import { dateShort, money } from "@/shared/format";
import { Icon } from "@/shared/icons";
import { ActionMenu, PrintMenu } from "@/shared/ExportMenu";
import { listSupplierDocuments, supplierStatement, type PrintableItem } from "@/shared/printApi";

const KIND_LABEL: Record<string, string> = {
  "supplier-payment": "Paiement",
  "goods-receipt": "Bon de réception",
  "supplier-invoice": "Facture",
  "purchase-order": "Bon de commande",
};

const STATUS_LABEL: Record<string, string> = {
  completed: "Validé", void: "Annulé", draft: "Brouillon", posted: "Comptabilisé", cancelled: "Annulé",
  validated: "Validée", partially_paid: "Partiellement payée", paid: "Payée", submitted: "Soumise", approved: "Approuvée",
  sent: "Envoyée", partially_received: "Partiellement reçue", received: "Reçue", invoiced: "Facturée", closed: "Clôturée",
};

/**
 * Pièces d'un fournisseur à (ré)imprimer : paiements (reçu avec reste à payer), bons de réception,
 * factures, bons de commande, et relevé du compte fournisseur en PDF ou Excel.
 */
export function SupplierDocumentsDialog({ supplierId, supplierName, canStatement, onClose }: {
  supplierId: string; supplierName: string; canStatement: boolean; onClose: () => void;
}) {
  const [rows, setRows] = useState<PrintableItem[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    listSupplierDocuments(supplierId).then(setRows).catch(() => { setRows([]); setError("Pièces indisponibles."); });
  }, [supplierId]);

  const columns: Array<Column<PrintableItem>> = [
    { key: "d", header: "Date", render: (r) => dateShort(r.date) },
    { key: "k", header: "Pièce", render: (r) => KIND_LABEL[r.kind] ?? r.kind },
    { key: "n", header: "Numéro", render: (r) => r.number },
    { key: "a", header: "Montant", numeric: true, render: (r) => (r.amount != null ? money(r.amount, r.currency ?? "") : "—") },
    { key: "s", header: "Statut", render: (r) => <Badge tone={r.status === "void" || r.status === "cancelled" ? "error" : undefined}>{STATUS_LABEL[r.status] ?? r.status}</Badge> },
    { key: "r", header: "Référence", render: (r) => r.detail ?? "—" },
    { key: "x", header: "", render: (r) => <PrintMenu kind={r.kind} id={r.id} ticket={r.kind === "supplier-payment"} iconOnly label="Imprimer" /> },
  ];

  return (
    <Modal title={`Pièces — ${supplierName}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 12, minWidth: "min(860px, 90vw)" }}>
        {canStatement && (
          <div>
            <ActionMenu label="Relevé fournisseur" icon={<Icon.Printer size={14} />} items={[
              { label: "Imprimer le relevé (A4)", hint: "Facturé, payé, reste à payer", run: () => supplierStatement(supplierId, "pdf", {}, true) },
              { label: "Télécharger le PDF", run: () => supplierStatement(supplierId, "pdf") },
              { label: "Télécharger en Excel", run: () => supplierStatement(supplierId, "xlsx") },
            ]} />
          </div>
        )}
        {error && <div className="nx-error-text" role="alert">{error}</div>}
        {!rows ? <Spinner /> : rows.length === 0
          ? <EmptyState title="Aucune pièce" hint="Paiements, réceptions, factures et commandes apparaîtront ici." />
          : <DataTable columns={columns} rows={rows} rowKey={(r) => `${r.kind}:${r.id}`} exportTitle={`Pièces — ${supplierName}`} />}
      </div>
    </Modal>
  );
}
