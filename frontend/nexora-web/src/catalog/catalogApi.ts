import { apiFetch } from "@/core/apiClient";

// --- Recherche & scan (Phase 23 §37-38) ---------------------------------

export interface CatalogHit {
  productId: string;
  sku: string;
  name: string;
  shortName: string | null;
  barcode: string | null;
  pluCode: string | null;
  productType: string;
  status: string;
  categoryName: string | null;
  brandName: string | null;
  salePrice: number | null;
  currency: string | null;
  exactBarcode: boolean;
}

export interface ProductListItem {
  id: string;
  sku: string;
  name: string;
  shortName: string | null;
  productType: string;
  status: string;
  categoryName: string | null;
  brandName: string | null;
  primaryBarcode: string | null;
  salePrice: number | null;
  currency: string | null;
}

export interface ProductPrice {
  id: string;
  priceType: string;
  priceListCode: string | null;
  amount: number;
  currency: string;
  taxIncluded: boolean;
  quantityMin: number;
  customerGroup: string | null;
  validFrom: string;
  validTo: string | null;
}

export interface ProductBarcode {
  id: string;
  barcode: string;
  barcodeType: string;
  isPrimary: boolean;
  status: string;
  packSize: number;
}

export interface ProductSupplier {
  id: string;
  supplierName: string;
  supplierSku: string | null;
  purchasePrice: number;
  currency: string;
  leadTimeDays: number;
  isPreferred: boolean;
}

export interface ProductDetail {
  id: string;
  sku: string;
  name: string;
  shortName: string | null;
  description: string | null;
  productType: string;
  status: string;
  pluCode: string | null;
  manufacturerRef: string | null;
  internalRef: string | null;
  imageUrl: string | null;
  categoryName: string | null;
  brandName: string | null;
  trackLots: boolean;
  isWeighed: boolean;
  prices: ProductPrice[];
  barcodes: ProductBarcode[];
  suppliers: ProductSupplier[];
}

export interface PriceListItem {
  id: string;
  code: string;
  name: string;
  listType: string;
  currency: string | null;
  customerGroup: string | null;
  priority: number;
  status: string;
}

export interface Tax {
  id: string;
  code: string;
  name: string;
  rate: number;
  type: string;
  isInclusive: boolean;
  status: string;
}

// --- Moteur de prix explicable (Phase 23 §22) --------------------------

export interface PriceStep {
  rule: string;
  label: string;
  delta: number;
  runningUnit: number;
  reference: string | null;
}

export interface PriceQuote {
  productId: string;
  description: string;
  quantity: number;
  currency: string;
  baseUnitPrice: number;
  netUnitPrice: number;
  taxRate: number;
  taxCode: string | null;
  unitTax: number;
  grossUnitPrice: number;
  lineNet: number;
  lineDiscount: number;
  lineTax: number;
  lineGross: number;
  steps: PriceStep[];
}

export interface PriceQuoteRequest {
  productId: string;
  quantity: number;
  currency: string;
  customerId?: string | null;
  priceListId?: string | null;
  inventoryLocationId?: string | null;
  at?: string | null;
  manualDiscount: number;
  couponCode?: string | null;
}

/** Sérialise une requête en query-string en ignorant les valeurs vides (Phase 23 §47). */
export const qs = (params: Record<string, string | number | undefined | null>) => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  const s = p.toString();
  return s ? `?${s}` : "";
};

export const searchCatalog = (q: string, limit = 20) =>
  apiFetch<CatalogHit[]>(`/catalog/search${qs({ q, limit })}`);

export const scanBarcode = (barcode: string) =>
  apiFetch<CatalogHit>(`/catalog/scan${qs({ barcode })}`);

export const listProducts = (opts: { q?: string; categoryId?: string; status?: string; limit?: number } = {}) =>
  apiFetch<ProductListItem[]>(`/catalog/products${qs(opts)}`);

export const getProduct = (id: string) => apiFetch<ProductDetail>(`/catalog/products/${id}`);

export const listPriceLists = () => apiFetch<PriceListItem[]>("/catalog/price-lists");

export const listTaxes = () => apiFetch<Tax[]>("/catalog/taxes");

export const quotePrice = (req: PriceQuoteRequest) =>
  apiFetch<PriceQuote>("/catalog/pricing/quote", { method: "POST", body: JSON.stringify(req) });

export const setProductStatus = (id: string, status: string) =>
  apiFetch<void>(`/catalog/products/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });

/** Recette Phase 55 PROD-004 — fiche modifiable. Le SKU, l'unité, le type et le statut ne sont pas
 *  modifiables ici ; catégorie / marque / taxe absentes = inchangées. */
export const updateProduct = (id: string, body: {
  name: string; description: string | null; shortName: string | null; pluCode: string | null;
}) => apiFetch<void>(`/catalog/products/${id}`, { method: "PUT", body: JSON.stringify(body) });
