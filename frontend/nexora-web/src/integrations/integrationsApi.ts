import { apiFetch } from "@/core/apiClient";

export interface Connector {
  id: string;
  code: string;
  name: string;
  connectorType: string;
  provider: string;
  environment: string;
  status: string;
  lastSyncAt: string | null;
  lastError: string | null;
  lastErrorAt: string | null;
  connectorVersion: string;
  hasSecret: boolean;
  config: Record<string, unknown>;
  updatedAt: string;
  /** « simulation » : aucun service externe, aucun argent réel (finalisation phase 15). */
  mode: "simulation" | "real";
  credentialsConfigured: boolean;
  credentialsHint: string | null;
  lastTestStatus: TestStatus | null;
  lastTestMessage: string | null;
  lastTestedAt: string | null;
  providerSupported: boolean;
}

export type TestStatus = "CONNECTED" | "AUTHENTICATION_FAILED" | "TIMEOUT" | "UNAVAILABLE" | "NOT_CONFIGURED";
export interface ConnectorTest { ok: boolean; detail: string; latencyMs: number; status: TestStatus; mode: "simulation" | "real" }
export interface PaymentProviderInfo { key: string; name: string; isSimulation: boolean; supportsRefund: boolean; currencies: string[] }

export interface PaymentIntent {
  id: string;
  connectorId: string;
  providerReference: string | null;
  amount: number;
  currency: string;
  method: string;
  purpose: string;
  status: string;
  paymentId: string | null;
  refundedAmount: number;
  expiresAt: string | null;
  confirmedAt: string | null;
  failureReason: string | null;
  createdAt: string;
  mode: "simulation" | "real";
  redirectUrl: string | null;
  providerOrderId: string | null;
  customerId: string | null;
}

export interface WebhookEvent {
  id: string;
  connectorId: string;
  providerEventId: string;
  eventType: string;
  signatureValid: boolean;
  status: string;
  error: string | null;
  receivedAt: string;
  processedAt: string | null;
}

export interface OutboundWebhook {
  id: string;
  name: string;
  targetUrl: string;
  subscribedEvents: string[];
  status: string;
  consecutiveFailures: number;
  lastDeliveryAt: string | null;
}

export interface OutboundDelivery {
  id: string;
  outboundWebhookId: string;
  eventType: string;
  eventId: string;
  attempts: number;
  maxAttempts: number;
  status: string;
  responseCode: number | null;
  nextAttemptAt: string;
  lastError: string | null;
  deliveredAt: string | null;
}

export interface ConnectorHealth {
  id: string;
  name: string;
  connectorType: string;
  provider: string;
  status: string;
  lastSyncAt: string | null;
  lastError: string | null;
  pendingPayments: number;
  lastWebhookAt: string | null;
}

export interface IntegrationCenter {
  generatedAt: string;
  activeConnectors: number;
  errorConnectors: number;
  pendingPayments: number;
  failedWebhooks: number;
  duplicateWebhooks: number;
  pendingOutboundDeliveries: number;
  exhaustedOutboundDeliveries: number;
  openBankExceptions: number;
  connectors: ConnectorHealth[];
  recommendedActions: string[];
}

export interface BankStatement {
  id: string;
  financialAccountId: string;
  statementNumber: string | null;
  statementDate: string;
  openingBalance: number;
  closingBalance: number;
  status: string;
  lineCount: number;
  matchedCount: number;
}

export const getCenter = () => apiFetch<IntegrationCenter>("/integrations/center");
export const listConnectors = () => apiFetch<Connector[]>("/integrations/connectors");
export const testConnector = (id: string) => apiFetch<ConnectorTest>(`/integrations/connectors/${id}/test`, { method: "POST" });
export const listPaymentProviders = () => apiFetch<PaymentProviderInfo[]>("/integrations/payment-providers");
export const upsertConnector = (body: { code: string; name: string; connectorType: string; provider: string; environment: string; config: Record<string, unknown> | null }) =>
  apiFetch<string>("/integrations/connectors", { method: "POST", body: JSON.stringify(body) });
/** Identifiants fournisseur : écriture seule, chiffrés côté serveur, jamais relus. */
export const setConnectorCredentials = (id: string, clientId: string, clientSecret: string) =>
  apiFetch<void>(`/integrations/connectors/${id}/credentials`, { method: "PUT", body: JSON.stringify({ clientId, clientSecret }) });
export const setConnectorStatus = (id: string, status: string) =>
  apiFetch<void>(`/integrations/connectors/${id}/status`, { method: "POST", body: JSON.stringify({ status }) });
export const rotateSecret = (id: string) =>
  apiFetch<{ secret: string }>(`/integrations/connectors/${id}/rotate-secret`, { method: "POST" });

export const listIntents = (status?: string) =>
  apiFetch<PaymentIntent[]>(`/integrations/payments/intents${status ? `?status=${status}` : ""}`);
export const createIntent = (body: {
  connectorId: string; idempotencyKey: string; amount: number; currency: string; method: string; purpose: string;
  customerId: string; settlementAccountId: string; saleId: null; allocations: null; expiresInMinutes: number; reference: string | null;
}) => apiFetch<PaymentIntent>("/integrations/payments/intents", { method: "POST", body: JSON.stringify(body), idempotencyKey: body.idempotencyKey });
/** Paiement réel : interroge le fournisseur (seul à pouvoir le confirmer). */
export const verifyIntent = (id: string) => apiFetch<PaymentIntent>(`/integrations/payments/intents/${id}/verify`, { method: "POST" });
/** SIMULATION seulement : réponse du fournisseur saisie à la main. */
export const simulateProviderResponse = (id: string, outcome: "confirmed" | "failed" | "cancelled", providerAmount: number | null) =>
  apiFetch<PaymentIntent>(`/integrations/payments/intents/${id}/callback`, {
    method: "POST", body: JSON.stringify({ outcome, providerAmount, providerReference: null }),
  });
export const expireStaleIntents = () =>
  apiFetch<{ expired: number }>("/integrations/payments/intents/expire", { method: "POST" });
export const refundIntent = (id: string, amount: number, reason: string) =>
  apiFetch<PaymentIntent>(`/integrations/payments/intents/${id}/refund`, {
    method: "POST",
    body: JSON.stringify({ amount, reason }),
  });

export const listWebhookEvents = (status?: string) =>
  apiFetch<WebhookEvent[]>(`/integrations/webhooks/events${status ? `?status=${status}` : ""}`);
export const listOutbound = () => apiFetch<OutboundWebhook[]>("/integrations/webhooks/outbound");
export const listDeliveries = (webhookId?: string) =>
  apiFetch<OutboundDelivery[]>(`/integrations/webhooks/outbound/deliveries${webhookId ? `?webhookId=${webhookId}` : ""}`);
export const retryDelivery = (id: string) =>
  apiFetch<void>(`/integrations/webhooks/outbound/deliveries/${id}/retry`, { method: "POST" });
export const deliverDue = () =>
  apiFetch<{ attempted: number }>("/integrations/webhooks/outbound/deliver-due", { method: "POST" });

export const listStatements = () => apiFetch<BankStatement[]>("/integrations/bank/statements");
export const autoMatchStatement = (id: string) =>
  apiFetch<number>(`/integrations/bank/statements/${id}/auto-match`, { method: "POST" });

// --- Clés d'API pour les intégrations serveur (Phase 35 §13-14) ---------------

export interface ApiKey {
  id: string;
  name: string;
  keyPrefix: string;
  environment: string;
  scopes: string[];
  rateLimitPerMinute: number;
  ownerUserId: string;
  expiresAt: string | null;
  lastUsedAt: string | null;
  lastUsedIp: string | null;
  revokedAt: string | null;
  createdAt: string;
  status: string;
  appInstallationId: string | null;
}

export interface ApiKeyCreated {
  key: ApiKey;
  secret: string;
}

export const listApiKeys = () => apiFetch<ApiKey[]>("/api-keys");

export const createApiKey = (input: {
  name: string;
  scopes: string[];
  environment?: string;
  expiresInDays?: number | null;
  rateLimitPerMinute?: number;
}) => apiFetch<ApiKeyCreated>("/api-keys", { method: "POST", body: JSON.stringify(input) });

export const rotateApiKey = (id: string) =>
  apiFetch<ApiKeyCreated>(`/api-keys/${id}/rotate`, { method: "POST" });

export const revokeApiKey = (id: string) =>
  apiFetch<void>(`/api-keys/${id}/revoke`, { method: "POST" });

// --- Marketplace & écosystème partenaires (Phase 46 §41-48) -------------------

export interface MarketplaceApp {
  id: string;
  slug: string;
  name: string;
  publisherName: string;
  category: string;
  certification: string;   // official | certified | community
  status: string;          // draft | pending_review | approved | rejected | suspended
  version: string;
  description: string | null;
  requestedScopes: string[];
  pricingModel: string;
  priceAmount: number | null;
  priceCurrencyCode: string;
  reviewedAt: string | null;
  reviewNotes: string | null;
}

export interface AppsPage {
  items: MarketplaceApp[];
  nextCursor: string | null;
}

export interface AppInstallation {
  id: string;
  marketplaceAppId: string;
  appSlug: string;
  appName: string;
  appCategory: string;
  appCertification: string;
  status: string;   // installed | disabled | uninstalled
  grantedScopes: string[];
  installedAt: string;
}

export interface DeveloperKeyUsage {
  apiKeyId: string;
  keyName: string;
  daily: { date: string; requestCount: number; rateLimitedCount: number }[];
}

export const listApps = (cursor?: string) => apiFetch<AppsPage>(`/apps${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ""}`);
export const listInstalledApps = () => apiFetch<AppInstallation[]>("/apps/installed");
export const installApp = (id: string, grantedScopes: string[]) =>
  apiFetch<AppInstallation>(`/apps/${id}/install`, { method: "POST", body: JSON.stringify({ grantedScopes }) });
export const uninstallApp = (id: string) => apiFetch<void>(`/apps/${id}/uninstall`, { method: "POST" });
export const disableApp = (id: string) => apiFetch<void>(`/apps/${id}/disable`, { method: "POST" });
export const listDeveloperUsage = (days = 30) => apiFetch<DeveloperKeyUsage[]>(`/developer/usage?days=${days}`);

// --- Curation du catalogue marketplace (personnel plateforme uniquement, §45) --

export const listAllApps = (status?: string, cursor?: string) =>
  apiFetch<AppsPage>(`/apps?${status ? `status=${status}&` : ""}${cursor ? `cursor=${encodeURIComponent(cursor)}` : ""}`);
export const submitApp = (input: {
  slug: string; name: string; publisherName: string; category: string; version: string;
  description?: string; requestedScopes: string[]; pricingModel?: string;
}) => apiFetch<MarketplaceApp>("/apps", { method: "POST", body: JSON.stringify(input) });
export const reviewApp = (id: string, decision: "approve" | "reject" | "suspend", notes?: string) =>
  apiFetch<MarketplaceApp>(`/apps/${id}/review`, { method: "POST", body: JSON.stringify({ decision, notes }) });

// ------------------------------------------------------------------ Libellés (finalisation phase 15)

type Tone = "success" | "warning" | "error" | "info" | undefined;
export const TEST_STATUS: Record<TestStatus, { label: string; tone: Tone }> = {
  CONNECTED: { label: "Connecté", tone: "success" },
  AUTHENTICATION_FAILED: { label: "Authentification refusée", tone: "error" },
  TIMEOUT: { label: "Délai dépassé", tone: "error" },
  UNAVAILABLE: { label: "Service indisponible", tone: "error" },
  NOT_CONFIGURED: { label: "Non configuré", tone: "warning" },
};
export const INTENT_STATUS: Record<string, { label: string; tone: Tone }> = {
  pending: { label: "En attente du client", tone: "warning" }, authorized: { label: "Autorisé", tone: "info" },
  confirmed: { label: "Payé", tone: "success" }, failed: { label: "Refusé", tone: "error" }, cancelled: { label: "Annulé", tone: "error" },
  expired: { label: "Expiré", tone: undefined }, amount_mismatch: { label: "Montant différent", tone: "error" },
  refunded: { label: "Remboursé", tone: "info" }, partially_refunded: { label: "Remboursé en partie", tone: "info" },
};
/** Les fournisseurs qui demandent des identifiants (MonCash) — la simulation et la carte n'en ont pas. */
export const needsCredentials = (c: Pick<Connector, "connectorType" | "provider">) => c.connectorType === "payment" && c.provider === "moncash";
/** Actions possibles sur un paiement selon son mode et son état (mêmes règles que le serveur). */
export function intentActions(i: Pick<PaymentIntent, "mode" | "status">, environment: string) {
  const open = i.status === "pending" || i.status === "authorized";
  return {
    verify: i.mode === "real" && open,
    simulate: i.mode === "simulation" && open && environment !== "production",
    refund: i.status === "confirmed",
  };
}
