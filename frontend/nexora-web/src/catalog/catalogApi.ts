import { apiDownload, apiFetch } from "@/core/apiClient";

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
  categoryId: string | null;
  brandId: string | null;
  unitId: string | null;
  unitCode: string | null;
  defaultTaxId: string | null;
  reorderRule: ReorderRule | null;
  hasImage: boolean;
  conversions: UnitConversion[] | null;
}

export interface ReorderRule {
  minQuantity: number;
  maxQuantity: number;
  reorderPoint: number;
  reorderQuantity: number;
  leadTimeDays: number;
  autoOrder: boolean;
}

export interface Category { id: string; parentId: string | null; name: string; code: string | null; status: string }
export interface Brand { id: string; name: string; status: string }
export interface Currency { code: string; name: string; symbol: string; decimalPlaces: number; isCompanyDefault: boolean }
export interface CustomerGroup { id: string; name: string; description: string | null; status: string }
export interface Unit { id: string; code: string; name: string; precision: number; isWeight: boolean }
export interface UnitConversion {
  id: string; productId: string | null; fromUnitId: string; fromUnitCode: string; toUnitId: string; toUnitCode: string; factor: number;
}
export interface ConversionResult { quantity: number; converted: number; rule: string }

export interface PromotionConditions {
  minQuantity: number | null; buyQuantity: number | null; getQuantity: number | null; bundleQuantity: number | null;
}
export interface PromotionTarget {
  targetType: "all" | "product" | "category" | "brand";
  productId: string | null; categoryId: string | null; brandId: string | null; label?: string | null;
}
export interface Promotion {
  id: string; name: string; promoType: string; value: number; priority: number;
  startsAt: string | null; endsAt: string | null; status: string;
  conditions: PromotionConditions | null; targets: PromotionTarget[] | null;
}
export interface SavePromotion {
  name: string; promoType: string; value: number; priority: number;
  startsAt: string | null; endsAt: string | null; conditions: PromotionConditions | null; targets: PromotionTarget[];
}
export interface Coupon {
  id: string; code: string; promotionId: string | null; discountType: string; discountValue: number;
  usageLimit: number | null; usedCount: number; validFrom: string | null; validTo: string | null; status: string;
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

/** Recette Phase 55 PROD-004 — fiche modifiable. Le SKU, l'unité et le type ne sont pas modifiables
 *  (le stock enregistré en dépend) ; catégorie / marque / taxe absentes = inchangées. */
export const updateProduct = (id: string, body: {
  name: string; description: string | null; shortName: string | null; pluCode: string | null;
  categoryId?: string | null; brandId?: string | null; defaultTaxId?: string | null;
}) => apiFetch<void>(`/catalog/products/${id}`, { method: "PUT", body: JSON.stringify(body) });

const post = <T>(path: string, body: unknown) => apiFetch<T>(path, { method: "POST", body: JSON.stringify(body) });
const put = <T>(path: string, body: unknown) => apiFetch<T>(path, { method: "PUT", body: JSON.stringify(body) });

// --- Création produit & compléments de fiche (finalisation produit, phase 1) ---------------

export interface CreateProductInput {
  sku: string; name: string; description: string | null; unitId: string; productType: string;
  barcode: string | null; categoryId: string | null; brandId: string | null; defaultTaxId: string | null;
  trackLots: boolean; isWeighed: boolean; shortName: string | null; pluCode: string | null; status: string | null;
}
/** Renvoie l'identifiant du produit créé. */
export const createProduct = (input: CreateProductInput) => post<string>("/catalog/products", input);

export interface SetPriceInput {
  priceType: "sale" | "purchase" | "promo"; amount: number; currency: string; quantityMin: number;
  customerGroupId: string | null; validFrom: string | null; priceListId: string | null; taxIncluded: boolean; minPrice: number | null;
}
export const setProductPrice = (productId: string, body: SetPriceInput) =>
  post<void>(`/catalog/products/${productId}/prices`, { ...body, productId });

export const addBarcode = (productId: string, body: { barcode: string; packSize: number; barcodeType: string | null; isPrimary: boolean }) =>
  post<void>(`/catalog/products/${productId}/barcodes`, body);

export const addProductSupplier = (productId: string, body: {
  supplierId: string; supplierSku: string | null; purchasePrice: number; currency: string; leadTimeDays: number; isPreferred: boolean;
}) => post<void>(`/catalog/products/${productId}/suppliers`, { ...body, productId });

export const setReorderRule = (productId: string, rule: ReorderRule) =>
  put<void>(`/catalog/products/${productId}/reorder-rule`, { ...rule, productId, locationId: null });

export const uploadProductImage = (productId: string, contentBase64: string, contentType: string) =>
  post<void>(`/catalog/products/${productId}/image`, { contentBase64, contentType });

/** Image produit en URL objet locale (la route exige l'authentification : pas d'attribut src direct). */
export const loadProductImage = async (productId: string) =>
  URL.createObjectURL((await apiDownload(`/catalog/products/${productId}/image`)).blob);

// --- Référentiels ---------------------------------------------------------------------

export const listCategories = () => apiFetch<Category[]>("/catalog/categories");
export const createCategory = (body: { name: string; code: string | null; parentId: string | null }) =>
  post<string>("/catalog/categories", body);
export const updateCategory = (id: string, body: { name: string; code: string | null; parentId: string | null; status: string }) =>
  put<void>(`/catalog/categories/${id}`, body);

export const listCurrencies = () => apiFetch<Currency[]>("/catalog/currencies");
export const listBrands = () => apiFetch<Brand[]>("/catalog/brands");
export const createBrand = (name: string) => post<string>("/catalog/brands", { name, description: null });

export const listCustomerGroups = () => apiFetch<CustomerGroup[]>("/catalog/customer-groups");
export const createCustomerGroup = (name: string, description: string | null) =>
  post<string>("/catalog/customer-groups", { name, description });

export const createPriceList = (body: {
  code: string; name: string; listType: string; currency: string | null; customerGroupId: string | null; priority: number;
}) => post<string>("/catalog/price-lists", body);

export const listUnits = () => apiFetch<Unit[]>("/catalog/units");
export const createUnit = (body: { code: string; name: string; precision: number; isWeight: boolean }) =>
  post<string>("/catalog/units", body);
export const listConversions = (productId?: string) =>
  apiFetch<UnitConversion[]>(`/catalog/unit-conversions${qs({ productId })}`);
export const createConversion = (body: { productId: string | null; fromUnitId: string; toUnitId: string; factor: number }) =>
  post<string>("/catalog/unit-conversions", body);
export const deleteConversion = (id: string) => apiFetch<void>(`/catalog/unit-conversions/${id}`, { method: "DELETE" });
/** Conversion calculée par le serveur : l'écran ne convertit jamais lui-même. */
export const convertUnits = (p: { productId?: string | null; fromUnitId: string; toUnitId: string; quantity: number }) =>
  apiFetch<ConversionResult>(`/catalog/units/convert${qs({ ...p, productId: p.productId ?? undefined })}`);

// --- Promotions & coupons ----------------------------------------------------------------

export const listPromotions = () => apiFetch<Promotion[]>("/catalog/promotions");
export const createPromotion = (body: SavePromotion) => post<string>("/catalog/promotions", body);
export const updatePromotion = (id: string, body: SavePromotion) => put<void>(`/catalog/promotions/${id}`, body);
export const setPromotionStatus = (id: string, status: "active" | "inactive") =>
  post<void>(`/catalog/promotions/${id}/status`, { status });

export const listCoupons = () => apiFetch<Coupon[]>("/catalog/coupons");
export const createCoupon = (body: {
  code: string; discountType: "percentage" | "amount"; discountValue: number; usageLimit: number | null;
  validFrom: string | null; validTo: string | null;
}) => post<string>("/catalog/coupons", body);
export const setCouponStatus = (id: string, status: "active" | "inactive") =>
  post<void>(`/catalog/coupons/${id}/status`, { status });
