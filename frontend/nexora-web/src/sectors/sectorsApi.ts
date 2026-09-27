import { apiFetch } from "@/core/apiClient";

export interface ModuleState {
  key: string;
  enabled: boolean;
}

export interface TerminologyEntry {
  key: string;
  locale: string;
  label: string;
}

export interface SectorConfig {
  companyId: string;
  primaryActivity: string;
  activities: string[];
  modules: ModuleState[];
  terminology: TerminologyEntry[];
}

export interface CustomField {
  id: string;
  entityType: string;
  fieldKey: string;
  label: string;
  dataType: string;
  isRequired: boolean;
  options: string[];
  position: number;
  status: string;
}

export const ACTIVITIES = [
  "supermarket", "grocery", "pharmacy", "restaurant", "boutique", "hotel",
  "hardware_store", "wholesaler", "distributor", "services", "production",
  "ecommerce", "agriculture", "other",
] as const;

export const MODULES = [
  "pharmacy", "restaurant", "retail", "wholesale", "services", "production",
  "boutique", "hotel", "ecommerce", "agriculture", "loyalty", "delivery",
  "appointments", "custom_fields", "generic",
] as const;

export const getSectorConfig = () => apiFetch<SectorConfig>("/sectors/config");

export const setActivities = (activities: string[]) =>
  apiFetch<void>("/sectors/activities", { method: "PUT", body: JSON.stringify({ activities }) });

export const setModule = (moduleKey: string, enabled: boolean) =>
  apiFetch<void>("/sectors/modules", { method: "PUT", body: JSON.stringify({ moduleKey, enabled }) });

export const listCustomFields = (entityType: string) =>
  apiFetch<CustomField[]>(`/sectors/custom-fields/${entityType}`);

export const defineCustomField = (body: {
  entityType: string;
  fieldKey: string;
  label: string;
  dataType: string;
  isRequired: boolean;
  options?: string[];
}) => apiFetch<string>("/sectors/custom-fields", { method: "POST", body: JSON.stringify(body) });
