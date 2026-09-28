/** Formateurs sensibles à la langue (Phase 13 §33, §40). Le fallback est toujours fr-FR. */

let currentLocale = "fr-FR";

export function setFormatLocale(locale: string | null | undefined) {
  currentLocale = locale && locale.length >= 2 ? locale : "fr-FR";
}

export function money(value: number, currency = "HTG", opts: { sign?: boolean } = {}): string {
  const s = value.toLocaleString(currentLocale, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  const withSign = opts.sign && value > 0 ? `+${s}` : s;
  return `${withSign} ${currency}`;
}

export function number(value: number, maxFrac = 0): string {
  return value.toLocaleString(currentLocale, { maximumFractionDigits: maxFrac });
}

export function percent(value: number, maxFrac = 1): string {
  return `${value.toLocaleString(currentLocale, { maximumFractionDigits: maxFrac })} %`;
}

/**
 * Lecture d'une date : « AAAA-MM-JJ » (date seule, sans heure) est une date CIVILE locale. `new Date("2026-09-28")`
 * la lirait comme minuit UTC et l'afficherait la veille à l'ouest de Greenwich (Haïti, UTC−4).
 */
export function parseDate(iso: string): Date {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
  return m ? new Date(Number(m[1]), Number(m[2]) - 1, Number(m[3])) : new Date(iso);
}

/** Date du jour « AAAA-MM-JJ » dans le fuseau de l'utilisateur (et non en UTC). */
export function todayIso(d: Date = new Date()): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export function dateShort(iso: string | Date | null): string {
  if (!iso) return "—";
  const d = typeof iso === "string" ? parseDate(iso) : iso;
  if (Number.isNaN(d.getTime())) return String(iso);
  return d.toLocaleDateString(currentLocale, { day: "2-digit", month: "2-digit", year: "numeric" });
}

export function dateTime(iso: string | Date | null): string {
  if (!iso) return "—";
  const d = typeof iso === "string" ? parseDate(iso) : iso;
  if (Number.isNaN(d.getTime())) return String(iso);
  return d.toLocaleString(currentLocale, { dateStyle: "medium", timeStyle: "short" });
}

/** Date longue dans la langue de l'application (« samedi 27 septembre »). */
export function dateLong(d: Date = new Date()): string {
  return d.toLocaleDateString(currentLocale, { weekday: "long", day: "numeric", month: "long" });
}

/** Distance temporelle courte, style « il y a 3 min ». */
export function relative(iso: string | null): string {
  if (!iso) return "—";
  const mins = Math.round((Date.now() - new Date(iso).getTime()) / 60000);
  if (Number.isNaN(mins)) return "—";
  if (mins < 1) return "à l'instant";
  if (mins < 60) return `il y a ${mins} min`;
  if (mins < 1440) return `il y a ${Math.round(mins / 60)} h`;
  return `il y a ${Math.round(mins / 1440)} j`;
}
