import { describe, expect, it } from "vitest";
import { formatFact, sourceLink, splitFacts, structureAnswer } from "@/ai/nexoraAi";

describe("NEXORA AI — structuration des réponses (NEXORA ONE §8)", () => {
  it("profit answer: lead sentence kept verbatim, numbered causes become a list without their numbers", () => {
    const answer = "Le bénéfice a baissé de 245 000,00 HTG : 1 000,00 HTG du 01/08 au 31/08, contre 755 000,00 HTG du 01/09 au 27/09. "
      + "Principaux facteurs : 1) chiffre d'affaires en baisse de 40,00 HTG (100,00 HTG → 60,00 HTG) : impact −16,00 HTG ; "
      + "2) taux de marge passé de 40 % à 30 % : impact −6,00 HTG ; 3) dépenses en hausse de 7,00 HTG : impact −7,00 HTG.";
    const blocks = structureAnswer(answer);
    expect(blocks[0].text).toMatch(/^Le bénéfice a baissé de 245 000,00 HTG/);
    expect(blocks[1].label).toBe("Principaux facteurs");
    expect(blocks[1].items).toHaveLength(3);
    expect(blocks[1].items[0]).toMatch(/^chiffre d'affaires en baisse/);
    expect(blocks[1].items[2]).toBe("dépenses en hausse de 7,00 HTG : impact −7,00 HTG");
  });

  it("a sentence without a list stays plain text; the answer text is never rewritten", () => {
    const blocks = structureAnswer("Aucune créance échue : tous les clients sont à jour de leurs paiements.");
    expect(blocks).toEqual([{ label: null, text: "Aucune créance échue : tous les clients sont à jour de leurs paiements.", items: [] }]);
  });

  it("server sources map to real screens with human labels", () => {
    expect(sourceLink("reports/aging/receivables")).toEqual({ label: "Balance âgée clients", to: "/credit" });
    expect(sourceLink("inventory/reorder-suggestions").to).toBe("/inventory");
    expect(sourceLink("dashboard/command-center").to).toBe("/");
    expect(sourceLink("customers/0d1f2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b/ledger").to).toBe("/customers/0d1f2c3b-4a5e-4f60-8a7b-9c0d1e2f3a4b");
    expect(sourceLink("hr?tab=advances").to).toBe("/hr?tab=advances");
  });

  it("facts: the dominant unit becomes a chart, previous-period values and other units become tiles", () => {
    const facts = [
      { key: "profit", label: "Bénéfice", value: 10, unit: "HTG", kind: "actual" },
      { key: "profit_previous", label: "Bénéfice — période précédente", value: 20, unit: "HTG", kind: "actual" },
      { key: "revenue", label: "CA", value: 100, unit: "HTG", kind: "actual" },
      { key: "margin_rate", label: "Taux de marge", value: 30, unit: "%", kind: "actual" },
    ];
    const { chart, tiles } = splitFacts(facts);
    expect(chart.map((f) => f.key)).toEqual(["profit", "revenue"]);
    expect(tiles.map((f) => f.key)).toEqual(["profit_previous", "margin_rate"]);
    expect(formatFact(facts[3])).toMatch(/^30\s%$/);
    expect(formatFact({ key: "x", label: "x", value: 3.14, unit: "jours", kind: "forecast" })).toMatch(/j$/);
  });
});
