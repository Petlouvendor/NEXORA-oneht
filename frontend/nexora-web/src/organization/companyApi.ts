import { apiFetch } from "@/core/apiClient";

export interface Company {
  id: string;
  groupId: string | null;
  name: string;
  legalName: string | null;
  status: string;
  mainActivityType: string;
  complexityLevel: string;
  deploymentMode: string;
  timezone: string;
  defaultCurrencyId: string;
}

export const listCompanies = () => apiFetch<Company[]>("/companies");
