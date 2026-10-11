import { apiFetch } from "@/core/apiClient";
import type { TenderInput } from "@/pos/posApi";

// Module Restaurant (Phase 8 ; finalisation phase 13) : salle → commande → cuisine → service → paiement.
// La commande est une vente en attente ; son règlement passe par le moteur de vente (prix, taxes, stock, recettes,
// caisse, comptabilité).

export type KitchenStatus = "pending" | "preparing" | "ready" | "served" | "cancelled";
export interface RestaurantArea { id: string; name: string; code: string; status: string; establishmentId: string | null; tableCount: number }
export interface RestaurantTable {
  id: string; name: string; code: string; seats: number; areaId: string | null; status: "available" | "occupied" | "reserved" | "inactive";
  currentSaleId: string | null; areaName: string | null; establishmentId: string | null; covers: number | null;
  kitchenStatus: KitchenStatus | null; openedAt: string | null; runningTotal: number | null; documentNumber: string | null;
}
export interface OrderLine {
  productId: string; description: string; quantity: number; unitPrice: number; lineTotal: number;
  lineId: string | null; discount: number; modifiers: string[] | null; addedAt: string | null;
}
export interface RestaurantOrder {
  saleId: string; documentNumber: string; tableId: string; tableName: string; covers: number; kitchenStatus: KitchenStatus;
  runningTotal: number; lines: OrderLine[]; notes: string | null; openedAt: string | null; firedAt: string | null; readyAt: string | null;
  servedAt: string | null; areaName: string | null; inventoryLocationId: string | null; currency: string | null; waiterName: string | null;
  status: string | null;
}
export interface OrderItem { productId: string; quantity: number; lineDiscount: number; modifiers: string[] | null }
export interface RestaurantSetup { establishments: Array<{ id: string; name: string; hasStockLocation: boolean }>; moduleEnabled: boolean }
export interface CheckoutResultDto { saleId: string; documentNumber: string; total: number; changeGiven: number }

export interface RecipeComponent {
  componentProductId: string; componentName: string; quantity: number; wastagePercent: number; kitchenStation: string | null;
  unitId: string | null; unitCode: string | null; unitCost: number;
}
export interface Recipe {
  id: string; productId: string; name: string; yieldQuantity: number; estimatedCost: number; components: RecipeComponent[];
  productName: string | null; yieldUnitId: string | null;
}
export interface RecipeProductOption { id: string; name: string; sku: string | null; productType: string; unitId: string | null; hasRecipe: boolean }
export interface RecipeOptions { dishes: RecipeProductOption[]; ingredients: RecipeProductOption[]; units: Array<{ id: string; code: string; name: string }> }

export const KITCHEN_STATUS: Record<KitchenStatus, { label: string; tone: "success" | "warning" | "error" | "info" | undefined }> = {
  pending: { label: "À préparer", tone: "warning" }, preparing: { label: "En préparation", tone: "info" },
  ready: { label: "Prêt", tone: "success" }, served: { label: "Servi", tone: undefined }, cancelled: { label: "Annulé", tone: "error" },
};
export const NEXT_KITCHEN: Partial<Record<KitchenStatus, { status: KitchenStatus; label: string }>> = {
  pending: { status: "preparing", label: "Commencer" }, preparing: { status: "ready", label: "Prêt" }, ready: { status: "served", label: "Servi" },
};

const post = (body: unknown, idempotencyKey?: string) => ({ method: "POST", body: JSON.stringify(body), ...(idempotencyKey ? { idempotencyKey } : {}) });

export const getSetup = () => apiFetch<RestaurantSetup>("/restaurant/setup");
export const listAreas = () => apiFetch<RestaurantArea[]>("/restaurant/areas");
export const listTables = () => apiFetch<RestaurantTable[]>("/restaurant/tables");
export const createArea = (body: { establishmentId: string; name: string; code: string }) => apiFetch<string>("/restaurant/areas", post(body));
export const createTable = (body: { establishmentId: string; areaId: string | null; name: string; code: string; seats: number }) =>
  apiFetch<string>("/restaurant/tables", post(body));

export const getOrder = (saleId: string) => apiFetch<RestaurantOrder>(`/restaurant/orders/${saleId}`);
export const placeOrder = (body: { tableId: string; covers: number; waiterUserId: string | null; notes: string | null; items: OrderItem[] }) =>
  apiFetch<RestaurantOrder>("/restaurant/orders", post(body));
export const addItems = (saleId: string, items: OrderItem[]) => apiFetch<RestaurantOrder>("/restaurant/orders/items", post({ saleId, items }));
export const cancelOrder = (saleId: string, reason: string) => apiFetch<void>("/restaurant/orders/cancel", post({ saleId, reason }));
export const kitchenBoard = () => apiFetch<RestaurantOrder[]>("/restaurant/kitchen");
export const advanceKitchen = (saleId: string, targetStatus: KitchenStatus) => apiFetch<void>("/restaurant/kitchen/advance", post({ saleId, targetStatus }));
/** Règlement : `operationKey` fixé à l'ouverture du dialogue — un double clic ne règle jamais deux fois. */
export const closeOrder = (body: {
  saleId: string; tenders: TenderInput[];
  amountTendered: number; tipAmount: number; cashSessionId: string | null;
}, operationKey: string) => apiFetch<CheckoutResultDto>("/restaurant/orders/close", post(body, operationKey));

export const listRecipes = () => apiFetch<Recipe[]>("/restaurant/recipes");
export const getRecipeOptions = () => apiFetch<RecipeOptions>("/restaurant/recipes/options");
export interface RecipeComponentInput { componentProductId: string; quantity: number; unitId: string; wastagePercent: number; kitchenStation: string | null }
export const defineRecipe = (body: { productId: string; name: string; yieldUnitId: string; yieldQuantity: number; components: RecipeComponentInput[] }) =>
  apiFetch<string>("/restaurant/recipes", post(body));

/** Minutes écoulées depuis `iso` (âge d'un ticket en cuisine). */
export const minutesSince = (iso: string | null, now: number = Date.now()) =>
  iso ? Math.max(0, Math.floor((now - new Date(iso).getTime()) / 60000)) : 0;

/** Lignes ajoutées après l'envoi en cuisine : nouvelle vague à signaler au cuisinier. */
export const isNewWave = (line: OrderLine, order: Pick<RestaurantOrder, "firedAt">) =>
  !!order.firedAt && !!line.addedAt && new Date(line.addedAt).getTime() > new Date(order.firedAt).getTime();

/** Précisions de cuisine saisies en texte libre, séparées par des virgules. */
export const parseModifiers = (text: string) =>
  text.split(",").map((m) => m.trim()).filter(Boolean).slice(0, 10);
