import { useState, type FormEvent } from "react";
import { dateShort, dateTime } from "@/shared/format";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { Badge, Button, Card, DataTable, Drawer, Field, Modal, Money, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  getAdjustment, listAdjustments, recordAdjustment, recordLoss, type Location,
} from "@/inventory/inventoryApi";
import { ADJUSTMENT_LABEL, LinesEditor, LocationSelect, ProductPicker, qty, type EditableLine } from "@/inventory/stockShared";

type Kind = "loss" | "adjustment";

/** Pertes (perte, casse, expiration, vol) et ajustements (correction ±, stock retrouvé) : motif obligatoire,
 * chaque ligne devient un mouvement tracé et la valeur est comptabilisée par le serveur. */
export function AdjustmentsPanel({ locations }: { locations: Location[] }) {
  const { hasPermission } = useAuth();
  const [type, setType] = useState("");
  const list = useAsync(() => listAdjustments({ type: type || undefined }), [type]);
  const [creating, setCreating] = useState<Kind | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);

  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 6 }}>
          {hasPermission("inventory.write_off") && <Button size="sm" onClick={() => setCreating("loss")}>Déclarer une perte</Button>}
          {hasPermission("inventory.adjust") && <Button size="sm" variant="subtle" onClick={() => setCreating("adjustment")}>Ajustement</Button>}
        </div>}>
        Pertes & ajustements
      </SectionLabel>
      <div style={{ maxWidth: 240, marginBottom: 10 }}>
        <Field label="Type">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(ADJUSTMENT_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </div>
      {list.error && <div role="alert" className="nx-error-text">{list.error}</div>}
      <DataTable exportTitle="Pertes et ajustements"
        columns={[
          { key: "n", header: "N°", render: (a) => (
            <button type="button" onClick={() => setOpenId(a.id)} style={{ appearance: "none", background: "transparent", border: "none", padding: 0,
              cursor: "pointer", color: "var(--nx-brand)", font: "inherit" }}><code>{a.documentNumber}</code></button>) },
          { key: "d", header: "Date", render: (a) => dateShort(a.occurredAt) },
          { key: "t", header: "Type", render: (a) => <Badge>{ADJUSTMENT_LABEL[a.adjustmentType] ?? a.adjustmentType}</Badge> },
          { key: "l", header: "Emplacement", render: (a) => a.locationName },
          { key: "r", header: "Motif", render: (a) => a.reason ?? "—" },
          { key: "q", header: "Quantité", numeric: true, render: (a) => `${a.quantityDelta > 0 ? "+" : ""}${qty(a.quantityDelta)}` },
          { key: "v", header: "Valeur", numeric: true, render: (a) => <Money value={a.value} sign /> },
          { key: "u", header: "Par", render: (a) => a.createdByName ?? "—" },
        ]}
        rows={list.data ?? []} rowKey={(a) => a.id} empty={list.loading ? "Chargement…" : "Aucune perte ni ajustement."} />
      {creating && (
        <AdjustmentDialog kind={creating} locations={locations} onClose={() => setCreating(null)}
                          onSaved={(id) => { setCreating(null); list.reload(); setOpenId(id); }} />
      )}
      {openId && <AdjustmentDrawer id={openId} onClose={() => setOpenId(null)} />}
    </Card>
  );
}

const LOSS_TYPES = { loss: "Perte", breakage: "Casse", expiry: "Expiration", theft: "Vol" } as const;
const ADJ_TYPES = { correction: "Correction (erreur de comptage / saisie)", found: "Stock retrouvé" } as const;

function AdjustmentDialog({ kind, locations, onClose, onSaved }: {
  kind: Kind; locations: Location[]; onClose: () => void; onSaved: (id: string) => void;
}) {
  const toast = useToast();
  const [locationId, setLocationId] = useState(locations.length === 1 ? locations[0].id : "");
  const [type, setType] = useState<string>(kind === "loss" ? "breakage" : "correction");
  const [reason, setReason] = useState("");
  const [lines, setLines] = useState<EditableLine[]>([]);
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const [operationId] = useState(newOperationId);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "inventory.reason_required": "reason", "inventory.location_unknown": "locationId", "inventory.no_lines": "lines",
    "inventory.unit_cost_required": "lines", "inventory.insufficient_stock": "lines",
  });
  const signed = kind === "adjustment" && type === "correction";

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!locationId) return fail("locationId", "Choisissez l'emplacement.");
    if (reason.trim().length < 3) return fail("reason", "Le motif est obligatoire (3 caractères au moins).");
    if (lines.length === 0) return fail("lines", "Ajoutez au moins un article.");
    const errs: Record<string, string> = {};
    for (const l of lines) {
      const q = num(l.quantity);
      if (q === null || q === 0) errs[l.productId] = "Quantité requise.";
      else if (!signed && q < 0) errs[l.productId] = "Quantité positive.";
      else if (kind === "loss" && l.available !== null && q > l.available) errs[l.productId] = `Disponible : ${qty(l.available)}.`;
      else if (kind === "adjustment" && q > 0 && l.available === null && num(l.unitCost ?? "") === null)
        errs[l.productId] = "Article sans stock ici : indiquez son coût unitaire.";
    }
    setLineErrors(errs);
    if (Object.keys(errs).length) return;

    const id = await submit(() => kind === "loss"
      ? recordLoss({
          operationUuid: operationId, locationId, lossType: type as keyof typeof LOSS_TYPES, reason: reason.trim(), occurredAt: null,
          lines: lines.map((l) => ({ productId: l.productId, quantity: num(l.quantity)!, lotId: null, note: l.note.trim() || null })),
        })
      : recordAdjustment({
          operationUuid: operationId, locationId, adjustmentType: type as keyof typeof ADJ_TYPES, reason: reason.trim(), occurredAt: null,
          lines: lines.map((l) => ({
            productId: l.productId, quantityDelta: num(l.quantity)!, lotId: null,
            unitCost: num(l.unitCost ?? ""), note: l.note.trim() || null,
          })),
        }));
    if (id) { toast.success(kind === "loss" ? "Perte enregistrée et comptabilisée." : "Ajustement enregistré et comptabilisé."); onSaved(id); }
  }

  return (
    <Modal title={kind === "loss" ? "Déclarer une perte" : "Ajustement de stock"} onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12 }}>
          <LocationSelect value={locationId} onChange={(v) => { setLocationId(v); setLines([]); }} locations={locations} required error={fieldErrors.locationId} />
          <Field label="Type">
            <Select value={type} onChange={(e) => setType(e.target.value)}>
              {Object.entries(kind === "loss" ? LOSS_TYPES : ADJ_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
        </div>
        <Field label="Motif" required error={fieldErrors.reason} hint="Conservé dans le document et le journal d'audit.">
          <TextInput value={reason} onChange={(e) => setReason(e.target.value)} invalid={!!fieldErrors.reason} />
        </Field>
        <ProductPicker locationId={locationId} catalogFallback={kind === "adjustment"} excluded={lines.map((l) => l.productId)}
                       onPick={(p) => setLines([...lines, { ...p, quantity: "", note: "", unitCost: "" }])} />
        <LinesEditor lines={lines} onChange={setLines} errors={lineErrors} signed={signed}
                     quantityLabel={signed ? "Écart (+/−)" : kind === "loss" ? "Quantité sortie" : "Quantité entrée"}
                     withCost={kind === "adjustment" && lines.some((l) => l.available === null)} />
        {signed && <p style={{ margin: 0, fontSize: 13, color: "var(--nx-text-2)" }}>Saisissez +5 pour ajouter 5 unités, −3 pour en retirer 3.</p>}
        {fieldErrors.lines && <div role="alert" className="nx-error-text">{fieldErrors.lines}</div>}
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy} variant={kind === "loss" ? "danger" : "primary"}>Enregistrer</Button>
        </div>
      </form>
    </Modal>
  );
}

function AdjustmentDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const detail = useAsync(() => getAdjustment(id), [id]);
  const d = detail.data;
  return (
    <Drawer title={d ? `${ADJUSTMENT_LABEL[d.adjustment.adjustmentType] ?? "Ajustement"} ${d.adjustment.documentNumber}` : "Ajustement"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {d && (
        <div style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <Badge>{d.adjustment.locationName}</Badge>
            <Badge>{dateTime(d.adjustment.occurredAt)}</Badge>
            {d.adjustment.createdByName && <Badge>par {d.adjustment.createdByName}</Badge>}
            {/* Lien vers l'écriture conservé depuis la phase 2 ; les documents antérieurs sont comptabilisés sans ce lien. */}
            {d.adjustment.journalEntryId && <Badge tone="success">Écriture comptable liée</Badge>}
          </div>
          {d.adjustment.reason && <div><strong>Motif :</strong> {d.adjustment.reason}</div>}
          <DataTable exportTitle={`Ajustement ${d.adjustment.documentNumber}`}
            columns={[
              { key: "p", header: "Article", render: (l) => <><code>{l.sku}</code> {l.productName}</> },
              { key: "q", header: "Quantité", numeric: true, render: (l) => `${l.quantityDelta > 0 ? "+" : ""}${qty(l.quantityDelta)}` },
              { key: "c", header: "Coût unit.", numeric: true, render: (l) => <Money value={l.unitCost} /> },
              { key: "v", header: "Valeur", numeric: true, render: (l) => <Money value={l.value} sign /> },
              { key: "n", header: "Justification", render: (l) => l.note ?? "—" },
            ]}
            rows={d.lines} rowKey={(l) => l.id} />
        </div>
      )}
    </Drawer>
  );
}
