import { apiFetch } from "@/core/apiClient";

// Configuration & adaptabilité (Phase 32 §6, §46-48, §52).

export interface SectorProfile {
  id: string;
  key: string;
  name: string;
  sectorType: string;
  version: number;
  status: string;
  isSystem: boolean;
  description: string | null;
  isOwn: boolean;
}

export interface ProfilePreviewItem {
  kind: string;
  key: string;
  label: string;
  alreadyPresent: boolean;
}

export interface ProfilePreview {
  profileKey: string;
  profileName: string;
  items: ProfilePreviewItem[];
  guardrails: string[];
}

export interface ApplyProfileResult {
  profileKey: string;
  dryRun: boolean;
  configVersionId: string | null;
  modulesApplied: number;
  fieldsCreated: number;
  terminologySet: number;
  settingsSet: number;
  warnings: string[];
}

export interface Guardrail {
  key: string;
  label: string;
  status: "pass" | "warn" | "fail";
  detail: string | null;
}

export interface GuardrailReport {
  canPublish: boolean;
  passed: number;
  failed: number;
  warnings: number;
  checks: Guardrail[];
  generatedAt: string;
}

export interface ConfigTransfer {
  id: string;
  kind: string;
  sourceCompanyId: string | null;
  sourceProfileKey: string | null;
  dryRun: boolean;
  configVersionId: string | null;
  summary: unknown;
  createdAt: string;
}

export const listSectorProfiles = () => apiFetch<SectorProfile[]>("/sector-profiles");
export const previewSectorProfile = (key: string) =>
  apiFetch<ProfilePreview>(`/sector-profiles/${key}/preview`, { method: "POST" });
export const applySectorProfile = (key: string, dryRun = false) =>
  apiFetch<ApplyProfileResult>(`/sector-profiles/${key}/apply`, {
    method: "POST",
    body: JSON.stringify({ dryRun }),
  });

export const validateConfig = () => apiFetch<GuardrailReport>("/configurations/validate");
export const exportConfig = () => apiFetch<unknown>("/configurations/export");
export const cloneConfig = (targetCompanyId: string) =>
  apiFetch<ApplyProfileResult>("/configurations/clone", {
    method: "POST",
    body: JSON.stringify({ targetCompanyId }),
  });
export const configHistory = () => apiFetch<ConfigTransfer[]>("/configurations/history");
