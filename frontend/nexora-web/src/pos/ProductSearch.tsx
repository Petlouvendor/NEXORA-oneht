import { useEffect, useRef, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Badge, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { money } from "@/shared/format";
import { scanProduct, searchProducts, type Availability, type PosProduct } from "@/pos/posApi";
import { scanOffline, searchOffline, type OfflineCatalogItem } from "@/pos/offlineStore";
import { displayPrice } from "@/pos/cartMath";

const AVAILABILITY: Record<Availability, { label: string; tone?: "success" | "warning" | "error" }> = {
  in_stock: { label: "Disponible", tone: "success" },
  low: { label: "Stock faible", tone: "warning" },
  out: { label: "Rupture", tone: "error" },
  untracked: { label: "Service" },
};

/**
 * Recherche produit du POS (guide §9, §24). Le scan reste le parcours prioritaire : Entrée sans
 * suggestion sélectionnée = recherche exacte du code (code-barres, PLU, SKU). Sinon, après quelques
 * caractères, l'API renvoie une liste courte et pertinente — jamais le catalogue complet, jamais de
 * coût. Sélection au clavier (↑ ↓ Entrée Échap), à la souris ou au doigt ; le produit choisi part
 * directement au panier (le prix est revalidé par le serveur à l'encaissement).
 */
export function ProductSearch({
  locationId, onPick, disabled, inputRef, offlineCatalog, offline,
}: {
  locationId: string | null;
  onPick: (p: PosProduct) => void;
  disabled?: boolean;
  inputRef?: React.RefObject<HTMLInputElement | null>;
  /** Phase 6 : catalogue du profil hors ligne, utilisé sans réseau (ou si le serveur est injoignable). */
  offlineCatalog?: OfflineCatalogItem[] | null;
  offline?: boolean;
}) {
  const [term, setTerm] = useState("");
  const [items, setItems] = useState<PosProduct[]>([]);
  const [minChars, setMinChars] = useState(2);
  const [highlight, setHighlight] = useState(-1);
  const [loading, setLoading] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const listId = "pos-search-results";

  // Recherche progressive : délai court après la frappe, requête précédente annulée.
  useEffect(() => {
    const q = term.trim();
    abortRef.current?.abort();
    if (q.length < minChars) {
      setItems([]);
      setHighlight(-1);
      setMessage(null);
      return;
    }
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    const local = () => {
      const found = searchOffline(offlineCatalog ?? [], q);
      setItems(found);
      setHighlight(found.length > 0 ? 0 : -1);
      setMessage(found.length === 0 ? `Aucun produit du catalogue hors ligne ne correspond à « ${q} ».` : null);
    };
    if (offline && offlineCatalog) { local(); return; }
    const timer = setTimeout(() => {
      setLoading(true);
      searchProducts(q, locationId, ctrl.signal)
        .then((r) => {
          setItems(r.items);
          setMinChars(r.minChars);
          setHighlight(r.items.length > 0 ? 0 : -1);
          setMessage(r.items.length === 0 ? `Aucun produit vendable ne correspond à « ${q} ». Modifiez la recherche.` : null);
        })
        .catch((e) => {
          if (ctrl.signal.aborted) return;
          // Serveur injoignable : on cherche dans le catalogue hors ligne s'il existe.
          if (e instanceof ApiError && e.isOffline && offlineCatalog) local();
          else setMessage(e instanceof ApiError ? e.message : "Recherche impossible.");
        })
        .finally(() => !ctrl.signal.aborted && setLoading(false));
    }, 200);
    return () => { clearTimeout(timer); ctrl.abort(); };
  }, [term, locationId, minChars, offline, offlineCatalog]);

  function pick(p: PosProduct) {
    onPick(p);
    setTerm("");
    setItems([]);
    setHighlight(-1);
    setMessage(null);
    inputRef?.current?.focus();
  }

  async function scanExact(code: string) {
    const localScan = () => {
      const hit = scanOffline(offlineCatalog ?? [], code);
      if (hit) pick(hit);
      else setMessage(`Aucun produit du catalogue hors ligne pour le code « ${code} ».`);
    };
    if (offline && offlineCatalog) { localScan(); return; }
    try {
      pick(await scanProduct(code, locationId));
    } catch (e) {
      if (e instanceof ApiError && e.isOffline && offlineCatalog) { localScan(); return; }
      setMessage(e instanceof ApiError && e.problem.status === 404
        ? `Aucun produit vendable pour le code « ${code} ».`
        : e instanceof ApiError ? e.message : "Scan impossible.");
    }
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === "ArrowDown" && items.length > 0) {
      e.preventDefault();
      setHighlight((h) => (h + 1) % items.length);
    } else if (e.key === "ArrowUp" && items.length > 0) {
      e.preventDefault();
      setHighlight((h) => (h <= 0 ? items.length - 1 : h - 1));
    } else if (e.key === "Enter") {
      e.preventDefault();
      const code = term.trim();
      if (!code) return;
      // Une douchette envoie le code puis Entrée avant que la liste n'arrive : recherche exacte.
      if (highlight >= 0 && items[highlight]) pick(items[highlight]);
      else void scanExact(code);
    } else if (e.key === "Escape") {
      setTerm("");
      setItems([]);
      setMessage(null);
    }
  }

  const open = items.length > 0;
  return (
    <div style={{ position: "relative", flex: 1 }}>
      <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
        <Icon.Search size={18} />
        <TextInput
          ref={inputRef} autoFocus disabled={disabled} value={term}
          placeholder={`Scanner un code ou taper ${minChars} caractères du nom / de la référence`}
          aria-label="Rechercher ou scanner un produit"
          role="combobox" aria-expanded={open} aria-controls={listId} aria-autocomplete="list"
          aria-activedescendant={highlight >= 0 ? `${listId}-${highlight}` : undefined}
          onChange={(e) => setTerm(e.target.value)} onKeyDown={onKeyDown}
        />
      </div>
      {open && (
        <ul id={listId} role="listbox" className="nx-card"
            style={{ position: "absolute", zIndex: 20, left: 26, right: 0, top: "calc(100% + 4px)", margin: 0, padding: 4, listStyle: "none" }}>
          {items.map((p, i) => (
            <li key={p.productId} id={`${listId}-${i}`} role="option" aria-selected={i === highlight}
                onMouseEnter={() => setHighlight(i)}
                onMouseDown={(e) => { e.preventDefault(); pick(p); }}
                style={{
                  display: "flex", justifyContent: "space-between", gap: 12, alignItems: "center", cursor: "pointer",
                  padding: "10px 10px", minHeight: 44, borderRadius: 8,
                  background: i === highlight ? "var(--nx-surface-2, rgba(0,245,212,.08))" : "transparent",
                }}>
              <span style={{ display: "grid" }}>
                <b>{p.name}</b>
                <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-xs)" }}>{p.sku}{p.barcode ? ` · ${p.barcode}` : ""}</span>
              </span>
              <span style={{ display: "flex", gap: 8, alignItems: "center", whiteSpace: "nowrap" }}>
                <Badge tone={AVAILABILITY[p.availability].tone}>{AVAILABILITY[p.availability].label}</Badge>
                <b className="nx-numeric">{displayPrice(p) != null ? money(displayPrice(p)!, p.currency ?? "HTG") : "Sans prix"}</b>
              </span>
            </li>
          ))}
        </ul>
      )}
      {(message || loading) && !open && (
        <div role="status" style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)", marginTop: 6, marginLeft: 26 }}>
          {loading ? "Recherche…" : message}
        </div>
      )}
    </div>
  );
}
