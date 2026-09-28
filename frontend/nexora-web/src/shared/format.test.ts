import { describe, expect, it } from "vitest";
import { dateShort, parseDate, setFormatLocale, todayIso } from "@/shared/format";

describe("dates civiles (défaut fuseau horaire, phase 3)", () => {
  it("« AAAA-MM-JJ » est lu comme une date locale, jamais comme minuit UTC", () => {
    const d = parseDate("2026-09-28");
    expect([d.getFullYear(), d.getMonth(), d.getDate()]).toEqual([2026, 8, 28]);
  });

  it("une date seule s'affiche au même jour quel que soit le fuseau", () => {
    setFormatLocale("fr-FR");
    expect(dateShort("2026-09-28")).toBe("28/09/2026");
  });

  it("todayIso utilise la date locale (après 20 h à Port-au-Prince, on reste au jour même)", () => {
    expect(todayIso(new Date(2026, 8, 28, 23, 30))).toBe("2026-09-28");
    expect(todayIso(new Date(2026, 0, 5, 0, 1))).toBe("2026-01-05");
  });
});
