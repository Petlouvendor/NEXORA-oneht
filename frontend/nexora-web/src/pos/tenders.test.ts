import { describe, expect, it } from "vitest";
import { quickCashAmounts, summarizeTenders, toTenderInputs, type TenderLine, type TenderMethodOption } from "@/pos/tenders";

const methods: TenderMethodOption[] = [
  { id: "cash", name: "Espèces HTG", type: "cash", requiresReference: false, accounts: [], defaultAccountId: null },
  { id: "card", name: "Carte", type: "card", requiresReference: true, accounts: [{ id: "bank", name: "Banque", accountType: "bank" }], defaultAccountId: "bank" },
  { id: "mc", name: "MonCash", type: "mobile_money", requiresReference: true,
    accounts: [{ id: "mm1", name: "MonCash A", accountType: "mobile_money" }, { id: "mm2", name: "MonCash B", accountType: "mobile_money" }], defaultAccountId: null },
];
const line = (methodId: string | null, amount: string, patch: Partial<TenderLine> = {}): TenderLine =>
  ({ key: `${methodId}-${amount}`, methodId, accountId: methodId === "card" ? "bank" : "", amount, reference: "", ...patch });

describe("Caisse — encaissement en plusieurs moyens", () => {
  it("la monnaie n'est rendue que sur les espèces", () => {
    const s = summarizeTenders([line("cash", "500"), line("card", "600", { reference: "A1" })], methods, 1000);
    expect(s).toMatchObject({ paid: 1100, cash: 500, nonCash: 600, change: 100, remaining: 0, nonCashOver: false });
  });

  it("une carte au-delà du reste dû est signalée (pas de monnaie sur une carte)", () => {
    const s = summarizeTenders([line("card", "1200", { reference: "A1" })], methods, 1000);
    expect(s.nonCashOver).toBe(true);
    expect(s.change).toBe(0);
  });

  it("référence et compte exigés selon le moyen", () => {
    const s = summarizeTenders([line("card", "300"), line("mc", "200", { reference: "T1" })], methods, 1000);
    expect(s.missingReference).toEqual(["card-300"]);
    expect(s.missingAccount).toEqual(["mc-200"]);
    expect(s.remaining).toBe(500);
  });

  it("espèces → caisse de la session ; autres moyens → leur compte ; montants nuls ignorés", () => {
    expect(toTenderInputs([line("cash", "400"), line("card", "600", { reference: "A1" }), line("mc", "")], methods, "drawer")).toEqual([
      { financialAccountId: "drawer", paymentMethodId: "cash", amount: 400, reference: null },
      { financialAccountId: "bank", paymentMethodId: "card", amount: 600, reference: "A1" },
    ]);
    // Repli hors ligne : espèces sans moyen configuré.
    expect(toTenderInputs([line(null, "150")], [], "drawer")).toEqual([
      { financialAccountId: "drawer", paymentMethodId: null, amount: 150, reference: null },
    ]);
  });

  it("montants rapides : exact puis billets supérieurs", () => {
    expect(quickCashAmounts(735)).toEqual([735, 750, 800, 1000]);
    expect(quickCashAmounts(0)).toEqual([]);
  });
});
