import type { ReactNode } from "react";
import { createPortal } from "react-dom";

/**
 * Couche supérieure : une fenêtre qui peut s'ouvrir par-dessus un tiroir ou une autre fenêtre (confirmation
 * forte, saisie demandée en cours d'action). Rendue en fin de page, au-dessus des tiroirs (niveau 60) — sinon
 * le voile du tiroir capte les clics et la fenêtre est inutilisable.
 */
export function TopLayer({ children }: { children: ReactNode }) {
  if (typeof document === "undefined") return <>{children}</>;
  return createPortal(<div style={{ position: "relative", zIndex: 90 }}>{children}</div>, document.body);
}
