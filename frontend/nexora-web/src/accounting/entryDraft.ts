import { round2 } from "@/shared/amounts";
import { num } from "@/shared/forms";
import type { DraftLineInput, JournalEntry } from "@/accounting/accountingApi";

/** Ligne saisie à l'écran (montants en texte, tels que tapés). */
export interface DraftRow { accountCode: string; debit: string; credit: string; description: string }

export const emptyRow = (): DraftRow => ({ accountCode: "", debit: "", credit: "", description: "" });

export const rowsFromEntry = (e: JournalEntry): DraftRow[] => e.lines.map((l) => ({
  accountCode: l.accountCode,
  debit: l.debit ? String(l.debit) : "",
  credit: l.credit ? String(l.credit) : "",
  description: l.description ?? "",
}));

/** Ligne entièrement vide : ignorée (l'utilisateur a ajouté une ligne sans la remplir). */
const blank = (r: DraftRow) => !r.accountCode && !r.debit.trim() && !r.credit.trim() && !r.description.trim();

export function draftTotals(rows: DraftRow[]) {
  const debit = round2(rows.reduce((s, r) => s + (num(r.debit) ?? 0), 0));
  const credit = round2(rows.reduce((s, r) => s + (num(r.credit) ?? 0), 0));
  return { debit, credit, gap: round2(debit - credit), balanced: debit > 0 && debit === credit };
}

/**
 * Contrôle avant envoi — mêmes règles que le serveur (le serveur reste l'autorité) : chaque ligne a un compte et
 * un montant positif au débit OU au crédit, au moins deux lignes, écriture équilibrée.
 * Renvoie les lignes prêtes à envoyer ou le premier problème rencontré.
 */
export function validateDraft(rows: DraftRow[]): { lines: DraftLineInput[] } | { error: string } {
  const lines: DraftLineInput[] = [];
  for (const [i, r] of rows.entries()) {
    if (blank(r)) continue;
    const n = i + 1;
    const d = num(r.debit) ?? 0;
    const c = num(r.credit) ?? 0;
    if (!r.accountCode) return { error: `Ligne ${n} : choisissez un compte.` };
    if ((r.debit.trim() && num(r.debit) == null) || (r.credit.trim() && num(r.credit) == null))
      return { error: `Ligne ${n} : montant illisible.` };
    if (d < 0 || c < 0) return { error: `Ligne ${n} : les montants sont positifs (utilisez l'autre colonne).` };
    if ((d > 0) === (c > 0)) return { error: `Ligne ${n} : saisissez un montant au débit OU au crédit.` };
    lines.push({ accountCode: r.accountCode, debit: round2(d), credit: round2(c), description: r.description.trim() || null });
  }
  if (lines.length < 2) return { error: "Une écriture compte au moins deux lignes." };
  const t = draftTotals(rows.filter((r) => !blank(r)));
  if (!t.balanced) return { error: `Écriture déséquilibrée : écart de ${t.gap.toFixed(2)} entre débit et crédit.` };
  return { lines };
}

/** Exercice courant : du 1er janvier à aujourd'hui (valeurs par défaut des états). */
export function yearToDate(today: string): { from: string; to: string } {
  return { from: `${today.slice(0, 4)}-01-01`, to: today };
}
