import { round2 } from "@/shared/amounts";
import type { CreditReceivable } from "@/credit/creditApi";

/** Créance pouvant recevoir une promesse : encore due, hors litige, sans promesse en cours. */
export const promisable = (r: CreditReceivable) =>
  r.remainingAmount > 0 && r.status !== "IN_DISPUTE" && !r.openPromiseId
  && ["OPEN", "PARTIALLY_PAID", "OVERDUE"].includes(r.status);

/** Reste dû des créances choisies, et leur devise (null si mélange de devises). */
export function selectionTotal(receivables: CreditReceivable[], selected: string[]) {
  const chosen = receivables.filter((r) => selected.includes(r.id));
  const currencies = [...new Set(chosen.map((r) => r.currency))];
  return {
    total: round2(chosen.reduce((s, r) => s + r.remainingAmount, 0)),
    currency: currencies.length === 1 ? currencies[0] : null,
    mixed: currencies.length > 1,
  };
}

/** Mêmes règles que le serveur (qui reste l'autorité) : le premier problème rencontré, ou null. */
export function validatePromise(input: {
  selected: string[]; amount: number | null; date: string; today: string; total: number; mixed: boolean;
}): { field: "receivables" | "amount" | "date"; message: string } | null {
  if (input.selected.length === 0) return { field: "receivables", message: "Choisissez au moins une créance." };
  if (input.mixed) return { field: "receivables", message: "Les créances d'une promesse doivent être dans la même devise." };
  if (input.amount == null || input.amount <= 0) return { field: "amount", message: "Montant promis positif requis." };
  if (input.amount > input.total) return { field: "amount", message: `Le montant dépasse le reste dû (${input.total.toFixed(2)}).` };
  if (!input.date) return { field: "date", message: "Date promise obligatoire." };
  if (input.date < input.today) return { field: "date", message: "La date promise ne peut pas être dans le passé." };
  return null;
}
