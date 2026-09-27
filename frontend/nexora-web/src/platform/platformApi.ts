import { apiFetch } from "@/core/apiClient";

// Administration SaaS & monétisation — console plateforme (Phase 38 §34-36, §50).

export interface SaasMetrics {
  mrr: number;
  arr: number;
  currencyCode: string;
  activeSubscriptions: number;
  trialingSubscriptions: number;
  pastDueSubscriptions: number;
  suspendedSubscriptions: number;
  canceledLast30d: number;
  churnRatePct: number;
  trialConversionPct: number;
  arpu: number;
  paymentSuccessRatePct: number;
  openTickets: number;
  paymentsAwaitingReview: number;
  generatedAt: string;
}

export interface Plan {
  id: string;
  code: string;
  name: string;
  tier: string;
  description: string | null;
  priceAmount: number;
  currencyCode: string;
  period: string;
  trialDays: number;
  quotas: Record<string, number>;
  features: Record<string, string>;
  isPublic: boolean;
  sortOrder: number;
  status: string;
}

export interface Customer {
  id: string;
  customerRef: string;
  legalName: string;
  primaryContactName: string | null;
  primaryContactEmail: string | null;
  billingEmail: string | null;
  countryCode: string;
  billingCurrency: string;
  taxId: string | null;
  taxExempt: boolean;
  commercialTerms: string | null;
  status: string;
  companyIds: string[];
  createdAt: string;
}

export interface SubscriptionAddon {
  id: string;
  addonId: string;
  addonCode: string;
  quantity: number;
  unitPrice: number;
}

export interface Subscription {
  id: string;
  customerId: string;
  planId: string;
  planCode: string;
  planName: string;
  tier: string;
  status: string;
  period: string;
  priceAmount: number;
  currencyCode: string;
  trialEndsAt: string | null;
  currentPeriodStart: string;
  currentPeriodEnd: string;
  autoRenew: boolean;
  cancelAtPeriodEnd: boolean;
  canceledAt: string | null;
  graceEndsAt: string | null;
  suspendedAt: string | null;
  suspendReason: string | null;
  paymentFailures: number;
  activatedAt: string | null;
  addons: SubscriptionAddon[];
  createdAt: string;
}

export interface Invoice {
  id: string;
  number: string;
  customerId: string | null;
  subscriptionId: string | null;
  kind: string;
  status: string;
  currencyCode: string;
  issueDate: string;
  dueDate: string;
  subtotalAmount: number;
  discountAmount: number;
  taxAmount: number;
  totalAmount: number;
  paidAmount: number;
  couponCode: string | null;
  paidAt: string | null;
  lines: { lineType: string; description: string; quantity: number; unitPrice: number; amount: number }[];
  createdAt: string;
}

export interface Payment {
  id: string;
  customerId: string | null;
  invoiceId: string | null;
  method: string;
  provider: string | null;
  amount: number;
  currencyCode: string;
  status: string;
  failureReason: string | null;
  reviewRequired: boolean;
  isManual: boolean;
  receivedAt: string;
}

export interface QuotaLine {
  resource: string;
  limit: number;
  used: number;
  percent: number;
  exceeded: boolean;
}
export interface QuotaReport {
  customerId: string;
  planCode: string;
  periodStart: string;
  lines: QuotaLine[];
}

export const saasMetrics = () => apiFetch<SaasMetrics>("/platform/metrics");
export const listPlans = () => apiFetch<Plan[]>("/platform/plans");
export const listCustomers = (status?: string) =>
  apiFetch<Customer[]>(`/platform/customers${status ? `?status=${status}` : ""}`);
export const createCustomer = (body: { legalName: string; primaryContactEmail?: string; billingCurrency?: string; countryCode?: string }) =>
  apiFetch<Customer>("/platform/customers", { method: "POST", body: JSON.stringify(body) });
export const customerSubscriptions = (id: string) =>
  apiFetch<Subscription[]>(`/platform/customers/${id}/subscriptions`);
export const customerInvoices = (id: string) => apiFetch<Invoice[]>(`/platform/customers/${id}/invoices`);
export const customerQuotas = (id: string) => apiFetch<QuotaReport>(`/platform/customers/${id}/quotas`);
export const createSubscription = (customerId: string, body: { planId: string; startTrial?: boolean; couponCode?: string }) =>
  apiFetch<Subscription>(`/platform/customers/${customerId}/subscription`, { method: "POST", body: JSON.stringify(body) });
export const platformUpgrade = (id: string, planId: string) =>
  apiFetch<Subscription>(`/platform/subscriptions/${id}/upgrade`, { method: "POST", body: JSON.stringify({ planId }) });
export const platformDowngrade = (id: string, planId: string) =>
  apiFetch<Subscription>(`/platform/subscriptions/${id}/downgrade`, { method: "POST", body: JSON.stringify({ planId }) });
export const platformSuspend = (id: string, reason: string) =>
  apiFetch<Subscription>(`/platform/subscriptions/${id}/suspend`, { method: "POST", body: JSON.stringify({ reason }) });
export const platformReactivate = (id: string) =>
  apiFetch<Subscription>(`/platform/subscriptions/${id}/reactivate`, { method: "POST" });
export const paymentsForReview = () => apiFetch<Payment[]>("/platform/payments/review");
export const recordManualPayment = (body: { customerId: string; invoiceId?: string | null; amount: number; currencyCode: string; method?: string }) =>
  apiFetch<Payment>("/platform/payments", { method: "POST", body: JSON.stringify(body) });
export const confirmPayment = (id: string) =>
  apiFetch<Payment>(`/platform/payments/${id}/confirm`, { method: "POST" });
