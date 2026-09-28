/** Estimations de montants pendant la saisie (achats, ventes). Même règle que le serveur, qui recalcule
 * toujours : net = qté × prix − remise ; taxe = net × taux, arrondie au centime. */
export const round2 = (v: number) => Math.round((v + Number.EPSILON) * 100) / 100;

export function estimateLine(quantity: number, unitPrice: number, discount: number, ratePercent: number) {
  const net = round2(quantity * unitPrice - discount);
  const tax = round2((net * ratePercent) / 100);
  return { net, tax, total: round2(net + tax) };
}

export function estimateTotals(lines: Array<{ quantity: number; unitPrice: number; discount: number; ratePercent: number }>) {
  return lines.reduce((acc, l) => {
    const e = estimateLine(l.quantity, l.unitPrice, l.discount, l.ratePercent);
    return { subtotal: round2(acc.subtotal + e.net), tax: round2(acc.tax + e.tax), total: round2(acc.total + e.total) };
  }, { subtotal: 0, tax: 0, total: 0 });
}

