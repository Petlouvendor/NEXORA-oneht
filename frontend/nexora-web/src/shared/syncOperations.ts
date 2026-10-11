import { updateEntryPayload, type OutboxEntry } from "@/shared/offlineQueue";
import type { SyncOperationInput } from "@/sync/syncApi";

// Traduction des opérations de la file hors ligne en opérations du moteur de synchronisation (finalisation phase 12).
// La file est COMMUNE à la caisse et au terrain : quel que soit l'écran qui la vide, chaque opération part avec son vrai
// type. Avant, l'expéditeur de la caisse envoyait tout comme « vente ».

/** Preuve capturée hors ligne : image en base64, envoyée comme pièce jointe avant l'opération. */
export interface PendingImage { base64: string; contentType: string }

export interface FieldDeliveryPayload {
  stopId: string;
  latitude: number | null; longitude: number | null;
  signatureAttachmentId: string | null; photoAttachmentId: string | null;
  amountCollected: number | null; paymentFinancialAccountId: string | null;
  /** Images pas encore envoyées (hors ligne). Remplacées par leurs identifiants dès l'envoi. */
  pendingSignature?: PendingImage | null; pendingPhoto?: PendingImage | null;
}

export interface FieldInterventionPayload {
  interventionId: string;
  checklist: Array<{ label: string; done: boolean }>;
  partsUsed: Array<{ productId: string; quantity: number }>;
  timeSpentMinutes: number; reportText: string;
  signatureAttachmentId: string | null;
  pendingSignature?: PendingImage | null;
}

const MAPPING: Record<string, { entityType: string; operationType: "create" | "update"; priority: number }> = {
  "sale.checkout": { entityType: "sale", operationType: "create", priority: 5 },
  "customer.payment": { entityType: "customer_payment", operationType: "create", priority: 5 },
  "field.delivery": { entityType: "delivery_stop", operationType: "update", priority: 4 },
  "field.intervention": { entityType: "field_intervention", operationType: "update", priority: 4 },
};

export type AttachmentUploader = (img: PendingImage, kind: "photo" | "signature", referenceType: string, referenceId: string) => Promise<string>;

/** Envoie les preuves en attente d'une opération terrain et mémorise leurs identifiants dans la file. */
async function prepare(entry: OutboxEntry, upload: AttachmentUploader): Promise<unknown> {
  if (entry.kind === "field.delivery") {
    const p = { ...(entry.payload as FieldDeliveryPayload) };
    if (p.pendingSignature) { p.signatureAttachmentId = await upload(p.pendingSignature, "signature", "delivery_stop", p.stopId); p.pendingSignature = null; }
    if (p.pendingPhoto) { p.photoAttachmentId = await upload(p.pendingPhoto, "photo", "delivery_stop", p.stopId); p.pendingPhoto = null; }
    updateEntryPayload(entry.id, p);
    const { pendingSignature: _s, pendingPhoto: _p, ...wire } = p;
    return wire;
  }
  if (entry.kind === "field.intervention") {
    const p = { ...(entry.payload as FieldInterventionPayload) };
    if (p.pendingSignature) { p.signatureAttachmentId = await upload(p.pendingSignature, "signature", "field_intervention", p.interventionId); p.pendingSignature = null; }
    updateEntryPayload(entry.id, p);
    const { pendingSignature: _s, ...wire } = p;
    return wire;
  }
  return entry.payload;
}

/** Opérations prêtes pour `pushBatch`. Une opération d'un type inconnu fait échouer le lot plutôt que de partir sous un faux type. */
export async function toSyncOperations(entries: OutboxEntry[], upload?: AttachmentUploader): Promise<SyncOperationInput[]> {
  const ops: SyncOperationInput[] = [];
  for (const e of entries) {
    const m = MAPPING[e.kind];
    if (!m) throw new Error(`Opération hors ligne de type inconnu : ${e.kind}.`);
    if (e.kind.startsWith("field.") && !upload) throw new Error("Les opérations terrain se synchronisent depuis l'écran Terrain.");
    ops.push({
      operationUuid: e.id, entityType: m.entityType, operationType: m.operationType,
      payload: upload ? await prepare(e, upload) : e.payload, priority: m.priority, clientCreatedAt: e.createdAt,
    });
  }
  return ops;
}

/** Les opérations que cet écran sait synchroniser (la caisse ne sait pas envoyer les preuves terrain). */
export const kindsFor = (screen: "pos" | "field") =>
  screen === "pos" ? ["sale.checkout", "customer.payment"] : ["field.delivery", "field.intervention"];

/** Faut-il mettre l'opération en file plutôt que d'afficher une erreur ? Seulement si le réseau manque — jamais pour un
 * refus du serveur (règle métier, droits), qui doit être montré tel quel. */
export const isOfflineFailure = (error: unknown, online: boolean) => !online || error instanceof TypeError;
