import { useEffect, useState } from "react";
import { number } from "@/shared/format";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { Badge, Button, Field, IconButton, Select, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { searchCatalog } from "@/catalog/catalogApi";
import { getLocations, getStockOverview, getWarehouses, type Location, type StockStatus } from "@/inventory/inventoryApi";

export const STOCK_STATUS: Record<StockStatus, { label: string; tone?: "success" | "warning" | "error" | "info" }> = {
  out: { label: "Rupture", tone: "error" },
  low: { label: "Stock faible", tone: "warning" },
  normal: { label: "Normal", tone: "success" },
  over: { label: "Surstock", tone: "info" },
};

export const MOVEMENT_LABEL: Record<string, string> = {
  receipt: "Réception", sale: "Vente", sale_return: "Retour client", purchase_return: "Retour fournisseur",
  transfer_in: "Transfert entrant", transfer_out: "Transfert sortant", adjustment: "Ajustement", loss: "Perte",
  breakage: "Casse", expiry: "Expiration", found: "Stock retrouvé", count: "Écart d'inventaire",
  production_input: "Consommation production", production_output: "Production", opening: "Stock d'ouverture",
  reservation: "Réservation", release: "Libération",
};

export const ADJUSTMENT_LABEL: Record<string, string> = {
  count: "Inventaire", loss: "Perte", breakage: "Casse", expiry: "Expiration", theft: "Vol",
  correction: "Correction", found: "Stock retrouvé", opening: "Ouverture",
};

export const TRANSFER_STATUS: Record<string, string> = {
  draft: "Brouillon", in_transit: "En transit", partially_received: "Reçu partiellement", completed: "Clôturé", cancelled: "Annulé",
};

export const COUNT_STATUS: Record<string, string> = {
  draft: "Brouillon", counting: "Comptage en cours", review: "À valider", validated: "Validé", cancelled: "Annulé",
};

export const COUNT_TYPE: Record<string, string> = { full: "Complet", partial: "Partiel", cycle: "Tournant" };

export const qty = (v: number | null | undefined, unit?: string | null) =>
  v == null ? "—" : `${number(v, 3)}${unit ? ` ${unit}` : ""}`;

/** Emplacements et entrepôts de l'entreprise active. */
export function useStockPlaces(companyId: string | null) {
  const state = useAsync(async () => {
    const [locations, warehouses] = await Promise.all([getLocations(), getWarehouses()]);
    return { locations, warehouses };
  }, [companyId]);
  const locations = state.data?.locations ?? [];
  return {
    locations,
    active: locations.filter((l) => l.status === "active" && l.locationType !== "transit"),
    warehouses: state.data?.warehouses ?? [],
    loading: state.loading, error: state.error, reload: state.reload,
  };
}

export function LocationSelect({ value, onChange, locations, label = "Emplacement", required, error, allowAll, exclude }: {
  value: string; onChange: (v: string) => void; locations: Location[]; label?: string; required?: boolean;
  error?: string | null; allowAll?: boolean; exclude?: string;
}) {
  return (
    <Field label={label} required={required} error={error}>
      <Select value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{allowAll ? "Tous les emplacements" : "—"}</option>
        {locations.filter((l) => l.id !== exclude).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
      </Select>
    </Field>
  );
}

export interface PickedProduct { productId: string; sku: string; name: string; available: number | null; unitCode: string | null }

/**
 * Sélection d'articles : cherche d'abord dans le stock de l'emplacement (quantité disponible affichée) ;
 * avec <c>catalogFallback</c> et la permission catalogue, propose aussi les articles sans stock ici
 * (entrée de stock retrouvé, inventaire d'un article jamais reçu).
 */
export function ProductPicker({ locationId, onPick, catalogFallback, excluded = [] }: {
  locationId: string; onPick: (p: PickedProduct) => void; catalogFallback?: boolean; excluded?: string[];
}) {
  const { hasPermission } = useAuth();
  const [term, setTerm] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => { const t = setTimeout(() => setDebounced(term.trim()), 250); return () => clearTimeout(t); }, [term]);

  const results = useAsync<PickedProduct[]>(async () => {
    if (debounced.length < 2 || !locationId) return [];
    const stock = await getStockOverview({ locationId, q: debounced, limit: 20 });
    const picked: PickedProduct[] = stock.rows.map((r) => ({
      productId: r.productId, sku: r.sku, name: r.productName, available: r.available, unitCode: r.unitCode,
    }));
    if (catalogFallback && hasPermission("catalog.view")) {
      const hits = await searchCatalog(debounced, 20).catch(() => []);
      for (const h of hits) {
        if (!picked.some((p) => p.productId === h.productId) && h.productType !== "service")
          picked.push({ productId: h.productId, sku: h.sku, name: h.name, available: null, unitCode: null });
      }
    }
    return picked;
  }, [debounced, locationId, catalogFallback]);

  const list = (results.data ?? []).filter((p) => !excluded.includes(p.productId));
  return (
    <div style={{ display: "grid", gap: 6 }}>
      <Field label="Ajouter un article" hint={locationId ? "Nom, SKU (2 caractères min.)" : "Choisissez d'abord l'emplacement."}>
        <TextInput value={term} disabled={!locationId} placeholder="Rechercher…" onChange={(e) => setTerm(e.target.value)} />
      </Field>
      {debounced.length >= 2 && (
        <div role="listbox" aria-label="Articles trouvés" className="nx-card" style={{ padding: 6, maxHeight: 220, overflowY: "auto", display: "grid", gap: 2 }}>
          {results.loading && <span style={{ color: "var(--nx-text-2)", padding: 6 }}>Recherche…</span>}
          {!results.loading && list.length === 0 && <span style={{ color: "var(--nx-text-2)", padding: 6 }}>Aucun article.</span>}
          {list.map((p) => (
            <button key={p.productId} type="button" role="option" aria-selected={false}
                    onClick={() => { onPick(p); setTerm(""); }}
                    style={{ display: "flex", justifyContent: "space-between", gap: 8, padding: "6px 8px", border: "none",
                             background: "transparent", cursor: "pointer", color: "inherit", font: "inherit", textAlign: "left", borderRadius: 6 }}>
              <span><code>{p.sku}</code> {p.name}</span>
              <span style={{ color: "var(--nx-text-2)", whiteSpace: "nowrap" }}>
                {p.available == null ? <Badge>hors stock ici</Badge> : `dispo ${qty(p.available, p.unitCode)}`}
              </span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}

/** Ligne d'article saisie dans un formulaire de mouvement. */
export interface EditableLine extends PickedProduct { quantity: string; note: string; unitCost?: string }

export function LinesEditor({ lines, onChange, quantityLabel = "Quantité", signed, withCost, errors }: {
  lines: EditableLine[]; onChange: (lines: EditableLine[]) => void; quantityLabel?: string;
  signed?: boolean; withCost?: boolean; errors?: Record<string, string>;
}) {
  const set = (i: number, patch: Partial<EditableLine>) => onChange(lines.map((l, j) => (j === i ? { ...l, ...patch } : l)));
  if (lines.length === 0) return <div style={{ color: "var(--nx-text-2)" }}>Aucun article ajouté.</div>;
  return (
    <div className="nx-scroll-x">
      <table className="nx-table">
        <thead>
          <tr>
            <th>Article</th><th className="nx-num">Disponible</th><th>{quantityLabel}</th>
            {withCost && <th>Coût unitaire</th>}<th>Justification</th><th />
          </tr>
        </thead>
        <tbody>
          {lines.map((l, i) => (
            <tr key={l.productId}>
              <td><code>{l.sku}</code> {l.name}</td>
              <td className="nx-num">{qty(l.available, l.unitCode)}</td>
              <td style={{ minWidth: 110 }}>
                <TextInput type="number" step="any" min={signed ? undefined : 0} value={l.quantity} aria-label={`${quantityLabel} — ${l.name}`}
                           invalid={!!errors?.[l.productId]} onChange={(e) => set(i, { quantity: e.target.value })} />
                {errors?.[l.productId] && <div className="nx-err" role="alert">{errors[l.productId]}</div>}
              </td>
              {withCost && (
                <td style={{ minWidth: 110 }}>
                  <TextInput type="number" step="0.01" min={0} value={l.unitCost ?? ""} aria-label={`Coût unitaire — ${l.name}`}
                             placeholder="coût moyen" onChange={(e) => set(i, { unitCost: e.target.value })} />
                </td>
              )}
              <td style={{ minWidth: 160 }}>
                <TextInput value={l.note} aria-label={`Justification — ${l.name}`} onChange={(e) => set(i, { note: e.target.value })} />
              </td>
              <td><IconButton label={`Retirer ${l.name}`} onClick={() => onChange(lines.filter((_, j) => j !== i))}><Icon.X size={14} /></IconButton></td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function RefreshButton({ onClick, busy }: { onClick: () => void; busy?: boolean }) {
  return <Button size="sm" variant="ghost" onClick={onClick} loading={busy}>Actualiser</Button>;
}
