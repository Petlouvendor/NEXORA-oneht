import { useEffect, useMemo, useState, type FormEvent } from "react";
import { dateShort, dateTime } from "@/shared/format";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, Money, SectionLabel, Select, StatePill, TextInput } from "@/shared/ui";
import {
  cancelCount, getCount, listCounts, saveCount, startCount, validateCount,
  type CountDetail, type CountLineInput, type Location,
} from "@/inventory/inventoryApi";
import { COUNT_STATUS, COUNT_TYPE, LocationSelect, ProductPicker, qty, type PickedProduct } from "@/inventory/stockShared";

/**
 * Inventaire : OUVRIR (fige la quantité système) → COMPTER (saisie enregistrée, sans impact) → REVUE des
 * écarts justifiés → VALIDER (ajustement + mouvements + écriture comptable, côté serveur) — ou ANNULER.
 */
export function CountsPanel({ locations }: { locations: Location[] }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const counts = useAsync(() => listCounts(status || undefined), [status]);
  const [creating, setCreating] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);

  return (
    <Card>
      <SectionLabel action={hasPermission("inventory.count") ? <Button size="sm" onClick={() => setCreating(true)}>Nouvel inventaire</Button> : undefined}>
        Inventaires
      </SectionLabel>
      <div style={{ maxWidth: 240, marginBottom: 10 }}>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(COUNT_STATUS).filter(([k]) => k !== "draft").map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </div>
      {counts.error && <div role="alert" className="nx-error-text">{counts.error}</div>}
      <DataTable exportTitle="Inventaires"
        columns={[
          { key: "n", header: "N°", render: (c) => (
            <button type="button" onClick={() => setOpenId(c.id)} style={{ appearance: "none", background: "transparent", border: "none", padding: 0,
              cursor: "pointer", color: "var(--nx-brand)", font: "inherit" }}><code>{c.documentNumber}</code></button>) },
          { key: "l", header: "Emplacement", render: (c) => c.locationName },
          { key: "t", header: "Type", render: (c) => COUNT_TYPE[c.countType] ?? c.countType },
          { key: "p", header: "Comptés", numeric: true, render: (c) => `${c.countedLines} / ${c.lineCount}` },
          { key: "e", header: "Écarts", numeric: true, render: (c) => (c.varianceLines ? <Badge tone="warning">{c.varianceLines}</Badge> : "0") },
          { key: "v", header: "Valeur des écarts", numeric: true, render: (c) => <Money value={c.varianceValue} sign /> },
          { key: "d", header: "Ouvert le", render: (c) => (c.startedAt ? dateShort(c.startedAt) : "—") },
          { key: "s", header: "Statut", render: (c) => <StatePill status={COUNT_STATUS[c.status] ?? c.status} /> },
        ]}
        rows={counts.data ?? []} rowKey={(c) => c.id} empty={counts.loading ? "Chargement…" : "Aucun inventaire."} />
      {creating && (
        <StartCountDialog locations={locations} onClose={() => setCreating(false)}
                          onStarted={(id) => { setCreating(false); counts.reload(); setOpenId(id); }} />
      )}
      {openId && <CountSheet key={openId} countId={openId} onClose={() => setOpenId(null)} onChanged={counts.reload} />}
    </Card>
  );
}

function StartCountDialog({ locations, onClose, onStarted }: {
  locations: Location[]; onClose: () => void; onStarted: (id: string) => void;
}) {
  const toast = useToast();
  const [locationId, setLocationId] = useState(locations.length === 1 ? locations[0].id : "");
  const [countType, setCountType] = useState<"full" | "partial" | "cycle">("full");
  const [products, setProducts] = useState<PickedProduct[]>([]);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "inventory.location_unknown": "locationId", "inventory.no_lines": "products",
  });

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!locationId) return fail("locationId", "Choisissez l'emplacement à inventorier.");
    if (countType !== "full" && products.length === 0) return fail("products", "Ajoutez les articles à compter.");
    const id = await submit(() => startCount(locationId, countType, countType === "full" ? [] : products.map((p) => p.productId)));
    if (id) { toast.success("Inventaire ouvert : les quantités système sont figées."); onStarted(id); }
  }

  return (
    <Modal title="Nouvel inventaire" onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <LocationSelect value={locationId} onChange={setLocationId} locations={locations} required error={fieldErrors.locationId} />
        <Field label="Type">
          <Select value={countType} onChange={(e) => setCountType(e.target.value as "full" | "partial" | "cycle")}>
            <option value="full">Complet — tous les articles présents ou suivis à l'emplacement</option>
            <option value="partial">Partiel — articles choisis</option>
            <option value="cycle">Tournant — articles choisis</option>
          </Select>
        </Field>
        {countType !== "full" && (
          <>
            <ProductPicker locationId={locationId} catalogFallback excluded={products.map((p) => p.productId)}
                           onPick={(p) => setProducts([...products, p])} />
            <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
              {products.map((p) => (
                <Badge key={p.productId} tone="info">
                  {p.name}
                  <button type="button" aria-label={`Retirer ${p.name}`} onClick={() => setProducts(products.filter((x) => x.productId !== p.productId))}
                          style={{ marginLeft: 6, border: "none", background: "transparent", color: "inherit", cursor: "pointer" }}>×</button>
                </Badge>
              ))}
            </div>
            {fieldErrors.products && <div role="alert" className="nx-error-text">{fieldErrors.products}</div>}
          </>
        )}
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy}>Ouvrir l'inventaire</Button>
        </div>
      </form>
    </Modal>
  );
}

interface Draft { counted: string; note: string }

function CountSheet({ countId, onClose, onChanged }: { countId: string; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const detail = useAsync(() => getCount(countId), [countId]);
  const [drafts, setDrafts] = useState<Record<string, Draft>>({});
  const [filter, setFilter] = useState<"all" | "todo" | "variance">("all");
  const [confirmValidate, setConfirmValidate] = useState(false);
  const [confirmCancel, setConfirmCancel] = useState(false);
  const [operationId] = useState(newOperationId);
  const { busy, error, submit } = useSubmit();
  const d: CountDetail | null = detail.data;
  const open = d && (d.count.status === "counting" || d.count.status === "review");

  useEffect(() => {
    if (!d) return;
    setDrafts(Object.fromEntries(d.lines.map((l) => [l.id, { counted: l.countedQuantity == null ? "" : String(l.countedQuantity), note: l.note ?? "" }])));
  }, [d]);

  const lines = useMemo(() => (d?.lines ?? []).filter((l) => {
    const counted = num(drafts[l.id]?.counted ?? "");
    if (filter === "todo") return counted === null;
    if (filter === "variance") return counted !== null && counted !== l.systemQuantity;
    return true;
  }), [d, drafts, filter]);

  const inputs = (): CountLineInput[] | string => {
    const out: CountLineInput[] = [];
    for (const l of d?.lines ?? []) {
      const dr = drafts[l.id];
      if (!dr || dr.counted.trim() === "") continue;
      const v = num(dr.counted);
      if (v === null || v < 0) return `Quantité invalide pour ${l.productName}.`;
      out.push({ productId: l.productId, lotId: null, countedQuantity: v, note: dr.note });
    }
    return out;
  };
  const dirty = d?.lines.some((l) => {
    const dr = drafts[l.id];
    return dr && (dr.counted !== (l.countedQuantity == null ? "" : String(l.countedQuantity)) || dr.note !== (l.note ?? ""));
  });
  const pendingVariances = (d?.lines ?? []).filter((l) => {
    const v = num(drafts[l.id]?.counted ?? "");
    return v !== null && v !== l.systemQuantity;
  }).length;

  async function save() {
    const payload = inputs();
    if (typeof payload === "string") { toast.error(payload); return; }
    const ok = await submit(async () => { await saveCount(countId, payload); return true; });
    if (ok) { toast.success("Comptage enregistré (aucun impact sur le stock avant validation)."); detail.reload(); onChanged(); }
  }

  async function validate(note: string) {
    setConfirmValidate(false);
    const payload = inputs();
    if (typeof payload === "string") { toast.error(payload); return; }
    const ok = await submit(async () => { await validateCount(countId, payload, note || null, operationId); return true; });
    if (ok) { toast.success("Inventaire validé : écarts ajustés et comptabilisés."); detail.reload(); onChanged(); }
  }

  async function cancel(reason: string) {
    setConfirmCancel(false);
    const ok = await submit(async () => { await cancelCount(countId, reason); return true; });
    if (ok) { toast.success("Inventaire annulé."); detail.reload(); onChanged(); }
  }

  return (
    <Drawer title={d ? `Inventaire ${d.count.documentNumber}` : "Inventaire"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {d && (
        <div style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <StatePill status={COUNT_STATUS[d.count.status] ?? d.count.status} />
            <Badge>{d.count.locationName}</Badge>
            <Badge>{COUNT_TYPE[d.count.countType]}</Badge>
            <Badge tone="info">{d.count.countedLines} / {d.count.lineCount} comptés</Badge>
            {d.count.varianceLines > 0 && <Badge tone="warning">{d.count.varianceLines} écart(s) enregistré(s)</Badge>}
          </div>
          {d.count.notes && <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>{d.count.notes}</div>}
          {open && (
            <p style={{ margin: 0, fontSize: 13, color: "var(--nx-text-2)" }}>
              Quantités système figées à l'ouverture ({d.count.startedAt ? dateTime(d.count.startedAt) : "—"}).
              Les ventes et réceptions intervenues depuis sont prises en compte à la validation.
            </p>
          )}
          <div style={{ maxWidth: 240 }}>
            <Field label="Afficher">
              <Select value={filter} onChange={(e) => setFilter(e.target.value as "all" | "todo" | "variance")}>
                <option value="all">Toutes les lignes</option>
                <option value="todo">À compter</option>
                <option value="variance">Avec écart</option>
              </Select>
            </Field>
          </div>
          <div className="nx-scroll-x">
            <table className="nx-table">
              <thead><tr><th>Article</th><th className="nx-num">Système</th><th>Compté</th><th className="nx-num">Écart</th><th>Justification</th></tr></thead>
              <tbody>
                {lines.map((l) => {
                  const dr = drafts[l.id] ?? { counted: "", note: "" };
                  const counted = num(dr.counted);
                  const variance = counted === null ? null : counted - l.systemQuantity;
                  return (
                    <tr key={l.id}>
                      <td><code>{l.sku}</code> {l.productName}</td>
                      <td className="nx-num">{qty(l.systemQuantity, l.unitCode)}</td>
                      <td style={{ minWidth: 110 }}>
                        {open ? (
                          <TextInput type="number" min={0} step="any" value={dr.counted} aria-label={`Quantité comptée — ${l.productName}`}
                                     onChange={(e) => setDrafts({ ...drafts, [l.id]: { ...dr, counted: e.target.value } })} />
                        ) : qty(l.countedQuantity, l.unitCode)}
                      </td>
                      <td className="nx-num" style={{ color: variance ? (variance < 0 ? "var(--nx-neg)" : "var(--nx-pos)") : undefined }}>
                        {variance === null ? "—" : `${variance > 0 ? "+" : ""}${qty(variance)}`}
                      </td>
                      <td style={{ minWidth: 180 }}>
                        {open ? (
                          <TextInput value={dr.note} aria-label={`Justification — ${l.productName}`}
                                     placeholder={variance ? "Cause de l'écart" : ""}
                                     onChange={(e) => setDrafts({ ...drafts, [l.id]: { ...dr, note: e.target.value } })} />
                        ) : l.note ?? "—"}
                      </td>
                    </tr>
                  );
                })}
                {lines.length === 0 && <tr><td colSpan={5} style={{ textAlign: "center", color: "var(--nx-text-2)", padding: 16 }}>Aucune ligne.</td></tr>}
              </tbody>
            </table>
          </div>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          {open && (
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              {hasPermission("inventory.count") && <Button onClick={() => void save()} loading={busy} disabled={!dirty}>Enregistrer le comptage</Button>}
              {hasPermission("inventory.validate_adjustment") && (
                <Button variant="subtle" onClick={() => setConfirmValidate(true)} disabled={busy}>Valider l'inventaire</Button>
              )}
              {hasPermission("inventory.count") && <Button variant="ghost" onClick={() => setConfirmCancel(true)} disabled={busy}>Annuler l'inventaire</Button>}
            </div>
          )}
        </div>
      )}
      {confirmValidate && d && (
        <ConfirmDialog
          title="Valider l'inventaire"
          message={pendingVariances
            ? `${pendingVariances} écart(s) seront ajustés en stock et comptabilisés (écart d'inventaire). Les lignes non comptées restent inchangées. Cette opération est définitive.`
            : "Aucun écart : l'inventaire sera clôturé sans mouvement de stock."}
          requireReason={pendingVariances > 0}
          confirmLabel="Valider définitivement"
          onConfirm={(reason) => void validate(reason)}
          onCancel={() => setConfirmValidate(false)}
        />
      )}
      {confirmCancel && (
        <ConfirmDialog title="Annuler l'inventaire" message="Les quantités saisies sont conservées pour mémoire ; aucun stock n'est modifié."
                       requireReason danger confirmLabel="Annuler l'inventaire"
                       onConfirm={(reason) => void cancel(reason)} onCancel={() => setConfirmCancel(false)} />
      )}
    </Drawer>
  );
}
