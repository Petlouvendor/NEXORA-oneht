import { apiFetch } from "@/core/apiClient";

/** Assistant d'installation (résumé fonctionnel §20). Les appels, sauf `status`, exigent le jeton
 *  d'installation affiché dans le journal de démarrage du serveur (en-tête `X-Setup-Token`). */

export interface SetupStatus {
  setupRequired: boolean;
  installed: boolean;
  restartRequired: boolean;
}

export interface SetupChoice {
  code: string;
  label: string;
}

export interface SetupOptions {
  activities: SetupChoice[];
  currencies: SetupChoice[];
  sslModes: string[];
}

export interface DatabaseSetup {
  host: string;
  port: number;
  database: string;
  username: string;
  password: string;
  sslMode: string;
}

export interface DatabaseTestResult {
  connected: boolean;
  serverVersion: string | null;
  databaseExists: boolean;
  alreadyInitialized: boolean;
  isSuperuser: boolean;
  canCreateRole: boolean;
  canCreateDatabase: boolean;
  ready: boolean;
  messages: string[];
}

export interface SetupCompany {
  name: string;
  activityType: string;
  currency: string;
}

export interface SetupAdmin {
  fullName: string;
  email: string;
  password: string;
}

export interface SetupSettings {
  requireCashSession: boolean;
  cashVarianceThreshold: number;
  maxDiscountPercent: number;
  allowNegativeStock: boolean;
}

export interface InstallRequest {
  database: DatabaseSetup;
  company: SetupCompany;
  admin: SetupAdmin;
  settings: SetupSettings;
}

export interface InstallResult {
  companyId: string;
  companyName: string;
  adminEmail: string;
  migrationsApplied: number;
  restartRequired: boolean;
}

const withToken = (token: string, body?: unknown): RequestInit => ({
  method: "POST",
  headers: { "X-Setup-Token": token.trim() },
  body: body === undefined ? undefined : JSON.stringify(body),
});

export const getSetupStatus = () => apiFetch<SetupStatus>("/setup/status");
export const getSetupOptions = () => apiFetch<SetupOptions>("/setup/options");
export const verifySetupToken = (token: string) => apiFetch<void>("/setup/verify-token", withToken(token));
export const testDatabase = (token: string, db: DatabaseSetup) =>
  apiFetch<DatabaseTestResult>("/setup/database/test", withToken(token, db));
export const installNexora = (token: string, request: InstallRequest) =>
  apiFetch<InstallResult>("/setup/install", withToken(token, request));

/** Règles du mot de passe administrateur — mêmes que l'API (PasswordPolicy). */
export function passwordProblem(password: string, email: string): string | null {
  if (password.length < 10) return "10 caractères minimum.";
  if (password.length > 128) return "128 caractères maximum.";
  if (!/\p{L}/u.test(password) || !/\d/.test(password)) return "Au moins une lettre et un chiffre.";
  const local = email.split("@")[0] ?? "";
  if (local.length >= 3 && password.toLowerCase().includes(local.toLowerCase()))
    return "Le mot de passe ne doit pas contenir l'identifiant du compte.";
  return null;
}
