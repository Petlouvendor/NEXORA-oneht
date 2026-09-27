import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { errorMessage, useAsync } from "@/shared/useAsync";
import {
  listBrands, listCategories, listCurrencies, listCustomerGroups, listPriceLists, listTaxes, listUnits,
  type Brand, type Category, type Currency, type CustomerGroup, type PriceListItem, type Tax, type Unit,
} from "@/catalog/catalogApi";

export interface CatalogRefs {
  categories: Category[];
  units: Unit[];
  brands: Brand[];
  taxes: Tax[];
  priceLists: PriceListItem[];
  customerGroups: CustomerGroup[];
  currencies: Currency[];
  /** Devise par défaut de l'entreprise (HTG si l'information manque). */
  defaultCurrency: string;
}

const EMPTY_REFS: CatalogRefs = {
  categories: [], units: [], brands: [], taxes: [], priceLists: [], customerGroups: [], currencies: [], defaultCurrency: "HTG",
};

/** Référentiels des formulaires du catalogue, chargés en une fois et rechargeables après création. */
export function useCatalogRefs(companyId: string | null) {
  const state = useAsync(async (): Promise<CatalogRefs> => {
    const [categories, units, brands, taxes, priceLists, customerGroups, currencies] = await Promise.all([
      listCategories(), listUnits(), listBrands(), listTaxes(), listPriceLists(), listCustomerGroups(), listCurrencies(),
    ]);
    const defaultCurrency = currencies.find((c) => c.isCompanyDefault)?.code ?? "HTG";
    return { categories, units, brands, taxes, priceLists, customerGroups, currencies, defaultCurrency };
  }, [companyId]);
  return { refs: state.data ?? EMPTY_REFS, loading: state.loading, error: state.error, reload: state.reload };
}

/** Libellé hiérarchique « Parent › Enfant » (anti-boucle : 10 niveaux au plus). */
export function categoryPath(categories: Category[], id: string | null | undefined): string {
  if (!id) return "";
  const byId = new Map(categories.map((c) => [c.id, c]));
  const parts: string[] = [];
  let cur = byId.get(id);
  for (let i = 0; cur && i < 10; i++) {
    parts.unshift(cur.name);
    cur = cur.parentId ? byId.get(cur.parentId) : undefined;
  }
  return parts.join(" › ");
}

/** Catégories triées par chemin complet, pour un <select> lisible. */
export function sortedCategories(categories: Category[]) {
  return categories
    .map((c) => ({ ...c, path: categoryPath(categories, c.id) }))
    .sort((a, b) => a.path.localeCompare(b.path, "fr"));
}

/**
 * Soumission de formulaire : un seul envoi à la fois (protection double clic), erreur globale et
 * erreur par champ déduite du code renvoyé par l'API (`fields` : code → nom du champ).
 */
export function useSubmit(fields: Record<string, string> = {}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const inFlight = useRef(false);

  const submit = useCallback(async <T,>(action: () => Promise<T>): Promise<T | undefined> => {
    if (inFlight.current) return undefined;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      return await action();
    } catch (e) {
      const msg = errorMessage(e);
      const field = e instanceof ApiError ? fields[e.problem.title] : undefined;
      if (field) setFieldErrors({ [field]: msg });
      else setError(msg);
      return undefined;
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }, [fields]);

  /** Erreur de validation détectée avant l'envoi (même présentation que celles du serveur). */
  const fail = useCallback((field: string, message: string) => setFieldErrors({ [field]: message }), []);

  // Le premier champ en erreur peut être hors de vue (long formulaire) : on l'amène à l'écran et on y place le focus.
  useEffect(() => {
    if (Object.keys(fieldErrors).length === 0) return;
    requestAnimationFrame(() => {
      const err = document.querySelector<HTMLElement>(".nx-err");
      err?.scrollIntoView({ block: "center", behavior: "smooth" });
      const input = err?.id ? document.querySelector<HTMLElement>(`[aria-describedby~="${err.id}"]`) : null;
      input?.focus({ preventScroll: true });
    });
  }, [fieldErrors]);

  return { busy, error, fieldErrors, submit, fail, setError };
}

/** Lit un fichier en base64 (sans l'en-tête data:). */
export function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(",")[1] ?? "");
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

/** Nombre saisi → nombre, ou null si vide / invalide. */
export const num = (v: string): number | null => {
  if (v.trim() === "") return null;
  const n = Number(v.replace(",", "."));
  return Number.isFinite(n) ? n : null;
};

export const PRODUCT_TYPE_LABEL: Record<string, string> = {
  stockable: "Stockable",
  service: "Service",
  composite: "Composé (recette, kit)",
  weight: "Vendu au poids",
  non_stock: "Non stocké",
};

export const STATUS_LABEL: Record<string, string> = {
  draft: "Brouillon", review: "En revue", active: "Actif", suspended: "Suspendu", inactive: "Inactif", archived: "Archivé",
};
