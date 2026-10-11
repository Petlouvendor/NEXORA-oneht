import { useEffect, useRef, useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { num } from "@/shared/forms";
import { money } from "@/shared/format";
import { estimateTotals } from "@/shared/amounts";
import { Icon } from "@/shared/icons";
import { Badge, Field, IconButton, Select, TextInput } from "@/shared/ui";
import { listCurrencies, listTaxes, searchCatalog, type CatalogHit, type Tax } from "@/catalog/catalogApi";
import { lookupCustomers, type CustomerLookup } from "@/customers/customersApi";
import { useStockPlaces } from "@/inventory/stockShared";

export const ORDER_STATUS: Record<string, string> = {
  draft: "Brouillon", confirmed: "Confirmée", reserved: "Stock réservé", partially_fulfilled: "Partiellement livrée",
  fulfilled: "Livrée", cancelled: "Annulée",
};
export const ORDER_TONE: Record<string, "success" | "warning" | "error" | "info" | undefined> = {
  draft: undefined, confirmed: "info", reserved: "info", partially_fulfilled: "warning", fulfilled: "success", cancelled: "error",
};
export const ORDER_TYPE: Record<string, string> = {
  pickup: "Retrait en magasin", delivery: "Livraison", wholesale: "Vente en gros", reservation: "Réservation",
};
export const QUOTE_STATUS: Record<string, string> = {
  draft: "Brouillon", sent: "Envoyé", accepted: "Accepté", rejected: "Refusé", expired: "Expiré",
  converted: "Converti en commande", cancelled: "Annulé",
};
export const QUOTE_TONE: Record<string, "success" | "warning" | "error" | "info" | undefined> = {
  draft: undefined, sent: "info", accepted: "success", rejected: "error", expired: "warning", converted: "success", cancelled: "error",
};

/** Livrable (vente créée) depuis ces états. */
export const orderDeliverable = (status: string) => ["confirmed", "reserved", "partially_fulfilled"].includes(status);
export const orderOpen = (status: string) => !["fulfilled", "cancelled"].includes(status);

/** Référentiels des formulaires de vente. */
export function useSalesRefs() {
  const { activeCompanyId } = useAuth();
  const places = useStockPlaces(activeCompanyId);
  const refs = useAsync(async () => {
    const [currencies, taxes] = await Promise.all([listCurrencies().catch(() => []), listTaxes().catch(() => [] as Tax[])]);
    return { currencies, taxes: taxes.filter((t) => t.status === "active") };
  }, [activeCompanyId]);
  const currencies = refs.data?.currencies ?? [];
  return {
    currencies,
    defaultCurrency: currencies.find((c) => c.isCompanyDefault)?.code ?? currencies[0]?.code ?? "HTG",
    taxes: refs.data?.taxes ?? [],
    locations: places.active,
  };
}
export type SalesRefs = ReturnType<typeof useSalesRefs>;

/** Ligne saisie : prix vide = prix catalogue du jour (fixé par le serveur). */
export interface SalesLine {
  productId: string; sku: string; name: string; catalogPrice: number | null;
  quantity: string; unitPrice: string; discount: string; taxId: string;
}

export const lineFromHit = (h: CatalogHit): SalesLine => ({
  productId: h.productId, sku: h.sku, name: h.name, catalogPrice: h.salePrice, quantity: "1", unitPrice: "", discount: "", taxId: "",
});

export const toLineInputs = (lines: SalesLine[]) => lines.map((l) => ({
  productId: l.productId, quantity: num(l.quantity) ?? 0, unitPriceOverride: l.unitPrice.trim() === "" ? null : num(l.unitPrice),
  lineDiscount: num(l.discount) ?? 0, taxId: l.taxId || null,
}));

/** Contrôles avant envoi (le serveur refait les siens) ; renvoie les erreurs par article. */
export function validateLines(lines: SalesLine[]): Record<string, string> {
  const errs: Record<string, string> = {};
  for (const l of lines) {
    const q = num(l.quantity), p = l.unitPrice.trim() === "" ? l.catalogPrice : num(l.unitPrice), d = num(l.discount) ?? 0;
    if (q == null || q <= 0) errs[l.productId] = "Quantité positive requise.";
    else if (l.unitPrice.trim() !== "" && (p == null || p < 0)) errs[l.productId] = "Prix invalide.";
    else if (p == null) errs[l.productId] = "Article sans prix de vente : saisissez un prix.";
    else if (d < 0 || d > q * p) errs[l.productId] = "Remise hors du montant de la ligne.";
  }
  return errs;
}

export function estimateSales(lines: SalesLine[], taxes: Tax[]) {
  return estimateTotals(lines.map((l) => ({
    quantity: num(l.quantity) ?? 0,
    unitPrice: (l.unitPrice.trim() === "" ? l.catalogPrice : num(l.unitPrice)) ?? 0,
    discount: num(l.discount) ?? 0,
    ratePercent: taxes.find((t) => t.id === l.taxId)?.rate ?? 0,
  })));
}

/** Recherche d'article vendable du catalogue (prix de vente affiché). */
export function SalesProductPicker({ onPick, excluded = [] }: { onPick: (h: CatalogHit) => void; excluded?: string[] }) {
  const [term, setTerm] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => { const t = setTimeout(() => setDebounced(term.trim()), 250); return () => clearTimeout(t); }, [term]);
  const results = useAsync(async () => (debounced.length < 2 ? [] : searchCatalog(debounced, 20)), [debounced]);
  const list = (results.data ?? []).filter((h) => h.status === "active" && !excluded.includes(h.productId));
  return (
    <div style={{ display: "grid", gap: 6 }}>
      <Field label="Ajouter un article" hint="Nom, SKU ou code-barres (2 caractères min.)">
        <TextInput value={term} placeholder="Rechercher…" onChange={(e) => setTerm(e.target.value)} />
      </Field>
      {debounced.length >= 2 && (
        <div role="listbox" aria-label="Articles trouvés" className="nx-card" style={{ padding: 6, maxHeight: 220, overflowY: "auto", display: "grid", gap: 2 }}>
          {results.loading && <span style={{ color: "var(--nx-text-2)", padding: 6 }}>Recherche…</span>}
          {!results.loading && list.length === 0 && <span style={{ color: "var(--nx-text-2)", padding: 6 }}>Aucun article.</span>}
          {list.map((h) => (
            <button key={h.productId} type="button" role="option" aria-selected={false} onClick={() => { onPick(h); setTerm(""); }}
                    style={{ display: "flex", justifyContent: "space-between", gap: 8, padding: "6px 8px", border: "none",
                             background: "transparent", cursor: "pointer", color: "inherit", font: "inherit", textAlign: "left", borderRadius: 6 }}>
              <span><code>{h.sku}</code> {h.name}</span>
              <span style={{ color: "var(--nx-text-2)", whiteSpace: "nowrap" }}>
                {h.salePrice != null ? money(h.salePrice, h.currency ?? "HTG") : <Badge tone="warning">sans prix</Badge>}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

export function SalesLinesEditor({ lines, onChange, taxes, currency, errors }: {
  lines: SalesLine[]; onChange: (lines: SalesLine[]) => void; taxes: Tax[]; currency: string; errors: Record<string, string>;
}) {
  const set = (i: number, patch: Partial<SalesLine>) => onChange(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  if (lines.length === 0) return <div style={{ color: "var(--nx-text-2)" }}>Aucun article ajouté.</div>;
  return (
    <div className="nx-scroll-x">
      <table className="nx-table">
        <thead><tr><th>Article</th><th>Quantité</th><th>Prix unitaire</th><th>Remise</th><th>Taxe</th><th /></tr></thead>
        <tbody>
          {lines.map((l, i) => (
            <tr key={l.productId}>
              <td><code>{l.sku}</code> {l.name}{errors[l.productId] && <div className="nx-err" role="alert">{errors[l.productId]}</div>}</td>
              <td style={{ minWidth: 90 }}><TextInput type="number" step="any" min={0} value={l.quantity} aria-label={`Quantité — ${l.name}`}
                invalid={!!errors[l.productId]} onChange={(e) => set(i, { quantity: e.target.value })} /></td>
              <td style={{ minWidth: 120 }}><TextInput type="number" step="0.01" min={0} value={l.unitPrice} aria-label={`Prix unitaire — ${l.name}`}
                placeholder={l.catalogPrice != null ? `${money(l.catalogPrice, currency)} (catalogue)` : "prix"}
                onChange={(e) => set(i, { unitPrice: e.target.value })} /></td>
              <td style={{ minWidth: 90 }}><TextInput type="number" step="0.01" min={0} value={l.discount} aria-label={`Remise — ${l.name}`}
                onChange={(e) => set(i, { discount: e.target.value })} /></td>
              <td style={{ minWidth: 130 }}>
                <Select value={l.taxId} aria-label={`Taxe — ${l.name}`} onChange={(e) => set(i, { taxId: e.target.value })}>
                  <option value="">Taxe de l'article</option>
                  {taxes.map((t) => <option key={t.id} value={t.id}>{t.name} ({t.rate} %)</option>)}
                </Select>
              </td>
              <td><IconButton label={`Retirer ${l.name}`} onClick={() => onChange(lines.filter((_, j) => j !== i))}><Icon.X size={14} /></IconButton></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/** Client (recherche progressive, 8 résultats au plus — jamais la base entière). */
export interface PickedCustomer { id: string; name: string; status?: string }

export function CustomerSelect({ value, onChange, label = "Client", required, error, disabled }: {
  value: PickedCustomer | null; onChange: (c: PickedCustomer | null) => void; label?: string; required?: boolean;
  error?: string | null; disabled?: boolean;
}) {
  const [q, setQ] = useState("");
  const [hits, setHits] = useState<CustomerLookup[]>([]);
  const seq = useRef(0);
  useEffect(() => {
    const term = q.trim();
    if (term.length < 2) { setHits([]); return; }
    const mine = ++seq.current;
    const t = setTimeout(() => { lookupCustomers(term).then((r) => { if (mine === seq.current) setHits(r); }).catch(() => setHits([])); }, 250);
    return () => clearTimeout(t);
  }, [q]);

  if (value) {
    return (
      <Field label={label} required={required} error={error}>
        <div style={{ display: "flex", gap: 8, alignItems: "center", minHeight: 36 }}>
          <b>{value.name}</b>
          {value.status && value.status !== "active" && <Badge tone="error">{value.status}</Badge>}
          {!disabled && <IconButton label="Changer de client" onClick={() => onChange(null)}><Icon.X size={14} /></IconButton>}
        </div>
      </Field>
    );
  }
  return (
    <div style={{ display: "grid", gap: 4 }}>
      <Field label={label} required={required} error={error} hint="Nom, code ou téléphone (2 caractères min.)">
        <TextInput value={q} disabled={disabled} placeholder="Rechercher un client…" onChange={(e) => setQ(e.target.value)} />
      </Field>
      {hits.length > 0 && (
        <div role="listbox" aria-label="Clients trouvés" className="nx-card" style={{ padding: 6, display: "grid", gap: 2 }}>
          {hits.map((c) => (
            <button key={c.id} type="button" role="option" aria-selected={false}
                    onClick={() => { onChange({ id: c.id, name: c.displayName, status: c.status }); setQ(""); setHits([]); }}
                    style={{ display: "flex", justifyContent: "space-between", gap: 8, padding: "6px 8px", border: "none",
                             background: "transparent", cursor: "pointer", color: "inherit", font: "inherit", textAlign: "left", borderRadius: 6 }}>
              <span>{c.displayName}</span>
              <span style={{ color: "var(--nx-text-2)" }}>{c.customerNumber}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
