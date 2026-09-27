import type { PrintColumn, PrintDocument, PrintFigure, PrintKind, PrintTable, PrintValue } from "@/shared/printApi";

/**
 * Export de « n'importe quel rapport » : relit un tableau affiché (ou tous ceux d'une page) et en fait
 * un document typé — les montants redeviennent des nombres, les dates des dates — pour produire un vrai
 * classeur Excel ou un PDF côté serveur. Les colonnes d'actions (boutons) sont ignorées.
 */

type Parsed = { value: PrintValue; kind: PrintKind; currency?: string };

const MONTHS: Record<string, number> = {
  "janv.": 1, janvier: 1, "févr.": 2, février: 2, mars: 3, "avr.": 4, avril: 4, mai: 5, juin: 6,
  "juil.": 7, juillet: 7, août: 8, "sept.": 9, septembre: 9, "oct.": 10, octobre: 10, "nov.": 11, novembre: 11,
  "déc.": 12, décembre: 12,
};

/** Espaces de groupement des nombres fr-FR : insécable (U+00A0) et fine insécable (U+202F). */
const NBSP = /[  ]/g;
const MINUS = "−";

const pad = (n: number) => String(n).padStart(2, "0");
const isoDate = (y: number, m: number, d: number) => `${y}-${pad(m)}-${pad(d)}`;
/** Heure murale du navigateur → instant ISO (le serveur la replace dans le fuseau de l'entreprise). */
const isoInstant = (y: number, m: number, d: number, h: number, min: number) => new Date(y, m - 1, d, h, min).toISOString();

/** Relit une valeur affichée : montant « 4 246,00 HTG », nombre, pourcentage, date « 15/09/2026 »,
 * date-heure « 24 sept. 2026 à 10:28 » ; sinon texte. */
export function parseCell(raw: string): Parsed {
  const text = raw.replace(NBSP, " ").replace(/\s+/g, " ").trim();
  if (text === "" || text === "—" || text === "-") return { value: null, kind: "text" };

  let m = /^(\d{2})\/(\d{2})\/(\d{4})$/.exec(text);
  if (m) return { value: isoDate(+m[3], +m[2], +m[1]), kind: "date" };
  m = /^(\d{2})\/(\d{2})\/(\d{4})[ ,à]+(\d{1,2}):(\d{2})$/.exec(text);
  if (m) return { value: isoInstant(+m[3], +m[2], +m[1], +m[4], +m[5]), kind: "datetime" };
  m = /^(\d{1,2}) ([a-zéû.]+) (\d{4})(?:(?: à |, | )(\d{1,2}):(\d{2}))?$/i.exec(text);
  if (m && MONTHS[m[2].toLowerCase()]) {
    const month = MONTHS[m[2].toLowerCase()];
    return m[4] !== undefined
      ? { value: isoInstant(+m[3], month, +m[1], +m[4], +m[5]), kind: "datetime" }
      : { value: isoDate(+m[3], month, +m[1]), kind: "date" };
  }

  m = /^([+\-−]?)([\d ]+(?:,\d+)?)\s*(%|[A-Z]{3})?$/.exec(text);
  // Une suite de chiffres sans séparateur (téléphone, code « 00123 ») est un identifiant, pas un nombre :
  // l'application affiche toujours les nombres avec séparateurs de milliers.
  const digits = m ? m[2].trim() : "";
  const identifier = m !== null && !m[3] && !/[ ,]/.test(digits) && (digits.length >= 6 || /^0\d/.test(digits));
  if (m && /\d/.test(m[2]) && !identifier) {
    const n = Number((m[1] === MINUS || m[1] === "-" ? "-" : "") + m[2].replace(/ /g, "").replace(",", "."));
    if (Number.isFinite(n)) {
      if (m[3] === "%") return { value: n, kind: "percent" };
      if (m[3]) return { value: n, kind: "money", currency: m[3] };
      return { value: n, kind: Number.isInteger(n) && !m[2].includes(",") ? "integer" : "number" };
    }
  }
  return { value: text, kind: "text" };
}

const BLOCK = new Set(["DIV", "P", "LI", "BR", "TR", "SECTION", "UL", "OL"]);
const SKIP = new Set(["BUTTON", "SELECT", "INPUT", "TEXTAREA", "SCRIPT", "STYLE", "svg", "SVG"]);

/** Texte visible d'une cellule, sans les commandes (boutons, champs) ; les blocs sont séparés d'un espace. */
export function cellText(node: Node): string {
  let out = "";
  const walk = (n: Node) => {
    if (n.nodeType === Node.TEXT_NODE) { out += n.textContent ?? ""; return; }
    if (n.nodeType !== Node.ELEMENT_NODE) return;
    const el = n as Element;
    if (SKIP.has(el.tagName) || el.getAttribute("data-export") === "skip" || el.getAttribute("aria-hidden") === "true") return;
    const block = BLOCK.has(el.tagName);
    if (block) out += " ";
    el.childNodes.forEach(walk);
    if (block) out += " ";
  };
  walk(node);
  return out.replace(/\s+/g, " ").trim();
}

const hasOnlyControls = (cell: Element) =>
  cellText(cell) === "" && cell.querySelector("button, select, input, a[href]") !== null;

const EMPTY: Parsed = { value: null, kind: "text" };

/** Tableau HTML → tableau typé (en-têtes, lignes, ligne de totaux du tfoot). */
export function readTable(table: HTMLTableElement, title?: string | null): PrintTable {
  const headers = Array.from(table.tHead?.rows[0]?.cells ?? table.rows[0]?.cells ?? []);
  const bodyRows = Array.from(table.tBodies).flatMap((b) => Array.from(b.rows))
    .filter((r) => !(r.cells.length === 1 && headers.length > 1));          // ligne « Rien à afficher »
  const footRow = table.tFoot?.rows[0] ?? null;

  const keep = headers.map((h, i) => {
    const label = cellText(h);
    if (label === "" || /^actions?$/i.test(label)) return false;
    const cells = bodyRows.map((r) => r.cells[i]).filter(Boolean);
    return !(cells.length > 0 && cells.every(hasOnlyControls));
  });

  const parsed = bodyRows.map((r) => headers.map((_, i) => (r.cells[i] ? parseCell(cellText(r.cells[i])) : EMPTY)));
  const columns: PrintColumn[] = [];
  const indexes: number[] = [];
  headers.forEach((h, i) => {
    if (!keep[i]) return;
    indexes.push(i);
    const values = parsed.map((row) => row[i]).filter((p) => p.value !== null);
    columns.push({ label: cellText(h), ...inferKind(values) });
  });

  const cast = (p: Parsed, col: PrintColumn): PrintValue => {
    if (p.value === null) return null;
    if (col.kind === "text") return p.kind === "text" ? p.value : displayOf(p);
    return p.value;
  };
  const rows = parsed.map((row) => indexes.map((i, c) => cast(row[i], columns[c])));
  let totals: PrintValue[] | null = null;
  if (footRow) {
    // Le pied peut fusionner des cellules : on l'aligne par la droite sur les colonnes du tableau.
    const cells = Array.from(footRow.cells);
    const expanded: Parsed[] = cells.flatMap((c) => [parseCell(cellText(c)), ...Array.from({ length: (c.colSpan || 1) - 1 }, () => EMPTY)]);
    const aligned: Parsed[] = [...Array.from({ length: Math.max(0, headers.length - expanded.length) }, () => EMPTY), ...expanded];
    totals = indexes.map((i, c) => (aligned[i] ? cast(aligned[i], columns[c]) : null));
  }
  return { title: title ?? null, columns, rows, totals };
}

/** Texte d'origine d'une valeur typée qui retombe dans une colonne texte. */
function displayOf(p: Parsed): string {
  if (p.kind === "money") return `${p.value} ${p.currency ?? ""}`.trim();
  return String(p.value);
}

function inferKind(values: Parsed[]): { kind: PrintKind; currency?: string | null } {
  if (values.length === 0) return { kind: "text" };
  const kinds = new Set(values.map((v) => v.kind));
  if (kinds.size === 1 && (kinds.has("date") || kinds.has("datetime") || kinds.has("percent"))) return { kind: values[0].kind };
  if (kinds.has("date") && kinds.has("datetime") && kinds.size === 2) return { kind: "datetime" };
  if ([...kinds].every((k) => k === "money" || k === "number" || k === "integer")) {
    if (kinds.has("money")) {
      const currencies = new Set(values.map((v) => v.currency).filter(Boolean));
      return { kind: "money", currency: currencies.size === 1 ? [...currencies][0] : null };
    }
    return { kind: kinds.has("number") ? "number" : "integer" };
  }
  return { kind: "text" };
}

/** Titre d'un tableau : titre de sa carte, sinon titre de la page. */
export function guessTitle(el: Element): string {
  const card = el.closest(".nx-card, section");
  const heading = card?.querySelector("h1, h2, h3, h4");
  const text = heading ? cellText(heading) : "";
  return text || pageTitle();
}

export function pageTitle(): string {
  const h = document.querySelector("#nx-main h1, main h1");
  return (h && cellText(h)) || document.title || "Export";
}

/** Onglet actif (sous-titre de l'export). */
export function activeTab(): string | null {
  const tab = document.querySelector("#nx-main [role=tab][aria-selected=true]");
  return tab ? cellText(tab) : null;
}

/** Un tableau affiché → document exportable. */
export function tableDocument(table: HTMLTableElement, title?: string): PrintDocument {
  const t = title || guessTitle(table);
  const pt = readTable(table, null);
  return { title: t, subtitle: activeTab(), tables: [pt], landscape: pt.columns.length > 7 };
}

/** Tous les tableaux et indicateurs d'une page → un document (une feuille Excel par tableau). */
export function pageDocument(root: ParentNode = document.querySelector("#nx-main") ?? document.body): PrintDocument {
  const tables = Array.from(root.querySelectorAll<HTMLTableElement>("table"))
    .filter((t) => t.closest("[aria-hidden=true]") === null)
    .map((t) => readTable(t, guessTitle(t)));
  const summary: PrintFigure[] = Array.from(root.querySelectorAll(".nx-kpi")).flatMap((k) => {
    const label = k.querySelector(".nx-kpi__label");
    const value = k.querySelector(".nx-kpi__value");
    if (!label || !value) return [];
    const p = parseCell(cellText(value));
    return [{ label: cellText(label), value: p.value, kind: p.kind, currency: p.currency ?? null }];
  });
  return { title: pageTitle(), subtitle: activeTab(), tables, summary, landscape: tables.some((t) => t.columns.length > 7) };
}

/** Clé d'écran journalisée (ex. « customers-credits »). */
export function screenKey(extra?: string | null): string {
  const base = (window.location.pathname.split("/").filter(Boolean)[0] ?? "accueil").toLowerCase();
  const tab = (extra ?? new URLSearchParams(window.location.search).get("tab") ?? "").toLowerCase();
  return `${base}${tab ? `-${tab}` : ""}`.replace(/[^a-z0-9_.:-]/g, "-").slice(0, 80) || "ecran";
}
