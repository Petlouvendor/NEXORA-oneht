import { useSearchParams } from "react-router-dom";
import { useCallback, useEffect, useMemo, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, Drawer, Field, Money, SectionLabel, Select, StatePill, TextInput,
  type Column,
} from "@/shared/ui";
import {
  getProduct, listPriceLists, listProducts, quotePrice, searchCatalog, setProductStatus, updateProduct,
  type PriceListItem, type PriceQuote, type ProductDetail, type ProductListItem,
} from "@/catalog/catalogApi";

const STATUS_NEXT: Record<string, string[]> = {
  draft: ["review", "active"],
  review: ["active", "draft"],
  active: ["suspended", "inactive", "archived"],
  suspended: ["active", "archived"],
  inactive: ["active", "archived"],
  archived: [],
};

const STEP_LABEL: Record<string, string> = {
  base_price: "Prix de base",
  price_list: "Liste tarifaire",
  customer_price: "Tarif client",
  quantity_tier: "Palier quantité",
  tax_extraction: "Extraction TTC",
  promotion: "Promotion",
  coupon: "Coupon",
  manual_discount: "Remise manuelle",
  tax: "Taxe",
  final: "Prix final",
};

/** Catalogue — recherche, fiche produit & simulateur de prix explicable (Phase 23 §22, §37-40, §45). */
export function CataloguePage() {
  const { hasPermission, activeCompanyId } = useAuth();
  // ?q= : ouverture depuis la recherche Ctrl+K (NEXORA ONE §5).
  const [params] = useSearchParams();
  const [term, setTerm] = useState(params.get("q") ?? "");
  const [rows, setRows] = useState<ProductListItem[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [selected, setSelected] = useState<ProductDetail | null>(null);
  const [priceLists, setPriceLists] = useState<PriceListItem[]>([]);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    const fetch = term.trim().length >= 2
      ? searchCatalog(term.trim(), 50).then((hits) => hits.map<ProductListItem>((h) => ({
          id: h.productId, sku: h.sku, name: h.name, shortName: h.shortName, productType: h.productType,
          status: h.status, categoryName: h.categoryName, brandName: h.brandName,
          primaryBarcode: h.barcode, salePrice: h.salePrice, currency: h.currency,
        })))
      : listProducts({ limit: 100 });
    fetch.then(setRows)
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"))
      .finally(() => setLoading(false));
  }, [term]);

  useEffect(() => {
    const t = setTimeout(load, 250);
    return () => clearTimeout(t);
  }, [load]);

  useEffect(() => {
    listPriceLists().then(setPriceLists).catch(() => setPriceLists([]));
  }, [activeCompanyId]);

  const openProduct = (id: string) => {
    getProduct(id).then(setSelected).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  };

  async function changeStatus(id: string, status: string) {
    setBusy(true);
    setError(null);
    try {
      await setProductStatus(id, status);
      openProduct(id);
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Transition refusée");
    } finally {
      setBusy(false);
    }
  }

  const columns: Array<Column<ProductListItem>> = [
    { key: "sku", header: "SKU", render: (r) => <code>{r.sku}</code> },
    {
      key: "name", header: "Désignation",
      render: (r) => (
        <button
          onClick={() => openProduct(r.id)}
          style={{
            appearance: "none", background: "transparent", border: "none", cursor: "pointer",
            font: "inherit", color: "var(--nx-brand)", padding: 0, textAlign: "left",
          }}
        >
          {r.name}
          {r.shortName && <span style={{ color: "var(--nx-text-2)" }}> · {r.shortName}</span>}
        </button>
      ),
    },
    { key: "cat", header: "Catégorie", render: (r) => r.categoryName ?? "—" },
    { key: "type", header: "Type", render: (r) => <Badge>{r.productType}</Badge> },
    {
      key: "price", header: "Prix", numeric: true,
      render: (r) => (r.salePrice == null ? "—" : <Money value={r.salePrice} currency={r.currency ?? "HTG"} />),
    },
    { key: "status", header: "Statut", render: (r) => <StatePill status={r.status} /> },
  ];

  if (!hasPermission("catalog.view")) {
    return <Card>Le catalogue n'est pas accessible avec vos permissions.</Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Catalogue</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <Card>
        <Field label="Recherche (SKU, désignation, code-barres, PLU, alias)">
          <TextInput
            value={term}
            placeholder="paracet, 3401011, PARA500, 2041…"
            onChange={(e) => setTerm(e.currentTarget.value)}
          />
        </Field>
        <div style={{ marginTop: 12 }}>
          <DataTable
            columns={columns}
            rows={rows}
            rowKey={(r) => r.id}
            empty={loading ? "Chargement…" : "Aucun produit."}
          />
        </div>
      </Card>

      <PriceSimulator products={rows} priceLists={priceLists} />

      {selected && (
        <Drawer title={selected.name} onClose={() => setSelected(null)}>
          <div style={{ display: "grid", gap: 16 }}>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
              <code>{selected.sku}</code>
              <StatePill status={selected.status} />
              <Badge>{selected.productType}</Badge>
              {selected.pluCode && <Badge tone="info">PLU {selected.pluCode}</Badge>}
            </div>

            {hasPermission("catalog.update") && (
              <ProductEditor key={selected.id} product={selected}
                onSaved={() => { openProduct(selected.id); load(); }} />
            )}

            {hasPermission("catalog.update") && (STATUS_NEXT[selected.status]?.length ?? 0) > 0 && (
              <div>
                <SectionLabel>Workflow (§45)</SectionLabel>
                <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
                  {STATUS_NEXT[selected.status].map((s) => (
                    <Button key={s} size="sm" variant="subtle" disabled={busy}
                      onClick={() => changeStatus(selected.id, s)}>
                      → {s}
                    </Button>
                  ))}
                </div>
              </div>
            )}

            <div>
              <SectionLabel>Prix ({selected.prices.length})</SectionLabel>
              <DataTable
                columns={[
                  { key: "t", header: "Type", render: (p) => p.priceListCode ?? p.priceType },
                  { key: "min", header: "Qté ≥", numeric: true, render: (p) => p.quantityMin },
                  { key: "a", header: "Montant", numeric: true, render: (p) => <Money value={p.amount} currency={p.currency} /> },
                  { key: "tax", header: "Base", render: (p) => (p.taxIncluded ? "TTC" : "HT") },
                  { key: "v", header: "Depuis", render: (p) => new Date(p.validFrom).toLocaleDateString() },
                  { key: "c", header: "", render: (p) => (p.validTo ? <Badge tone="warning">clos</Badge> : <Badge tone="success">actif</Badge>) },
                ]}
                rows={selected.prices}
                rowKey={(p) => p.id}
                empty="Aucun prix."
              />
            </div>

            <div>
              <SectionLabel>Codes-barres ({selected.barcodes.length})</SectionLabel>
              <DataTable
                columns={[
                  { key: "b", header: "Code", render: (b) => <code>{b.barcode}</code> },
                  { key: "t", header: "Type", render: (b) => b.barcodeType },
                  { key: "p", header: "", render: (b) => (b.isPrimary ? <Badge tone="info">principal</Badge> : null) },
                  { key: "s", header: "Statut", render: (b) => <StatePill status={b.status} /> },
                ]}
                rows={selected.barcodes}
                rowKey={(b) => b.id}
                empty="Aucun code-barres."
              />
            </div>

            {/* Guide §7.2 : fournisseurs et prix d'achat seulement avec catalog.view_cost (masqués aussi par l'API). */}
            {hasPermission("catalog.view_cost") && <div>
              <SectionLabel>Fournisseurs ({selected.suppliers.length})</SectionLabel>
              <DataTable
                columns={[
                  { key: "n", header: "Fournisseur", render: (sp) => sp.supplierName },
                  { key: "r", header: "Réf.", render: (sp) => sp.supplierSku ?? "—" },
                  { key: "p", header: "Coût", numeric: true, render: (sp) => <Money value={sp.purchasePrice} currency={sp.currency} /> },
                  { key: "d", header: "Délai (j)", numeric: true, render: (sp) => sp.leadTimeDays },
                  { key: "f", header: "", render: (sp) => (sp.isPreferred ? <Badge tone="success">principal</Badge> : null) },
                ]}
                rows={selected.suppliers}
                rowKey={(sp) => sp.id}
                empty="Aucun fournisseur lié."
              />
            </div>}
          </div>
        </Drawer>
      )}
    </div>
  );
}

function PriceSimulator({ products, priceLists }: { products: ProductListItem[]; priceLists: PriceListItem[] }) {
  const [productId, setProductId] = useState("");
  const [quantity, setQuantity] = useState("1");
  const [currency, setCurrency] = useState("HTG");
  const [priceListId, setPriceListId] = useState("");
  const [manualDiscount, setManualDiscount] = useState("0");
  const [coupon, setCoupon] = useState("");
  const [quote, setQuote] = useState<PriceQuote | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const pid = productId || products[0]?.id || "";
  const canRun = useMemo(() => pid && Number(quantity) > 0, [pid, quantity]);

  async function run() {
    if (!canRun) return;
    setBusy(true);
    setError(null);
    try {
      const q = await quotePrice({
        productId: pid,
        quantity: Number(quantity),
        currency,
        priceListId: priceListId || null,
        manualDiscount: Number(manualDiscount) || 0,
        couponCode: coupon.trim() || null,
      });
      setQuote(q);
    } catch (e) {
      setQuote(null);
      setError(e instanceof ApiError ? e.message : "Erreur");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <SectionLabel>Simulateur de prix — résultat explicable (§22)</SectionLabel>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <Field label="Produit">
          <Select value={pid} onChange={(e) => setProductId(e.currentTarget.value)}>
            {products.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </Select>
        </Field>
        <Field label="Quantité">
          <TextInput type="number" min={0} step="any" value={quantity} onChange={(e) => setQuantity(e.currentTarget.value)} />
        </Field>
        <Field label="Devise">
          <Select value={currency} onChange={(e) => setCurrency(e.currentTarget.value)}>
            <option>HTG</option><option>USD</option><option>EUR</option>
          </Select>
        </Field>
        <Field label="Liste tarifaire">
          <Select value={priceListId} onChange={(e) => setPriceListId(e.currentTarget.value)}>
            <option value="">(prix par défaut)</option>
            {priceLists.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </Select>
        </Field>
        <Field label="Remise manuelle">
          <TextInput type="number" min={0} step="any" value={manualDiscount} onChange={(e) => setManualDiscount(e.currentTarget.value)} />
        </Field>
        <Field label="Coupon">
          <TextInput value={coupon} onChange={(e) => setCoupon(e.currentTarget.value)} placeholder="code" />
        </Field>
      </div>
      <div style={{ marginTop: 12 }}>
        <Button onClick={run} loading={busy} disabled={!canRun}>Calculer le prix</Button>
      </div>
      {error && <div className="nx-error-text" style={{ marginTop: 12 }}>{error}</div>}

      {quote && (
        <div style={{ marginTop: 16, display: "grid", gap: 12 }}>
          <div style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
            <Metric label="Net ligne (HT)" value={<Money value={quote.lineNet} currency={quote.currency} />} />
            <Metric label="Remises" value={<Money value={-quote.lineDiscount} currency={quote.currency} sign />} />
            <Metric label={`Taxe ${quote.taxCode ?? ""}`} value={<Money value={quote.lineTax} currency={quote.currency} />} />
            <Metric label="Total ligne (TTC)" value={<strong><Money value={quote.lineGross} currency={quote.currency} /></strong>} />
          </div>
          <DataTable
            columns={[
              { key: "r", header: "Règle", render: (st) => STEP_LABEL[st.rule] ?? st.rule },
              { key: "l", header: "Détail", render: (st) => st.label },
              { key: "d", header: "Δ / unité", numeric: true, render: (st) => (st.delta === 0 ? "—" : <Money value={st.delta} currency={quote.currency} sign />) },
              { key: "u", header: "Prix unité", numeric: true, render: (st) => <Money value={st.runningUnit} currency={quote.currency} /> },
            ]}
            rows={quote.steps.map((st, i) => ({ ...st, _k: i }))}
            rowKey={(st) => String(st._k)}
            empty="—"
          />
        </div>
      )}
    </Card>
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

/** Modification de la fiche (recette Phase 55 PROD-004) — le SKU reste immuable. */
function ProductEditor({ product, onSaved }: { product: ProductDetail; onSaved: () => void }) {
  const [open, setOpen] = useState(false);
  const [name, setName] = useState(product.name);
  const [shortName, setShortName] = useState(product.shortName ?? "");
  const [pluCode, setPluCode] = useState(product.pluCode ?? "");
  const [description, setDescription] = useState(product.description ?? "");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (!open) return <div><Button size="sm" variant="subtle" onClick={() => setOpen(true)}>Modifier la fiche</Button></div>;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await updateProduct(product.id, {
        name, shortName: shortName || null, pluCode: pluCode || null, description: description || null,
      });
      setOpen(false);
      onSaved();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Enregistrement impossible");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ display: "grid", gap: 10 }}>
      <SectionLabel>Modifier la fiche</SectionLabel>
      <Field label="Nom" required><TextInput value={name} maxLength={200} onChange={(e) => setName(e.target.value)} /></Field>
      <Field label="Nom court (ticket, écran POS)"><TextInput value={shortName} onChange={(e) => setShortName(e.target.value)} /></Field>
      <Field label="Code PLU"><TextInput value={pluCode} onChange={(e) => setPluCode(e.target.value)} /></Field>
      <Field label="Description"><TextInput value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <div style={{ display: "flex", gap: 8 }}>
        <Button size="sm" onClick={save} loading={busy} disabled={!name.trim()}>Enregistrer</Button>
        <Button size="sm" variant="ghost" onClick={() => setOpen(false)}>Annuler</Button>
      </div>
    </div>
  );
}
