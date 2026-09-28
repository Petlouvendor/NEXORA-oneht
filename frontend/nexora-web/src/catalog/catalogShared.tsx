import { useAsync } from "@/shared/useAsync";
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

// Utilitaires de formulaire mutualisés (shared/forms.ts) — réexportés pour les écrans du catalogue.
export { fileToBase64, num, useSubmit } from "@/shared/forms";

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
