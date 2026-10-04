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

describe("langue de formatage (finalisation phase 12)", () => {
  it("le créole, que le navigateur ne sait pas formater, retombe sur le français d'Haïti — jamais l'anglais", async () => {
    const { setFormatLocale, formatLocale, dateTime } = await import("@/shared/format");
    setFormatLocale("ht-HT");
    expect(formatLocale()).toMatch(/^fr/);
    expect(dateTime("2026-10-03T20:51:00")).not.toMatch(/Oct|PM/);
    setFormatLocale("fr-FR");
  });
});
