import { apiFetch } from "@/core/apiClient";

// GED, documents, archivage & signature (Phase 45 §3-6, §13-31, §39, §45, §49).
// Bibliothèque distincte des documents ÉMIS (Phase 30, /documents) et des pièces jointes
// terrain (Phase 44, /attachments) — exposée sous /ged-documents pour éviter toute ambiguïté
// de route avec le contrôleur existant.

export interface GedDocument {
  id: string; establishmentId: string | null; name: string; documentType: string; category: string | null;
  tags: string[]; referenceType: string | null; referenceId: string | null; customerId: string | null;
  supplierId: string | null; fiscalYear: number | null; documentDate: string | null; status: string;
  currentVersionNo: number | null; locked: boolean; lockedReason: string | null; legalHold: boolean;
  legalHoldReason: string | null; retentionUntil: string | null; deletionRequestedAt: string | null;
  createdAt: string; updatedAt: string;
}
export interface UploadResult { document: GedDocument; potentialDuplicateOf: string | null }
export interface DocumentVersion { id: string; versionNo: number; contentType: string; sizeBytes: number; sha256Hash: string; changeReason: string; ocrStatus: string; createdAt: string }
export interface DocumentLink { id: string; linkedType: string; linkedId: string; createdAt: string }
export interface DocumentShare { id: string; token: string; allowDownload: boolean; expiresAt: string; revokedAt: string | null; createdAt: string }
export interface OcrResult { documentId: string; versionNo: number; status: string; text: string | null; confidence: number | null }
export interface Signer { id: string; sequenceNo: number; signerType: string; userId: string | null; externalName: string | null; externalEmail: string | null; role: string | null; status: string; dueAt: string | null; signedAt: string | null; declinedReason: string | null }
export interface SignatureRequest { id: string; documentId: string; status: string; completedAt: string | null; signers: Signer[]; createdAt: string }
export interface RetentionPolicy { id: string; isSystemDefault: boolean; documentType: string | null; retentionYears: number; legalBasis: string }
export interface GedAuditEntry { id: string; tableName: string; action: string; userId: string | null; occurredAt: string }

const q = (params: Record<string, string | number | boolean | undefined>) => {
  const usp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== "") usp.set(k, String(v));
  const s = usp.toString();
  return s ? `?${s}` : "";
};

// --- §7, §16 Documents & versions ---
export const searchDocuments = (filter: { text?: string; documentType?: string; status?: string; tags?: string[] } = {}) =>
  apiFetch<GedDocument[]>(`/ged-documents/search${q({ text: filter.text, documentType: filter.documentType, status: filter.status })}`);
export const getDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}`);
export const createDocument = (body: { name: string; documentType: string; category?: string | null; tags?: string[]; contentBase64: string; contentType: string }) =>
  apiFetch<UploadResult>("/ged-documents", { method: "POST", body: JSON.stringify(body) });
export const addVersion = (id: string, body: { contentBase64: string; contentType: string; changeReason?: string }) =>
  apiFetch<UploadResult>(`/ged-documents/${id}/versions`, { method: "POST", body: JSON.stringify(body) });
export const listVersions = (id: string) => apiFetch<DocumentVersion[]>(`/ged-documents/${id}/versions`);

// --- §23-24 Workflow ---
export const listWorkflowQueue = () => apiFetch<GedDocument[]>("/document-workflows");
export const submitForReview = (documentId: string) => apiFetch<void>("/document-workflows", { method: "POST", body: JSON.stringify(documentId) });
export const approveDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/approve`, { method: "POST" });
export const rejectDocument = (id: string, reason: string) => apiFetch<GedDocument>(`/ged-documents/${id}/reject`, { method: "POST", body: JSON.stringify(reason) });

// --- §18 Verrouillage ---
export const lockDocument = (id: string, reason: string) => apiFetch<GedDocument>(`/ged-documents/${id}/lock`, { method: "POST", body: JSON.stringify(reason) });
export const unlockDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/unlock`, { method: "POST" });

// --- §27 Archivage ---
export const archiveDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/archive`, { method: "POST" });
export const restoreDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/restore`, { method: "POST" });

// --- §31 Partage ---
export const shareDocument = (id: string, body: { expiresAt: string; allowDownload: boolean; password?: string | null }) =>
  apiFetch<DocumentShare>(`/ged-documents/${id}/share`, { method: "POST", body: JSON.stringify(body) });
export const revokeShare = (shareId: string) => apiFetch<void>(`/document-shares/${shareId}/revoke`, { method: "POST" });

// --- §11-12 OCR ---
export const runOcr = (documentId: string) => apiFetch<OcrResult>(`/ged-documents/ocr?documentId=${documentId}`, { method: "POST" });

// --- §19-22 Signature ---
export const listSignatureRequests = (status?: string) => apiFetch<SignatureRequest[]>(`/signature-requests${status ? `?status=${status}` : ""}`);
export const requestSignature = (documentId: string, signers: Array<{ signerType: "user" | "external"; userId?: string; externalName?: string; externalEmail?: string; role?: string }>) =>
  apiFetch<SignatureRequest>(`/ged-documents/${documentId}/signature`, { method: "POST", body: JSON.stringify({ signers }) });
export const signAs = (requestId: string, signerId: string, note?: string) =>
  apiFetch<SignatureRequest>(`/signature-requests/${requestId}/signers/${signerId}/sign`, { method: "POST", body: JSON.stringify({ note }) });
export const declineAs = (requestId: string, signerId: string, reason: string) =>
  apiFetch<SignatureRequest>(`/signature-requests/${requestId}/signers/${signerId}/decline`, { method: "POST", body: JSON.stringify(reason) });

// --- §27-29 Rétention & legal hold ---
export const listRetentionPolicies = () => apiFetch<RetentionPolicy[]>("/document-retention-policies");
export const saveRetentionPolicy = (body: { documentType?: string | null; retentionYears: number; legalBasis: string }) =>
  apiFetch<RetentionPolicy>("/document-retention-policies", { method: "POST", body: JSON.stringify(body) });
export const setLegalHold = (id: string, hold: boolean, reason?: string) =>
  apiFetch<void>(`/ged-documents/${id}/legal-hold?hold=${hold}`, { method: "POST", body: JSON.stringify(reason ?? null) });
export const listEligibleForDeletion = () => apiFetch<GedDocument[]>("/ged-documents/eligible-for-deletion");
export const requestDeletion = (id: string, reason: string) => apiFetch<void>(`/ged-documents/${id}/request-deletion`, { method: "POST", body: JSON.stringify(reason) });
export const confirmDeletion = (id: string) => apiFetch<void>(`/ged-documents/${id}/confirm-deletion`, { method: "POST" });

// --- §45 Audit ---
export const listGedAudit = (documentId?: string) => apiFetch<GedAuditEntry[]>(`/document-audit${documentId ? `?documentId=${documentId}` : ""}`);

// --- Utilitaire : lire un fichier local en base64 (§7 upload) ---
export function readFileAsBase64(file: File): Promise<{ base64: string; contentType: string }> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const result = reader.result as string;
      const base64 = result.split(",")[1] ?? "";
      resolve({ base64, contentType: file.type || "application/pdf" });
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}
