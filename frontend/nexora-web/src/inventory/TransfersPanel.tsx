import { useState, type FormEvent } from "react";
import { dateShort } from "@/shared/format";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  cancelTransfer, closeTransfer, createTransfer, getTransfer, listTransfers, receiveTransfer, type Location,
} from "@/inventory/inventoryApi";
import { LinesEditor, LocationSelect, ProductPicker, qty, TRANSFER_STATUS, type EditableLine } from "@/inventory/stockShared";

const STATUS_TONE: Record<string, "success" | "warning" | "error" | "info" | undefined> = {
  in_transit: "info", partially_received: "warning", completed: "success", cancelled: "error",
};

/** Transferts : EXPÉDIÉ (sortie de la source) → EN TRANSIT → REÇU (total ou partiel) → CLÔTURÉ (écart = perte en transit). */
export function TransfersPanel({ locations }: { locations: Location[] }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const transfers = useAsync(() => listTransfers(status || undefined), [status]);
  const [creating, setCreating] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);

  return (
    <Card>
      <SectionLabel action={hasPermission("inventory.transfer") ? <Button size="sm" onClick={() => setCreating(true)}>Nouveau transfert</Button> : undefined}>
        Transferts
      </SectionLabel>
      <div style={{ maxWidth: 240, marginBottom: 10 }}>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(TRANSFER_STATUS).filter(([k]) => k !== "draft").map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </div>
      {transfers.error && <div role="alert" className="nx-error-text">{transfers.error}</div>}
      <DataTable exportTitle="Transferts"
        columns={[
          { key: "n", header: "N°", render: (t) => (
            <button type="button" onClick={() => setOpenId(t.id)} style={{ appearance: "none", background: "transparent", border: "none", padding: 0,
              cursor: "pointer", color: "var(--nx-brand)", font: "inherit" }}><code>{t.documentNumber}</code></button>) },
          { key: "s", header: "De", render: (t) => t.sourceLocationName },
          { key: "d", header: "Vers", render: (t) => t.destLocationName },
          { key: "q", header: "Reçu / expédié", numeric: true, render: (t) => `${qty(t.quantityReceived)} / ${qty(t.quantityShipped)}` },
          { key: "e", header: "Expédié le", render: (t) => (t.shippedAt ? dateShort(t.shippedAt) : "—") },
          { key: "st", header: "Statut", render: (t) => <Badge tone={STATUS_TONE[t.status]}>{TRANSFER_STATUS[t.status] ?? t.status}</Badge> },
        ]}
        rows={transfers.data ?? []} rowKey={(t) => t.id} empty={transfers.loading ? "Chargement…" : "Aucun transfert."} />
      {creating && (
        <NewTransferDialog locations={locations} onClose={() => setCreating(false)}
                           onCreated={(id) => { setCreating(false); transfers.reload(); setOpenId(id); }} />
      )}
      {openId && <TransferDrawer key={openId} id={openId} onClose={() => setOpenId(null)} onChanged={transfers.reload} />}
    </Card>
  );
}

function NewTransferDialog({ locations, onClose, onCreated }: { locations: Location[]; onClose: () => void; onCreated: (id: string) => void }) {
  const toast = useToast();
  const [source, setSource] = useState("");
  const [dest, setDest] = useState("");
  const [notes, setNotes] = useState("");
  const [lines, setLines] = useState<EditableLine[]>([]);
  const [lineErrors, setLineErrors] = useState<Record<string, string>>({});
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "inventory.transfer_same_location": "dest", "inventory.location_unknown": "source", "inventory.insufficient_stock": "lines",
  });

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!source) return fail("source", "Choisissez l'emplacement de départ.");
    if (!dest || dest === source) return fail("dest", "Choisissez une destination différente de la source.");
    if (lines.length === 0) return fail("lines", "Ajoutez au moins un article.");
    const errs: Record<string, string> = {};
    for (const l of lines) {
      const q = num(l.quantity);
      if (q === null || q <= 0) errs[l.productId] = "Quantité positive requise.";
      else if (l.available !== null && q > l.available) errs[l.productId] = `Disponible : ${qty(l.available)}.`;
    }
    setLineErrors(errs);
    if (Object.keys(errs).length) return;
    const id = await submit(() => createTransfer({
      sourceLocationId: source, destLocationId: dest, notes: notes.trim() || null,
      lines: lines.map((l) => ({ productId: l.productId, quantity: num(l.quantity)!, lotId: null })),
    }));
    if (id) { toast.success("Transfert expédié : la marchandise est en transit."); onCreated(id); }
  }

  return (
    <Modal title="Nouveau transfert" onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12 }}>
          <LocationSelect label="Depuis" value={source} onChange={(v) => { setSource(v); setLines([]); }} locations={locations} required error={fieldErrors.source} />
          <LocationSelect label="Vers" value={dest} onChange={setDest} locations={locations} required error={fieldErrors.dest} exclude={source} />
        </div>
        <ProductPicker locationId={source} excluded={lines.map((l) => l.productId)}
                       onPick={(p) => setLines([...lines, { ...p, quantity: "", note: "" }])} />
        <LinesEditor lines={lines} onChange={setLines} quantityLabel="À transférer" errors={lineErrors} />
        {fieldErrors.lines && <div role="alert" className="nx-error-text">{fieldErrors.lines}</div>}
        <Field label="Note (transporteur, référence…)"><TextInput value={notes} onChange={(e) => setNotes(e.target.value)} /></Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy}>Expédier</Button>
        </div>
      </form>
    </Modal>
  );
}

function TransferDrawer({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const detail = useAsync(() => getTransfer(id), [id]);
  const [receiving, setReceiving] = useState<Record<string, string> | null>(null);
  const [confirm, setConfirm] = useState<"close" | "cancel" | null>(null);
  const [operationId] = useState(newOperationId);
  const { busy, error, submit } = useSubmit();
  const d = detail.data;
  const open = d && (d.transfer.status === "in_transit" || d.transfer.status === "partially_received");
  const canAct = hasPermission("inventory.transfer");
  const remaining = d?.lines.reduce((a, l) => a + l.remaining, 0) ?? 0;

  const done = (msg: string) => { toast.success(msg); setReceiving(null); detail.reload(); onChanged(); };

  async function receive() {
    if (!d || !receiving) return;
    const lines: Array<{ transferLineId: string; quantity: number }> = [];
    for (const l of d.lines) {
      const v = num(receiving[l.id] ?? "");
      if (v === null || v === 0) continue;
      if (v < 0 || v > l.remaining) { toast.error(`${l.productName} : entre 0 et ${qty(l.remaining)}.`); return; }
      lines.push({ transferLineId: l.id, quantity: v });
    }
    if (lines.length === 0) { toast.error("Aucune quantité reçue."); return; }
    const ok = await submit(async () => { await receiveTransfer(id, lines); return true; });
    if (ok) done("Réception enregistrée : le stock de destination est mis à jour.");
  }

  async function act(reason: string) {
    const kind = confirm;
    setConfirm(null);
    const ok = await submit(async () => {
      if (kind === "close") await closeTransfer(id, reason, operationId);
      else await cancelTransfer(id, reason);
      return true;
    });
    if (ok) done(kind === "close" ? "Transfert clôturé : l'écart est constaté en perte." : "Transfert annulé : la marchandise est revenue à la source.");
  }

  return (
    <Drawer title={d ? `Transfert ${d.transfer.documentNumber}` : "Transfert"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {d && (
        <div style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <Badge tone={STATUS_TONE[d.transfer.status]}>{TRANSFER_STATUS[d.transfer.status] ?? d.transfer.status}</Badge>
            <Badge>{d.transfer.sourceLocationName} → {d.transfer.destLocationName}</Badge>
            {d.transfer.createdByName && <Badge>par {d.transfer.createdByName}</Badge>}
          </div>
          {d.transfer.notes && <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>{d.transfer.notes}</div>}
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th><th className="nx-num">Expédié</th><th className="nx-num">Reçu</th><th className="nx-num">Reste</th>{receiving && <th>Reçu maintenant</th>}</tr></thead>
              <tbody>
                {d.lines.map((l) => (
                  <tr key={l.id}>
                    <td><code>{l.sku}</code> {l.productName}</td>
                    <td className="nx-num">{qty(l.quantityShipped)}</td>
                    <td className="nx-num">{qty(l.quantityReceived)}</td>
                    <td className="nx-num">{l.remaining ? <Badge tone="warning">{qty(l.remaining)}</Badge> : "0"}</td>
                    {receiving && (
                      <td style={{ minWidth: 110 }}>
                        <TextInput type="number" min={0} max={l.remaining} step="any" disabled={l.remaining === 0}
                                   aria-label={`Quantité reçue — ${l.productName}`} value={receiving[l.id] ?? ""}
                                   onChange={(e) => setReceiving({ ...receiving, [l.id]: e.target.value })} />
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          {open && canAct && !receiving && (
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              <Button onClick={() => setReceiving(Object.fromEntries(d.lines.map((l) => [l.id, String(l.remaining)])))} disabled={busy}>Réceptionner</Button>
              {d.transfer.status === "partially_received" && remaining > 0 && (
                <Button variant="subtle" onClick={() => setConfirm("close")} disabled={busy}>Clôturer avec écart</Button>
              )}
              {d.lines.every((l) => l.quantityReceived === 0) && (
                <Button variant="ghost" onClick={() => setConfirm("cancel")} disabled={busy}>Annuler le transfert</Button>
              )}
            </div>
          )}
          {receiving && (
            <div style={{ display: "flex", gap: 8 }}>
              <Button onClick={() => void receive()} loading={busy}>Confirmer la réception</Button>
              <Button variant="ghost" onClick={() => setReceiving(null)} disabled={busy}>Annuler</Button>
            </div>
          )}
        </div>
      )}
      {confirm && (
        <ConfirmDialog
          title={confirm === "close" ? "Clôturer avec écart" : "Annuler le transfert"}
          message={confirm === "close"
            ? `${qty(remaining)} unité(s) non reçue(s) seront constatées en perte en transit (sortie comptabilisée). Définitif.`
            : "La marchandise sera réintégrée au stock de l'emplacement de départ."}
          requireReason danger={confirm === "close"} confirmLabel="Confirmer"
          onConfirm={(reason) => void act(reason)} onCancel={() => setConfirm(null)} />
      )}
    </Drawer>
  );
}
