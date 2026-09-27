export const config = {
  apiBaseUrl: import.meta.env.VITE_API_BASE_URL ?? "/api/v1",
  tokenStorageKey: "nexora.token",
  companyStorageKey: "nexora.activeCompany",
} as const;
