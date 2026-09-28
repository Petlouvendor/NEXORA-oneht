import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, money } from "@/shared/format";
import { Icon } from "@/shared/icons";
import { Button, Card, DataTable, Field, IconButton, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { LocationSelect, ProductPicker, useStockPlaces, type PickedProduct } from "@/inventory/stockShared";
import {
  listPayables, listSupplierReturns, recordSupplierReturn, type GoodsReceiptDetail, type ReturnInput,
} from "@/purchasing/purchasingApi";
import { qtyText, type PurchasingRefs } from "@/purchasing/purchasingShared";

/** Retours fournisseur : sortie de stock, motif obligatoire ; imputés sur une dette ou en avoir à recevoir. */
export function ReturnsPanel({ refs, onChanged }: { refs: PurchasingRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [supplierId, setSupplierId] = useState("");
  const returns = useAsync(() => listSupplierReturns({ supplierId: supplierId || undefined, limit: 200 }), [supplierId]);
  const [creating, setCreating] = useState(false);

  return (
    <Card>
      <SectionLabel action={hasPermission("purchasing.return") ? <Button size="sm" onClick={() => setCreating(true)}>Nouveau retour</Button> : undefined}>
        Retours fournisseur
      </SectionLabel>
      <div style={{ maxWidth: 280, marginBottom: 10 }}>
        <Field label="Fournisseur">
          <Select value={supplierId} onChange={(e) => setSupplierId(e.target.value)}>
            <option value="">Tous</option>
            {refs.suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </Select>
        </Field>
      </div>
      {returns.error && <div role="alert" className="nx-error-text">{returns.error}</div>}
      <DataTable exportTitle="Retours fournisseur"
        columns={[
          { key: "n", header: "N°", render: (r) => <code>{r.documentNumber}</code> },
          { key: "d", header: "Date", render: (r) => (r.returnedAt ? dateShort(r.returnedAt) : "—") },
          { key: "s", header: "Fournisseur", render: (r) => r.supplierName },
          { key: "l", header: "Emplacement", render: (r) => r.locationName ?? "—" },
          { key: "m", header: "Motif", render: (r) => r.reason ?? "—" },
          { key: "c", header: "Lignes", numeric: true, render: (r) => r.lineCount },
          { key: "t", header: "Montant", numeric: true, render: (r) => money(r.total, r.currency) },
        ]}
        rows={returns.data ?? []} rowKey={(r) => r.id} empty={returns.loading ? "Chargement…" : "Aucun retour."} />
      {creating && <ReturnDialog refs={refs} receipt={null} onClose={() => setCreating(false)}
                                 onDone={() => { setCreating(false); returns.reload(); onChanged(); }} />}
    </Card>
  );
}

interface ReturnLineState { productId: string; sku: string; name: string; unitCode: string | null; max: number | null; quantity: string; unitPrice: string }

export function ReturnDialog({ refs, receipt, onClose, onDone }: {
  refs: PurchasingRefs; receipt: GoodsReceiptDetail | null; onClose: () => void; onDone: () => void;
}) {
  const { activeCompanyId } = useAuth();
  const toast = useToast();
  const places = useStockPlaces(activeCompanyId);
  const [operationUuid] = useState(newOperationId);
  const [supplierId, setSupplierId] = useState(receipt?.receipt.supplierId ?? "");
  const [locationId, setLocationId] = useState(receipt?.receipt.locationId ?? "");
  const [reason, setReason] = useState("");
  const [payableId, setPayableId] = useState("");
  const [lines, setLines] = useState<ReturnLineState[]>(() => (receipt ? mergeReceiptLines(receipt) : []));
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const payables = useAsync(async () => (supplierId ? (await listPayables(true)).filter((p) => p.supplierId === supplierId && p.remainingAmount > 0) : []), [supplierId]);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "purchasing.reason_required": "reason", "purchasing.location_unknown": "location",
    "purchasing.return_exceeds_payable": "payable", "purchasing.payable_closed": "payable",
  });
  const payable = (payables.data ?? []).find((p) => p.id === payableId);
  const currency = payable?.currency ?? refs.defaultCurrency;
  const total = lines.reduce((a, l) => a + (num(l.quantity) ?? 0) * (num(l.unitPrice) ?? 0), 0);

  const set = (id: string, patch: Partial<ReturnLineState>) => setLines((ls) => ls.map((l) => (l.productId === id ? { ...l, ...patch } : l)));
  const add = (p: PickedProduct) => setLines((ls) => [...ls, { productId: p.productId, sku: p.sku, name: p.name, unitCode: p.unitCode, max: p.available, quantity: "", unitPrice: "" }]);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!supplierId) return fail("supplier", "Choisissez le fournisseur.");
    if (!locationId) return fail("location", "Choisissez l'emplacement d'où sort la marchandise.");
    if (reason.trim().length < 3) return fail("reason", "Le motif du retour est obligatoire.");
    const errs: Record<string, string> = {};
    const kept = lines.filter((l) => (num(l.quantity) ?? 0) > 0);
    for (const l of lines) {
      const q = num(l.quantity) ?? 0, p = num(l.unitPrice);
      if (q < 0) errs[l.productId] = "Quantité négative interdite.";
      else if (q > 0 && (p == null || p < 0)) errs[l.productId] = "Prix unitaire invalide.";
      else if (l.max != null && q > l.max) errs[l.productId] = `Au plus ${qtyText(l.max, l.unitCode)}.`;
    }
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    if (kept.length === 0) return fail("lines", "Indiquez au moins une quantité à retourner.");
    if (payable && total > payable.remainingAmount + 1e-9) return fail("payable", `L'avoir dépasse le reste à payer (${money(payable.remainingAmount, payable.currency)}).`);
    const body: ReturnInput = {
      operationUuid, supplierId, inventoryLocationId: locationId, goodsReceiptId: receipt?.receipt.id ?? null, payableId: payableId || null,
      currency, reason: reason.trim(),
      lines: kept.map((l) => ({ productId: l.productId, quantity: num(l.quantity)!, unitPrice: num(l.unitPrice) ?? 0, taxAmount: 0, lotId: null })),
    };
    const id = await submit(() => recordSupplierReturn(body));
    if (id) { toast.success("Retour enregistré : stock et compte fournisseur mis à jour."); onDone(); }
  }

  return (
    <Modal title={receipt ? `Retour sur la réception ${receipt.receipt.documentNumber}` : "Nouveau retour fournisseur"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer le retour</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(760px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 12 }}>
          <Field label="Fournisseur" required error={fieldErrors.supplier}>
            <Select value={supplierId} disabled={!!receipt} onChange={(e) => { setSupplierId(e.target.value); setPayableId(""); }}>
              <option value="">—</option>
              {refs.suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </Select>
          </Field>
          <LocationSelect label="Sortie depuis" required value={locationId} onChange={setLocationId} locations={places.active} error={fieldErrors.location} />
          <Field label="Imputation" error={fieldErrors.payable} hint="Sur une dette : le reste à payer diminue d'autant.">
            <Select value={payableId} onChange={(e) => setPayableId(e.target.value)}>
              <option value="">Avoir à recevoir (aucune dette)</option>
              {(payables.data ?? []).map((p) => (
                <option key={p.id} value={p.id}>{p.invoiceNumber ?? "Dette"}{p.supplierReference ? ` · ${p.supplierReference}` : ""} — reste {money(p.remainingAmount, p.currency)}</option>
              ))}
            </Select>
          </Field>
        </div>
        <Field label="Motif du retour" required error={fieldErrors.reason}>
          <TextInput value={reason} maxLength={500} placeholder="Produits périmés, casse, erreur de livraison…" onChange={(e) => setReason(e.target.value)} />
        </Field>
        {!receipt && <ProductPicker locationId={locationId} onPick={add} excluded={lines.map((l) => l.productId)} />}
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        {lines.length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>Aucun article.</div> : (
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th><th className="nx-num">{receipt ? "Reçu" : "Disponible"}</th><th>Quantité retournée</th><th>Prix unitaire</th>{!receipt && <th />}</tr></thead>
              <tbody>
                {lines.map((l) => (
                  <tr key={l.productId}>
                    <td><code>{l.sku}</code> {l.name}{lineErrors[l.productId] && <div className="nx-err" role="alert">{lineErrors[l.productId]}</div>}</td>
                    <td className="nx-num">{qtyText(l.max, l.unitCode)}</td>
                    <td style={{ minWidth: 100 }}><TextInput type="number" step="any" min={0} value={l.quantity} aria-label={`Quantité retournée — ${l.name}`}
                      invalid={!!lineErrors[l.productId]} onChange={(e) => set(l.productId, { quantity: e.target.value })} /></td>
                    <td style={{ minWidth: 110 }}><TextInput type="number" step="0.0001" min={0} value={l.unitPrice} aria-label={`Prix unitaire — ${l.name}`}
                      onChange={(e) => set(l.productId, { unitPrice: e.target.value })} /></td>
                    {!receipt && <td><IconButton label={`Retirer ${l.name}`} onClick={() => setLines((ls) => ls.filter((x) => x.productId !== l.productId))}><Icon.X size={14} /></IconButton></td>}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div style={{ textAlign: "right" }}>Montant du retour : <b>{money(total, currency)}</b></div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Une ligne par article de la réception (plusieurs lots d'un même article sont regroupés). */
function mergeReceiptLines(receipt: GoodsReceiptDetail): ReturnLineState[] {
  const byProduct = new Map<string, ReturnLineState>();
  for (const l of receipt.lines) {
    const cur = byProduct.get(l.productId);
    if (cur) cur.max = (cur.max ?? 0) + l.quantityReceived;
    else byProduct.set(l.productId, { productId: l.productId, sku: l.sku, name: l.productName, unitCode: l.unitCode,
      max: l.quantityReceived, quantity: "", unitPrice: String(l.unitCost) });
  }
  return [...byProduct.values()].filter((l) => (l.max ?? 0) > 0);
}
