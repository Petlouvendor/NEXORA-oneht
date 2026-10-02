import { describe, expect, it } from "vitest";
import { parseStatementCsv } from "@/finance/treasuryApi";

describe("lecture d'un relevé bancaire CSV (phase 7)", () => {
  it("montant signé, dates françaises, décimales à virgule", () => {
    const r = parseStatementCsv("Date;Libellé;Montant;Référence\n05/09/2026;Dépôt espèces;1 500,00;DEP-1\n06/09/2026;Frais tenue;-75,50;FEE-9\n");
    expect(r.errors).toEqual([]);
    expect(r.lines).toEqual([
      { valueDate: "2026-09-05", amount: 1500, description: "Dépôt espèces", reference: "DEP-1" },
      { valueDate: "2026-09-06", amount: -75.5, description: "Frais tenue", reference: "FEE-9" },
    ]);
  });

  it("colonnes débit / crédit et dates ISO", () => {
    const r = parseStatementCsv("date,libelle,debit,credit\n2026-09-07,Virement reçu,,2000.00\n2026-09-08,Chèque 12,300.25,\n");
    expect(r.lines.map((l) => l.amount)).toEqual([2000, -300.25]);
  });

  it("chaque ligne illisible est signalée, jamais ignorée en silence", () => {
    const r = parseStatementCsv("Date;Libellé;Montant\n31/13/2026x;Erreur;10\n05/09/2026;Nul;0\n05/09/2026;OK;10\n");
    expect(r.lines).toHaveLength(1);
    expect(r.errors).toHaveLength(2);
    expect(r.errors[0]).toContain("Ligne 2");
  });
});
