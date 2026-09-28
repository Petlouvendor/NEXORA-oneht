import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, money, number } from "@/shared/format";
import { Icon } from "@/shared/icons";
import { PrintMenu } from "@/shared/ExportMenu";
import { Badge, Button, Card, DataTable, Drawer, Field, IconButton, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import type { ProductDetail } from "@/catalog/catalogApi";
import {
  getSupplierInvoice, listSupplierInvoices, matchInvoice, recordSupplierInvoice,
  type InvoiceInput, type PurchaseOrderDetail, type ThreeWayMatchReport,
} from "@/purchasing/purchasingApi";
import {
  estimateLine, estimateTotals, INVOICE_STATUS, knownPurchasePrice, MATCH_STATUS, PurchaseProductPicker, round2, taxRate, today,
  type PurchasingRefs,
} from "@/purchasing/purchasingShared";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Factures fournisseur : saisie (depuis une commande ou libre), comptabilisation, rapprochement 3-way. */
export function InvoicesPanel({ refs, onChanged }: { refs: PurchasingRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [supplierId, setSupplierId] = useState("");
  const invoices = useAsync(() => listSupplierInvoices({ supplierId: supplierId || undefined, limit: 200 }), [supplierId]);
  const [creating, setCreating] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);
  const reload = () => { invoices.reload(); onChanged(); };

  return (
    <Card>
      <SectionLabel action={hasPermission("purchasing.invoice") ? <Button size="sm" onClick={() => setCreating(true)}>Saisir une facture</Button> : undefined}>
        Factures fournisseur
      </SectionLabel>
      <div style={{ maxWidth: 280, marginBottom: 10 }}>
        <Field label="Fournisseur">
          <Select value={supplierId} onChange={(e) => setSupplierId(e.target.value)}>
            <option value="">Tous</option>
            {refs.suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </Select>
        </Field>
      </div>
      {invoices.error && <div role="alert" className="nx-error-text">{invoices.error}</div>}
      <DataTable exportTitle="Factures fournisseur"
        columns={[
          { key: "n", header: "N°", render: (i) => <button type="button" style={linkStyle} onClick={() => setOpenId(i.id)}><code>{i.documentNumber}</code></button> },
          { key: "r", header: "Réf. fournisseur", render: (i) => i.supplierReference ?? "—" },
          { key: "s", header: "Fournisseur", render: (i) => i.supplierName },
          { key: "d", header: "Date", render: (i) => dateShort(i.invoiceDate) },
          { key: "e", header: "Échéance", render: (i) => (i.dueDate ? dateShort(i.dueDate) : "—") },
          { key: "t", header: "Total", numeric: true, render: (i) => money(i.total, i.currency) },
          { key: "rest", header: "Reste à payer", numeric: true, render: (i) => (i.remainingAmount != null ? money(i.remainingAmount, i.currency) : "—") },
          { key: "m", header: "Rapprochement", render: (i) => (i.paymentBlocked ? <Badge tone="error">paiement bloqué</Badge>
            : <Badge tone={i.matchStatus === "matched" ? "success" : i.matchStatus === "variance" ? "error" : undefined}>{MATCH_STATUS[i.matchStatus] ?? i.matchStatus}</Badge>) },
        ]}
        rows={invoices.data ?? []} rowKey={(i) => i.id} empty={invoices.loading ? "Chargement…" : "Aucune facture."} />
      {creating && <InvoiceDialog refs={refs} order={null} onClose={() => setCreating(false)}
                                  onDone={(id) => { setCreating(false); reload(); if (id) setOpenId(id); }} />}
      {openId && <InvoiceDrawer key={openId} id={openId} onClose={() => setOpenId(null)} onChanged={reload} />}
    </Card>
  );
}

interface InvoiceLineState { key: string; productId: string | null; sku: string | null; label: string; quantity: string; unitPrice: string; taxId: string; discount: string }

/** Lignes d'une facture de commande : quantités REÇUES au prix commandé (base du rapprochement). */
export function invoiceLinesFromOrder(order: PurchaseOrderDetail): InvoiceLineState[] {
  return order.lines.filter((l) => l.quantityReceived > 0).map((l) => ({
    key: l.id, productId: l.productId, sku: l.sku, label: l.productName, quantity: String(l.quantityReceived), unitPrice: String(l.unitPrice),
    taxId: l.taxId ?? "", discount: l.discount ? String(round2((l.discount * l.quantityReceived) / l.quantityOrdered)) : "",
  }));
}

const INVOICE_FIELDS: Record<string, string> = {
  "purchasing.duplicate_invoice": "reference", "purchasing.supplier_unknown": "supplier", "purchasing.due_date": "dueDate",
  "purchasing.tender_required": "account", "purchasing.currency_unknown": "currency",
};

export function InvoiceDialog({ refs, order, onClose, onDone }: {
  refs: PurchasingRefs; order: PurchaseOrderDetail | null; onClose: () => void; onDone: (invoiceId: string | null) => void;
}) {
  const toast = useToast();
  const [operationUuid] = useState(newOperationId);
  const [supplierId, setSupplierId] = useState(order?.supplierId ?? "");
  const [reference, setReference] = useState("");
  const [invoiceDate, setInvoiceDate] = useState(today());
  const [dueDate, setDueDate] = useState("");
  const [currency, setCurrency] = useState(order?.currency ?? refs.defaultCurrency);
  const [onCredit, setOnCredit] = useState(true);
  const [accountId, setAccountId] = useState("");
  const [capitalise, setCapitalise] = useState(true);
  const [lines, setLines] = useState<InvoiceLineState[]>(() => (order ? invoiceLinesFromOrder(order) : []));
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const { busy, error, fieldErrors, submit, fail } = useSubmit(INVOICE_FIELDS);
  const supplierName = refs.suppliers.find((s) => s.id === supplierId)?.name ?? null;
  const receiptId = order && order.receipts.length === 1 ? order.receipts[0].id : null;

  const set = (key: string, patch: Partial<InvoiceLineState>) => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  const addProduct = (p: ProductDetail) => {
    const price = knownPurchasePrice(p, supplierName);
    setLines((ls) => [...ls, { key: p.id, productId: p.id, sku: p.sku, label: p.name, quantity: "", unitPrice: price != null ? String(price) : "", taxId: p.defaultTaxId ?? "", discount: "" }]);
  };
  const addFree = () => setLines((ls) => [...ls, { key: `free-${Date.now()}`, productId: null, sku: null, label: "", quantity: "1", unitPrice: "", taxId: "", discount: "" }]);

  const totals = estimateTotals(lines.map((l) => ({
    quantity: num(l.quantity) ?? 0, unitPrice: num(l.unitPrice) ?? 0, discount: num(l.discount) ?? 0, ratePercent: taxRate(refs.taxes, l.taxId),
  })));

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!supplierId) return fail("supplier", "Choisissez le fournisseur.");
    if (!invoiceDate) return fail("invoiceDate", "Date de facture obligatoire.");
    if (dueDate && dueDate < invoiceDate) return fail("dueDate", "L'échéance précède la date de facture.");
    if (!onCredit && !accountId) return fail("account", "Choisissez le compte de paiement (caisse ou banque).");
    if (lines.length === 0) return fail("lines", "Ajoutez au moins une ligne.");
    const errs: Record<string, string> = {};
    for (const l of lines) {
      const q = num(l.quantity), p = num(l.unitPrice), d = num(l.discount) ?? 0;
      if (!l.productId && !l.label.trim()) errs[l.key] = "Libellé obligatoire.";
      else if (q == null || q <= 0) errs[l.key] = "Quantité positive requise.";
      else if (p == null || p < 0) errs[l.key] = "Prix invalide.";
      else if (d < 0 || d > q * p) errs[l.key] = "Remise hors du montant de la ligne.";
    }
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    const body: InvoiceInput = {
      operationUuid, supplierId, purchaseOrderId: order?.id ?? null, currency, invoiceDate, dueDate: dueDate || null,
      onCredit, tenderFinancialAccountId: onCredit ? null : accountId, capitaliseToInventory: capitalise,
      supplierReference: reference.trim() || null, goodsReceiptId: receiptId,
      lines: lines.map((l) => ({
        productId: l.productId, description: l.label.trim(), quantity: num(l.quantity)!, unitPrice: num(l.unitPrice)!,
        taxId: l.taxId || null, taxAmount: 0, discount: num(l.discount) ?? 0,
      })),
    };
    const r = await submit(() => recordSupplierInvoice(body));
    if (r) {
      toast.success(r.wasReplay ? "Cette facture était déjà enregistrée." : onCredit ? "Facture comptabilisée : la dette fournisseur est créée." : "Facture comptabilisée et payée.");
      onDone(r.invoiceId);
    }
  }

  return (
    <Modal title={order ? `Facture de la commande ${order.documentNumber}` : "Saisir une facture fournisseur"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Comptabiliser la facture</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(860px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12 }}>
          <Field label="Fournisseur" required error={fieldErrors.supplier}>
            <Select value={supplierId} disabled={!!order} onChange={(e) => setSupplierId(e.target.value)}>
              <option value="">—</option>
              {refs.suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </Select>
          </Field>
          <Field label="N° de facture du fournisseur" error={fieldErrors.reference} hint="Empêche la double saisie.">
            <TextInput value={reference} maxLength={100} onChange={(e) => setReference(e.target.value)} />
          </Field>
          <Field label="Date de facture" required error={fieldErrors.invoiceDate}>
            <TextInput type="date" value={invoiceDate} max={today()} onChange={(e) => setInvoiceDate(e.target.value)} />
          </Field>
          <Field label="Échéance" error={fieldErrors.dueDate} hint="Vide : selon les conditions du fournisseur.">
            <TextInput type="date" value={dueDate} min={invoiceDate} onChange={(e) => setDueDate(e.target.value)} />
          </Field>
          <Field label="Devise" required error={fieldErrors.currency}>
            <Select value={currency} disabled={!!order} onChange={(e) => setCurrency(e.target.value)}>
              {(refs.currencies.length ? refs.currencies.map((c) => c.code) : [currency]).map((c) => <option key={c} value={c}>{c}</option>)}
            </Select>
          </Field>
          <Field label="Règlement">
            <Select value={onCredit ? "credit" : "cash"} onChange={(e) => setOnCredit(e.target.value === "credit")}>
              <option value="credit">À crédit (crée une dette)</option>
              <option value="cash">Payée comptant</option>
            </Select>
          </Field>
          {!onCredit && (
            <Field label="Payée depuis" required error={fieldErrors.account}>
              <Select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
                <option value="">—</option>
                {refs.accounts.filter((a) => a.currency === currency).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
              </Select>
            </Field>
          )}
          <Field label="Nature">
            <Select value={capitalise ? "stock" : "expense"} onChange={(e) => setCapitalise(e.target.value === "stock")}>
              <option value="stock">Marchandises (stock)</option>
              <option value="expense">Frais / charges</option>
            </Select>
          </Field>
        </div>
        {!order && (
          <div style={{ display: "grid", gap: 6 }}>
            <PurchaseProductPicker onPick={addProduct} excluded={lines.flatMap((l) => (l.productId ? [l.productId] : []))} />
            <div><Button size="sm" variant="ghost" onClick={addFree}>+ Ligne libre (transport, service…)</Button></div>
          </div>
        )}
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        {lines.length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>{order ? "Rien n'a encore été reçu sur cette commande." : "Aucune ligne."}</div> : (
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Désignation</th><th>Quantité</th><th>Prix unitaire</th><th>Remise</th><th>Taxe</th><th className="nx-num">Total estimé</th><th /></tr></thead>
              <tbody>
                {lines.map((l) => {
                  const est = estimateLine(num(l.quantity) ?? 0, num(l.unitPrice) ?? 0, num(l.discount) ?? 0, taxRate(refs.taxes, l.taxId));
                  return (
                    <tr key={l.key}>
                      <td style={{ minWidth: 180 }}>
                        {l.productId ? <><code>{l.sku}</code> {l.label}</> : <TextInput value={l.label} placeholder="Libellé" aria-label="Libellé de la ligne" onChange={(e) => set(l.key, { label: e.target.value })} />}
                        {lineErrors[l.key] && <div className="nx-err" role="alert">{lineErrors[l.key]}</div>}
                      </td>
                      <td style={{ minWidth: 90 }}><TextInput type="number" step="any" min={0} value={l.quantity} aria-label={`Quantité — ${l.label}`}
                        invalid={!!lineErrors[l.key]} onChange={(e) => set(l.key, { quantity: e.target.value })} /></td>
                      <td style={{ minWidth: 110 }}><TextInput type="number" step="0.0001" min={0} value={l.unitPrice} aria-label={`Prix unitaire — ${l.label}`}
                        onChange={(e) => set(l.key, { unitPrice: e.target.value })} /></td>
                      <td style={{ minWidth: 90 }}><TextInput type="number" step="0.01" min={0} value={l.discount} aria-label={`Remise — ${l.label}`}
                        onChange={(e) => set(l.key, { discount: e.target.value })} /></td>
                      <td style={{ minWidth: 120 }}>
                        <Select value={l.taxId} aria-label={`Taxe — ${l.label}`} onChange={(e) => set(l.key, { taxId: e.target.value })}>
                          <option value="">Aucune</option>
                          {refs.taxes.map((t) => <option key={t.id} value={t.id}>{t.name} ({t.rate} %)</option>)}
                        </Select>
                      </td>
                      <td className="nx-num">{money(est.total, currency)}</td>
                      <td><IconButton label={`Retirer ${l.label || "la ligne"}`} onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))}><Icon.X size={14} /></IconButton></td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <div style={{ display: "flex", gap: 16, justifyContent: "flex-end", flexWrap: "wrap" }}>
          <span>Hors taxe : <b>{money(totals.subtotal, currency)}</b></span>
          <span>Taxes : <b>{money(totals.tax, currency)}</b></span>
          <span>Total estimé : <b>{money(totals.total, currency)}</b></span>
        </div>
        <div style={{ fontSize: 12, color: "var(--nx-text-2)", textAlign: "right" }}>Taxes et totaux définitifs calculés par le serveur.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

export function InvoiceDrawer({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const detail = useAsync(() => getSupplierInvoice(id), [id]);
  const [report, setReport] = useState<ThreeWayMatchReport | null>(null);
  const { busy, error, submit } = useSubmit();
  const d = detail.data;

  async function runMatch() {
    const r = await submit(() => matchInvoice(id));
    if (r) { setReport(r); detail.reload(); onChanged(); }
  }

  return (
    <Drawer title={d ? `Facture ${d.invoice.documentNumber}` : "Facture"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {!d ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 12 }}>
          <div>{d.invoice.supplierName}{d.invoice.supplierReference ? ` · réf. ${d.invoice.supplierReference}` : ""}
            {d.invoice.purchaseOrderNumber && <> · commande <code>{d.invoice.purchaseOrderNumber}</code></>}</div>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <Badge>{INVOICE_STATUS[d.invoice.status] ?? d.invoice.status}</Badge>
            <span>du {dateShort(d.invoice.invoiceDate)}{d.invoice.dueDate ? `, échéance ${dateShort(d.invoice.dueDate)}` : ""}</span>
            {d.invoice.paymentBlocked && <Badge tone="error">paiement bloqué</Badge>}
          </div>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <PrintMenu kind="supplier-invoice" id={d.invoice.id} label="Imprimer" />
            {hasPermission("purchasing.match") && d.invoice.purchaseOrderId && (
              <Button size="sm" variant="subtle" disabled={busy} onClick={runMatch}>Rapprocher (commande ↕ réception ↕ facture)</Button>
            )}
          </div>
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Désignation</th><th className="nx-num">Qté</th><th className="nx-num">Prix</th><th className="nx-num">Remise</th><th className="nx-num">Montant HT</th></tr></thead>
              <tbody>
                {d.lines.map((l) => (
                  <tr key={l.id}>
                    <td>{l.sku ? <code>{l.sku}</code> : null} {l.description}</td>
                    <td className="nx-num">{number(l.quantity, 3)}</td>
                    <td className="nx-num">{money(l.unitPrice, d.invoice.currency)}</td>
                    <td className="nx-num">{l.discount ? money(l.discount, d.invoice.currency) : "—"}</td>
                    <td className="nx-num">{money(l.lineTotal, d.invoice.currency)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot><tr><td colSpan={3}>HT {money(d.invoice.subtotal, d.invoice.currency)} · taxes {money(d.invoice.taxTotal, d.invoice.currency)}</td>
                <td colSpan={2} className="nx-num"><b>{money(d.invoice.total, d.invoice.currency)}</b></td></tr></tfoot>
            </table>
          </div>
          {d.invoice.remainingAmount != null && <div>Reste à payer : <b>{money(d.invoice.remainingAmount, d.invoice.currency)}</b></div>}
          {report && (
            <>
              <SectionLabel>Rapprochement — {report.paymentBlocked ? <Badge tone="error">écart · paiement bloqué</Badge> : <Badge tone="success">rapprochée</Badge>}</SectionLabel>
              <DataTable
                columns={[
                  { key: "d", header: "Article", render: (l) => l.description },
                  { key: "o", header: "Commandé", numeric: true, render: (l) => `${number(l.orderedQuantity, 3)} × ${money(l.orderedUnitPrice, d.invoice.currency)}` },
                  { key: "r", header: "Reçu", numeric: true, render: (l) => number(l.receivedQuantity, 3) },
                  { key: "i", header: "Facturé", numeric: true, render: (l) => `${number(l.invoicedQuantity, 3)} × ${money(l.invoicedUnitPrice, d.invoice.currency)}` },
                  { key: "s", header: "", render: (l) => (l.status === "matched" ? <Badge tone="success">ok</Badge> : <Badge tone="error">{l.detail}</Badge>) },
                ]}
                rows={report.lines.map((l, i) => ({ ...l, _k: i }))} rowKey={(l) => String(l._k)} />
            </>
          )}
        </div>
      )}
    </Drawer>
  );
}
