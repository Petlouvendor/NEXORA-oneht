import { apiFetch } from "@/core/apiClient";
import { round2 } from "@/shared/amounts";
import type { TenderInput } from "@/pos/posApi";

/**
 * Encaissement en plusieurs moyens (finalisation, phase 5). Règles identiques au serveur, qui reste
 * l'autorité : les espèces vont dans la caisse de la session et sont seules à rendre la monnaie ; carte,
 * mobile money, virement ou chèque ne dépassent jamais le reste dû et s'encaissent sur leur compte.
 */

export interface TenderAccountOption { id: string; name: string; accountType: string }
export interface TenderMethodOption {
  id: string; name: string; type: string; requiresReference: boolean;
  accounts: TenderAccountOption[]; defaultAccountId: string | null;
}

export const getTenderOptions = (currency: string) =>
  apiFetch<TenderMethodOption[]>(`/pos/tender-options?currency=${encodeURIComponent(currency)}`);

/** Ligne d'encaissement saisie. `methodId` null = espèces sans moyen configuré (repli hors ligne). */
export interface TenderLine { key: string; methodId: string | null; accountId: string; amount: string; reference: string }

export const METHOD_ICON: Record<string, string> = {
  cash: "Espèces", card: "Carte", mobile_money: "Mobile money", bank_transfer: "Virement", check: "Chèque", voucher: "Bon", other: "Autre",
};

const amountOf = (l: TenderLine) => {
  const n = Number(l.amount.replace(",", "."));
  return Number.isFinite(n) && n > 0 ? round2(n) : 0;
};

export const isCashLine = (l: TenderLine, methods: TenderMethodOption[]) =>
  l.methodId === null || methods.find((m) => m.id === l.methodId)?.type === "cash";

export interface TenderSummary {
  paid: number; cash: number; nonCash: number; remaining: number; change: number;
  /** Paiements hors espèces au-delà du reste dû : refusés (pas de monnaie sur une carte). */
  nonCashOver: boolean;
  /** Lignes sans référence alors que le moyen l'exige. */
  missingReference: string[];
  /** Lignes hors espèces sans compte d'encaissement choisi. */
  missingAccount: string[];
}

export function summarizeTenders(lines: TenderLine[], methods: TenderMethodOption[], due: number): TenderSummary {
  let cash = 0, nonCash = 0;
  const missingReference: string[] = [];
  const missingAccount: string[] = [];
  for (const l of lines) {
    const a = amountOf(l);
    if (a <= 0) continue;
    const m = methods.find((x) => x.id === l.methodId);
    if (isCashLine(l, methods)) cash += a;
    else {
      nonCash += a;
      if (!l.accountId) missingAccount.push(l.key);
    }
    if (m?.requiresReference && !l.reference.trim()) missingReference.push(l.key);
  }
  cash = round2(cash);
  nonCash = round2(nonCash);
  const paid = round2(cash + nonCash);
  const nonCashOver = nonCash > due + 0.001;
  return {
    paid, cash, nonCash, nonCashOver, missingReference, missingAccount,
    remaining: round2(Math.max(0, due - paid)),
    change: nonCashOver ? 0 : round2(Math.max(0, paid - due)),
  };
}

/** Lignes envoyées au serveur : espèces → caisse de la session ; autres moyens → leur compte. */
export function toTenderInputs(lines: TenderLine[], methods: TenderMethodOption[], drawerAccountId: string): TenderInput[] {
  return lines
    .filter((l) => amountOf(l) > 0)
    .map((l) => ({
      financialAccountId: isCashLine(l, methods) ? drawerAccountId : l.accountId,
      paymentMethodId: l.methodId,
      amount: amountOf(l),
      reference: l.reference.trim() || null,
    }));
}

/** Montants rapides pour les espèces : exact, puis arrondis supérieurs usuels (billets). */
export function quickCashAmounts(remaining: number): number[] {
  if (remaining <= 0) return [];
  const out = [round2(remaining)];
  for (const step of [50, 100, 250, 500, 1000]) {
    const v = Math.ceil(remaining / step) * step;
    if (v > remaining + 0.001 && !out.includes(v)) out.push(v);
  }
  return out.slice(0, 5);
}

let seq = 0;
export const newTenderLine = (method: TenderMethodOption | null): TenderLine => ({
  key: `t${Date.now()}-${seq++}`,
  methodId: method?.id ?? null,
  accountId: method && method.type !== "cash" ? method.defaultAccountId ?? "" : "",
  amount: "",
  reference: "",
});
