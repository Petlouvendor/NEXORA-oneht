import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";

/** Route protégée : redirige vers /login si non authentifié (Design System §7). */
export function ProtectedRoute({ children, permission }: { children: ReactNode; permission?: string }) {
  const { token, ready, hasPermission } = useAuth();
  const location = useLocation();

  if (!ready) return <div style={{ padding: 24 }}>Chargement…</div>;
  if (!token) return <Navigate to="/login" replace state={{ from: location }} />;
  if (permission && !hasPermission(permission)) {
    return <div style={{ padding: 24 }} className="nx-error-text">Accès refusé — permission requise : {permission}</div>;
  }
  return <>{children}</>;
}

/**
 * Garde d'écran par permission (guide §27 : masquer un bouton ne vaut pas permission). Protège
 * l'accès par URL directe ; l'API refuse de toute façon les données (403) — c'est l'autorité.
 */
export function RequirePermission({ permission, children }: { permission?: string; children: ReactNode }) {
  const { hasPermission } = useAuth();
  if (permission && !hasPermission(permission)) {
    return (
      <div style={{ padding: 24 }} role="alert">
        <h1 style={{ marginTop: 0 }}>Accès refusé</h1>
        <p className="nx-error-text">Cet écran n'est pas accessible avec votre rôle.</p>
      </div>
    );
  }
  return <>{children}</>;
}
