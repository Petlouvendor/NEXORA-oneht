import { apiDownload, apiFetch } from "@/core/apiClient";
import { saveFile } from "@/shared/printApi";

// GED, documents, archivage & signature (Phase 45 ; finalisation phase 10 : workflow de signature complet).
// Bibliothèque distincte des documents ÉMIS (Phase 30, /documents) et des pièces jointes terrain
// (Phase 44, /attachments) — exposée sous /ged-documents.

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
export interface DocumentShare { id: string; token: string; allowDownload: boolean; expiresAt: string; revokedAt: string | null; createdAt: string }
export interface OcrResult { documentId: string; versionNo: number; status: string; text: string | null; confidence: number | null }
export interface Signer {
  id: string; sequenceNo: number; signerType: "user" | "external"; userId: string | null; externalName: string | null;
  externalEmail: string | null; role: string | null; status: string; dueAt: string | null; signedAt: string | null;
  declinedReason: string | null; userName: string | null; recordedByName: string | null; declinedAt: string | null;
}
export interface SignatureRequest {
  id: string; documentId: string; status: string; completedAt: string | null; signers: Signer[]; createdAt: string;
  documentName: string | null; versionNo: number | null; requestedById: string | null; requestedByName: string | null;
  message: string | null; cancelledReason: string | null; cancelledByName: string | null;
  /** Signataire dont c'est le tour. */
  currentSignerId: string | null;
  /** Vrai si l'utilisateur courant doit répondre (signataire désigné, ou recueille la réponse d'un externe). */
  isMyTurn: boolean;
}
export interface SignerCandidate { id: string; fullName: string; email: string | null }
export interface SignerInput {
  signerType: "user" | "external"; userId?: string | null; externalName?: string | null; externalEmail?: string | null;
  role?: string | null; dueAt?: string | null;
}
export interface RetentionPolicy { id: string; isSystemDefault: boolean; documentType: string | null; retentionYears: number; legalBasis: string }
export interface GedAuditEntry { id: string; tableName: string; action: string; userId: string | null; occurredAt: string }

export const DOCUMENT_TYPES: Record<string, string> = {
  invoice_sale: "Facture de vente", invoice_purchase: "Facture d'achat", receipt: "Reçu", credit_note: "Avoir",
  quote: "Devis", purchase_order: "Bon de commande", delivery_note: "Bon de livraison", contract: "Contrat",
  accounting_voucher: "Pièce comptable", expense_proof: "Justificatif de dépense", statement: "Relevé",
  tax_document: "Document fiscal", customer_document: "Document client", supplier_document: "Document fournisseur",
  internal: "Document interne", photo: "Photo", scan: "Scan", other: "Autre",
};
export const DOCUMENT_STATUS: Record<string, { label: string; tone?: "success" | "warning" | "error" | "info" }> = {
  draft: { label: "Brouillon", tone: "warning" },
  pending_review: { label: "En revue", tone: "warning" },
  approved: { label: "Approuvé", tone: "success" },
  signed: { label: "Signé", tone: "success" },
  archived: { label: "Archivé", tone: "info" },
  frozen: { label: "Figé", tone: "error" },
};
export const REQUEST_STATUS: Record<string, { label: string; tone?: "success" | "warning" | "error" | "info" }> = {
  pending: { label: "En cours", tone: "warning" },
  completed: { label: "Signé", tone: "success" },
  cancelled: { label: "Annulé / refusé", tone: "error" },
  expired: { label: "Expiré", tone: "error" },
};
export const SIGNER_STATUS: Record<string, { label: string; tone?: "success" | "warning" | "error" | "info" }> = {
  pending: { label: "En attente", tone: "warning" },
  signed: { label: "Signé", tone: "success" },
  declined: { label: "Refusé", tone: "error" },
};
/** Formats acceptés à l'import (contrôlés aussi côté serveur). */
export const ACCEPTED_FILES = ".pdf,.jpg,.jpeg,.png,.webp,.tiff,.docx,.xlsx,.csv";
export const MAX_FILE_BYTES = 25 * 1024 * 1024;

const q = (params: Record<string, string | number | boolean | undefined | null>) => {
  const usp = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null && v !== "") usp.set(k, String(v));
  const s = usp.toString();
  return s ? `?${s}` : "";
};
const post = (body?: unknown) => ({ method: "POST", ...(body === undefined ? {} : { body: JSON.stringify(body) }) });

// --- Documents & versions ---
export const searchDocuments = (filter: { text?: string; documentType?: string; status?: string } = {}) =>
  apiFetch<GedDocument[]>(`/ged-documents/search${q(filter)}`);
export const getDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}`);
export const createDocument = (body: { name: string; documentType: string; category?: string | null; tags?: string[]; contentBase64: string; contentType: string }) =>
  apiFetch<UploadResult>("/ged-documents", post(body));
export const addVersion = (id: string, body: { contentBase64: string; contentType: string; changeReason: string }) =>
  apiFetch<UploadResult>(`/ged-documents/${id}/versions`, post(body));
export const listVersions = (id: string) => apiFetch<DocumentVersion[]>(`/ged-documents/${id}/versions`);
/** Télécharge une version (la courante si `versionNo` est omis) — fichier réel, authentifié. */
export async function downloadVersion(doc: { id: string; name: string }, versionNo?: number) {
  const { blob, filename } = await apiDownload(`/ged-documents/${doc.id}/content${q({ versionNo })}`);
  saveFile(blob, filename ?? fileName(doc.name, blob.type, versionNo));
}
const EXT: Record<string, string> = {
  "application/pdf": "pdf", "image/jpeg": "jpg", "image/png": "png", "image/webp": "webp", "image/tiff": "tiff", "text/csv": "csv",
  "application/vnd.openxmlformats-officedocument.wordprocessingml.document": "docx",
  "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet": "xlsx",
};
export const fileName = (name: string, contentType: string, versionNo?: number) =>
  `${name.replace(/[\\/:*?"<>|]+/g, "_")}${versionNo ? ` (v${versionNo})` : ""}.${EXT[contentType] ?? "bin"}`;

// --- Revue ---
export const listWorkflowQueue = () => apiFetch<GedDocument[]>("/document-workflows");
export const submitForReview = (documentId: string) => apiFetch<void>("/document-workflows", post(documentId));
export const approveDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/approve`, post());
export const rejectDocument = (id: string, reason: string) => apiFetch<GedDocument>(`/ged-documents/${id}/reject`, post(reason));

// --- Verrouillage, archivage ---
export const lockDocument = (id: string, reason: string) => apiFetch<GedDocument>(`/ged-documents/${id}/lock`, post(reason));
export const unlockDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/unlock`, post());
export const archiveDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/archive`, post());
export const restoreDocument = (id: string) => apiFetch<GedDocument>(`/ged-documents/${id}/restore`, post());

// --- Partage ---
export const shareDocument = (id: string, body: { expiresAt: string; allowDownload: boolean; password?: string | null }) =>
  apiFetch<DocumentShare>(`/ged-documents/${id}/share`, post(body));
export const revokeShare = (shareId: string) => apiFetch<void>(`/document-shares/${shareId}/revoke`, post());
export const shareUrl = (token: string) => `${window.location.origin}/api/v1/shared/${token}`;

// --- OCR ---
export const runOcr = (documentId: string) => apiFetch<OcrResult>(`/ged-documents/ocr?documentId=${documentId}`, post());

// --- Signature ---
export const listSignatureRequests = (filter: { status?: string; mine?: boolean; documentId?: string } = {}) =>
  apiFetch<SignatureRequest[]>(`/signature-requests${q(filter)}`);
export const listSignatureCandidates = () => apiFetch<SignerCandidate[]>("/signature-candidates");
export const requestSignature = (documentId: string, signers: SignerInput[], message: string | null) =>
  apiFetch<SignatureRequest>(`/ged-documents/${documentId}/signature`, post({ signers, message }));
export const signAs = (requestId: string, signerId: string, note: string | null) =>
  apiFetch<SignatureRequest>(`/signature-requests/${requestId}/signers/${signerId}/sign`, post({ note }));
export const declineAs = (requestId: string, signerId: string, reason: string) =>
  apiFetch<SignatureRequest>(`/signature-requests/${requestId}/signers/${signerId}/decline`, post(reason));
export const cancelSignature = (requestId: string, reason: string) =>
  apiFetch<void>(`/signature-requests/${requestId}/cancel`, post(reason));
/** Nom affiché d'un signataire. */
export const signerName = (s: Signer) => (s.signerType === "external" ? `${s.externalName ?? "?"} (externe)` : s.userName ?? "Utilisateur");

// --- Rétention & legal hold ---
export const listRetentionPolicies = () => apiFetch<RetentionPolicy[]>("/document-retention-policies");
export const saveRetentionPolicy = (body: { documentType: string | null; retentionYears: number; legalBasis: string }) =>
  apiFetch<RetentionPolicy>("/document-retention-policies", post(body));
export const setLegalHold = (id: string, hold: boolean, reason: string | null) =>
  apiFetch<void>(`/ged-documents/${id}/legal-hold?hold=${hold}`, post(reason));
export const listEligibleForDeletion = () => apiFetch<GedDocument[]>("/ged-documents/eligible-for-deletion");
export const requestDeletion = (id: string, reason: string) => apiFetch<void>(`/ged-documents/${id}/request-deletion`, post(reason));
export const confirmDeletion = (id: string) => apiFetch<void>(`/ged-documents/${id}/confirm-deletion`, post());
export const cancelDeletion = (id: string) => apiFetch<void>(`/ged-documents/${id}/cancel-deletion`, post());

// --- Audit ---
export const listGedAudit = (documentId?: string) => apiFetch<GedAuditEntry[]>(`/document-audit${q({ documentId })}`);

/** Lit un fichier local en base64 (sans l'en-tête data:). */
export function readFileAsBase64(file: File): Promise<{ base64: string; contentType: string }> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve({ base64: String(reader.result).split(",")[1] ?? "", contentType: file.type || "application/octet-stream" });
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

/** Contrôle d'une liste de signataires avant envoi (le serveur reste l'autorité). */
export function validateSigners(signers: SignerInput[]): string | null {
  if (signers.length === 0) return "Ajoutez au moins un signataire.";
  if (signers.length > 10) return "Dix signataires au plus.";
  const seen = new Set<string>();
  for (const [i, s] of signers.entries()) {
    const n = i + 1;
    if (s.signerType === "user") {
      if (!s.userId) return `Signataire ${n} : choisissez l'utilisateur.`;
      if (seen.has(s.userId)) return `Signataire ${n} : déjà présent dans la liste.`;
      seen.add(s.userId);
    } else {
      if (!s.externalName?.trim()) return `Signataire ${n} : nom requis.`;
      if (s.externalEmail?.trim() && !s.externalEmail.includes("@")) return `Signataire ${n} : e-mail invalide.`;
    }
    if (s.dueAt && new Date(s.dueAt).getTime() <= Date.now()) return `Signataire ${n} : l'échéance doit être dans le futur.`;
  }
  return null;
}
