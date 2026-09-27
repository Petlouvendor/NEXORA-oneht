import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, Drawer, Money, SectionLabel, StatePill,
  type Column,
} from "@/shared/ui";
import {
  cancelOrder, completeOrder, confirmOrder, getOrder, listOrders, reserveOrder,
  type Order, type OrderSummary,
} from "@/sales/ordersApi";

const NEXT_ACTION: Record<string, Array<{ label: string; run: (id: string) => Promise<unknown> }>> = {
  draft: [{ label: "Confirmer", run: confirmOrder }],
  confirmed: [
    { label: "Réserver le stock", run: reserveOrder },
  ],
  reserved: [],
  partially_fulfilled: [],
};

/** Commandes clients (Phase 25 §18-20) : cycle création → confirmation → réservation → exécution. */
export function OrdersPage() {
  const { hasPermission } = useAuth();
  const [rows, setRows] = useState<OrderSummary[]>([]);
  const [selected, setSelected] = useState<Order | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setError(null);
    listOrders(undefined, 100)
      .then(setRows)
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, []);

  useEffect(load, [load]);

  const open = (id: string) =>
    getOrder(id).then(setSelected).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await fn();
      if (selected) await open(selected.id);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Action refusée");
    } finally {
      setBusy(false);
    }
  }

  const cols: Array<Column<OrderSummary>> = [
    {
      key: "n", header: "N°",
      render: (o) => (
        <button
          onClick={() => open(o.id)}
          style={{ appearance: "none", background: "transparent", border: "none", cursor: "pointer", font: "inherit", color: "var(--nx-brand)", padding: 0 }}
        >
          {o.documentNumber}
        </button>
      ),
    },
    { key: "t", header: "Type", render: (o) => <Badge>{o.orderType}</Badge> },
    { key: "c", header: "Client", render: (o) => o.customerName ?? "—" },
    { key: "s", header: "Statut", render: (o) => <StatePill status={o.status} /> },
    { key: "tot", header: "Total", numeric: true, render: (o) => <Money value={o.total} currency={o.currency} /> },
    { key: "dep", header: "Acompte", numeric: true, render: (o) => (o.depositAmount > 0 ? <Money value={o.depositAmount} currency={o.currency} /> : "—") },
    { key: "rem", header: "Reste dû", numeric: true, render: (o) => <Money value={o.remainingBalance} currency={o.currency} /> },
  ];

  if (!hasPermission("pos.manage_order")) {
    return <Card>La gestion des commandes n'est pas accessible avec vos permissions.</Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Commandes clients</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <Card>
        <DataTable columns={cols} rows={rows} rowKey={(o) => o.id} empty="Aucune commande." />
      </Card>

      {selected && (
        <Drawer title={`Commande ${selected.documentNumber}`} onClose={() => setSelected(null)}>
          <div style={{ display: "grid", gap: 16 }}>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
              <StatePill status={selected.status} />
              <Badge>{selected.orderType}</Badge>
              {selected.onCredit && <Badge tone="warning">à crédit</Badge>}
            </div>

            <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
              <Metric label="Total" value={<Money value={selected.total} currency={selected.currency} />} />
              <Metric label="Acompte" value={<Money value={selected.depositAmount} currency={selected.currency} />} />
              <Metric label="Reste dû" value={<strong><Money value={selected.remainingBalance} currency={selected.currency} /></strong>} />
            </div>

            <div>
              <SectionLabel>Lignes</SectionLabel>
              <DataTable
                columns={[
                  { key: "d", header: "Article", render: (l) => l.description },
                  { key: "q", header: "Qté", numeric: true, render: (l) => `${l.quantityFulfilled} / ${l.quantity}` },
                  { key: "p", header: "PU", numeric: true, render: (l) => <Money value={l.unitPrice} currency={selected.currency} /> },
                  { key: "t", header: "Total ligne", numeric: true, render: (l) => <Money value={l.lineTotal} currency={selected.currency} /> },
                ]}
                rows={selected.lines}
                rowKey={(l) => l.id}
              />
            </div>

            {selected.saleIds.length > 0 && (
              <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>
                Vente(s) générée(s) : {selected.saleIds.join(", ")}
              </div>
            )}

            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              {(NEXT_ACTION[selected.status] ?? []).map((a) => (
                <Button key={a.label} size="sm" variant="subtle" disabled={busy} onClick={() => run(() => a.run(selected.id))}>
                  {a.label}
                </Button>
              ))}
              {["confirmed", "reserved", "partially_fulfilled"].includes(selected.status) && (
                <Button size="sm" disabled={busy} onClick={() => run(() => completeOrder(selected.id, [], true, selected.dueDate))}>
                  Exécuter (à crédit)
                </Button>
              )}
              {selected.status !== "fulfilled" && selected.status !== "cancelled" && (
                <Button size="sm" variant="danger" disabled={busy}
                  onClick={() => {
                    const r = window.prompt("Motif d'annulation ?");
                    if (r) run(() => cancelOrder(selected.id, r));
                  }}>
                  Annuler
                </Button>
              )}
            </div>
          </div>
        </Drawer>
      )}
    </div>
  );
}

function Metric({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{label}</div>
      <div style={{ fontSize: 18 }}>{value}</div>
    </div>
  );
}
