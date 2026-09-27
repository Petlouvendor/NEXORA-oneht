import { useMemo, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Button, Card, DataTable, Field, Money, SectionLabel, Select, TextInput } from "@/shared/ui";
import { quotePrice, type PriceListItem, type PriceQuote, type ProductListItem } from "@/catalog/catalogApi";
import type { Currency } from "@/catalog/catalogApi";

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

/** Simulateur de prix explicable (Phase 23 §22) — même calcul que la caisse (promotions : PromotionCalculator). */
export function PriceSimulator({ products, priceLists, currencies, defaultCurrency }: {
  products: ProductListItem[]; priceLists: PriceListItem[]; currencies: Currency[]; defaultCurrency: string;
}) {
  const [productId, setProductId] = useState("");
  const [quantity, setQuantity] = useState("1");
  const [currency, setCurrency] = useState(defaultCurrency);
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
            {(currencies.length ? currencies.map((c) => c.code) : [defaultCurrency]).map((c) => <option key={c}>{c}</option>)}
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
