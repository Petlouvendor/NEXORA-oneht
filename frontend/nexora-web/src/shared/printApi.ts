import { apiDownload, apiFetch } from "@/core/apiClient";

/**
 * Impression & export : reçus et documents construits par le serveur (vrais PDF A4 ou ticket 80 mm,
 * vrais classeurs Excel), export de n'importe quel tableau affiché, export des rapports de pilotage.
 */

export type PrintFormat = "pdf" | "ticket" | "xlsx" | "csv";
export type PrintableKind =
  | "customer-payment" | "customer-credit" | "credit-memo" | "customer-refund" | "customer-expense" | "sale"
  | "supplier-payment" | "supplier-invoice" | "purchase-order" | "goods-receipt";

export type PrintKind = "text" | "money" | "number" | "integer" | "date" | "datetime" | "percent";
export type PrintValue = string | number | null;

export interface PrintColumn { label: string; kind?: PrintKind; currency?: string | null }
export interface PrintTable { title?: string | null; columns: PrintColumn[]; rows: PrintValue[][]; totals?: PrintValue[] | null }
export interface PrintFigure { label: string; value: PrintValue; kind?: PrintKind; currency?: string | null; emphasis?: boolean }
export interface PrintDocument {
  title: string; reference?: string | null; subtitle?: string | null;
  details?: PrintFigure[]; tables?: PrintTable[]; summary?: PrintFigure[]; notes?: string[]; landscape?: boolean;
}

const EXT: Record<PrintFormat, string> = { pdf: "pdf", ticket: "pdf", xlsx: "xlsx", csv: "csv" };

/** Enregistre un fichier reçu du serveur (nom du serveur, sinon nom de repli). */
export function saveFile(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.rel = "noopener";
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30_000);
}

/** Onglet ouvert pendant le clic (sinon bloqué comme fenêtre surgissante), rempli quand le PDF arrive. */
function reserveTab(): Window | null {
  const tab = window.open("", "_blank");
  if (tab) {
    try {
      tab.document.title = "Préparation du document…";
      tab.document.body.innerHTML = '<p style="font:16px system-ui,sans-serif;margin:2rem">Préparation du document…</p>';
    } catch { /* onglet non modifiable : il recevra quand même le PDF */ }
  }
  return tab;
}

/**
 * Affiche un PDF dans l'onglet réservé : la visionneuse du navigateur l'ouvre, prêt à imprimer (bouton
 * Imprimer ou Ctrl+P, A4 ou imprimante ticket). Un navigateur réglé pour « télécharger les PDF »
 * l'enregistre à la place — fichier complet, construit localement. Sans onglet (fenêtres bloquées), le PDF
 * est enregistré.
 */
export function showPdf(blob: Blob, filename: string, tab: Window | null) {
  if (tab && !tab.closed) {
    const url = URL.createObjectURL(blob);
    tab.location.href = url;
    setTimeout(() => URL.revokeObjectURL(url), 10 * 60_000);
    return;
  }
  saveFile(blob, filename);
}

/** Récupère un PDF et l'ouvre pour impression ; l'onglet est réservé avant tout appel réseau. */
export async function openPrintable(path: string, fallbackName: string) {
  const tab = reserveTab();
  try {
    const { blob, filename } = await apiDownload(path);
    showPdf(blob, filename ?? fallbackName, tab);
  } catch (e) {
    tab?.close();
    throw e;
  }
}

const docPath = (kind: PrintableKind, id: string, format: PrintFormat) =>
  `/print/${kind}/${encodeURIComponent(id)}?format=${format}`;

/** Ouvre un reçu / document pour impression (A4 ou ticket 80 mm) dans un nouvel onglet. */
export function printDocument(kind: PrintableKind, id: string, format: "pdf" | "ticket" = "pdf") {
  return openPrintable(docPath(kind, id, format), `${kind}.pdf`);
}

/** Télécharge un reçu / document (PDF, ticket, Excel ou CSV). */
export async function downloadDocument(kind: PrintableKind, id: string, format: PrintFormat) {
  const { blob, filename } = await apiDownload(docPath(kind, id, format));
  saveFile(blob, filename ?? `${kind}.${EXT[format]}`);
}

const query = (params: Record<string, string | null | undefined>) => {
  const qs = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v) qs.set(k, v);
  return qs.toString();
};

/** Relevé de compte client (total versé, reste à verser, mouvements) : impression ou téléchargement. */
export async function customerStatement(
  customerId: string, format: PrintFormat, opts: { from?: string; to?: string; currency?: string } = {}, print = false,
) {
  const path = `/print/customers/${encodeURIComponent(customerId)}/statement?${query({ format, ...opts })}`;
  if (print && format !== "xlsx" && format !== "csv") return openPrintable(path, "releve-client.pdf");
  const { blob, filename } = await apiDownload(path);
  saveFile(blob, filename ?? `releve-client.${EXT[format]}`);
}

/** Relevé fournisseur (facturé, payé, reste à payer). */
export async function supplierStatement(
  supplierId: string, format: PrintFormat, opts: { from?: string; to?: string } = {}, print = false,
) {
  const path = `/print/suppliers/${encodeURIComponent(supplierId)}/statement?${query({ format, ...opts })}`;
  if (print && format !== "xlsx" && format !== "csv") return openPrintable(path, "releve-fournisseur.pdf");
  const { blob, filename } = await apiDownload(path);
  saveFile(blob, filename ?? `releve-fournisseur.${EXT[format]}`);
}

/** Pièce imprimable d'un fournisseur (paiement, réception, facture, commande). */
export interface PrintableItem {
  kind: PrintableKind; id: string; number: string; date: string; amount: number | null; currency: string | null;
  status: string; detail: string | null;
}

/** Pièces d'un fournisseur que l'utilisateur a le droit d'imprimer, les plus récentes d'abord. */
export const listSupplierDocuments = (supplierId: string) =>
  apiFetch<PrintableItem[]>(`/print/suppliers/${encodeURIComponent(supplierId)}/documents`);

/** Export d'un tableau affiché (document « non contractuel ») — exige reporting.export. */
export async function exportScreen(screenKey: string, document: PrintDocument, format: "xlsx" | "pdf" | "csv") {
  const { blob, filename } = await apiDownload(`/exports/screen?format=${format}`, {
    method: "POST",
    body: JSON.stringify({ screenKey, document }),
  });
  saveFile(blob, filename ?? `${screenKey}.${EXT[format]}`);
}

/** Rapports de pilotage exportés par le serveur (données complètes, filtres appliqués). */
export async function exportReport(reportKey: string, format: "xlsx" | "pdf" | "csv", filter: Record<string, unknown> = {}) {
  const { blob, filename } = await apiDownload("/reports/export", {
    method: "POST",
    body: JSON.stringify({ reportKey, format, filter }),
  });
  saveFile(blob, filename ?? `${reportKey}.${EXT[format]}`);
}
