import { describe, expect, it } from "vitest";
import { alertDomain, classifyRisks, firstName, greeting, pctChange, previousWindow } from "@/dashboard/homeModel";
import type { Recommendation } from "@/intelligence/intelligenceApi";

const rec = (over: Partial<Recommendation>): Recommendation => ({
  id: "r", establishmentId: null, category: "stock_reorder", ruleKey: "k", title: "Réapprovisionner", why: "", evidence: {},
  impactEstimate: {}, proposedAction: "Commander 120 unités", expectedResult: "", impactScore: 1, urgencyScore: 1,
  confidenceScore: 1, confidenceLevel: "high", confidenceReason: "", priorityScore: 1, priorityLevel: "critical",
  nature: "risk", drillPath: null, periodStart: "2026-09-01", status: "new", snoozeUntil: null, outcome: null,
  outcomeNote: null, expiresAt: null, overrideValue: null, createdAt: "2026-09-27T08:00:00Z", viewedAt: null, ...over,
});

describe("Dashboard NEXORA ONE (§6)", () => {
  it("greets according to the time of day with the first name", () => {
    expect(greeting(new Date(2026, 8, 27, 8), "Marie")).toBe("Bonjour, Marie");
    expect(greeting(new Date(2026, 8, 27, 14), "Marie")).toBe("Bon après-midi, Marie");
    expect(greeting(new Date(2026, 8, 27, 21), null)).toBe("Bonsoir");
    expect(firstName("Jean-Pierre Louis")).toBe("Jean-Pierre");
    expect(firstName(null, "direction@abc-group.demo")).toBe("Direction");
  });

  it("compares with the previous window of the same length", () => {
    expect(previousWindow("2026-09-01", "2026-09-30")).toEqual({ from: "2026-08-02", to: "2026-08-31" });
    expect(previousWindow("2026-09-27", "2026-09-27")).toEqual({ from: "2026-09-26", to: "2026-09-26" });
  });

  it("variation is null when the base is zero instead of an infinite percentage", () => {
    expect(pctChange(110, 100)).toBe(10);
    expect(pctChange(-50, -100)).toBe(50);
    expect(pctChange(10, 0)).toBeNull();
  });

  it("risks: alerts and open risk recommendations land in Stock / Finance / Clients / Opérations, worst first", () => {
    expect(alertDomain("low_stock")).toBe("stock");
    expect(alertDomain("overdue_receivable")).toBe("clients");
    expect(alertDomain("cash_variance")).toBe("operations");
    expect(alertDomain("low_cash")).toBe("finance");

    const groups = classifyRisks(
      [{ key: "low_cash", severity: "warning", title: "Trésorerie basse", detail: "", drillPath: null, count: 1 }],
      [rec({ id: "a", category: "cashflow_risk", priorityLevel: "critical" }),
       rec({ id: "b", category: "sales_spike", nature: "opportunity", priorityLevel: "opportunity" }),
       rec({ id: "c", category: "receivable_priority", priorityLevel: "watch" })],
    );
    expect(groups.map((g) => g.domain)).toEqual(["stock", "finance", "clients", "operations"]);
    const finance = groups.find((g) => g.domain === "finance")!;
    expect(finance.items.map((i) => i.tone)).toEqual(["error", "warning"]);
    expect(groups.flatMap((g) => g.items).some((i) => i.key === "rec:b")).toBe(false);
    expect(groups.find((g) => g.domain === "clients")!.items).toHaveLength(1);
  });
});
