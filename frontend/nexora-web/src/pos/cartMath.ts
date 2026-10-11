import { round2 } from "@/shared/amounts";
import type { PosProduct } from "@/pos/posApi";

/**
 * Montants de la caisse, calculés comme le serveur (phase 6) : prix ramené au hors taxe (arrondi à 4 décimales
 * s'il est saisi TTC), brut = qté × prix HT, net = brut − remise, taxe = net × taux (au centime), total = net + taxe.
 * Avant, la caisse affichait et encaissait le hors taxe : le serveur refusait la vente, ou créait en silence un
 * crédit du montant de la taxe.
 */
export const round4 = (v: number) => Math.round((v + Number.EPSILON) * 10000) / 10000;

export function unitPriceExcludingTax(p: Pick<PosProduct, "salePrice" | "taxRate" | "priceIncludesTax">): number {
  const price = p.salePrice ?? 0;
  const rate = p.taxRate ?? 0;
  return p.priceIncludesTax && rate > 0 ? round4(price / (1 + rate / 100)) : price;
}

/** Prix affiché à la recherche : toutes taxes comprises. */
export function displayPrice(p: Pick<PosProduct, "salePrice" | "taxRate" | "priceIncludesTax">): number | null {
  if (p.salePrice == null) return null;
  const rate = p.taxRate ?? 0;
  return p.priceIncludesTax || rate === 0 ? p.salePrice : round2(p.salePrice * (1 + rate / 100));
}

export interface LineAmounts { gross: number; net: number; tax: number; total: number }

export function lineAmounts(quantity: number, unitPriceHt: number, discount: number, taxRate: number): LineAmounts {
  const gross = round2(quantity * unitPriceHt);
  const net = round2(gross - discount);
  const tax = round2((net * taxRate) / 100);
  return { gross, net, tax, total: round2(net + tax) };
}
