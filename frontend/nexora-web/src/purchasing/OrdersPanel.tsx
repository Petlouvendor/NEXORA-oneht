import { useEffect, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateShort, money } from "@/shared/format";
import { Icon } from "@/shared/icons";
import { PrintMenu } from "@/shared/ExportMenu";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, IconButton, Modal, SectionLabel, Select, TextInput,
} from "@/shared/ui";
import { getProduct, type ProductDetail } from "@/catalog/catalogApi";
import {
  approvePurchaseOrder, cancelPurchaseOrder, closePurchaseOrder, createPurchaseOrder, getPurchaseOrder, listPurchaseOrders,
  sendPurchaseOrder, submitPurchaseOrder, updatePurchaseOrder, type PurchaseOrderDetail, type PurchaseOrderInput,
} from "@/purchasing/purchasingApi";
import {
  estimateLine, estimateTotals, knownPurchasePrice, ORDER_STATUS, ORDER_TONE, orderEditable, orderReceivable,
  PurchaseProductPicker, purchaseUnits, qtyText, taxRate, today, type PurchasingRefs,
} from "@/purchasing/purchasingShared";
import { ReceiveDialog } from "@/purchasing/ReceiptsPanel";
import { InvoiceDialog } from "@/purchasing/InvoicesPanel";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Commandes fournisseur : BROUILLON → SOUMISE → APPROUVÉE → ENVOYÉE → REÇUE (partiellement) → FACTURÉE ; soldée ou annulée. */
export function OrdersPanel({ refs, onChanged }: { refs: PurchasingRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const orders = useAsync(() => listPurchaseOrders(status || undefined), [status]);
  const [editing, setEditing] = useState<PurchaseOrderDetail | "new" | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const reload = () => { orders.reload(); onChanged(); };

  return (
    <Card>
      <SectionLabel action={hasPermission("purchasing.order") ? <Button size="sm" onClick={() => setEditing("new")}>Nouvelle commande</Button> : undefined}>
        Commandes fournisseur
      </SectionLabel>
      <div style={{ maxWidth: 240, marginBottom: 10 }}>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(ORDER_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </div>
      {orders.error && <div role="alert" className="nx-error-text">{orders.error}</div>}
      <DataTable exportTitle="Commandes fournisseur"
        columns={[
          { key: "n", header: "N°", render: (o) => <button type="button" style={linkStyle} onClick={() => setOpenId(o.id)}><code>{o.documentNumber}</code></button> },
          { key: "d", header: "Date", render: (o) => dateShort(o.orderDate) },
          { key: "sup", header: "Fournisseur", render: (o) => o.supplierName },
          { key: "s", header: "Statut", render: (o) => <Badge tone={ORDER_TONE[o.status]}>{ORDER_STATUS[o.status] ?? o.status}</Badge> },
          { key: "r", header: "Reçu", numeric: true, render: (o) => `${Math.round(o.receivedRatio * 100)} %` },
          { key: "t", header: "Total", numeric: true, render: (o) => money(o.total, o.currency) },
        ]}
        rows={orders.data ?? []} rowKey={(o) => o.id} empty={orders.loading ? "Chargement…" : "Aucune commande."} />
      {editing && (
        <OrderDialog refs={refs} order={editing === "new" ? null : editing} onClose={() => setEditing(null)}
                     onSaved={(id) => { setEditing(null); reload(); setOpenId(id); }} />
      )}
      {openId && (
        <OrderDrawer key={openId} id={openId} refs={refs} onClose={() => setOpenId(null)} onChanged={reload}
                     onEdit={(o) => { setOpenId(null); setEditing(o); }} />
      )}
    </Card>
  );
}

interface LineState { product: ProductDetail; unitId: string; quantity: string; unitPrice: string; taxId: string; discount: string }

const FIELD_OF_CODE: Record<string, string> = {
  "purchasing.supplier_unknown": "supplier", "purchasing.supplier_inactive": "supplier", "purchasing.currency_unknown": "currency",
  "purchasing.expected_date": "expectedDate", "purchasing.no_lines": "lines",
};

/** Création ou modification (brouillon / soumise) d'une commande. */
export function OrderDialog({ refs, order, onClose, onSaved }: {
  refs: PurchasingRefs; order: PurchaseOrderDetail | null; onClose: () => void; onSaved: (id: string) => void;
}) {
  const toast = useToast();
  const [supplierId, setSupplierId] = useState(order?.supplierId ?? "");
  const [currency, setCurrency] = useState(order?.currency ?? refs.defaultCurrency);
  const [expectedDate, setExpectedDate] = useState(order?.expectedDate ?? "");
  const [notes, setNotes] = useState(order?.notes ?? "");
  const [lines, setLines] = useState<LineState[]>([]);
  const [loadingLines, setLoadingLines] = useState(!!order);
  const [lineErrors, setLineErrors] = useState<Record<number, string>>({});
  const { busy, error, fieldErrors, submit, fail, setError } = useSubmit(FIELD_OF_CODE);
  const supplierName = refs.suppliers.find((s) => s.id === supplierId)?.name ?? null;

  // Modification : on recharge chaque article pour retrouver ses unités d'achat.
  useEffect(() => {
    if (!order) return;
    let alive = true;
    Promise.all(order.lines.map(async (l): Promise<LineState> => {
      const product = await getProduct(l.productId);
      const inPurchaseUnit = l.purchaseUnitId && l.purchaseQuantity;
      const factor = inPurchaseUnit ? l.quantityOrdered / l.purchaseQuantity! : 1;
      return {
        product, unitId: l.purchaseUnitId ?? product.unitId ?? "",
        quantity: String(inPurchaseUnit ? l.purchaseQuantity : l.quantityOrdered),
        unitPrice: String(Math.round(l.unitPrice * factor * 10000) / 10000),
        taxId: l.taxId ?? "", discount: l.discount ? String(l.discount) : "",
      };
    }))
      .then((ls) => { if (alive) setLines(ls); })
      .catch(() => { if (alive) setError("Impossible de recharger les articles de la commande."); })
      .finally(() => { if (alive) setLoadingLines(false); });
    return () => { alive = false; };
  }, [order, setError]);

  const set = (i: number, patch: Partial<LineState>) => setLines((ls) => ls.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  const add = (p: ProductDetail) => {
    const price = knownPurchasePrice(p, supplierName);
    setLines((ls) => [...ls, { product: p, unitId: p.unitId ?? "", quantity: "", unitPrice: price != null ? String(price) : "", taxId: p.defaultTaxId ?? "", discount: "" }]);
  };

  const totals = estimateTotals(lines.map((l) => ({
    quantity: num(l.quantity) ?? 0, unitPrice: num(l.unitPrice) ?? 0, discount: num(l.discount) ?? 0, ratePercent: taxRate(refs.taxes, l.taxId),
  })));

  async function save(e: FormEvent | null, submitOrder: boolean) {
    e?.preventDefault();
    if (!supplierId) return fail("supplier", "Choisissez le fournisseur.");
    if (lines.length === 0) return fail("lines", "Ajoutez au moins un article.");
    if (expectedDate && expectedDate < today()) return fail("expectedDate", "La date prévue est déjà passée.");
    const errs: Record<number, string> = {};
    lines.forEach((l, i) => {
      const q = num(l.quantity), p = num(l.unitPrice), d = num(l.discount) ?? 0;
      if (q == null || q <= 0) errs[i] = "Quantité positive requise.";
      else if (p == null || p < 0) errs[i] = "Prix d'achat invalide.";
      else if (d < 0 || d > q * p) errs[i] = "Remise hors du montant de la ligne.";
    });
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    const body: PurchaseOrderInput = {
      supplierId, warehouseId: order?.warehouseId ?? null, currency, expectedDate: expectedDate || null, notes: notes.trim() || null,
      submit: submitOrder,
      lines: lines.map((l) => ({
        productId: l.product.id, quantity: num(l.quantity)!, unitPrice: num(l.unitPrice)!, taxId: l.taxId || null,
        discount: num(l.discount) ?? 0, unitId: l.unitId && l.unitId !== l.product.unitId ? l.unitId : null,
      })),
    };
    const id = await submit(async () => {
      if (order) { await updatePurchaseOrder(order.id, body); return order.id; }
      return createPurchaseOrder(body);
    });
    if (id) {
      toast.success(order ? "Commande modifiée." : submitOrder ? "Commande créée et soumise à approbation." : "Brouillon enregistré.");
      onSaved(id);
    }
  }

  return (
    <Modal title={order ? `Modifier la commande ${order.documentNumber}` : "Nouvelle commande fournisseur"} onClose={onClose}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Annuler</Button>
        {(!order || order.status === "draft") && <Button variant="subtle" disabled={busy || loadingLines} onClick={() => save(null, false)}>Enregistrer le brouillon</Button>}
        <Button disabled={busy || loadingLines} onClick={() => save(null, true)}>{order?.status === "submitted" ? "Enregistrer" : "Soumettre à approbation"}</Button>
      </>}>
      <form onSubmit={(e) => save(e, true)} style={{ display: "grid", gap: 12, minWidth: "min(860px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12 }}>
          <Field label="Fournisseur" required error={fieldErrors.supplier}>
            <Select value={supplierId} onChange={(e) => setSupplierId(e.target.value)}>
              <option value="">—</option>
              {refs.activeSuppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </Select>
          </Field>
          <Field label="Devise" required error={fieldErrors.currency}>
            <Select value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {(refs.currencies.length ? refs.currencies.map((c) => c.code) : [currency]).map((c) => <option key={c} value={c}>{c}</option>)}
            </Select>
          </Field>
          <Field label="Livraison prévue" error={fieldErrors.expectedDate}>
            <TextInput type="date" min={today()} value={expectedDate} onChange={(e) => setExpectedDate(e.target.value)} />
          </Field>
        </div>
        <Field label="Notes">
          <TextInput value={notes} maxLength={500} onChange={(e) => setNotes(e.target.value)} />
        </Field>
        <PurchaseProductPicker onPick={add} excluded={lines.map((l) => l.product.id)} />
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        {loadingLines ? <div style={{ color: "var(--nx-text-2)" }}>Chargement des lignes…</div> : lines.length === 0 ? (
          <div style={{ color: "var(--nx-text-2)" }}>Aucun article ajouté.</div>
        ) : (
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th><th>Unité</th><th>Quantité</th><th>Prix unitaire</th><th>Remise</th><th>Taxe</th><th className="nx-num">Total estimé</th><th /></tr></thead>
              <tbody>
                {lines.map((l, i) => {
                  const units = purchaseUnits(l.product);
                  const unit = units.find((u) => u.id === l.unitId);
                  const est = estimateLine(num(l.quantity) ?? 0, num(l.unitPrice) ?? 0, num(l.discount) ?? 0, taxRate(refs.taxes, l.taxId));
                  return (
                    <tr key={l.product.id}>
                      <td><code>{l.product.sku}</code> {l.product.name}
                        {unit && unit.factor !== 1 && num(l.quantity) ? (
                          <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>= {qtyText((num(l.quantity) ?? 0) * unit.factor, l.product.unitCode)}</div>
                        ) : null}
                        {lineErrors[i] && <div className="nx-err" role="alert">{lineErrors[i]}</div>}
                      </td>
                      <td style={{ minWidth: 100 }}>
                        <Select value={l.unitId} aria-label={`Unité — ${l.product.name}`} onChange={(e) => set(i, { unitId: e.target.value })}>
                          {units.map((u) => <option key={u.id} value={u.id}>{u.code}{u.factor !== 1 ? ` (${u.factor})` : ""}</option>)}
                        </Select>
                      </td>
                      <td style={{ minWidth: 90 }}><TextInput type="number" step="any" min={0} value={l.quantity} aria-label={`Quantité — ${l.product.name}`}
                        invalid={!!lineErrors[i]} onChange={(e) => set(i, { quantity: e.target.value })} /></td>
                      <td style={{ minWidth: 110 }}><TextInput type="number" step="0.0001" min={0} value={l.unitPrice} aria-label={`Prix unitaire — ${l.product.name}`}
                        onChange={(e) => set(i, { unitPrice: e.target.value })} /></td>
                      <td style={{ minWidth: 90 }}><TextInput type="number" step="0.01" min={0} value={l.discount} aria-label={`Remise — ${l.product.name}`}
                        onChange={(e) => set(i, { discount: e.target.value })} /></td>
                      <td style={{ minWidth: 120 }}>
                        <Select value={l.taxId} aria-label={`Taxe — ${l.product.name}`} onChange={(e) => set(i, { taxId: e.target.value })}>
                          <option value="">Aucune</option>
                          {refs.taxes.map((t) => <option key={t.id} value={t.id}>{t.name} ({t.rate} %)</option>)}
                        </Select>
                      </td>
                      <td className="nx-num">{money(est.total, currency)}</td>
                      <td><IconButton label={`Retirer ${l.product.name}`} onClick={() => setLines((ls) => ls.filter((_, j) => j !== i))}><Icon.X size={14} /></IconButton></td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <div style={{ display: "flex", gap: 16, justifyContent: "flex-end", flexWrap: "wrap" }}>
          <span>Sous-total : <b>{money(totals.subtotal, currency)}</b></span>
          <span>Taxes : <b>{money(totals.tax, currency)}</b></span>
          <span>Total estimé : <b>{money(totals.total, currency)}</b></span>
        </div>
        <div style={{ fontSize: 12, color: "var(--nx-text-2)", textAlign: "right" }}>Les montants définitifs sont recalculés par le serveur.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

type OrderAction = "submit" | "approve" | "send" | "cancel" | "close";
const ACTION_TEXT: Record<OrderAction, { title: string; message: string; label: string; done: string; danger?: boolean }> = {
  submit: { title: "Soumettre la commande", message: "La commande part en approbation.", label: "Soumettre", done: "Commande soumise." },
  approve: { title: "Approuver la commande", message: "Une commande approuvée n'est plus modifiable.", label: "Approuver", done: "Commande approuvée." },
  send: { title: "Envoyer au fournisseur", message: "La commande est marquée comme transmise au fournisseur.", label: "Marquer envoyée", done: "Commande envoyée." },
  cancel: { title: "Annuler la commande", message: "La commande est annulée : elle ne pourra plus être reçue.", label: "Annuler la commande", done: "Commande annulée.", danger: true },
  close: { title: "Solder la commande", message: "Le reste à recevoir est abandonné ; la commande est clôturée.", label: "Solder", done: "Commande soldée.", danger: true },
};
const ACTION_CALL: Record<OrderAction, (id: string) => Promise<void>> = {
  submit: submitPurchaseOrder, approve: approvePurchaseOrder, send: sendPurchaseOrder, cancel: cancelPurchaseOrder, close: closePurchaseOrder,
};

export function OrderDrawer({ id, refs, onClose, onChanged, onEdit }: {
  id: string; refs: PurchasingRefs; onClose: () => void; onChanged: () => void; onEdit: (o: PurchaseOrderDetail) => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const detail = useAsync(() => getPurchaseOrder(id), [id]);
  const [confirm, setConfirm] = useState<OrderAction | null>(null);
  const [receiving, setReceiving] = useState(false);
  const [invoicing, setInvoicing] = useState(false);
  const { busy, error, submit } = useSubmit();
  const o = detail.data;
  const refresh = () => { detail.reload(); onChanged(); };

  async function run(action: OrderAction) {
    setConfirm(null);
    const ok = await submit(async () => { await ACTION_CALL[action](id); return true; });
    if (ok) { toast.success(ACTION_TEXT[action].done); refresh(); }
  }

  const canOrder = hasPermission("purchasing.order");
  const actions: Array<{ key: OrderAction; show: boolean }> = o ? [
    { key: "submit", show: canOrder && o.status === "draft" },
    { key: "approve", show: hasPermission("purchasing.approve_order") && o.status === "submitted" },
    { key: "send", show: canOrder && o.status === "approved" },
    { key: "close", show: canOrder && o.status === "partially_received" },
    { key: "cancel", show: canOrder && ["draft", "submitted", "approved", "sent"].includes(o.status) },
  ] : [];
  const invoiced = (o?.invoices ?? []).some((i) => i.status !== "cancelled");

  return (
    <Drawer title={o ? `Commande ${o.documentNumber}` : "Commande"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {!o ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 14 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <Badge tone={ORDER_TONE[o.status]}>{ORDER_STATUS[o.status] ?? o.status}</Badge>
            <span>{o.supplierName}</span>
            <span style={{ color: "var(--nx-text-2)" }}>· {dateShort(o.orderDate)}{o.expectedDate ? ` · livraison prévue ${dateShort(o.expectedDate)}` : ""}</span>
          </div>
          {o.notes && <div style={{ color: "var(--nx-text-2)" }}>{o.notes}</div>}
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {canOrder && orderEditable(o.status) && <Button size="sm" variant="subtle" onClick={() => onEdit(o)}>Modifier</Button>}
            {actions.filter((a) => a.show).map((a) => (
              <Button key={a.key} size="sm" variant={ACTION_TEXT[a.key].danger ? "ghost" : "subtle"} disabled={busy} onClick={() => setConfirm(a.key)}>
                {ACTION_TEXT[a.key].label}
              </Button>
            ))}
            {hasPermission("purchasing.receive") && orderReceivable(o.status) && <Button size="sm" onClick={() => setReceiving(true)}>Réceptionner</Button>}
            {hasPermission("purchasing.invoice") && ["partially_received", "received"].includes(o.status) && !invoiced
              && <Button size="sm" variant="subtle" onClick={() => setInvoicing(true)}>Saisir la facture</Button>}
            <PrintMenu kind="purchase-order" id={o.id} label="Imprimer" />
          </div>
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th><th className="nx-num">Commandé</th><th className="nx-num">Reçu</th><th className="nx-num">Reste</th><th className="nx-num">Prix</th><th className="nx-num">Taxe</th><th className="nx-num">Total</th></tr></thead>
              <tbody>
                {o.lines.map((l) => (
                  <tr key={l.id}>
                    <td><code>{l.sku}</code> {l.productName}</td>
                    <td className="nx-num">{qtyText(l.quantityOrdered, l.baseUnitCode)}
                      {l.purchaseUnitCode && l.purchaseQuantity != null && <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{qtyText(l.purchaseQuantity, l.purchaseUnitCode)}</div>}
                    </td>
                    <td className="nx-num">{qtyText(l.quantityReceived)}</td>
                    <td className="nx-num">{qtyText(l.remaining)}</td>
                    <td className="nx-num">{money(l.unitPrice, o.currency)}</td>
                    <td className="nx-num">{money(l.taxAmount, o.currency)}</td>
                    <td className="nx-num">{money(l.lineTotal + l.taxAmount, o.currency)}</td>
                  </tr>
                ))}
              </tbody>
              <tfoot>
                <tr><td colSpan={5}>Sous-total {money(o.subtotal, o.currency)} · taxes {money(o.taxTotal, o.currency)}</td>
                  <td colSpan={2} className="nx-num"><b>{money(o.total, o.currency)}</b></td></tr>
              </tfoot>
            </table>
          </div>
          <SectionLabel>Réceptions ({o.receipts.length})</SectionLabel>
          {o.receipts.length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>Aucune réception.</div> : o.receipts.map((r) => (
            <div key={r.id} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <code>{r.documentNumber}</code> {dateShort(r.receivedAt)} · {qtyText(r.quantityAccepted)} accepté(s)
              {r.quantityRejected > 0 && <Badge tone="warning">{qtyText(r.quantityRejected)} refusé(s)</Badge>}
              <PrintMenu kind="goods-receipt" id={r.id} iconOnly label="Imprimer le bon de réception" />
            </div>
          ))}
          <SectionLabel>Factures ({o.invoices.length})</SectionLabel>
          {o.invoices.length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>Aucune facture.</div> : o.invoices.map((i) => (
            <div key={i.id} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <code>{i.documentNumber}</code> {i.supplierReference ?? ""} · {money(i.total, i.currency)}
              {i.paymentBlocked && <Badge tone="error">paiement bloqué</Badge>}
            </div>
          ))}
        </div>
      )}
      {confirm && (
        <ConfirmDialog title={ACTION_TEXT[confirm].title} message={ACTION_TEXT[confirm].message} confirmLabel={ACTION_TEXT[confirm].label}
                       danger={ACTION_TEXT[confirm].danger} onCancel={() => setConfirm(null)} onConfirm={() => run(confirm)} />
      )}
      {receiving && o && (
        <ReceiveDialog refs={refs} order={o} onClose={() => setReceiving(false)}
                       onDone={() => { setReceiving(false); refresh(); }} />
      )}
      {invoicing && o && (
        <InvoiceDialog refs={refs} order={o} onClose={() => setInvoicing(false)}
                       onDone={() => { setInvoicing(false); refresh(); }} />
      )}
    </Drawer>
  );
}
