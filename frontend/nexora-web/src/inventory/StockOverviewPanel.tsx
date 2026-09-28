import { useEffect, useMemo, useState } from "react";
import { dateShort, dateTime } from "@/shared/format";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { Badge, Button, Card, DataTable, Drawer, Field, KpiCard, Money, Select, TextInput, type Column } from "@/shared/ui";
import { listCategories } from "@/catalog/catalogApi";
import { sortedCategories } from "@/catalog/catalogShared";
import { getMovements, getStockOverview, type StockOverviewRow, type StockStatus } from "@/inventory/inventoryApi";
import { LocationSelect, MOVEMENT_LABEL, qty, STOCK_STATUS } from "@/inventory/stockShared";
import type { Location } from "@/inventory/inventoryApi";

const PAGE = 100;

/** Tableau de stock : état calculé par le serveur (rupture, faible, normal, surstock), valeurs masquées
 * sans inventory.view_valuation, filtres et pagination serveur. */
export function StockOverviewPanel({ locations }: { locations: Location[] }) {
  const { hasPermission, activeCompanyId } = useAuth();
  const [locationId, setLocationId] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [status, setStatus] = useState<StockStatus | "expiring" | "">("");
  const [term, setTerm] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(0);
  const [detail, setDetail] = useState<StockOverviewRow | null>(null);
  useEffect(() => { const t = setTimeout(() => { setSearch(term.trim()); setPage(0); }, 300); return () => clearTimeout(t); }, [term]);
  useEffect(() => setPage(0), [locationId, categoryId, status]);

  const categories = useAsync(() => (hasPermission("catalog.view") ? listCategories() : Promise.resolve([])), [activeCompanyId]);
  const cats = useMemo(() => sortedCategories(categories.data ?? []), [categories.data]);
  const overview = useAsync(() => getStockOverview({
    locationId: locationId || undefined, categoryId: categoryId || undefined, status: status || undefined,
    q: search || undefined, limit: PAGE, offset: page * PAGE,
  }), [locationId, categoryId, status, search, page]);

  const o = overview.data;
  const valued = o?.valuationVisible ?? false;
  const columns: Array<Column<StockOverviewRow>> = [
    { key: "p", header: "Article", render: (r) => (
      <button type="button" onClick={() => setDetail(r)} style={{ appearance: "none", background: "transparent", border: "none",
        padding: 0, cursor: "pointer", color: "var(--nx-brand)", font: "inherit", textAlign: "left" }}>
        <code>{r.sku}</code> {r.productName}
      </button>) },
    { key: "c", header: "Catégorie", render: (r) => r.categoryName ?? "—" },
    { key: "l", header: "Emplacement", render: (r) => r.locationName },
    { key: "oh", header: "En stock", numeric: true, render: (r) => qty(r.onHand, r.unitCode) },
    { key: "rs", header: "Réservé", numeric: true, render: (r) => (r.reserved ? qty(r.reserved) : "—") },
    { key: "av", header: "Disponible", numeric: true, render: (r) => <strong>{qty(r.available)}</strong> },
    { key: "mn", header: "Min", numeric: true, render: (r) => qty(r.minQuantity) },
    { key: "rp", header: "Pt cde", numeric: true, render: (r) => qty(r.reorderPoint) },
    { key: "mx", header: "Max", numeric: true, render: (r) => qty(r.maxQuantity) },
    ...(valued ? [{ key: "v", header: "Valeur", numeric: true, render: (r: StockOverviewRow) => <Money value={r.stockValue} /> }] : []),
    { key: "lm", header: "Dernier mvt", render: (r) => (r.lastMovementAt ? dateShort(r.lastMovementAt) : "—") },
    { key: "s", header: "État", render: (r) => (
      <span style={{ display: "inline-flex", gap: 4, flexWrap: "wrap" }}>
        <Badge tone={STOCK_STATUS[r.stockStatus].tone}>{STOCK_STATUS[r.stockStatus].label}</Badge>
        {r.nearestExpiry && <Badge tone="warning">pér. {dateShort(r.nearestExpiry)}</Badge>}
      </span>) },
  ];

  const pages = o ? Math.max(1, Math.ceil(o.total / PAGE)) : 1;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Lignes de stock" value={String(o?.total ?? "…")} />
        <KpiCard label="Ruptures" value={String(o?.outCount ?? "…")} tone={o?.outCount ? "warning" : undefined} tag={o?.outCount ? "!" : undefined} />
        <KpiCard label="Stock faible" value={String(o?.lowCount ?? "…")} tone={o?.lowCount ? "warning" : undefined} />
        <KpiCard label="Surstock" value={String(o?.overCount ?? "…")} />
        {valued && <KpiCard label="Valeur du stock" value={<Money value={o?.totalValue ?? 0} />} />}
      </div>
      <Card>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 12, alignItems: "end" }}>
          <Field label="Recherche"><TextInput value={term} placeholder="SKU ou désignation" onChange={(e) => setTerm(e.target.value)} /></Field>
          <LocationSelect value={locationId} onChange={setLocationId} locations={locations} allowAll />
          {cats.length > 0 && (
            <Field label="Catégorie">
              <Select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
                <option value="">Toutes</option>
                {cats.map((c) => <option key={c.id} value={c.id}>{c.path}</option>)}
              </Select>
            </Field>
          )}
          <Field label="État">
            <Select value={status} onChange={(e) => setStatus(e.target.value as StockStatus | "expiring" | "")}>
              <option value="">Tous</option>
              <option value="out">Rupture</option>
              <option value="low">Stock faible</option>
              <option value="normal">Normal</option>
              <option value="over">Surstock</option>
              <option value="expiring">Péremption ≤ 60 j</option>
            </Select>
          </Field>
        </div>
        {overview.error && <div role="alert" className="nx-error-text" style={{ marginTop: 10 }}>{overview.error}</div>}
        {!valued && o && <p style={{ fontSize: 13, color: "var(--nx-text-2)" }}>Valeurs masquées : permission « inventory.view_valuation » requise.</p>}
        <div style={{ marginTop: 12 }}>
          <DataTable exportTitle="Tableau de stock" columns={columns} rows={o?.rows ?? []}
                     rowKey={(r) => `${r.productId}:${r.locationId}`}
                     empty={overview.loading ? "Chargement…" : "Aucune ligne de stock pour ces critères."} />
        </div>
        {o && o.total > PAGE && (
          <div style={{ display: "flex", gap: 8, alignItems: "center", justifyContent: "flex-end", marginTop: 10 }}>
            <Button size="sm" variant="ghost" disabled={page === 0 || overview.loading} onClick={() => setPage(page - 1)}>Précédent</Button>
            <span style={{ fontSize: 13 }}>Page {page + 1} / {pages}</span>
            <Button size="sm" variant="ghost" disabled={page + 1 >= pages || overview.loading} onClick={() => setPage(page + 1)}>Suivant</Button>
          </div>
        )}
      </Card>
      {detail && <MovementsDrawer row={detail} onClose={() => setDetail(null)} />}
    </div>
  );
}

function MovementsDrawer({ row, onClose }: { row: StockOverviewRow; onClose: () => void }) {
  const moves = useAsync(() => getMovements({ productId: row.productId, locationId: row.locationId, limit: 100 }), [row.productId, row.locationId]);
  return (
    <Drawer title={`${row.productName} — ${row.locationName}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <Badge tone={STOCK_STATUS[row.stockStatus].tone}>{STOCK_STATUS[row.stockStatus].label}</Badge>
          <Badge>En stock {qty(row.onHand, row.unitCode)}</Badge>
          <Badge>Disponible {qty(row.available, row.unitCode)}</Badge>
        </div>
        {moves.error && <div role="alert" className="nx-error-text">{moves.error}</div>}
        <DataTable exportTitle={`Mouvements ${row.sku}`}
          columns={[
            { key: "d", header: "Date", render: (m) => dateTime(m.occurredAt) },
            { key: "t", header: "Type", render: (m) => MOVEMENT_LABEL[m.movementType] ?? m.movementType },
            { key: "q", header: "Qté", numeric: true, render: (m) => (
              <span style={{ color: m.quantity < 0 ? "var(--nx-neg)" : "var(--nx-pos)" }}>{m.quantity > 0 ? "+" : ""}{qty(m.quantity)}</span>) },
            { key: "lot", header: "Lot", render: (m) => m.lotNumber ?? "—" },
            { key: "r", header: "Origine", render: (m) => m.referenceType ?? "—" },
          ]}
          rows={moves.data ?? []} rowKey={(m) => m.id} empty={moves.loading ? "Chargement…" : "Aucun mouvement."} />
      </div>
    </Drawer>
  );
}
