import { apiFetch } from "@/core/apiClient";

export type ThemeChoice = "system" | "light" | "dark";
export type Density = "comfortable" | "compact";
export type DashboardMode = "summary" | "detailed";

export interface UserPreferences {
  theme: ThemeChoice;
  density: Density;
  locale: string;
  reducedMotion: boolean;
  dashboardMode: DashboardMode;
  favorites: string[];
  tableColumns: Record<string, unknown>;
  notificationPrefs: Record<string, unknown>;
  /** Accessibilité (Phase 40 §31-33). */
  highContrast: boolean;
  /** Échelle du texte 0.85 – 1.50 (Phase 40 §31). */
  textScale: number;
  /** Fuseau horaire de l'utilisateur (Phase 40 §34). */
  timezone: string;
}

export interface CompanyBranding {
  companyId: string;
  displayName: string | null;
  accentColor: string | null;
  logoUrl: string | null;
  receiptFormat: "standard" | "compact" | "detailed";
  documentFooter: string | null;
  dateFormat: string;
  numberLocale: string;
  defaultLocale: string;
}

export const DEFAULT_PREFERENCES: UserPreferences = {
  theme: "system",
  density: "comfortable",
  locale: "fr-FR",
  reducedMotion: false,
  dashboardMode: "summary",
  favorites: [],
  tableColumns: {},
  notificationPrefs: {},
  highContrast: false,
  textScale: 1,
  timezone: "America/Port-au-Prince",
};

export const getPreferences = () => apiFetch<UserPreferences>("/user-preferences");

export const savePreferences = (patch: Partial<UserPreferences>) =>
  apiFetch<UserPreferences>("/user-preferences", { method: "PUT", body: JSON.stringify(patch) });

export const getBranding = () => apiFetch<CompanyBranding>("/companies/branding");

export const saveBranding = (patch: Partial<Omit<CompanyBranding, "companyId">>) =>
  apiFetch<CompanyBranding>("/companies/branding", { method: "PUT", body: JSON.stringify(patch) });
