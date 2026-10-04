import { apiFetch } from "@/core/apiClient";
import { secureGet, secureSet, secureStoreAvailable } from "@/shared/secureStore";
import type { CurrentSession, PosProduct } from "@/pos/posApi";
import type { TenderMethodOption } from "@/pos/tenders";

/**
 * Caisse hors ligne (finalisation, phase 6). Tant que le réseau est là, la caisse télécharge son profil hors
 * ligne (politique, catalogue vendable de l'emplacement — prix de vente seulement, jamais de coût —, session,
 * moyens d'encaissement) et le garde chiffré dans le coffre du navigateur. Sans réseau, elle vend sur ce profil :
 * la vente part dans la file d'attente et n'est jamais présentée comme confirmée avant la synchronisation.
 */

export interface OfflineCatalogItem {
  productId: string; name: string; sku: string; barcode: string | null; codes: string[]; searchText: string;
  salePrice: number | null; currency: string | null; availability: PosProduct["availability"];
  taxRate?: number; priceIncludesTax?: boolean;
}
export interface OfflinePolicy {
  allowOfflineSales: boolean; allowOfflineCredit: boolean; offlineCreditLimit: number; allowOfflineDiscount: boolean;
  allowOfflineRefund: boolean; allowOfflineCustomerPayment: boolean; maxOfflineMinutes: number; allowedPaymentMethods: string[];
}
export interface OfflineProfileDto {
  generatedAt: string; policy: OfflinePolicy; catalog: OfflineCatalogItem[]; truncated: boolean; maxItems: number;
}
/** Profil conservé : lié à l'utilisateur et à l'entreprise (jamais servi à un autre compte du même poste). */
export interface StoredProfile {
  userId: string; companyId: string; savedAt: string; profile: OfflineProfileDto;
  session: CurrentSession | null; tenderMethods: TenderMethodOption[];
}

const PROFILE_KEY = "pos.offline-profile";
const IDENTIFIER_KEY = "nexora.pos.deviceIdentifier";
const DEVICE_KEY = "nexora.pos.syncDeviceId";
const CONTACT_KEY = "nexora.pos.lastServerContact";

export const getOfflineProfile = (locationId: string | null) =>
  apiFetch<OfflineProfileDto>(`/pos/offline-profile${locationId ? `?locationId=${locationId}` : ""}`);

/** Identifiant aléatoire et stable du navigateur de caisse (jamais une donnée personnelle). */
export function deviceIdentifier(): string {
  try {
    let id = localStorage.getItem(IDENTIFIER_KEY);
    if (!id) {
      id = `browser-${crypto.randomUUID()}`;
      localStorage.setItem(IDENTIFIER_KEY, id);
    }
    return id;
  } catch {
    return `browser-${crypto.randomUUID()}`;
  }
}

export function storedDeviceId(): string {
  try { return localStorage.getItem(DEVICE_KEY) ?? ""; } catch { return ""; }
}

/** Enregistre (ou retrouve) l'appareil de synchronisation de ce navigateur. */
/** `endpoint` : la caisse s'enregistre par /pos/device, le terrain par /field-ops/device (même registre, même identifiant). */
export async function ensureDevice(name: string, endpoint = "/pos/device"): Promise<{ id: string; status: string }> {
  const d = await apiFetch<{ id: string; name: string; status: string }>(endpoint, {
    method: "POST", body: JSON.stringify({ deviceIdentifier: deviceIdentifier(), name }),
  });
  try { localStorage.setItem(DEVICE_KEY, d.id); } catch { /* stockage indisponible */ }
  markServerContact();
  return d;
}

export function forgetDevice(): void {
  try { localStorage.removeItem(DEVICE_KEY); } catch { /* ignore */ }
}

/** Dernier échange réussi avec le serveur : repère de la durée hors ligne autorisée. */
export function markServerContact(at: Date = new Date()): void {
  try { localStorage.setItem(CONTACT_KEY, at.toISOString()); } catch { /* ignore */ }
}
export function lastServerContact(): Date | null {
  try {
    const v = localStorage.getItem(CONTACT_KEY);
    return v ? new Date(v) : null;
  } catch { return null; }
}

export async function saveProfile(p: StoredProfile): Promise<void> {
  // Sans chiffrement possible (HTTP hors localhost), le catalogue n'est pas conservé : la caisse le dit.
  if (!secureStoreAvailable()) return;
  await secureSet(PROFILE_KEY, p);
}

export async function loadProfile(userId: string, companyId: string): Promise<StoredProfile | null> {
  if (!secureStoreAvailable()) return null;
  try {
    const p = await secureGet<StoredProfile>(PROFILE_KEY);
    return p && p.userId === userId && p.companyId === companyId ? p : null;
  } catch {
    return null;
  }
}

/** Texte comparable : minuscules, sans accents ni ponctuation superflue (comme search_norm côté serveur). */
export const norm = (s: string) => s.normalize("NFD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/\s+/g, " ").trim();

const toProduct = (i: OfflineCatalogItem): PosProduct => ({
  productId: i.productId, name: i.name, sku: i.sku, barcode: i.barcode, salePrice: i.salePrice, currency: i.currency,
  availability: i.availability, taxRate: i.taxRate ?? 0, priceIncludesTax: i.priceIncludesTax ?? false,
});

/** Recherche locale : code exact (code-barres, PLU, SKU) d'abord, puis nom / SKU contenant le texte. */
export function searchOffline(catalog: OfflineCatalogItem[], term: string, max = 8): PosProduct[] {
  const t = norm(term);
  if (!t) return [];
  const exact = catalog.filter((i) => i.codes.includes(term.trim()) || norm(i.sku) === t);
  const starts = catalog.filter((i) => !exact.includes(i) && norm(i.name).startsWith(t));
  const contains = catalog.filter((i) => !exact.includes(i) && !starts.includes(i) && norm(i.searchText).includes(t));
  return [...exact, ...starts, ...contains].slice(0, max).map(toProduct);
}

export function scanOffline(catalog: OfflineCatalogItem[], code: string): PosProduct | null {
  const c = code.trim();
  const hit = catalog.find((i) => i.codes.includes(c) || i.sku.toLowerCase() === c.toLowerCase());
  return hit ? toProduct(hit) : null;
}

export interface OfflineSaleFacts {
  hasDiscount: boolean;
  creditAmount: number;
  tenderTypes: string[];
}

/**
 * Ce que la politique interdit à une vente hors ligne (mêmes règles que le serveur à la synchronisation).
 * Renvoie le motif du refus, ou null si la vente peut être mise en file.
 */
export function offlineRefusal(policy: OfflinePolicy, facts: OfflineSaleFacts, lastContact: Date | null, now: Date = new Date()): string | null {
  if (!policy.allowOfflineSales) return "Les ventes hors ligne sont désactivées par la politique de l'entreprise.";
  if (policy.maxOfflineMinutes > 0 && lastContact && (now.getTime() - lastContact.getTime()) / 60000 > policy.maxOfflineMinutes)
    return `Caisse hors ligne depuis plus de ${policy.maxOfflineMinutes} min : reconnectez-vous pour continuer à vendre.`;
  if (facts.hasDiscount && !policy.allowOfflineDiscount) return "Remise interdite hors ligne.";
  if (facts.creditAmount > 0) {
    if (!policy.allowOfflineCredit) return "Vente à crédit interdite hors ligne.";
    if (policy.offlineCreditLimit > 0 && facts.creditAmount > policy.offlineCreditLimit)
      return `Crédit hors ligne limité à ${policy.offlineCreditLimit}.`;
  }
  const allowed = policy.allowedPaymentMethods.map((m) => m.toLowerCase());
  const refused = facts.tenderTypes.find((t) => allowed.length > 0 && !allowed.includes(t.toLowerCase()));
  if (refused) return `Moyen « ${refused} » non autorisé hors ligne (autorisés : ${policy.allowedPaymentMethods.join(", ")}).`;
  return null;
}
