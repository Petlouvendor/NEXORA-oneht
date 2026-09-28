import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money } from "@/shared/format";
import { Icon } from "@/shared/icons";
import { PrintMenu } from "@/shared/ExportMenu";
import { Badge, Button, Card, DataTable, Drawer, Field, IconButton, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { LocationSelect, useStockPlaces } from "@/inventory/stockShared";
import type { ProductDetail } from "@/catalog/catalogApi";
import {
  getReceipt, listReceipts, receiveGoods, type GoodsReceiptDetail, type PurchaseOrderDetail, type ReceiveInput,
} from "@/purchasing/purchasingApi";
import { knownPurchasePrice, PurchaseProductPicker, purchaseUnits, qtyText, type PurchaseUnit, type PurchasingRefs } from "@/purchasing/purchasingShared";
import { ReturnDialog } from "@/purchasing/ReturnsPanel";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Bons de réception (avec ou sans commande) : quantités acceptées (entrées en stock) et refusées (motif). */
export function ReceiptsPanel({ refs, onChanged }: { refs: PurchasingRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [supplierId, setSupplierId] = useState("");
  const receipts = useAsync(() => listReceipts({ supplierId: supplierId || undefined, limit: 200 }), [supplierId]);
  const [creating, setCreating] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);

  return (
    <Card>
      <SectionLabel action={hasPermission("purchasing.receive") ? <Button size="sm" onClick={() => setCreating(true)}>Réception sans commande</Button> : undefined}>
        Réceptions
      </SectionLabel>
      <div style={{ maxWidth: 280, marginBottom: 10 }}>
        <Field label="Fournisseur">
          <Select value={supplierId} onChange={(e) => setSupplierId(e.target.value)}>
            <option value="">Tous</option>
            {refs.suppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
          </Select>
        </Field>
      </div>
      {receipts.error && <div role="alert" className="nx-error-text">{receipts.error}</div>}
      <DataTable exportTitle="Réceptions fournisseur"
        columns={[
          { key: "n", header: "N°", render: (r) => <button type="button" style={linkStyle} onClick={() => setOpenId(r.id)}><code>{r.documentNumber}</code></button> },
          { key: "d", header: "Reçu le", render: (r) => dateShort(r.receivedAt) },
          { key: "s", header: "Fournisseur", render: (r) => r.supplierName },
          { key: "o", header: "Commande", render: (r) => r.purchaseOrderNumber ?? "—" },
          { key: "l", header: "Emplacement", render: (r) => r.locationName ?? "—" },
          { key: "a", header: "Accepté", numeric: true, render: (r) => qtyText(r.quantityAccepted) },
          { key: "x", header: "Refusé", numeric: true, render: (r) => (r.quantityRejected > 0 ? <Badge tone="warning">{qtyText(r.quantityRejected)}</Badge> : "—") },
          { key: "v", header: "Valeur", numeric: true, render: (r) => money(r.value, refs.defaultCurrency) },
        ]}
        rows={receipts.data ?? []} rowKey={(r) => r.id} empty={receipts.loading ? "Chargement…" : "Aucune réception."} />
      {creating && (
        <ReceiveDialog refs={refs} order={null} onClose={() => setCreating(false)}
                       onDone={(id) => { setCreating(false); receipts.reload(); onChanged(); setOpenId(id); }} />
      )}
      {openId && <ReceiptDrawer key={openId} id={openId} refs={refs} onClose={() => setOpenId(null)}
                                onChanged={() => { receipts.reload(); onChanged(); }} />}
    </Card>
  );
}

interface ReceiveLineState {
  key: string; productId: string; sku: string; name: string; baseUnitCode: string | null; poLineId: string | null;
  units: PurchaseUnit[]; unitId: string; remaining: number | null; accepted: string; rejected: string; reason: string;
  unitCost: string; lot: string; expiry: string; trackLots: boolean; baseCost: number | null;
}

const factorOf = (l: ReceiveLineState) => l.units.find((u) => u.id === l.unitId)?.factor ?? 1;

/** Lignes initiales d'une réception de commande : reste à recevoir, dans l'unité d'achat de la commande si elle en a une. */
export function receiveLinesFromOrder(order: PurchaseOrderDetail): ReceiveLineState[] {
  return order.lines.filter((l) => l.remaining > 0).map((l) => {
    const units: PurchaseUnit[] = [{ id: "", code: l.baseUnitCode ?? "unité", factor: 1 }];
    if (l.purchaseUnitId && l.purchaseQuantity) units.push({ id: l.purchaseUnitId, code: l.purchaseUnitCode ?? "unité d'achat", factor: l.quantityOrdered / l.purchaseQuantity });
    const purchase = units[1];
    const wholePurchaseUnits = purchase && Number.isInteger(l.remaining / purchase.factor);
    const unit = wholePurchaseUnits ? purchase : units[0];
    return {
      key: l.id, productId: l.productId, sku: l.sku, name: l.productName, baseUnitCode: l.baseUnitCode, poLineId: l.id,
      units, unitId: unit.id, remaining: l.remaining, accepted: String(l.remaining / unit.factor), rejected: "", reason: "",
      unitCost: String(Math.round(l.unitPrice * unit.factor * 10000) / 10000), lot: "", expiry: "", trackLots: false, baseCost: l.unitPrice,
    };
  });
}

export function ReceiveDialog({ refs, order, onClose, onDone }: {
  refs: PurchasingRefs; order: PurchaseOrderDetail | null; onClose: () => void; onDone: (receiptId: string) => void;
}) {
  const { activeCompanyId } = useAuth();
  const toast = useToast();
  const places = useStockPlaces(activeCompanyId);
  const [operationUuid] = useState(newOperationId);
  const [supplierId, setSupplierId] = useState(order?.supplierId ?? "");
  const [locationId, setLocationId] = useState("");
  const [notes, setNotes] = useState("");
  const [lines, setLines] = useState<ReceiveLineState[]>(() => (order ? receiveLinesFromOrder(order) : []));
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "purchasing.location_unknown": "location", "purchasing.supplier_unknown": "supplier" });
  const supplierName = refs.suppliers.find((s) => s.id === supplierId)?.name ?? null;

  const set = (key: string, patch: Partial<ReceiveLineState>) => setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  const changeUnit = (l: ReceiveLineState, unitId: string) => {
    const f = l.units.find((u) => u.id === unitId)?.factor ?? 1;
    const base = l.baseCost ?? (num(l.unitCost) ?? 0) / factorOf(l);
    set(l.key, { unitId, unitCost: String(Math.round(base * f * 10000) / 10000) });
  };
  const add = (p: ProductDetail) => {
    const units = purchaseUnits(p);
    const price = knownPurchasePrice(p, supplierName);
    setLines((ls) => [...ls, {
      key: p.id, productId: p.id, sku: p.sku, name: p.name, baseUnitCode: p.unitCode, poLineId: null,
      units: units.map((u) => (u.id === p.unitId ? { ...u, id: "" } : u)), unitId: "", remaining: null,
      accepted: "", rejected: "", reason: "", unitCost: price != null ? String(price) : "", lot: "", expiry: "", trackLots: p.trackLots, baseCost: price,
    }]);
  };

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!supplierId) return fail("supplier", "Choisissez le fournisseur.");
    if (!locationId) return fail("location", "Choisissez l'emplacement de réception.");
    const errs: Record<string, string> = {};
    const kept = lines.filter((l) => (num(l.accepted) ?? 0) > 0 || (num(l.rejected) ?? 0) > 0);
    for (const l of lines) {
      const a = num(l.accepted) ?? 0, r = num(l.rejected) ?? 0, c = num(l.unitCost);
      if (a < 0 || r < 0) errs[l.key] = "Quantités négatives interdites.";
      else if (r > 0 && !l.reason.trim()) errs[l.key] = "Motif du refus obligatoire.";
      else if ((a > 0 || r > 0) && (c == null || c < 0)) errs[l.key] = "Coût unitaire invalide.";
      else if (l.remaining != null && a * factorOf(l) > l.remaining + 1e-9) errs[l.key] = `Au plus ${qtyText(l.remaining, l.baseUnitCode)} restant à recevoir.`;
      else if (a > 0 && l.trackLots && !l.lot.trim()) errs[l.key] = "Numéro de lot obligatoire pour cet article.";
    }
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    if (!kept.some((l) => (num(l.accepted) ?? 0) > 0)) return fail("lines", "Aucune quantité acceptée : rien n'entrerait en stock.");
    const body: ReceiveInput = {
      operationUuid, supplierId, inventoryLocationId: locationId, purchaseOrderId: order?.id ?? null, receivedAt: null,
      notes: notes.trim() || null,
      lines: kept.map((l) => ({
        productId: l.productId, purchaseOrderLineId: l.poLineId, quantity: num(l.accepted) ?? 0, unitCost: num(l.unitCost) ?? 0,
        lotNumber: l.lot.trim() || null, expirationDate: l.expiry || null, unitId: l.unitId || null,
        quantityRejected: num(l.rejected) ?? 0, rejectionReason: l.reason.trim() || null,
      })),
    };
    const id = await submit(() => receiveGoods(body));
    if (id) { toast.success("Réception enregistrée : le stock est à jour."); onDone(id); }
  }

  return (
    <Modal title={order ? `Réception de la commande ${order.documentNumber}` : "Réception sans commande"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Valider la réception</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(900px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 12 }}>
          <Field label="Fournisseur" required error={fieldErrors.supplier}>
            <Select value={supplierId} disabled={!!order} onChange={(e) => setSupplierId(e.target.value)}>
              <option value="">—</option>
              {refs.activeSuppliers.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              {order && !refs.activeSuppliers.some((s) => s.id === order.supplierId) && <option value={order.supplierId}>{order.supplierName}</option>}
            </Select>
          </Field>
          <LocationSelect label="Emplacement de réception" required value={locationId} onChange={setLocationId} locations={places.active} error={fieldErrors.location} />
          <Field label="Notes (bon de livraison, transporteur…)">
            <TextInput value={notes} maxLength={500} onChange={(e) => setNotes(e.target.value)} />
          </Field>
        </div>
        {!order && <PurchaseProductPicker onPick={add} excluded={lines.map((l) => l.productId)} />}
        {fieldErrors.lines && <div className="nx-err" role="alert">{fieldErrors.lines}</div>}
        {lines.length === 0 ? <div style={{ color: "var(--nx-text-2)" }}>{order ? "Tout a déjà été reçu." : "Aucun article ajouté."}</div> : (
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th>{order && <th className="nx-num">Reste</th>}<th>Unité</th><th>Accepté</th><th>Refusé</th><th>Motif du refus</th><th>Coût unitaire</th><th>Lot</th><th>Péremption</th>{!order && <th />}</tr></thead>
              <tbody>
                {lines.map((l) => (
                  <tr key={l.key}>
                    <td><code>{l.sku}</code> {l.name}
                      {factorOf(l) !== 1 && num(l.accepted) ? <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>= {qtyText((num(l.accepted) ?? 0) * factorOf(l), l.baseUnitCode)}</div> : null}
                      {lineErrors[l.key] && <div className="nx-err" role="alert">{lineErrors[l.key]}</div>}
                    </td>
                    {order && <td className="nx-num">{qtyText(l.remaining, l.baseUnitCode)}</td>}
                    <td style={{ minWidth: 90 }}>
                      <Select value={l.unitId} aria-label={`Unité — ${l.name}`} onChange={(e) => changeUnit(l, e.target.value)}>
                        {l.units.map((u) => <option key={u.id || "base"} value={u.id}>{u.code}{u.factor !== 1 ? ` (${u.factor})` : ""}</option>)}
                      </Select>
                    </td>
                    <td style={{ minWidth: 80 }}><TextInput type="number" step="any" min={0} value={l.accepted} aria-label={`Quantité acceptée — ${l.name}`}
                      invalid={!!lineErrors[l.key]} onChange={(e) => set(l.key, { accepted: e.target.value })} /></td>
                    <td style={{ minWidth: 80 }}><TextInput type="number" step="any" min={0} value={l.rejected} aria-label={`Quantité refusée — ${l.name}`}
                      onChange={(e) => set(l.key, { rejected: e.target.value })} /></td>
                    <td style={{ minWidth: 140 }}><TextInput value={l.reason} placeholder="casse, non conforme…" aria-label={`Motif du refus — ${l.name}`}
                      onChange={(e) => set(l.key, { reason: e.target.value })} /></td>
                    <td style={{ minWidth: 100 }}><TextInput type="number" step="0.0001" min={0} value={l.unitCost} aria-label={`Coût unitaire — ${l.name}`}
                      onChange={(e) => set(l.key, { unitCost: e.target.value, baseCost: null })} /></td>
                    <td style={{ minWidth: 90 }}><TextInput value={l.lot} aria-label={`Lot — ${l.name}`} onChange={(e) => set(l.key, { lot: e.target.value })} /></td>
                    <td style={{ minWidth: 130 }}><TextInput type="date" value={l.expiry} aria-label={`Péremption — ${l.name}`} onChange={(e) => set(l.key, { expiry: e.target.value })} /></td>
                    {!order && <td><IconButton label={`Retirer ${l.name}`} onClick={() => setLines((ls) => ls.filter((x) => x.key !== l.key))}><Icon.X size={14} /></IconButton></td>}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
          Seule la quantité acceptée entre en stock. Une quantité refusée reste sur le bon de réception avec son motif.
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

export function ReceiptDrawer({ id, refs, onClose, onChanged }: { id: string; refs: PurchasingRefs; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const detail = useAsync(() => getReceipt(id), [id]);
  const [returning, setReturning] = useState<GoodsReceiptDetail | null>(null);
  const d = detail.data;
  return (
    <Drawer title={d ? `Réception ${d.receipt.documentNumber}` : "Réception"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {!d ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 12 }}>
          <div>{d.receipt.supplierName} · {dateTime(d.receipt.receivedAt)} · {d.receipt.locationName ?? "—"}
            {d.receipt.purchaseOrderNumber && <> · commande <code>{d.receipt.purchaseOrderNumber}</code></>}</div>
          {d.receipt.notes && <div style={{ color: "var(--nx-text-2)", whiteSpace: "pre-wrap" }}>{d.receipt.notes}</div>}
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <PrintMenu kind="goods-receipt" id={d.receipt.id} label="Imprimer le bon" />
            {hasPermission("purchasing.return") && <Button size="sm" variant="subtle" onClick={() => setReturning(d)}>Retour fournisseur</Button>}
          </div>
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th><th className="nx-num">Accepté</th><th className="nx-num">Refusé</th><th>Motif</th><th className="nx-num">Coût</th><th>Lot</th></tr></thead>
              <tbody>
                {d.lines.map((l) => (
                  <tr key={l.id}>
                    <td><code>{l.sku}</code> {l.productName}</td>
                    <td className="nx-num">{qtyText(l.quantityReceived, l.unitCode)}
                      {l.purchaseUnitCode && l.purchaseQuantity != null && <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>saisi : {qtyText(l.purchaseQuantity, l.purchaseUnitCode)}</div>}
                    </td>
                    <td className="nx-num">{l.quantityRejected > 0 ? qtyText(l.quantityRejected, l.unitCode) : "—"}</td>
                    <td>{l.rejectionReason ?? "—"}</td>
                    <td className="nx-num">{money(l.unitCost, refs.defaultCurrency)}</td>
                    <td>{l.lotNumber ?? "—"}{l.expirationDate ? ` (${dateShort(l.expirationDate)})` : ""}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}
      {returning && <ReturnDialog refs={refs} receipt={returning} onClose={() => setReturning(null)}
                                  onDone={() => { setReturning(null); onChanged(); }} />}
    </Drawer>
  );
}
