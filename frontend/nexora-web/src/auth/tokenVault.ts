import { config } from "@/core/config";
import { secureGet, secureRemove, secureSet, secureStoreAvailable } from "@/shared/secureStore";

/**
 * Conservation du jeton d'accès (résumé fonctionnel §15, recette BUG-012) : chiffré dans le coffre
 * du navigateur, jamais en clair dans `localStorage`. Sans contexte sécurisé (HTTP hors localhost),
 * repli sur `sessionStorage` : le jeton disparaît à la fermeture du navigateur (nouvelle connexion
 * demandée) plutôt que de rester écrit en clair sur le disque.
 */
const SECURE_NAME = "auth.token";

function dropLegacy(): string | null {
  try {
    const legacy = localStorage.getItem(config.tokenStorageKey);
    if (legacy !== null) localStorage.removeItem(config.tokenStorageKey);
    return legacy;
  } catch {
    return null;
  }
}

export async function loadToken(): Promise<string | null> {
  const legacy = dropLegacy();   // ancienne copie en clair : reprise une fois, puis effacée
  if (secureStoreAvailable()) {
    if (legacy) {
      await saveToken(legacy);
      return legacy;
    }
    return secureGet<string>(SECURE_NAME).catch(() => null);
  }
  try {
    if (legacy) sessionStorage.setItem(config.tokenStorageKey, legacy);
    return sessionStorage.getItem(config.tokenStorageKey);
  } catch {
    return legacy;
  }
}

export async function saveToken(token: string): Promise<void> {
  dropLegacy();
  if (secureStoreAvailable()) {
    try {
      await secureSet(SECURE_NAME, token);
      return;
    } catch {
      /* coffre indisponible (quota, IndexedDB bloqué) : repli de session ci-dessous */
    }
  }
  try { sessionStorage.setItem(config.tokenStorageKey, token); } catch { /* jeton gardé en mémoire seulement */ }
}

export async function clearToken(): Promise<void> {
  dropLegacy();
  try { sessionStorage.removeItem(config.tokenStorageKey); } catch { /* ignore */ }
  if (secureStoreAvailable()) await secureRemove(SECURE_NAME).catch(() => undefined);
}
