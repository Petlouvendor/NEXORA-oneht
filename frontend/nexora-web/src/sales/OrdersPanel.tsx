import { useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money, number, todayIso } from "@/shared/format";
import { round2 } from "@/shared/amounts";
import { Icon } from "@/shared/icons";
import { PrintMenu } from "@/shared/ExportMenu";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, IconButton, Modal, SectionLabel, Select, TextInput,
} from "@/shared/ui";
import {
  cancelOrder, completeOrder, confirmOrder, createOrder, getOrder, getOrderPaymentOptions, listOrders, recordDeposit,
  reserveOrder, updateOrder, type CreateOrderRequest, type Order,
} from "@/sales/ordersApi";
import { DOCUMENT_TYPE_LABEL, issueDocument, openDocumentPdf, searchDocuments } from "@/documents/documentsApi";
import {
  CustomerSelect, estimateSales, lineFromHit, ORDER_STATUS, ORDER_TONE, ORDER_TYPE, orderDeliverable, orderOpen,
  SalesLinesEditor, SalesProductPicker, toLineInputs, validateLines, type PickedCustomer, type SalesLine, type SalesRefs,
} from "@/sales/salesShared";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Commandes clients : BROUILLON → CONFIRMÉE → (STOCK RÉSERVÉ) → LIVRÉE (en une ou plusieurs fois) ; acompte à tout moment. */
export function OrdersPanel({ refs, openId, onOpen }: { refs: SalesRefs; openId: string | null; onOpen: (id: string | null) => void }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const orders = useAsync(() => listOrders(status || undefined, 200), [status]);
  const [editing, setEditing] = useState<Order | "new" | null>(null);
  const canManage = hasPermission("pos.manage_order");

  return (
    <Card>
      <SectionLabel action={canManage ? <Button size="sm" onClick={() => setEditing("new")}>Nouvelle commande</Button> : undefined}>
        Commandes clients
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
      <DataTable exportTitle="Commandes clients"
        columns={[
          { key: "n", header: "N°", render: (o) => <button type="button" style={linkStyle} onClick={() => onOpen(o.id)}><code>{o.documentNumber}</code></button> },
          { key: "d", header: "Date", render: (o) => dateShort(o.createdAt) },
          { key: "c", header: "Client", render: (o) => o.customerName ?? "—" },
          { key: "t", header: "Type", render: (o) => ORDER_TYPE[o.orderType] ?? o.orderType },
          { key: "s", header: "Statut", render: (o) => <Badge tone={ORDER_TONE[o.status]}>{ORDER_STATUS[o.status] ?? o.status}</Badge> },
          { key: "tot", header: "Total", numeric: true, render: (o) => money(o.total, o.currency) },
          { key: "dep", header: "Acompte", numeric: true, render: (o) => (o.depositAmount > 0 ? money(o.depositAmount, o.currency) : "—") },
        ]}
        rows={orders.data ?? []} rowKey={(o) => o.id} empty={orders.loading ? "Chargement…" : "Aucune commande."} />
      {editing && (
        <OrderDialog refs={refs} order={editing === "new" ? null : editing} onClose={() => setEditing(null)}
                     onSaved={(id) => { setEditing(null); orders.reload(); onOpen(id); }} />
      )}
      {openId && (
        <OrderDrawer key={openId} id={openId} onClose={() => onOpen(null)} onChanged={orders.reload}
                     onEdit={(o) => { onOpen(null); setEditing(o); }} />
      )}
    </Card>
  );
}

const ORDER_FIELDS: Record<string, string> = {
  "order.customer_required": "customer", "order.customer_unknown": "customer", "order.customer_inactive": "customer",
  "order.no_location": "location", "order.location_unknown": "location", "order.currency_unknown": "currency",
  "order.due_date": "dueDate", "order.no_lines": "lines", "order.duplicate_line": "lines", "order.quantity": "lines",
};

export function OrderDialog({ refs, order, onClose, onSaved }: {
  refs: SalesRefs; order: Order | null; onClose: () => void; onSaved: (id: string) => void;
}) {
  const toast = useToast();
  const [customer, setCustomer] = useState<PickedCustomer | null>(order?.customerId ? { id: order.customerId, name: order.customerName ?? "Client" } : null);
  const [orderType, setOrderType] = useState(order?.orderType ?? "pickup");
  const [locationId, setLocationId] = useState(order?.inventoryLocationId ?? "");
  const [currency, setCurrency] = useState(order?.currency ?? refs.defaultCurrency);
  const [expectedAt, setExpectedAt] = useState(order?.expectedAt ? order.expectedAt.slice(0, 16) : "");
  const [onCredit, setOnCredit] = useState(order?.onCredit ?? false);
  const [dueDate, setDueDate] = useState(order?.dueDate ?? "");
  const [address, setAddress] = useState(order?.deliveryAddress ?? "");
  const [notes, setNotes] = useState(order?.notes ?? "");
  const [lines, setLines] = useState<SalesLine[]>(() => (order?.lines ?? []).map((l) => ({
    productId: l.productId, sku: "", name: l.description, catalogPrice: l.unitPrice,
    quantity: String(l.quantity), unitPrice: "", discount: l.discount ? String(l.discount) : "", taxId: "",
  })));
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const { busy, error, fieldErrors, submit, fail } = useSubmit(ORDER_FIELDS);
  const totals = estimateSales(lines, refs.taxes);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (onCredit && !customer) return fail("customer", "Un client est obligatoire pour une commande à crédit.");
    if (!locationId) return fail("location", "Choisissez l'emplacement qui servira la commande.");
    if (lines.length === 0) return fail("lines", "Ajoutez au moins un article.");
    const errs = validateLines(lines);
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    const body: CreateOrderRequest = {
      customerId: customer?.id ?? null, currency, orderType, inventoryLocationId: locationId, onCredit,
      expectedAt: expectedAt ? new Date(expectedAt).toISOString() : null, dueDate: onCredit && dueDate ? dueDate : null,
      deliveryAddress: orderType === "delivery" ? address.trim() || null : null, notes: notes.trim() || null,
      lines: toLineInputs(lines).map((l) => ({ ...l, unitPriceOverride: l.unitPriceOverride })),
    };
    const id = await submit(async () => {
      if (order) { await updateOrder(order.id, body); return order.id; }
      return createOrder(body);
    });
    if (id) { toast.success(order ? "Commande modifiée." : "Commande enregistrée en brouillon."); onSaved(id); }
  }

  return (
    <Modal title={order ? `Modifier la commande ${order.documentNumber}` : "Nouvelle commande client"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(860px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 12 }}>
          <CustomerSelect value={customer} onChange={setCustomer} error={fieldErrors.customer} required={onCredit}
                          disabled={!!order && order.depositAmount > 0} />
          <Field label="Type">
            <Select value={orderType} onChange={(e) => setOrderType(e.target.value)}>
              {Object.entries(ORDER_TYPE).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
          <Field label="Servie depuis" required error={fieldErrors.location}>
            <Select value={locationId} onChange={(e) => setLocationId(e.target.value)}>
              <option value="">—</option>
              {refs.locations.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </Select>
          </Field>
          <Field label="Devise" required error={fieldErrors.currency}>
            <Select value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {(refs.currencies.length ? refs.currencies.map((c) => c.code) : [currency]).map((c) => <option key={c} value={c}>{c}</option>)}
            </Select>
          </Field>
          <Field label="Retrait / livraison prévu">
            <TextInput type="datetime-local" value={expectedAt} onChange={(e) => setExpectedAt(e.target.value)} />
          </Field>
          <Field label="Règlement">
            <Select value={onCredit ? "credit" : "cash"} onChange={(e) => setOnCredit(e.target.value === "credit")}>
              <option value="cash">Au comptant à la livraison</option>
              <option value="credit">À crédit (client obligatoire)</option>
            </Select>
          </Field>
          {onCredit && (
            <Field label="Échéance du crédit" error={fieldErrors.dueDate}>
              <TextInput type="date" min={todayIso()} value={dueDate} onChange={(e) => setDueDate(e.target.value)} />
            </Field>
          )}
        </div>
        {orderType === "delivery" && (
          <Field label="Adresse de livraison"><TextInput value={address} maxLength={500} onChange={(e) => setAddress(e.target.value)} /></Field>
        )}
        <Field label="Notes"><TextInput value={notes} maxLength={1000} onChange={(e) => setNotes(e.target.value)} /></Field>
        <SalesProductPicker onPick={(h) => setLines((ls) => [...ls, lineFromHit(h)])} excluded={lines.map((l) => l.productId)} />
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        <SalesLinesEditor lines={lines} onChange={setLines} taxes={refs.taxes} currency={currency} errors={lineErrors} />
        <div style={{ display: "flex", gap: 16, justifyContent: "flex-end", flexWrap: "wrap" }}>
          <span>Estimation HT : <b>{money(totals.subtotal, currency)}</b></span>
          {totals.tax > 0 && <span>Taxes choisies : <b>{money(totals.tax, currency)}</b></span>}
        </div>
        <div style={{ fontSize: 12, color: "var(--nx-text-2)", textAlign: "right" }}>
          Prix, promotions et taxes de l'article sont appliqués et figés par le serveur à l'enregistrement.
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

type Action = "confirm" | "reserve";

export function OrderDrawer({ id, onClose, onChanged, onEdit }: {
  id: string; onClose: () => void; onChanged: () => void; onEdit: (o: Order) => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const detail = useAsync(() => getOrder(id), [id]);
  const docs = useAsync(() => (hasPermission("documents.view") ? searchDocuments({ sourceType: "sales_order", sourceId: id }) : Promise.resolve([])), [id]);
  const [dialog, setDialog] = useState<"deposit" | "deliver" | "cancel" | null>(null);
  const { busy, error, submit } = useSubmit();
  const o = detail.data;
  const refresh = () => { detail.reload(); docs.reload(); onChanged(); };
  const canManage = hasPermission("pos.manage_order");
  const canIssue = hasPermission("documents.issue");

  async function run(action: Action) {
    const ok = await submit(async () => { await (action === "confirm" ? confirmOrder(id) : reserveOrder(id)); return true; });
    if (ok) { toast.success(action === "confirm" ? "Commande confirmée." : "Stock réservé pour la commande."); refresh(); }
  }

  async function issue(type: string) {
    const d = await submit(() => issueDocument(type, "sales_order", id));
    if (d) { toast.success(`${DOCUMENT_TYPE_LABEL[type] ?? "Document"} ${d.documentNumber} émis.`); docs.reload(); }
  }

  return (
    <Drawer title={o ? `Commande ${o.documentNumber}` : "Commande"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {!o ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 14 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <Badge tone={ORDER_TONE[o.status]}>{ORDER_STATUS[o.status] ?? o.status}</Badge>
            <Badge>{ORDER_TYPE[o.orderType] ?? o.orderType}</Badge>
            {o.onCredit && <Badge tone="warning">à crédit</Badge>}
          </div>
          <div style={{ display: "grid", gap: 2 }}>
            <div>Client : {o.customerId && hasPermission("customers.view")
              ? <Link to={`/customers/${o.customerId}`}>{o.customerName ?? "—"}</Link> : o.customerName ?? "Client de passage"}</div>
            <div>Servie depuis : {o.locationName ?? <span className="nx-error-text">aucun emplacement — à compléter</span>}</div>
            {o.expectedAt && <div>Prévue le : {dateTime(o.expectedAt)}</div>}
            {o.deliveryAddress && <div>Livraison : {o.deliveryAddress}</div>}
            {o.notes && <div style={{ color: "var(--nx-text-2)" }}>{o.notes}</div>}
            {o.cancelledReason && <div className="nx-error-text">Annulée : {o.cancelledReason}</div>}
          </div>
          <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
            <Metric label="Total" value={money(o.total, o.currency)} />
            <Metric label="Acompte versé" value={money(o.depositAmount, o.currency)} />
            <Metric label="Acompte disponible" value={money(o.depositAvailable, o.currency)} />
          </div>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {canManage && o.status === "draft" && <Button size="sm" variant="subtle" onClick={() => onEdit(o)}>Modifier</Button>}
            {canManage && o.status === "draft" && <Button size="sm" disabled={busy} onClick={() => run("confirm")}>Confirmer</Button>}
            {canManage && o.status === "confirmed" && <Button size="sm" variant="subtle" disabled={busy || !o.inventoryLocationId} onClick={() => run("reserve")}>Réserver le stock</Button>}
            {canManage && orderOpen(o.status) && o.customerId && o.remainingBalance > 0 &&
              <Button size="sm" variant="subtle" onClick={() => setDialog("deposit")}>Encaisser un acompte</Button>}
            {canManage && orderDeliverable(o.status) && <Button size="sm" onClick={() => setDialog("deliver")}>Livrer</Button>}
            {canManage && orderOpen(o.status) && <Button size="sm" variant="ghost" onClick={() => setDialog("cancel")}>Annuler la commande</Button>}
            <PrintMenu kind="sales-order" id={o.id} label="Imprimer" />
          </div>

          <SectionLabel>Lignes</SectionLabel>
          <DataTable
            columns={[
              { key: "d", header: "Article", render: (l) => l.description },
              { key: "q", header: "Livré / commandé", numeric: true, render: (l) => `${number(l.quantityFulfilled, 3)} / ${number(l.quantity, 3)}` },
              { key: "p", header: "P.U.", numeric: true, render: (l) => money(l.unitPrice, o.currency) },
              { key: "r", header: "Remise", numeric: true, render: (l) => (l.discount ? money(l.discount, o.currency) : "—") },
              { key: "t", header: "Total TTC", numeric: true, render: (l) => money(l.lineTotal, o.currency) },
            ]}
            rows={o.lines} rowKey={(l) => l.id} />

          <SectionLabel>Ventes générées ({o.sales?.length ?? 0})</SectionLabel>
          {(o.sales ?? []).length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>Aucune livraison pour l'instant.</div> : (o.sales ?? []).map((s) => (
            <div key={s.id} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <Link to={`/sales/${s.id}`}><code>{s.documentNumber}</code></Link> {dateTime(s.soldAt)} · {money(s.total, o.currency)}
              {s.amountDue > 0 ? <Badge tone="warning">reste dû {money(s.amountDue, o.currency)}</Badge> : <Badge tone="success">réglée</Badge>}
            </div>
          ))}

          {hasPermission("documents.view") && (
            <>
              <SectionLabel action={canIssue ? (
                <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                  <Button size="sm" variant="ghost" disabled={busy} onClick={() => issue("proforma")}>Proforma</Button>
                  {o.depositAmount > 0 && <Button size="sm" variant="ghost" disabled={busy} onClick={() => issue("deposit_invoice")}>Facture d'acompte</Button>}
                  {o.orderType === "delivery" && <Button size="sm" variant="ghost" disabled={busy} onClick={() => issue("delivery_note")}>Bon de livraison</Button>}
                </div>) : undefined}>
                Documents émis
              </SectionLabel>
              {(docs.data ?? []).length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>Aucun document.</div> : (docs.data ?? []).map((d) => (
                <div key={d.id} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                  <Badge>{DOCUMENT_TYPE_LABEL[d.documentType] ?? d.documentType}</Badge><code>{d.documentNumber}</code>
                  {d.status === "cancelled" && <Badge tone="error">annulé</Badge>}
                  <IconButton label={`Ouvrir ${d.documentNumber} (PDF)`} onClick={() => void openDocumentPdf(d.id)}><Icon.Printer size={14} /></IconButton>
                </div>
              ))}
            </>
          )}
        </div>
      )}
      {dialog === "deposit" && o && <DepositDialog order={o} onClose={() => setDialog(null)} onDone={() => { setDialog(null); refresh(); }} />}
      {dialog === "deliver" && o && <DeliverDialog order={o} onClose={() => setDialog(null)} onDone={() => { setDialog(null); refresh(); }} />}
      {dialog === "cancel" && o && (
        <ConfirmDialog title={`Annuler la commande ${o.documentNumber}`} danger requireReason confirmLabel="Annuler la commande"
          message="La commande est annulée ; le stock réservé est libéré."
          impact={o.depositAvailable > 0 ? <>L'acompte de <b>{money(o.depositAvailable, o.currency)}</b> reste un avoir du client (utilisable ou remboursable depuis son compte).</> : undefined}
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            setDialog(null);
            const ok = await submit(async () => { await cancelOrder(o.id, reason); return true; });
            if (ok) { toast.success("Commande annulée."); refresh(); }
          }} />
      )}
    </Drawer>
  );
}

function Metric({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{label}</div>
      <div style={{ fontSize: 18 }} className="nx-numeric">{value}</div>
    </div>
  );
}

function DepositDialog({ order, onClose, onDone }: { order: Order; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const options = useAsync(getOrderPaymentOptions, []);
  const [operationUuid] = useState(newOperationId);
  const [amount, setAmount] = useState("");
  const [accountId, setAccountId] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "order.deposit_amount": "amount", "order.deposit_exceeds": "amount" });
  const accounts = (options.data?.accounts ?? []).filter((a) => a.currency === order.currency);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    if (a == null || a <= 0) return fail("amount", "Montant positif requis.");
    if (a > order.remainingBalance) return fail("amount", `Au plus ${money(order.remainingBalance, order.currency)}.`);
    if (!accountId) return fail("account", "Choisissez le compte d'encaissement.");
    const ok = await submit(async () => { await recordDeposit(order.id, a, accountId, operationUuid); return true; });
    if (ok) { toast.success("Acompte encaissé : il figure au compte du client."); onDone(); }
  }

  return (
    <Modal title={`Acompte — commande ${order.documentNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Encaisser</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(420px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {options.error && <div role="alert" className="nx-error-text">{options.error}</div>}
        <div>Reste à couvrir : <b>{money(order.remainingBalance, order.currency)}</b></div>
        <Field label={`Montant (${order.currency})`} required error={fieldErrors.amount}>
          <TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        <Field label="Encaissé sur" required error={fieldErrors.account}>
          <Select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            <option value="">—</option>
            {accounts.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </Select>
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

interface TenderRow { key: number; accountId: string; methodId: string; amount: string }

function DeliverDialog({ order, onClose, onDone }: { order: Order; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const options = useAsync(getOrderPaymentOptions, []);
  const [operationUuid] = useState(newOperationId);
  const outstanding = order.lines.filter((l) => l.quantity - l.quantityFulfilled > 0);
  const [qty, setQty] = useState<Record<string, string>>(() => Object.fromEntries(outstanding.map((l) => [l.id, String(l.quantity - l.quantityFulfilled)])));
  const [onCredit, setOnCredit] = useState(order.onCredit);
  const [dueDate, setDueDate] = useState(order.dueDate ?? "");
  const [tenders, setTenders] = useState<TenderRow[]>([{ key: 1, accountId: "", methodId: "", amount: "" }]);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "order.payment_incomplete": "tenders", "order.over_fulfil": "lines" });
  const accounts = (options.data?.accounts ?? []).filter((a) => a.currency === order.currency);
  const methods = options.data?.methods ?? [];

  const estimated = round2(outstanding.reduce((a, l) => a + (num(qty[l.id] ?? "") ?? 0) * (l.lineTotal / l.quantity), 0));
  const deposit = Math.min(order.depositAvailable, estimated);
  const toCollect = round2(estimated - deposit);
  const tendered = round2(tenders.reduce((a, t) => a + (num(t.amount) ?? 0), 0));
  const setTender = (key: number, patch: Partial<TenderRow>) => setTenders((ts) => ts.map((t) => (t.key === key ? { ...t, ...patch } : t)));

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const lineQuantities: Record<string, number> = {};
    for (const l of outstanding) {
      const q = num(qty[l.id] ?? "") ?? 0;
      if (q < 0 || q > l.quantity - l.quantityFulfilled + 1e-9) return fail("lines", `${l.description} : au plus ${number(l.quantity - l.quantityFulfilled, 3)}.`);
      lineQuantities[l.id] = q;
    }
    if (Object.values(lineQuantities).every((q) => q === 0)) return fail("lines", "Aucune quantité à livrer.");
    const used = tenders.filter((t) => (num(t.amount) ?? 0) > 0);
    if (used.some((t) => !t.accountId)) return fail("tenders", "Choisissez le compte de chaque encaissement.");
    if (!onCredit && tendered + 1e-9 < toCollect) return fail("tenders", `Il reste ${money(round2(toCollect - tendered), order.currency)} à encaisser (ou livrez à crédit).`);
    const r = await submit(() => completeOrder(order.id,
      used.map((t) => ({ financialAccountId: t.accountId, paymentMethodId: t.methodId || null, amount: num(t.amount)! })),
      onCredit, onCredit ? dueDate || null : null, lineQuantities, operationUuid));
    if (r) {
      toast.success(`Livraison enregistrée : vente ${r.documentNumber}${r.creditAmount > 0 ? ` (reste dû ${money(r.creditAmount, order.currency)})` : ""}.`);
      onDone();
    }
  }

  return (
    <Modal title={`Livrer la commande ${order.documentNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Valider la livraison</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(720px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {options.error && <div role="alert" className="nx-error-text">{options.error}</div>}
        <div className="nx-scroll-x">
          <table className="nx-table">
            <thead><tr><th>Article</th><th className="nx-num">Reste à livrer</th><th>Livré maintenant</th></tr></thead>
            <tbody>
              {outstanding.map((l) => (
                <tr key={l.id}>
                  <td>{l.description}</td>
                  <td className="nx-num">{number(l.quantity - l.quantityFulfilled, 3)}</td>
                  <td style={{ maxWidth: 140 }}><TextInput type="number" step="any" min={0} value={qty[l.id] ?? ""} aria-label={`Quantité livrée — ${l.description}`}
                    onChange={(e) => setQty((m) => ({ ...m, [l.id]: e.target.value }))} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
          <Metric label="Montant livré (estimé)" value={money(estimated, order.currency)} />
          <Metric label="Acompte imputé" value={money(deposit, order.currency)} />
          <Metric label="À encaisser" value={money(toCollect, order.currency)} />
        </div>
        {order.customerId && (
          <Field label="Règlement du reste">
            <Select value={onCredit ? "credit" : "cash"} onChange={(e) => setOnCredit(e.target.value === "credit")}>
              <option value="cash">Encaissé maintenant</option>
              <option value="credit">À crédit (le non encaissé devient une créance)</option>
            </Select>
          </Field>
        )}
        {onCredit && (
          <Field label="Échéance"><TextInput type="date" min={todayIso()} value={dueDate} onChange={(e) => setDueDate(e.target.value)} /></Field>
        )}
        <SectionLabel action={<Button size="sm" variant="ghost" onClick={() => setTenders((ts) => [...ts, { key: Date.now(), accountId: "", methodId: "", amount: "" }])}>+ Moyen de paiement</Button>}>
          Encaissements
        </SectionLabel>
        {tenders.map((t) => (
          <div key={t.key} style={{ display: "grid", gridTemplateColumns: "2fr 2fr 1fr auto", gap: 8, alignItems: "end" }}>
            <Field label="Compte">
              <Select value={t.accountId} onChange={(e) => setTender(t.key, { accountId: e.target.value })}>
                <option value="">—</option>
                {accounts.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
              </Select>
            </Field>
            <Field label="Moyen">
              <Select value={t.methodId} onChange={(e) => setTender(t.key, { methodId: e.target.value })}>
                <option value="">—</option>
                {methods.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
              </Select>
            </Field>
            <Field label="Montant"><TextInput type="number" min={0} step="0.01" value={t.amount} onChange={(e) => setTender(t.key, { amount: e.target.value })} /></Field>
            <IconButton label="Retirer ce paiement" disabled={tenders.length === 1} onClick={() => setTenders((ts) => ts.filter((x) => x.key !== t.key))}><Icon.X size={14} /></IconButton>
          </div>
        ))}
        <div>Total encaissé : <b>{money(tendered, order.currency)}</b></div>
        {fieldErrors.tenders && <div className="nx-err" role="alert">{fieldErrors.tenders}</div>}
        <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
          Le serveur recalcule le montant livré, impute l'acompte disponible, puis crée la vente (stock et comptabilité).
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
