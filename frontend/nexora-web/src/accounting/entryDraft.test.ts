import { describe, expect, it } from "vitest";
import { draftTotals, emptyRow, validateDraft, yearToDate, type DraftRow } from "@/accounting/entryDraft";

const row = (accountCode: string, debit: string, credit: string): DraftRow => ({ ...emptyRow(), accountCode, debit, credit });

describe("saisie d'écriture (phase 8)", () => {
  it("totalise au centime et détecte l'équilibre", () => {
    const t = draftTotals([row("6800", "0.1", ""), row("6800", "0.2", ""), row("5711", "", "0,30")]);
    expect(t).toMatchObject({ debit: 0.3, credit: 0.3, gap: 0, balanced: true });
  });

  it("refuse une ligne au débit ET au crédit, sans compte ou négative — comme le serveur", () => {
    expect(validateDraft([row("6800", "10", "5"), row("5711", "", "5")])).toEqual({ error: expect.stringContaining("Ligne 1") });
    expect(validateDraft([row("", "10", ""), row("5711", "", "10")])).toEqual({ error: "Ligne 1 : choisissez un compte." });
    expect(validateDraft([row("6800", "-10", ""), row("5711", "", "-10")])).toEqual({ error: expect.stringContaining("positifs") });
    expect(validateDraft([row("6800", "abc", ""), row("5711", "", "10")])).toEqual({ error: "Ligne 1 : montant illisible." });
  });

  it("ignore les lignes vides, exige deux lignes et l'équilibre", () => {
    expect(validateDraft([row("6800", "10", ""), emptyRow()])).toEqual({ error: "Une écriture compte au moins deux lignes." });
    expect(validateDraft([row("6800", "10", ""), row("5711", "", "9")])).toEqual({ error: "Écriture déséquilibrée : écart de 1.00 entre débit et crédit." });
    const ok = validateDraft([row("6800", "10", ""), emptyRow(), row("5711", "", "10")]);
    expect(ok).toEqual({ lines: [
      { accountCode: "6800", debit: 10, credit: 0, description: null },
      { accountCode: "5711", debit: 0, credit: 10, description: null },
    ] });
  });

  it("exercice en cours : du 1er janvier à aujourd'hui", () => {
    expect(yearToDate("2026-10-02")).toEqual({ from: "2026-01-01", to: "2026-10-02" });
  });
});
