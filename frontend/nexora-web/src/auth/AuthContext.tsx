import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { apiFetch, configureApiClient } from "@/core/apiClient";
import { config } from "@/core/config";
import type { AuthTokenResponse, CurrentUser } from "@/auth/types";
import { revokeServerSession } from "@/auth/accountApi";
import { clearToken, loadToken, saveToken } from "@/auth/tokenVault";

interface AuthState {
  user: CurrentUser | null;
  token: string | null;
  activeCompanyId: string | null;
  ready: boolean;
  /** Connexion en attente du deuxième facteur (Phase 33 §8). */
  mfaPending: boolean;
  login: (email: string, password: string, companyId?: string) => Promise<void>;
  verifyMfa: (code: string) => Promise<void>;
  cancelMfa: () => void;
  logout: () => void;
  setActiveCompany: (companyId: string) => void;
  hasPermission: (code: string) => boolean;
}

const AuthCtx = createContext<AuthState | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  // Jeton conservé chiffré (BUG-012, tokenVault) : lu de façon asynchrone au démarrage ; `ready`
  // reste faux tant qu'il n'est pas chargé, l'écran ne redirige donc pas trop tôt vers la connexion.
  const [token, setToken] = useState<string | null>(null);
  const [tokenLoaded, setTokenLoaded] = useState(false);
  const tokenRef = useRef<string | null>(null);
  tokenRef.current = token;
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [activeCompanyId, setActive] = useState<string | null>(() => localStorage.getItem(config.companyStorageKey));
  const [ready, setReady] = useState(false);
  const [mfaToken, setMfaToken] = useState<string | null>(null);

  const logout = useCallback(() => {
    // Recette Phase 55 AUTH-004 : la session serveur est révoquée, plus seulement le navigateur vidé.
    void revokeServerSession(tokenRef.current);
    void clearToken();
    localStorage.removeItem(config.companyStorageKey);
    setToken(null);
    setUser(null);
    setActive(null);
    setMfaToken(null);
  }, []);

  configureApiClient({
    token: () => token,
    company: () => activeCompanyId,
    onUnauthorized: logout,
  });

  const setActiveCompany = useCallback((companyId: string) => {
    localStorage.setItem(config.companyStorageKey, companyId);
    setActive(companyId);
  }, []);

  const acceptToken = useCallback((res: AuthTokenResponse, companyId?: string) => {
    void saveToken(res.accessToken);
    setMfaToken(null);
    setToken(res.accessToken);
    setUser(res.user);
    const first = companyId ?? res.user.companies[0]?.companyId ?? null;
    if (first) setActiveCompany(first);
  }, [setActiveCompany]);

  const login = useCallback(async (email: string, password: string, companyId?: string) => {
    const res = await apiFetch<AuthTokenResponse>("/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password, companyId: companyId ?? null }),
    });
    if (res.mfaRequired && res.mfaToken) {
      setMfaToken(res.mfaToken);
      return;
    }
    acceptToken(res, companyId);
  }, [acceptToken]);

  const verifyMfa = useCallback(async (code: string) => {
    if (!mfaToken) throw new Error("Aucune vérification MFA en attente.");
    const res = await apiFetch<AuthTokenResponse>("/auth/mfa/verify", {
      method: "POST",
      body: JSON.stringify({ mfaToken, code }),
    });
    acceptToken(res);
  }, [mfaToken, acceptToken]);

  const cancelMfa = useCallback(() => setMfaToken(null), []);

  useEffect(() => {
    let cancelled = false;
    loadToken()
      .then((stored) => { if (!cancelled) setToken((current) => current ?? stored); })
      .catch(() => { /* coffre illisible : nouvelle connexion demandée */ })
      .finally(() => { if (!cancelled) setTokenLoaded(true); });
    return () => { cancelled = true; };
  }, []);

  // Réhydratation : recharge le profil courant si un jeton est présent.
  useEffect(() => {
    let cancelled = false;
    interface MeResponse {
      userId: string;
      permissions: string[];
      memberships: Array<{ companyId: string; companyName: string; groupId: string | null; roles: string[] }>;
    }
    async function hydrate() {
      if (!tokenLoaded) return;
      if (!token) { setReady(true); return; }
      try {
        const me = await apiFetch<MeResponse>("/me");
        if (!cancelled) {
          setUser((prev) => ({
            id: me.userId,
            email: prev?.email ?? "",
            fullName: prev?.fullName ?? null,
            mfaEnabled: prev?.mfaEnabled ?? false,
            companies: me.memberships.map((m) => ({
              companyId: m.companyId, companyName: m.companyName, groupId: m.groupId, roles: m.roles,
            })),
            permissions: me.permissions,
          }));
        }
      } catch {
        if (!cancelled) logout();
      } finally {
        if (!cancelled) setReady(true);
      }
    }
    void hydrate();
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token, tokenLoaded]);

  const hasPermission = useCallback(
    (code: string) => user?.permissions.includes(code) ?? false,
    [user],
  );

  const value = useMemo<AuthState>(
    () => ({
      user, token, activeCompanyId, ready, mfaPending: mfaToken !== null,
      login, verifyMfa, cancelMfa, logout, setActiveCompany, hasPermission,
    }),
    [user, token, activeCompanyId, ready, mfaToken, login, verifyMfa, cancelMfa, logout, setActiveCompany, hasPermission],
  );

  return <AuthCtx.Provider value={value}>{children}</AuthCtx.Provider>;
}

export function useAuth(): AuthState {
  const ctx = useContext(AuthCtx);
  if (!ctx) throw new Error("useAuth doit être utilisé dans <AuthProvider>");
  return ctx;
}
