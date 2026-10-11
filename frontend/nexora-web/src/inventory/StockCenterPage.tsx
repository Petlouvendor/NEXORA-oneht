import { useState, type FormEvent } from "react";
import { dateShort, dateTime, money } from "@/shared/format";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, EmptyState, Field, Modal, Money, SectionLabel, Select, StatePill, Tabs, TextInput,
  type Column,
} from "@/shared/ui";
import {
  blockLot, createLocation, createWarehouse, getAlerts, getExpiringLots, getLots, getMovements, getSites, recallLot, sweepExpiredLots,
  unblockLot, type ExpiringLot, type Lot, type StockAlert, type StockMovement,
} from "@/inventory/inventoryApi";
import { MOVEMENT_LABEL, qty, useStockPlaces } from "@/inventory/stockShared";
import { StockOverviewPanel } from "@/inventory/StockOverviewPanel";
import { CountsPanel } from "@/inventory/CountsPanel";
import { TransfersPanel } from "@/inventory/TransfersPanel";
import { AdjustmentsPanel } from "@/inventory/AdjustmentsPanel";

const ALERT_TONE: Record<string, "error" | "warning" | "info"> = {
  out_of_stock: "error", expired: "error", low_stock: "warning", near_expiry: "warning", overstock: "info",
};

/**
 * Centre de stock (Phase 24 + finalisation produit, phase 2) : tableau de stock, inventaires, transferts,
 * pertes & ajustements, lots & péremptions, mouvements, emplacements. Aucune quantité ne change sans
 * mouvement tracé côté serveur.
 */
export function StockCenterPage() {
  const { hasPermission, activeCompanyId } = useAuth();
  const [params, setParams] = useSearchParams();
  const places = useStockPlaces(activeCompanyId);
  const [tab, setTab] = useState(params.get("tab") ?? "overview");

  if (!hasPermission("inventory.view")) {
    return <Card><EmptyState title="Stock" hint="Le stock n'est pas accessible avec vos permissions." /></Card>;
  }
  const tabs = [
    { id: "overview", label: "État du stock" },
    { id: "counts", label: "Inventaires" },
    { id: "transfers", label: "Transferts" },
    { id: "adjustments", label: "Pertes & ajustements" },
    { id: "lots", label: "Lots & péremptions" },
    { id: "alerts", label: "Alertes" },
    { id: "movements", label: "Mouvements" },
    { id: "locations", label: "Emplacements" },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Stock</h1>
      {places.error && <div role="alert" className="nx-error-text">Emplacements indisponibles : {places.error}</div>}
      <Tabs tabs={tabs} active={tab} onChange={(id) => { setTab(id); setParams(id === "overview" ? {} : { tab: id }, { replace: true }); }} />
      {tab === "overview" && <StockOverviewPanel locations={places.active} />}
      {tab === "counts" && <CountsPanel locations={places.active} />}
      {tab === "transfers" && <TransfersPanel locations={places.active} />}
      {tab === "adjustments" && <AdjustmentsPanel locations={places.active} />}
      {tab === "lots" && <LotsPanel />}
      {tab === "alerts" && <AlertsPanel />}
      {tab === "movements" && <MovementsPanel />}
      {tab === "locations" && <LocationsPanel places={places} />}
    </div>
  );
}

// ---------------------------------------------------------------- Alertes

function AlertsPanel() {
  const alerts = useAsync(getAlerts, []);
  const cols: Array<Column<StockAlert>> = [
    { key: "t", header: "Type", render: (a) => <Badge tone={ALERT_TONE[a.alertType]}>{a.alertType}</Badge> },
    { key: "p", header: "Produit", render: (a) => <>{a.productName} <code>{a.sku}</code></> },
    { key: "l", header: "Emplacement", render: (a) => a.locationName },
    { key: "oh", header: "En stock", numeric: true, render: (a) => qty(a.onHand) },
    { key: "av", header: "Dispo", numeric: true, render: (a) => qty(a.available) },
    { key: "th", header: "Seuil", numeric: true, render: (a) => qty(a.threshold) },
    { key: "d", header: "Détail", render: (a) => a.detail || "—" },
  ];
  return (
    <Card>
      {alerts.error && <div role="alert" className="nx-error-text">{alerts.error}</div>}
      <DataTable exportTitle="Alertes de stock" columns={cols} rows={alerts.data ?? []}
                 rowKey={(a) => a.alertType + a.productId + a.locationId} empty={alerts.loading ? "Chargement…" : "Aucune alerte."} />
    </Card>
  );
}

// ---------------------------------------------------------------- Mouvements

function MovementsPanel() {
  const [page, setPage] = useState(0);
  const moves = useAsync(() => getMovements({ limit: 100, offset: page * 100 }), [page]);
  const cols: Array<Column<StockMovement>> = [
    { key: "d", header: "Date", render: (m) => dateTime(m.occurredAt) },
    { key: "t", header: "Type", render: (m) => <Badge>{MOVEMENT_LABEL[m.movementType] ?? m.movementType}</Badge> },
    { key: "p", header: "Produit", render: (m) => <code>{m.sku}</code> },
    { key: "l", header: "Emplacement", render: (m) => m.locationName },
    { key: "lot", header: "Lot", render: (m) => m.lotNumber ?? "—" },
    { key: "q", header: "Qté", numeric: true, render: (m) => <span style={{ color: m.quantity < 0 ? "var(--nx-neg)" : "var(--nx-pos)" }}>{qty(m.quantity)}</span> },
    { key: "c", header: "Coût unit.", numeric: true, render: (m) => <Money value={m.unitCost} /> },
    { key: "r", header: "Source", render: (m) => m.referenceType ?? "—" },
  ];
  return (
    <Card>
      {moves.error && <div role="alert" className="nx-error-text">{moves.error}</div>}
      <DataTable exportTitle="Mouvements de stock" columns={cols} rows={moves.data ?? []} rowKey={(m) => m.id}
                 empty={moves.loading ? "Chargement…" : "Aucun mouvement."} />
      <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 10, alignItems: "center" }}>
        <Button size="sm" variant="ghost" disabled={page === 0 || moves.loading} onClick={() => setPage(page - 1)}>Plus récents</Button>
        <span style={{ fontSize: 13 }}>Page {page + 1}</span>
        <Button size="sm" variant="ghost" disabled={(moves.data?.length ?? 0) < 100 || moves.loading} onClick={() => setPage(page + 1)}>Plus anciens</Button>
      </div>
    </Card>
  );
}

// ---------------------------------------------------------------- Lots (sans window.prompt)

type LotAction = { kind: "block" | "unblock" | "recall"; lot: Lot };

function LotsPanel() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const lots = useAsync(() => Promise.all([getExpiringLots(60), getLots({ expiringWithinDays: 3650 })]), []);
  const [action, setAction] = useState<LotAction | null>(null);
  const [sweeping, setSweeping] = useState(false);
  const [busy, setBusy] = useState(false);
  const canManage = hasPermission("inventory.manage_lot");
  const [expiring, all] = lots.data ?? [[], []];

  async function sweep() {
    setSweeping(false);
    setBusy(true);
    try {
      const r = await sweepExpiredLots();
      toast.success(`${r.lotsExpired} lot(s) expiré(s) sorti(s) du stock — valeur ${money(r.stockValueWrittenOff)}.`);
      lots.reload();
    } catch (e) { toast.error(e instanceof Error ? e.message : "Opération impossible"); }
    finally { setBusy(false); }
  }

  const expCols: Array<Column<ExpiringLot>> = [
    { key: "s", header: "Statut", render: (e) => <StatePill status={e.expiryStatus} /> },
    { key: "l", header: "Lot", render: (e) => <code>{e.lotNumber}</code> },
    { key: "p", header: "Produit", render: (e) => e.productName },
    { key: "exp", header: "Expiration", render: (e) => dateShort(e.expirationDate) },
    { key: "d", header: "Jours", numeric: true, render: (e) => e.daysToExpiry },
    { key: "q", header: "Stock", numeric: true, render: (e) => qty(e.onHand) },
    { key: "v", header: "Valeur", numeric: true, render: (e) => <Money value={e.stockValue} /> },
  ];
  const lotCols: Array<Column<Lot>> = [
    { key: "l", header: "Lot", render: (l) => <code>{l.lotNumber}</code> },
    { key: "p", header: "Produit", render: (l) => <>{l.productName} <code>{l.sku}</code></> },
    { key: "st", header: "Statut", render: (l) => <StatePill status={l.status} /> },
    { key: "e", header: "Expiration", render: (l) => (l.expirationDate ? dateShort(l.expirationDate) : "—") },
    { key: "q", header: "En stock", numeric: true, render: (l) => qty(l.onHand) },
    { key: "s", header: "Fournisseur", render: (l) => l.supplierName ?? "—" },
    { key: "r", header: "Motif", render: (l) => l.blockedReason ?? l.recallReference ?? "—" },
    {
      key: "a", header: "", render: (l) => canManage ? (
        <div style={{ display: "flex", gap: 6 }}>
          {l.status === "active" && <Button size="sm" variant="subtle" onClick={() => setAction({ kind: "block", lot: l })}>Bloquer</Button>}
          {l.status === "blocked" && <Button size="sm" variant="subtle" onClick={() => setAction({ kind: "unblock", lot: l })}>Débloquer</Button>}
          {l.status !== "recalled" && <Button size="sm" variant="danger" onClick={() => setAction({ kind: "recall", lot: l })}>Rappeler</Button>}
        </div>
      ) : null,
    },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={hasPermission("inventory.write_off")
          ? <Button size="sm" variant="subtle" loading={busy} onClick={() => setSweeping(true)}>Sortir les lots expirés</Button> : undefined}>
          Péremptions à 60 jours ({expiring.length})
        </SectionLabel>
        {lots.error && <div role="alert" className="nx-error-text">{lots.error}</div>}
        <DataTable exportTitle="Péremptions" columns={expCols} rows={expiring} rowKey={(e) => e.lotId}
                   empty={lots.loading ? "Chargement…" : "Aucun lot proche de la péremption."} />
      </Card>
      <Card>
        <SectionLabel>Lots ({all.length})</SectionLabel>
        <DataTable exportTitle="Lots" columns={lotCols} rows={all} rowKey={(l) => l.id} empty={lots.loading ? "Chargement…" : "Aucun lot."} />
      </Card>
      {action && <LotActionDialog action={action} onClose={() => setAction(null)} onDone={() => { setAction(null); lots.reload(); }} />}
      {sweeping && (
        <ConfirmDialog title="Sortir les lots expirés" danger confirmLabel="Sortir du stock"
                       message="Tout lot actif dont la date est dépassée est passé « expiré » ; son stock est sorti et la perte comptabilisée."
                       onConfirm={() => void sweep()} onCancel={() => setSweeping(false)} />
      )}
    </div>
  );
}

function LotActionDialog({ action, onClose, onDone }: { action: LotAction; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [reason, setReason] = useState("");
  const [reference, setReference] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit();
  const title = { block: "Bloquer le lot", unblock: "Débloquer le lot", recall: "Rappeler le lot" }[action.kind];

  async function save(e: FormEvent) {
    e.preventDefault();
    if (action.kind === "recall" && !reference.trim()) return fail("reference", "La référence de rappel est obligatoire.");
    if (reason.trim().length < 3) return fail("reason", "Le motif est obligatoire (3 caractères au moins).");
    const ok = await submit(async () => {
      if (action.kind === "block") await blockLot(action.lot.id, reason.trim());
      else if (action.kind === "unblock") await unblockLot(action.lot.id, reason.trim());
      else await recallLot(action.lot.id, reference.trim(), reason.trim());
      return true;
    });
    if (ok) { toast.success(`${title} : fait.`); onDone(); }
  }

  return (
    <Modal title={`${title} ${action.lot.lotNumber}`} onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <p style={{ margin: 0 }}>{action.lot.productName} — {qty(action.lot.onHand)} en stock.
          {action.kind === "block" && " Un lot bloqué ne peut plus être vendu ni transféré."}
          {action.kind === "recall" && " Le rappel est définitif et tracé."}</p>
        {action.kind === "recall" && (
          <Field label="Référence du rappel" required error={fieldErrors.reference}>
            <TextInput value={reference} onChange={(e) => setReference(e.target.value)} autoFocus />
          </Field>
        )}
        <Field label="Motif" required error={fieldErrors.reason} hint="Conservé dans le journal d'audit.">
          <TextInput value={reason} onChange={(e) => setReason(e.target.value)} autoFocus={action.kind !== "recall"} />
        </Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy} variant={action.kind === "recall" ? "danger" : "primary"}>Confirmer</Button>
        </div>
      </form>
    </Modal>
  );
}

// ---------------------------------------------------------------- Emplacements & entrepôts

const WAREHOUSE_TYPES: Record<string, string> = { general: "Général", cold: "Froid", bonded: "Sous douane", transit: "Transit", quarantine: "Quarantaine" };
const LOCATION_TYPES: Record<string, string> = { store: "Magasin", warehouse: "Entrepôt", virtual: "Virtuel", production: "Production" };

function LocationsPanel({ places }: { places: ReturnType<typeof useStockPlaces> }) {
  const { hasPermission } = useAuth();
  const canManage = hasPermission("inventory.manage_warehouse");
  const [dialog, setDialog] = useState<"warehouse" | "location" | null>(null);
  const whName = (id: string | null) => places.warehouses.find((w) => w.id === id)?.name ?? "—";
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={canManage ? <Button size="sm" onClick={() => setDialog("warehouse")}>Nouvel entrepôt</Button> : undefined}>
          Entrepôts ({places.warehouses.length})
        </SectionLabel>
        <DataTable exportTitle="Entrepôts"
          columns={[
            { key: "c", header: "Code", render: (w) => <code>{w.code}</code> },
            { key: "n", header: "Nom", render: (w) => w.name },
            { key: "t", header: "Type", render: (w) => WAREHOUSE_TYPES[w.type] ?? w.type },
            { key: "a", header: "Adresse", render: (w) => w.address ?? "—" },
            { key: "s", header: "Statut", render: (w) => <StatePill status={w.status} /> },
          ]}
          rows={places.warehouses} rowKey={(w) => w.id} empty={places.loading ? "Chargement…" : "Aucun entrepôt."} />
      </Card>
      <Card>
        <SectionLabel action={canManage ? <Button size="sm" onClick={() => setDialog("location")}>Nouvel emplacement</Button> : undefined}>
          Emplacements de stock ({places.locations.length})
        </SectionLabel>
        <DataTable exportTitle="Emplacements"
          columns={[
            { key: "n", header: "Nom", render: (l) => l.name },
            { key: "c", header: "Code", render: (l) => (l.code ? <code>{l.code}</code> : "—") },
            { key: "t", header: "Type", render: (l) => LOCATION_TYPES[l.locationType] ?? l.locationType },
            { key: "w", header: "Entrepôt", render: (l) => whName(l.warehouseId) },
            { key: "z", header: "Zone / allée / étagère", render: (l) => [l.zone, l.aisle, l.shelf, l.bin].filter(Boolean).join(" · ") || "—" },
            { key: "s", header: "Statut", render: (l) => <StatePill status={l.status} /> },
          ]}
          rows={places.locations} rowKey={(l) => l.id} empty={places.loading ? "Chargement…" : "Aucun emplacement."} />
      </Card>
      {dialog && <PlaceDialog kind={dialog} places={places} onClose={() => setDialog(null)} onSaved={() => { setDialog(null); places.reload(); }} />}
    </div>
  );
}

function PlaceDialog({ kind, places, onClose, onSaved }: {
  kind: "warehouse" | "location"; places: ReturnType<typeof useStockPlaces>; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const sites = useAsync(getSites, []);
  const [f, setF] = useState({
    name: "", code: "", type: kind === "warehouse" ? "general" : "warehouse", establishmentId: "", address: "", capacity: "",
    warehouseId: "", storeId: "", zone: "", aisle: "", shelf: "", bin: "",
  });
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "inventory.warehouse_exists": "code", "inventory.warehouse_fields": "name", "inventory.establishment_unknown": "establishmentId",
    "inventory.location_target": "warehouseId", "inventory.warehouse_unknown": "warehouseId",
  });
  const establishments = (sites.data ?? []).filter((s) => s.kind === "establishment" && s.status === "active");
  const stores = (sites.data ?? []).filter((s) => s.kind === "store" && s.status === "active");

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.name.trim()) return fail("name", "Le nom est obligatoire.");
    const capacity = num(f.capacity);
    if (kind === "warehouse") {
      if (!f.code.trim()) return fail("code", "Le code est obligatoire.");
      if (!f.establishmentId) return fail("establishmentId", "Choisissez l'établissement.");
      const ok = await submit(async () => {
        await createWarehouse({ name: f.name.trim(), code: f.code.trim().toUpperCase(), type: f.type, establishmentId: f.establishmentId,
                                address: f.address.trim() || null, capacity });
        return true;
      });
      if (ok) { toast.success("Entrepôt créé. Ajoutez-lui un emplacement de stock pour y recevoir de la marchandise."); onSaved(); }
      return;
    }
    const physical = f.type === "store" || f.type === "warehouse";
    if (physical && !f.warehouseId && !f.storeId) return fail("warehouseId", "Rattachez l'emplacement à un entrepôt ou un magasin.");
    const ok = await submit(async () => {
      await createLocation({
        name: f.name.trim(), code: f.code.trim() || null, locationType: f.type,
        warehouseId: f.warehouseId || null, storeId: f.storeId || null,
        zone: f.zone.trim() || null, aisle: f.aisle.trim() || null, shelf: f.shelf.trim() || null, bin: f.bin.trim() || null, capacity,
      });
      return true;
    });
    if (ok) { toast.success("Emplacement créé."); onSaved(); }
  }

  const grid: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 12 };
  return (
    <Modal title={kind === "warehouse" ? "Nouvel entrepôt" : "Nouvel emplacement de stock"} onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <div style={grid}>
          <Field label="Nom" required error={fieldErrors.name}><TextInput value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} autoFocus /></Field>
          <Field label="Code" required={kind === "warehouse"} error={fieldErrors.code}><TextInput value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} /></Field>
          <Field label="Type">
            <Select value={f.type} onChange={(e) => setF({ ...f, type: e.target.value })}>
              {Object.entries(kind === "warehouse" ? WAREHOUSE_TYPES : LOCATION_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
        </div>
        {kind === "warehouse" ? (
          <div style={grid}>
            <Field label="Établissement" required error={fieldErrors.establishmentId}>
              <Select value={f.establishmentId} onChange={(e) => setF({ ...f, establishmentId: e.target.value })}>
                <option value="">{sites.loading ? "Chargement…" : "—"}</option>
                {establishments.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
              </Select>
            </Field>
            <Field label="Adresse"><TextInput value={f.address} onChange={(e) => setF({ ...f, address: e.target.value })} /></Field>
            <Field label="Capacité"><TextInput type="number" min={0} step="any" value={f.capacity} onChange={(e) => setF({ ...f, capacity: e.target.value })} /></Field>
          </div>
        ) : (
          <>
            <div style={grid}>
              <Field label="Entrepôt" error={fieldErrors.warehouseId}>
                <Select value={f.warehouseId} onChange={(e) => setF({ ...f, warehouseId: e.target.value, storeId: e.target.value ? "" : f.storeId })}>
                  <option value="">—</option>
                  {places.warehouses.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
                </Select>
              </Field>
              <Field label="ou magasin">
                <Select value={f.storeId} onChange={(e) => setF({ ...f, storeId: e.target.value, warehouseId: e.target.value ? "" : f.warehouseId })}>
                  <option value="">—</option>
                  {stores.map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
                </Select>
              </Field>
            </div>
            <div style={grid}>
              <Field label="Zone"><TextInput value={f.zone} onChange={(e) => setF({ ...f, zone: e.target.value })} /></Field>
              <Field label="Allée"><TextInput value={f.aisle} onChange={(e) => setF({ ...f, aisle: e.target.value })} /></Field>
              <Field label="Étagère"><TextInput value={f.shelf} onChange={(e) => setF({ ...f, shelf: e.target.value })} /></Field>
              <Field label="Case"><TextInput value={f.bin} onChange={(e) => setF({ ...f, bin: e.target.value })} /></Field>
            </div>
          </>
        )}
        {(error || sites.error) && <div role="alert" className="nx-error-text">{error ?? sites.error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy}>Créer</Button>
        </div>
      </form>
    </Modal>
  );
}
