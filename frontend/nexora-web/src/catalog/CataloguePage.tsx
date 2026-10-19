import { useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import {
  Badge, Button, Card, DataTable, EmptyState, Field, KpiCard, Money, Select, StatePill, Tabs, TextInput, type Column,
} from "@/shared/ui";
import { listProducts, searchCatalog, type ProductListItem } from "@/catalog/catalogApi";
import { PRODUCT_TYPE_LABEL, sortedCategories, STATUS_LABEL, useCatalogRefs } from "@/catalog/catalogShared";
import { CreateProductModal, ProductDrawer } from "@/catalog/ProductForms";
import { CategoriesPanel, PricingPanel, UnitsPanel } from "@/catalog/CatalogReferences";
import { PromotionsPanel } from "@/catalog/PromotionsPanel";
import { PriceSimulator } from "@/catalog/PriceSimulator";

/**
 * Catalogue (Phase 23 + finalisation produit, phase 1) — produits, catégories, marques, unités et
 * conversions, listes de prix, groupes de clients, promotions, coupons, simulateur. Chaque écriture
 * passe par l'API (validations, permissions et audit côté serveur) ; l'écran n'invente aucun calcul.
 */
export function CataloguePage() {
  const { hasPermission, activeCompanyId } = useAuth();
  const [params, setParams] = useSearchParams();
  const catalog = useCatalogRefs(activeCompanyId);
  const tabs = [
    { id: "products", label: "Produits" },
    { id: "categories", label: "Catégories & marques" },
    { id: "units", label: "Unités & conversions" },
    { id: "pricing", label: "Listes de prix & groupes" },
    { id: "promotions", label: "Promotions & coupons" },
    { id: "simulator", label: "Simulateur de prix" },
  ];
  const [tab, setTab] = useState(params.get("tab") ?? "products");

  if (!hasPermission("catalog.view")) {
    return <Card><EmptyState title="Catalogue" hint="Le catalogue n'est pas accessible avec vos permissions." /></Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Catalogue</h1>
      {catalog.error && (
        <div role="alert" className="nx-error-text">
          Référentiels indisponibles : {catalog.error} <Button size="sm" variant="ghost" onClick={catalog.reload}>Réessayer</Button>
        </div>
      )}
      <Tabs tabs={tabs} active={tab} onChange={(id) => { setTab(id); setParams(id === "products" ? {} : { tab: id }, { replace: true }); }} />
      {tab === "products" && <ProductsPanel refs={catalog.refs} refsLoading={catalog.loading} onRefsChanged={catalog.reload} initialQuery={params.get("q") ?? ""} />}
      {tab === "categories" && <CategoriesPanel refs={catalog.refs} onChanged={catalog.reload} />}
      {tab === "units" && <UnitsPanel refs={catalog.refs} onChanged={catalog.reload} />}
      {tab === "pricing" && <PricingPanel refs={catalog.refs} onChanged={catalog.reload} />}
      {tab === "promotions" && <PromotionsPanel refs={catalog.refs} />}
      {tab === "simulator" && <SimulatorTab refs={catalog.refs} />}
    </div>
  );
}

const PAGE = 500;

function ProductsPanel({ refs, refsLoading, onRefsChanged, initialQuery }: {
  refs: ReturnType<typeof useCatalogRefs>["refs"]; refsLoading: boolean; onRefsChanged: () => void; initialQuery: string;
}) {
  const { hasPermission } = useAuth();
  const [term, setTerm] = useState(initialQuery);
  const [debounced, setDebounced] = useState(initialQuery);
  const [status, setStatus] = useState("");
  const [categoryId, setCategoryId] = useState("");
  const [creating, setCreating] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);
  const cats = useMemo(() => sortedCategories(refs.categories), [refs.categories]);

  useEffect(() => {
    const t = setTimeout(() => setDebounced(term.trim()), 250);
    return () => clearTimeout(t);
  }, [term]);

  // Recherche tolérante (accents, code-barres, PLU, alias) sans filtre ; liste filtrée sinon.
  const searching = debounced.length >= 2 && !status && !categoryId;
  const products = useAsync<ProductListItem[]>(() => (
    searching
      ? searchCatalog(debounced, 100).then((hits) => hits.map((h) => ({
          id: h.productId, sku: h.sku, name: h.name, shortName: h.shortName, productType: h.productType,
          status: h.status, categoryName: h.categoryName, brandName: h.brandName,
          primaryBarcode: h.barcode, salePrice: h.salePrice, currency: h.currency,
        })))
      : listProducts({ q: debounced || undefined, status: status || undefined, categoryId: categoryId || undefined, limit: PAGE })
  ), [debounced, status, categoryId]);

  // Pages suivantes (audit 2026-10 éd. 2) : la liste s'arrêtait aux 500 premiers produits, sans moyen de voir la suite.
  const [more, setMore] = useState<ProductListItem[]>([]);
  const [moreDone, setMoreDone] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  useEffect(() => { setMore([]); setMoreDone(false); }, [products.data]);
  const rows = useMemo(() => [...(products.data ?? []), ...more], [products.data, more]);
  const canLoadMore = !searching && !moreDone && (products.data?.length ?? 0) === PAGE && rows.length % PAGE === 0;
  const loadMore = async () => {
    setLoadingMore(true);
    try {
      const next = await listProducts({ q: debounced || undefined, status: status || undefined, categoryId: categoryId || undefined,
                                        limit: PAGE, offset: rows.length });
      setMore((m) => [...m, ...next]);
      if (next.length < PAGE) setMoreDone(true);
    } finally {
      setLoadingMore(false);
    }
  };
  const noPrice = rows.filter((r) => r.salePrice == null && r.productType !== "service").length;

  const columns: Array<Column<ProductListItem>> = [
    { key: "sku", header: "SKU", render: (r) => <code>{r.sku}</code> },
    {
      key: "name", header: "Désignation", render: (r) => (
        <button type="button" onClick={() => setOpenId(r.id)} className="nx-linklike"
                style={{ appearance: "none", background: "transparent", border: "none", cursor: "pointer", font: "inherit", color: "var(--nx-brand)", padding: 0, textAlign: "left" }}>
          {r.name}{r.shortName && <span style={{ color: "var(--nx-text-2)" }}> · {r.shortName}</span>}
        </button>
      ),
    },
    { key: "cat", header: "Catégorie", render: (r) => r.categoryName ?? "—" },
    { key: "type", header: "Type", render: (r) => <Badge>{PRODUCT_TYPE_LABEL[r.productType] ?? r.productType}</Badge> },
    { key: "bc", header: "Code-barres", render: (r) => (r.primaryBarcode ? <code>{r.primaryBarcode}</code> : "—") },
    {
      key: "price", header: "Prix de vente", numeric: true,
      render: (r) => (r.salePrice == null ? <Badge tone="warning">sans prix</Badge> : <Money value={r.salePrice} currency={r.currency ?? refs.defaultCurrency} />),
    },
    { key: "status", header: "Statut", render: (r) => <StatePill status={r.status} /> },
  ];

  return (
    <>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        <KpiCard label="Produits affichés" value={String(rows.length)} />
        <KpiCard label="Actifs" value={String(rows.filter((r) => r.status === "active").length)} />
        <KpiCard label="Sans prix de vente" value={String(noPrice)} tone={noPrice ? "warning" : undefined} tag={noPrice ? "!" : undefined} />
        <KpiCard label="Catégories" value={String(refs.categories.length)} />
      </div>
      <Card>
        <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "flex-end" }}>
          <div style={{ flex: "2 1 260px" }}>
            <Field label="Recherche (SKU, désignation, code-barres, PLU, alias)">
              <TextInput value={term} placeholder="paracet, 3401011, PARA500…" onChange={(e) => setTerm(e.currentTarget.value)} />
            </Field>
          </div>
          <div style={{ flex: "1 1 160px" }}>
            <Field label="Statut">
              <Select value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="">Tous</option>
                {Object.entries(STATUS_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </Select>
            </Field>
          </div>
          <div style={{ flex: "1 1 200px" }}>
            <Field label="Catégorie">
              <Select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
                <option value="">Toutes</option>
                {cats.map((c) => <option key={c.id} value={c.id}>{c.path}</option>)}
              </Select>
            </Field>
          </div>
          {hasPermission("catalog.create") && (
            <Button onClick={() => setCreating(true)} disabled={refsLoading}>Nouveau produit</Button>
          )}
        </div>
        {products.error && <div role="alert" className="nx-error-text" style={{ marginTop: 10 }}>{products.error}</div>}
        <div style={{ marginTop: 12 }}>
          <DataTable
            exportTitle="Catalogue produits"
            columns={columns}
            rows={rows}
            rowKey={(r) => r.id}
            empty={products.loading ? "Chargement…" : debounced || status || categoryId
              ? "Aucun produit ne correspond à ces critères."
              : "Aucun produit. Créez le premier avec « Nouveau produit » ou importez un fichier (Migration & onboarding)."}
          />
          {canLoadMore && (
            <div style={{ marginTop: 10, display: "flex", justifyContent: "center" }}>
              <Button variant="ghost" onClick={() => void loadMore()} loading={loadingMore}>
                {loadingMore ? "Chargement…" : `Afficher les ${PAGE} produits suivants`}
              </Button>
            </div>
          )}
        </div>
      </Card>
      {creating && (
        <CreateProductModal refs={refs} onClose={() => setCreating(false)}
                            onCreated={(id) => { setCreating(false); products.reload(); onRefsChanged(); setOpenId(id); }} />
      )}
      {openId && (
        <ProductDrawer key={openId} productId={openId} refs={refs} onClose={() => setOpenId(null)} onChanged={products.reload} />
      )}
    </>
  );
}

function SimulatorTab({ refs }: { refs: ReturnType<typeof useCatalogRefs>["refs"] }) {
  const products = useAsync(() => listProducts({ status: "active", limit: 500 }), []);
  if (products.error) return <div role="alert" className="nx-error-text">{products.error}</div>;
  return <PriceSimulator products={products.data ?? []} priceLists={refs.priceLists} currencies={refs.currencies} defaultCurrency={refs.defaultCurrency} />;
}
