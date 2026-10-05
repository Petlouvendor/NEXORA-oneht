import { apiFetch } from "@/core/apiClient";
import { config } from "@/core/config";

// Comptes & mots de passe (recette Phase 55 : AUTH-004, AUTH-007, AUTH-008).

/** Mot de passe oublié : le serveur répond 202 que le compte existe ou non. */
export const forgotPassword = (email: string) =>
  apiFetch<void>("/auth/password/forgot", { method: "POST", body: JSON.stringify({ email }) });

export const resetPassword = (token: string, newPassword: string) =>
  apiFetch<void>("/auth/password/reset", { method: "POST", body: JSON.stringify({ token, newPassword }) });

/** Change le mot de passe ; toutes les sessions tombent ensuite, y compris celle-ci. */
export const changePassword = (currentPassword: string, newPassword: string) =>
  apiFetch<void>("/me/password", { method: "POST", body: JSON.stringify({ currentPassword, newPassword }) });

/**
 * Révoque côté serveur la session portée par ce jeton (AUTH-004). Volontairement hors
 * d'`apiFetch` : un 401 déclencherait `onUnauthorized` → `logout` → nouvel appel, en boucle.
 * Meilleur effort : la déconnexion locale a lieu quoi qu'il arrive.
 */
export async function revokeServerSession(token: string | null): Promise<void> {
  if (!token) {
    try { await fetch(`${config.apiBaseUrl}/auth/logout`, { method: "POST", credentials: "same-origin", keepalive: true }); } catch { /* hors ligne */ }
    return;
  }
  try {
    await fetch(`${config.apiBaseUrl}/me/logout`, {
      method: "POST",
      headers: { Authorization: `Bearer ${token}`, Accept: "application/json" },
      keepalive: true,
    });
  } catch {
    /* hors ligne : le jeton expirera de lui-même */
  }
  try {
    // Le jeton d'accès a pu expirer : la session est aussi fermée par son cookie de rafraîchissement.
    await fetch(`${config.apiBaseUrl}/auth/logout`, { method: "POST", credentials: "same-origin", keepalive: true });
  } catch {
    /* hors ligne */
  }
}
