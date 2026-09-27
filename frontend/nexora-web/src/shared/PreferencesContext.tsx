import {
  createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState,
  type ReactNode,
} from "react";
import { useAuth } from "@/auth/AuthContext";
import { setFormatLocale } from "@/shared/format";
import { setLocale } from "@/shared/i18n";
import {
  DEFAULT_PREFERENCES, getBranding, getPreferences, saveBranding, savePreferences,
  type CompanyBranding, type UserPreferences,
} from "@/shared/preferencesApi";

const LS_KEY = "nexora.prefs";

function readCache(): UserPreferences {
  try {
    const raw = localStorage.getItem(LS_KEY);
    return raw ? { ...DEFAULT_PREFERENCES, ...JSON.parse(raw) } : DEFAULT_PREFERENCES;
  } catch {
    return DEFAULT_PREFERENCES;
  }
}

interface PrefApi {
  prefs: UserPreferences;
  branding: CompanyBranding | null;
  update: (patch: Partial<UserPreferences>) => void;
  toggleTheme: () => void;
  toggleFavorite: (path: string) => void;
  isFavorite: (path: string) => boolean;
  updateBranding: (patch: Partial<Omit<CompanyBranding, "companyId">>) => Promise<CompanyBranding>;
}

const Ctx = createContext<PrefApi | null>(null);

/** Applique le hex d'accent en dérivant l'encre (§35 : jamais dégrader la lisibilité). */
function applyAccent(hex: string | null) {
  const root = document.documentElement;
  if (!hex || !/^#[0-9A-Fa-f]{6}$/.test(hex)) {
    root.style.removeProperty("--nx-brand");
    root.style.removeProperty("--nx-brand-strong");
    root.style.removeProperty("--nx-brand-ink");
    return;
  }
  const r = parseInt(hex.slice(1, 3), 16), g = parseInt(hex.slice(3, 5), 16), b = parseInt(hex.slice(5, 7), 16);
  const lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
  root.style.setProperty("--nx-brand", hex);
  root.style.setProperty("--nx-brand-strong", hex);
  root.style.setProperty("--nx-brand-ink", lum > 0.55 ? "#04211d" : "#ffffff");
}

export function PreferencesProvider({ children }: { children: ReactNode }) {
  const { token, ready, activeCompanyId } = useAuth();
  const [prefs, setPrefs] = useState<UserPreferences>(readCache);
  const [branding, setBranding] = useState<CompanyBranding | null>(null);
  const saveTimer = useRef<number | undefined>(undefined);

  // Applique le thème / la densité / le mouvement / l'accessibilité au document (avant peinture).
  useLayoutEffect(() => {
    const root = document.documentElement;
    root.dataset.theme = prefs.theme;
    root.dataset.density = prefs.density;
    root.dataset.motion = prefs.reducedMotion ? "reduced" : "full";
    root.dataset.contrast = prefs.highContrast ? "high" : "normal";
    const scale = Math.min(1.5, Math.max(0.85, prefs.textScale || 1));
    root.style.setProperty("--nx-text-scale", String(scale));
    setFormatLocale(prefs.locale);
    setLocale(prefs.locale);
  }, [prefs.theme, prefs.density, prefs.reducedMotion, prefs.highContrast, prefs.textScale, prefs.locale]);

  useLayoutEffect(() => { applyAccent(branding?.accentColor ?? null); }, [branding?.accentColor]);

  // Charge les préférences serveur à l'authentification.
  useEffect(() => {
    if (!ready || !token) return;
    let cancelled = false;
    getPreferences()
      .then((p) => { if (!cancelled) { setPrefs(p); localStorage.setItem(LS_KEY, JSON.stringify(p)); } })
      .catch(() => { /* on garde le cache local */ });
    return () => { cancelled = true; };
  }, [ready, token]);

  // Recharge la personnalisation quand l'entreprise active change.
  useEffect(() => {
    if (!ready || !token || !activeCompanyId) { setBranding(null); return; }
    let cancelled = false;
    getBranding().then((b) => !cancelled && setBranding(b)).catch(() => !cancelled && setBranding(null));
    return () => { cancelled = true; };
  }, [ready, token, activeCompanyId]);

  const persist = useCallback((next: UserPreferences) => {
    localStorage.setItem(LS_KEY, JSON.stringify(next));
    if (!token) return;
    window.clearTimeout(saveTimer.current);
    saveTimer.current = window.setTimeout(() => { void savePreferences(next).catch(() => {}); }, 400);
  }, [token]);

  const update = useCallback((patch: Partial<UserPreferences>) => {
    setPrefs((cur) => {
      const next = { ...cur, ...patch };
      persist(next);
      return next;
    });
  }, [persist]);

  const toggleTheme = useCallback(() => {
    update({ theme: prefs.theme === "dark" ? "light" : "dark" });
  }, [prefs.theme, update]);

  const toggleFavorite = useCallback((path: string) => {
    setPrefs((cur) => {
      const has = cur.favorites.includes(path);
      const next = { ...cur, favorites: has ? cur.favorites.filter((f) => f !== path) : [...cur.favorites, path] };
      persist(next);
      return next;
    });
  }, [persist]);

  const updateBranding = useCallback(async (patch: Partial<Omit<CompanyBranding, "companyId">>) => {
    const saved = await saveBranding(patch);
    setBranding(saved);
    return saved;
  }, []);

  const value = useMemo<PrefApi>(() => ({
    prefs, branding, update, toggleTheme, toggleFavorite,
    isFavorite: (p) => prefs.favorites.includes(p),
    updateBranding,
  }), [prefs, branding, update, toggleTheme, toggleFavorite, updateBranding]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function usePreferences(): PrefApi {
  const ctx = useContext(Ctx);
  if (!ctx) throw new Error("usePreferences doit être utilisé dans <PreferencesProvider>");
  return ctx;
}
