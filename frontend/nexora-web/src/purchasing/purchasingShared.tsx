import { useEffect, useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { number, todayIso } from "@/shared/format";
import { Badge, Field, TextInput } from "@/shared/ui";
import { getProduct, listCurrencies, listTaxes, searchCatalog, type ProductDetail, type Tax } from "@/catalog/catalogApi";
import { listPaymentAccounts, listSuppliers } from "@/purchasing/purchasingApi";

export const ORDER_STATUS: Record<string, string> = {
  draft: "Brouillon", submitted: "Soumise", approved: "Approuvée", sent: "Envoyée",
  partially_received: "Partiellement reçue", received: "Reçue", invoiced: "Facturée", closed: "Soldée", cancelled: "Annulée",
};
export const ORDER_TONE: Record<string, "success" | "warning" | "error" | "info" | undefined> = {
  draft: undefined, submitted: "info", approved: "info", sent: "info", partially_received: "warning",
  received: "success", invoiced: "success", closed: "success", cancelled: "error",
};
export const INVOICE_STATUS: Record<string, string> = {
  draft: "Brouillon", validated: "Validée", posted: "Comptabilisée", partially_paid: "Partiellement payée", paid: "Payée", cancelled: "Annulée",
};
export const MATCH_STATUS: Record<string, string> = { pending: "À rapprocher", matched: "Rapprochée", variance: "Écart", not_applicable: "Sans commande" };

/** Commande encore modifiable (avant approbation). */
export const orderEditable = (status: string) => status === "draft" || status === "submitted";
/** Commande pouvant être réceptionnée. */
export const orderReceivable = (status: string) => ["approved", "sent", "partially_received"].includes(status);

export const round2 = (v: number) => Math.round((v + Number.EPSILON) * 100) / 100;

/** Estimation d'une ligne pendant la saisie — même règle que le serveur (net = qté × prix − remise ; taxe = net × taux, arrondie au centime). */
export function estimateLine(quantity: number, unitPrice: number, discount: number, ratePercent: number) {
  const net = round2(quantity * unitPrice - discount);
  const tax = round2((net * ratePercent) / 100);
  return { net, tax, total: round2(net + tax) };
}

export function estimateTotals(lines: Array<{ quantity: number; unitPrice: number; discount: number; ratePercent: number }>) {
  return lines.reduce((acc, l) => {
    const e = estimateLine(l.quantity, l.unitPrice, l.discount, l.ratePercent);
    return { subtotal: round2(acc.subtotal + e.net), tax: round2(acc.tax + e.tax), total: round2(acc.total + e.total) };
  }, { subtotal: 0, tax: 0, total: 0 });
}

export const today = () => todayIso();
export const qtyText = (v: number | null | undefined, unit?: string | null) =>
  v == null ? "—" : `${number(v, 3)}${unit ? ` ${unit}` : ""}`;

/** Unités d'achat possibles d'un article : l'unité de base, plus toute unité convertible VERS l'unité de base (carton → bouteille). */
export interface PurchaseUnit { id: string; code: string; factor: number }
export function purchaseUnits(p: ProductDetail): PurchaseUnit[] {
  const base: PurchaseUnit[] = p.unitId ? [{ id: p.unitId, code: p.unitCode ?? "unité", factor: 1 }] : [];
  const extra = (p.conversions ?? [])
    .filter((c) => c.toUnitId === p.unitId && c.fromUnitId !== p.unitId)
    .map((c) => ({ id: c.fromUnitId, code: c.fromUnitCode, factor: c.factor }));
  return [...base, ...extra.filter((u, i) => extra.findIndex((x) => x.id === u.id) === i)];
}

/** Référentiels partagés par les formulaires d'achat. */
export function usePurchasingRefs() {
  const { hasPermission, activeCompanyId } = useAuth();
  const state = useAsync(async () => {
    const [suppliers, taxes, currencies, accounts] = await Promise.all([
      listSuppliers(),
      listTaxes().catch(() => [] as Tax[]),
      listCurrencies().catch(() => []),
      hasPermission("purchasing.invoice") || hasPermission("purchasing.pay") ? listPaymentAccounts().catch(() => []) : Promise.resolve([]),
    ]);
    return { suppliers, taxes: taxes.filter((t) => t.status === "active"), currencies, accounts };
  }, [activeCompanyId]);
  const currencies = state.data?.currencies ?? [];
  return {
    suppliers: state.data?.suppliers ?? [],
    activeSuppliers: (state.data?.suppliers ?? []).filter((s) => s.status === "active"),
    taxes: state.data?.taxes ?? [],
    currencies,
    defaultCurrency: currencies.find((c) => c.isCompanyDefault)?.code ?? currencies[0]?.code ?? "HTG",
    accounts: state.data?.accounts ?? [],
    loading: state.loading, error: state.error, reload: state.reload,
  };
}
export type PurchasingRefs = ReturnType<typeof usePurchasingRefs>;

export const taxRate = (taxes: Tax[], id: string | null | undefined) => taxes.find((t) => t.id === id)?.rate ?? 0;

/**
 * Recherche d'un article du catalogue à acheter (pas limité au stock existant). Le détail de l'article
 * est chargé au choix : unités d'achat, taxe par défaut, prix d'achat du fournisseur.
 */
export function PurchaseProductPicker({ onPick, excluded = [], disabled }: {
  onPick: (p: ProductDetail) => void; excluded?: string[]; disabled?: boolean;
}) {
  const [term, setTerm] = useState("");
  const [debounced, setDebounced] = useState("");
  const [loadingId, setLoadingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { const t = setTimeout(() => setDebounced(term.trim()), 250); return () => clearTimeout(t); }, [term]);
  const results = useAsync(async () => (debounced.length < 2 ? [] : searchCatalog(debounced, 20)), [debounced]);
  const list = (results.data ?? []).filter((h) => h.productType !== "service" && h.status === "active" && !excluded.includes(h.productId));

  async function pick(id: string) {
    setLoadingId(id);
    setError(null);
    try { onPick(await getProduct(id)); setTerm(""); }
    catch { setError("Article indisponible."); }
    finally { setLoadingId(null); }
  }

  return (
    <div style={{ display: "grid", gap: 6 }}>
      <Field label="Ajouter un article" hint="Nom, SKU ou code-barres (2 caractères min.)" error={error}>
        <TextInput value={term} disabled={disabled} placeholder="Rechercher…" onChange={(e) => setTerm(e.target.value)} />
      </Field>
      {debounced.length >= 2 && (
        <div role="listbox" aria-label="Articles trouvés" className="nx-card" style={{ padding: 6, maxHeight: 220, overflowY: "auto", display: "grid", gap: 2 }}>
          {results.loading && <span style={{ color: "var(--nx-text-2)", padding: 6 }}>Recherche…</span>}
          {!results.loading && list.length === 0 && <span style={{ color: "var(--nx-text-2)", padding: 6 }}>Aucun article.</span>}
          {list.map((h) => (
            <button key={h.productId} type="button" role="option" aria-selected={false} disabled={loadingId !== null}
                    onClick={() => pick(h.productId)}
                    style={{ display: "flex", justifyContent: "space-between", gap: 8, padding: "6px 8px", border: "none",
                             background: "transparent", cursor: "pointer", color: "inherit", font: "inherit", textAlign: "left", borderRadius: 6 }}>
              <span><code>{h.sku}</code> {h.name}</span>
              {loadingId === h.productId ? <span style={{ color: "var(--nx-text-2)" }}>…</span> : h.categoryName && <Badge>{h.categoryName}</Badge>}
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Prix d'achat connu pour ce fournisseur (fiche article), sinon celui du fournisseur préféré. */
export function knownPurchasePrice(p: ProductDetail, supplierName?: string | null) {
  const s = p.suppliers.find((x) => supplierName != null && x.supplierName === supplierName)
    ?? p.suppliers.find((x) => x.isPreferred) ?? p.suppliers[0];
  return s?.purchasePrice ?? null;
}
