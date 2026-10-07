import { lazy, type ComponentType } from "react";

const RELOAD_FLAG = "nexora.chunk-reload";

/**
 * Écran chargé à la demande (finalisation phase 20, §49 « lazy loading ») : l'interface n'est plus un seul fichier
 * de 1,4 Mo téléchargé en entier au démarrage — chaque écran arrive quand on l'ouvre.
 *
 * Après un déploiement, les anciens noms de fichiers n'existent plus : le chargement échoue. La page est alors
 * rechargée une seule fois (nouvelle version), jamais en boucle ; un second échec remonte l'erreur.
 */
export function lazyPage<M, K extends keyof M>(load: () => Promise<M>, name: K): ComponentType<Record<string, unknown>> {
  return lazy(async () => {
    try {
      const module = await load();
      sessionStorage.removeItem(RELOAD_FLAG);
      return { default: module[name] as unknown as ComponentType<Record<string, unknown>> };
    } catch (error) {
      let alreadyReloaded = false;
      try { alreadyReloaded = sessionStorage.getItem(RELOAD_FLAG) === "1"; } catch { /* stockage indisponible */ }
      if (!alreadyReloaded && navigator.onLine) {
        try { sessionStorage.setItem(RELOAD_FLAG, "1"); } catch { /* stockage indisponible */ }
        window.location.reload();
        return new Promise<never>(() => undefined);   // la page se recharge
      }
      throw error;
    }
  });
}
