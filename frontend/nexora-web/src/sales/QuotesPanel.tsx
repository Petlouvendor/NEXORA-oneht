import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, money, number, todayIso } from "@/shared/format";
import { PrintMenu } from "@/shared/ExportMenu";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  acceptQuote, cancelQuote, convertQuote, createQuote, getQuote, listQuotes, rejectQuote, sendQuote, updateQuote,
  type Quote, type QuoteInput,
} from "@/documents/documentsApi";
import {
  CustomerSelect, estimateSales, lineFromHit, ORDER_TYPE, QUOTE_STATUS, QUOTE_TONE, SalesLinesEditor, SalesProductPicker,
  toLineInputs, validateLines, type PickedCustomer, type SalesLine, type SalesRefs,
} from "@/sales/salesShared";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Devis : BROUILLON (modifiable) → ENVOYÉ → ACCEPTÉ / REFUSÉ / EXPIRÉ → CONVERTI en commande ; annulable avec motif. */
export function QuotesPanel({ refs, onOrderCreated }: { refs: SalesRefs; onOrderCreated: (orderId: string) => void }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const quotes = useAsync(() => listQuotes(status || undefined), [status]);
  const [editing, setEditing] = useState<Quote | "new" | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

  return (
    <Card>
      <SectionLabel action={hasPermission("documents.create_quote") ? <Button size="sm" onClick={() => setEditing("new")}>Nouveau devis</Button> : undefined}>
        Devis
      </SectionLabel>
      <div style={{ maxWidth: 240, marginBottom: 10 }}>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(QUOTE_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </div>
      {quotes.error && <div role="alert" className="nx-error-text">{quotes.error}</div>}
      <DataTable exportTitle="Devis"
        columns={[
          { key: "n", header: "N°", render: (q) => <button type="button" style={linkStyle} onClick={() => setOpenId(q.id)}><code>{q.documentNumber}</code></button> },
          { key: "d", header: "Date", render: (q) => dateShort(q.quoteDate) },
          { key: "c", header: "Client", render: (q) => q.customerName ?? "—" },
          { key: "v", header: "Validité", render: (q) => (q.validUntil ? dateShort(q.validUntil) : "—") },
          { key: "s", header: "Statut", render: (q) => <Badge tone={QUOTE_TONE[q.status]}>{QUOTE_STATUS[q.status] ?? q.status}</Badge> },
          { key: "t", header: "Total", numeric: true, render: (q) => money(q.total, q.currency) },
        ]}
        rows={quotes.data ?? []} rowKey={(q) => q.id} empty={quotes.loading ? "Chargement…" : "Aucun devis."} />
      {editing && (
        <QuoteDialog refs={refs} quote={editing === "new" ? null : editing} onClose={() => setEditing(null)}
                     onSaved={(id) => { setEditing(null); quotes.reload(); setOpenId(id); }} />
      )}
      {openId && (
        <QuoteDrawer key={openId} id={openId} refs={refs} onClose={() => setOpenId(null)} onChanged={quotes.reload}
                     onEdit={(q) => { setOpenId(null); setEditing(q); }}
                     onConverted={(orderId) => { setOpenId(null); quotes.reload(); onOrderCreated(orderId); }} />
      )}
    </Card>
  );
}

const QUOTE_FIELDS: Record<string, string> = {
  "quote.customer_not_found": "customer", "quote.valid_until": "validUntil", "quote.currency_unknown": "currency",
  "quote.no_lines": "lines", "quote.duplicate_line": "lines", "quote.quantity": "lines",
};

function QuoteDialog({ refs, quote, onClose, onSaved }: {
  refs: SalesRefs; quote: Quote | null; onClose: () => void; onSaved: (id: string) => void;
}) {
  const toast = useToast();
  const [customer, setCustomer] = useState<PickedCustomer | null>(quote?.customerId ? { id: quote.customerId, name: quote.customerName ?? "Client" } : null);
  const [currency, setCurrency] = useState(quote?.currency ?? refs.defaultCurrency);
  const [validUntil, setValidUntil] = useState(quote?.validUntil ?? "");
  const [terms, setTerms] = useState(quote?.terms ?? "");
  const [notes, setNotes] = useState(quote?.notes ?? "");
  const [lines, setLines] = useState<SalesLine[]>(() => (quote?.lines ?? []).map((l) => ({
    productId: l.productId, sku: "", name: l.description, catalogPrice: l.unitPrice,
    quantity: String(l.quantity), unitPrice: "", discount: l.discount ? String(l.discount) : "", taxId: "",
  })));
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const { busy, error, fieldErrors, submit, fail } = useSubmit(QUOTE_FIELDS);
  const totals = estimateSales(lines, refs.taxes);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (validUntil && validUntil < todayIso()) return fail("validUntil", "La date de validité est déjà passée.");
    if (lines.length === 0) return fail("lines", "Ajoutez au moins un article.");
    const errs = validateLines(lines);
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    const body: QuoteInput = {
      customerId: customer?.id ?? null, currency, inventoryLocationId: null, validUntil: validUntil || null,
      terms: terms.trim() || null, notes: notes.trim() || null, lines: toLineInputs(lines),
    };
    const id = await submit(async () => {
      if (quote) { await updateQuote(quote.id, body); return quote.id; }
      return createQuote(body);
    });
    if (id) { toast.success(quote ? "Devis modifié." : "Devis enregistré en brouillon."); onSaved(id); }
  }

  return (
    <Modal title={quote ? `Modifier le devis ${quote.documentNumber}` : "Nouveau devis"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(860px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 12 }}>
          <CustomerSelect value={customer} onChange={setCustomer} error={fieldErrors.customer} />
          <Field label="Devise" required error={fieldErrors.currency}>
            <Select value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {(refs.currencies.length ? refs.currencies.map((c) => c.code) : [currency]).map((c) => <option key={c} value={c}>{c}</option>)}
            </Select>
          </Field>
          <Field label="Valable jusqu'au" error={fieldErrors.validUntil}>
            <TextInput type="date" min={todayIso()} value={validUntil} onChange={(e) => setValidUntil(e.target.value)} />
          </Field>
        </div>
        <Field label="Conditions (paiement, livraison…)"><TextInput value={terms} maxLength={1000} onChange={(e) => setTerms(e.target.value)} /></Field>
        <Field label="Notes internes"><TextInput value={notes} maxLength={1000} onChange={(e) => setNotes(e.target.value)} /></Field>
        <SalesProductPicker onPick={(h) => setLines((ls) => [...ls, lineFromHit(h)])} excluded={lines.map((l) => l.productId)} />
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        <SalesLinesEditor lines={lines} onChange={setLines} taxes={refs.taxes} currency={currency} errors={lineErrors} />
        <div style={{ textAlign: "right" }}>Estimation HT : <b>{money(totals.subtotal, currency)}</b></div>
        <div style={{ fontSize: 12, color: "var(--nx-text-2)", textAlign: "right" }}>
          Le devis fige les prix du jour (promotions et taxes comprises) : ils seront repris tels quels à la commande.
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function QuoteDrawer({ id, refs, onClose, onChanged, onEdit, onConverted }: {
  id: string; refs: SalesRefs; onClose: () => void; onChanged: () => void; onEdit: (q: Quote) => void; onConverted: (orderId: string) => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const detail = useAsync(() => getQuote(id), [id]);
  const [dialog, setDialog] = useState<"reject" | "cancel" | "convert" | null>(null);
  const { busy, error, submit } = useSubmit();
  const q = detail.data;
  const canQuote = hasPermission("documents.create_quote");
  const refresh = () => { detail.reload(); onChanged(); };

  async function act(fn: () => Promise<void>, done: string) {
    const ok = await submit(async () => { await fn(); return true; });
    if (ok) { toast.success(done); refresh(); }
  }

  return (
    <Drawer title={q ? `Devis ${q.documentNumber}` : "Devis"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {!q ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 14 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <Badge tone={QUOTE_TONE[q.status]}>{QUOTE_STATUS[q.status] ?? q.status}</Badge>
            <span>{q.customerName ?? "Sans client"}</span>
            <span style={{ color: "var(--nx-text-2)" }}>· {dateShort(q.quoteDate)}{q.validUntil ? ` · valable jusqu'au ${dateShort(q.validUntil)}` : ""}</span>
          </div>
          {q.terms && <div>{q.terms}</div>}
          {q.notes && <div style={{ color: "var(--nx-text-2)", whiteSpace: "pre-wrap" }}>{q.notes}</div>}
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {canQuote && q.status === "draft" && <Button size="sm" variant="subtle" onClick={() => onEdit(q)}>Modifier</Button>}
            {canQuote && q.status === "draft" && <Button size="sm" disabled={busy} onClick={() => act(() => sendQuote(q.id), "Devis marqué envoyé.")}>Marquer envoyé</Button>}
            {canQuote && q.status === "sent" && <Button size="sm" disabled={busy} onClick={() => act(() => acceptQuote(q.id), "Devis accepté.")}>Accepté par le client</Button>}
            {canQuote && q.status === "sent" && <Button size="sm" variant="ghost" onClick={() => setDialog("reject")}>Refusé par le client</Button>}
            {hasPermission("pos.manage_order") && (q.status === "accepted" || q.status === "sent") &&
              <Button size="sm" onClick={() => setDialog("convert")}>Convertir en commande</Button>}
            {canQuote && ["draft", "sent", "accepted"].includes(q.status) && <Button size="sm" variant="ghost" onClick={() => setDialog("cancel")}>Annuler</Button>}
            <PrintMenu kind="quote" id={q.id} label="Imprimer" />
          </div>
          <DataTable
            columns={[
              { key: "d", header: "Article", render: (l) => l.description },
              { key: "q", header: "Qté", numeric: true, render: (l) => number(l.quantity, 3) },
              { key: "p", header: "P.U.", numeric: true, render: (l) => money(l.unitPrice, q.currency) },
              { key: "r", header: "Remise", numeric: true, render: (l) => (l.discount ? money(l.discount, q.currency) : "—") },
              { key: "x", header: "Taxe", numeric: true, render: (l) => (l.taxAmount ? money(l.taxAmount, q.currency) : "—") },
              { key: "t", header: "Total", numeric: true, render: (l) => money(l.lineTotal, q.currency) },
            ]}
            rows={q.lines} rowKey={(l) => l.id} />
          <div style={{ textAlign: "right" }}>
            HT {money(q.subtotal - q.discountTotal, q.currency)} · taxes {money(q.taxTotal, q.currency)} · <b>Total {money(q.total, q.currency)}</b>
          </div>
        </div>
      )}
      {dialog === "reject" && q && (
        <ConfirmDialog title="Devis refusé par le client" requireReason confirmLabel="Enregistrer le refus"
          message="Indiquez la raison donnée par le client (prix, délai, concurrent…)." onCancel={() => setDialog(null)}
          onConfirm={(reason) => { setDialog(null); void act(() => rejectQuote(q.id, reason), "Refus enregistré."); }} />
      )}
      {dialog === "cancel" && q && (
        <ConfirmDialog title="Annuler le devis" danger requireReason confirmLabel="Annuler le devis"
          message="Le devis est annulé : il ne pourra plus être converti." onCancel={() => setDialog(null)}
          onConfirm={(reason) => { setDialog(null); void act(() => cancelQuote(q.id, reason), "Devis annulé."); }} />
      )}
      {dialog === "convert" && q && (
        <ConvertDialog quote={q} refs={refs} onClose={() => setDialog(null)} onDone={(orderId) => { setDialog(null); onConverted(orderId); }} />
      )}
    </Drawer>
  );
}

function ConvertDialog({ quote, refs, onClose, onDone }: { quote: Quote; refs: SalesRefs; onClose: () => void; onDone: (orderId: string) => void }) {
  const toast = useToast();
  const [orderType, setOrderType] = useState("delivery");
  const [locationId, setLocationId] = useState(refs.locations[0]?.id ?? "");
  const [onCredit, setOnCredit] = useState(false);
  const [dueDate, setDueDate] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "quote.customer_required": "credit", "order.location_unknown": "location" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!locationId) return fail("location", "Choisissez l'emplacement qui servira la commande.");
    if (onCredit && !quote.customerId) return fail("credit", "Un devis sans client ne peut pas devenir une commande à crédit.");
    const id = await submit(() => convertQuote(quote.id, { orderType, inventoryLocationId: locationId, onCredit, dueDate: onCredit && dueDate ? dueDate : null }));
    if (id) { toast.success("Commande créée depuis le devis (brouillon à confirmer)."); onDone(id); }
  }

  return (
    <Modal title={`Convertir le devis ${quote.documentNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer la commande</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Type de commande">
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
        <Field label="Règlement" error={fieldErrors.credit}>
          <Select value={onCredit ? "credit" : "cash"} onChange={(e) => setOnCredit(e.target.value === "credit")}>
            <option value="cash">Au comptant à la livraison</option>
            <option value="credit">À crédit</option>
          </Select>
        </Field>
        {onCredit && <Field label="Échéance"><TextInput type="date" min={todayIso()} value={dueDate} onChange={(e) => setDueDate(e.target.value)} /></Field>}
        <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>Les prix du devis sont repris tels quels.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
