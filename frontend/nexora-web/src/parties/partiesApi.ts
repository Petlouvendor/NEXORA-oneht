import { apiFetch } from "@/core/apiClient";

// Fiches tiers (recette Phase 55 : CLI-001/002, ACH-001/011).

export interface Customer {
  id: string;
  customerNumber: string;
  name: string;
  type: "individual" | "business";
  phone: string | null;
  email: string | null;
  address: string | null;
  taxIdentifier: string | null;
  status: string;
  creditLimit: number | null;   // null = non visible avec vos permissions (guide §7.2)
  currency: string;
  openReceivables: number | null;
  createdAt: string;
}

export interface CustomerInput {
  name: string;
  type: "individual" | "business";
  phone?: string | null;
  email?: string | null;
  address?: string | null;
  taxIdentifier?: string | null;
  /** Exige `credit.set_limit` côté serveur dès qu'elle est > 0. */
  creditLimit?: number;
}

export interface SupplierSummary {
  id: string;
  supplierNumber: string;
  name: string;
  status: string;
  currency: string;
  openPayables: number;
}

export interface SupplierInput {
  name: string;
  type: "individual" | "business";
  phone?: string | null;
  email?: string | null;
  address?: string | null;
  taxIdentifier?: string | null;
}

export const listCustomers = (q?: string, limit = 100) => {
  const qs = new URLSearchParams({ limit: String(limit) });
  if (q?.trim()) qs.set("q", q.trim());
  return apiFetch<Customer[]>(`/customers?${qs}`);
};
export const getCustomer = (id: string) => apiFetch<Customer>(`/customers/${id}`);
export const createCustomer = (body: CustomerInput) =>
  apiFetch<Customer>("/customers", { method: "POST", body: JSON.stringify(body) });
export const updateCustomer = (id: string, body: CustomerInput) =>
  apiFetch<Customer>(`/customers/${id}`, { method: "PUT", body: JSON.stringify(body) });

export const listSuppliers = () => apiFetch<SupplierSummary[]>("/suppliers");
export const createSupplier = (body: SupplierInput) =>
  apiFetch<unknown>("/suppliers", { method: "POST", body: JSON.stringify(body) });
export const updateSupplier = (id: string, body: SupplierInput) =>
  apiFetch<unknown>(`/suppliers/${id}`, { method: "PUT", body: JSON.stringify(body) });
