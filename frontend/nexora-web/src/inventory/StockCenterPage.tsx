import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, KpiCard, Money, StatePill, Tabs,
  type Column,
} from "@/shared/ui";
import {
  blockLot, getAlerts, getExpiringLots, getLots, getMovements, recallLot, sweepExpiredLots, unblockLot,
  type ExpiringLot, type Lot, type StockAlert, type StockMovement,
} from "@/inventory/inventoryApi";

const ALERT_TONE: Record<string, "error" | "warning" | "info"> = {
  out_of_stock: "error", expired: "error",
  low_stock: "warning", near_expiry: "warning",
  overstock: "info",
};

/** Centre de stock — alertes, péremptions, mouvements & cycle de vie des lots (Phase 24 §13-15, §39-40). */
export function StockCenterPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("alerts");
  const [alerts, setAlerts] = useState<StockAlert[]>([]);
  const [expiring, setExpiring] = useState<ExpiringLot[]>([]);
  const [lots, setLots] = useState<Lot[]>([]);
  const [movements, setMovements] = useState<StockMovement[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [sweepMsg, setSweepMsg] = useState<string | null>(null);

  const canManageLot = hasPermission("inventory.manage_lot");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      getAlerts().catch(() => []),
      getExpiringLots(60).catch(() => []),
      getLots({ expiringWithinDays: 3650 }).catch(() => []),
      getMovements({ limit: 100 }).catch(() => []),
    ])
      .then(([a, e, l, m]) => { setAlerts(a); setExpiring(e); setLots(l); setMovements(m); })
      .catch((err) => setError(err instanceof ApiError ? err.message : "Erreur de chargement"));
  }, []);

  useEffect(load, [load]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  async function sweep() {
    setBusy(true);
    setError(null);
    try {
      const r = await sweepExpiredLots();
      setSweepMsg(`${r.lotsExpired} lot(s) expiré(s) — ${r.stockValueWrittenOff.toLocaleString("fr")} sortis du stock.`);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Erreur");
    } finally {
      setBusy(false);
    }
  }

  if (!hasPermission("inventory.view")) {
    return <Card>Le stock n'est pas accessible avec vos permissions.</Card>;
  }

  const nearCount = expiring.filter((e) => e.expiryStatus === "near").length;
  const expiredCount = expiring.filter((e) => e.expiryStatus === "expired").length;
  const movementCount = movements.length;

  const alertCols: Array<Column<StockAlert>> = [
    { key: "t", header: "Type", render: (a) => <Badge tone={ALERT_TONE[a.alertType]}>{a.alertType}</Badge> },
    { key: "p", header: "Produit", render: (a) => <>{a.productName} <code>{a.sku}</code></> },
    { key: "l", header: "Emplacement", render: (a) => a.locationName },
    { key: "oh", header: "En stock", numeric: true, render: (a) => a.onHand },
    { key: "av", header: "Dispo", numeric: true, render: (a) => a.available },
    { key: "th", header: "Seuil", numeric: true, render: (a) => a.threshold ?? "—" },
    { key: "d", header: "Détail", render: (a) => a.detail || "—" },
  ];

  const expCols: Array<Column<ExpiringLot>> = [
    { key: "s", header: "Statut", render: (e) => <StatePill status={e.expiryStatus} /> },
    { key: "l", header: "Lot", render: (e) => <code>{e.lotNumber}</code> },
    { key: "p", header: "Produit", render: (e) => e.productName },
    { key: "exp", header: "Expiration", render: (e) => e.expirationDate },
    { key: "d", header: "Jours", numeric: true, render: (e) => e.daysToExpiry },
    { key: "q", header: "Stock", numeric: true, render: (e) => e.onHand },
    { key: "v", header: "Valeur", numeric: true, render: (e) => <Money value={e.stockValue} /> },
  ];

  const lotCols: Array<Column<Lot>> = [
    { key: "l", header: "Lot", render: (l) => <code>{l.lotNumber}</code> },
    { key: "p", header: "Produit", render: (l) => <>{l.productName} <code>{l.sku}</code></> },
    { key: "st", header: "Statut", render: (l) => <StatePill status={l.status} /> },
    { key: "e", header: "Expiration", render: (l) => l.expirationDate ?? "—" },
    { key: "q", header: "En stock", numeric: true, render: (l) => l.onHand },
    { key: "s", header: "Fournisseur", render: (l) => l.supplierName ?? "—" },
    {
      key: "a", header: "", render: (l) => canManageLot ? (
        <div style={{ display: "flex", gap: 6 }}>
          {l.status === "active" && (
            <Button size="sm" variant="subtle" disabled={busy}
              onClick={() => run(() => blockLot(l.id, window.prompt("Motif du blocage ?") || "blocage"))}>Bloquer</Button>
          )}
          {l.status === "blocked" && (
            <Button size="sm" variant="subtle" disabled={busy}
              onClick={() => run(() => unblockLot(l.id, window.prompt("Motif du déblocage ?") || "ok"))}>Débloquer</Button>
          )}
          {l.status !== "recalled" && (
            <Button size="sm" variant="danger" disabled={busy}
              onClick={() => {
                const ref = window.prompt("Référence de rappel ?");
                if (ref) run(() => recallLot(l.id, ref, window.prompt("Motif ?") || "rappel"));
              }}>Rappeler</Button>
          )}
        </div>
      ) : null,
    },
  ];

  const mvCols: Array<Column<StockMovement>> = [
    { key: "d", header: "Date", render: (m) => new Date(m.occurredAt).toLocaleString("fr") },
    { key: "t", header: "Type", render: (m) => <Badge>{m.movementType}</Badge> },
    { key: "p", header: "Produit", render: (m) => <code>{m.sku}</code> },
    { key: "l", header: "Emplacement", render: (m) => m.locationName },
    { key: "lot", header: "Lot", render: (m) => m.lotNumber ?? "—" },
    { key: "q", header: "Qté", numeric: true, render: (m) => <span style={{ color: m.quantity < 0 ? "var(--nx-neg)" : "var(--nx-pos)" }}>{m.quantity}</span> },
    { key: "c", header: "Coût unit.", numeric: true, render: (m) => <Money value={m.unitCost} /> },
    { key: "r", header: "Source", render: (m) => m.referenceType ?? "—" },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", flexWrap: "wrap", gap: 8 }}>
        <h1 style={{ margin: 0 }}>Stock</h1>
        {hasPermission("inventory.write_off") && (
          <Button variant="subtle" onClick={sweep} loading={busy}>Balayer les lots expirés</Button>
        )}
      </div>
      {error && <div className="nx-error-text">{error}</div>}
      {sweepMsg && <div className="nx-card" style={{ borderColor: "var(--nx-brand)" }}>{sweepMsg}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        <KpiCard label="Alertes stock" value={String(alerts.length)} />
        <KpiCard label="Lots proches péremption" value={String(nearCount)} />
        <KpiCard label="Lots expirés (stock détenu)" value={String(expiredCount)} />
        <KpiCard label="Mouvements récents" value={String(movementCount)} />
      </div>

      <Card>
        <Tabs
          tabs={[
            { id: "alerts", label: `Alertes (${alerts.length})` },
            { id: "expiring", label: `Péremptions (${expiring.length})` },
            { id: "lots", label: `Lots (${lots.length})` },
            { id: "movements", label: "Mouvements" },
          ]}
          active={tab}
          onChange={setTab}
        />
        <div style={{ marginTop: 12 }}>
          {tab === "alerts" && <DataTable columns={alertCols} rows={alerts} rowKey={(a) => a.alertType + a.productId + a.locationId} empty="Aucune alerte." />}
          {tab === "expiring" && <DataTable columns={expCols} rows={expiring} rowKey={(e) => e.lotId} empty="Aucun lot proche de la péremption." />}
          {tab === "lots" && <DataTable columns={lotCols} rows={lots} rowKey={(l) => l.id} empty="Aucun lot." />}
          {tab === "movements" && <DataTable columns={mvCols} rows={movements} rowKey={(m) => m.id} empty="Aucun mouvement." />}
        </div>
      </Card>
    </div>
  );
}
