import { describe, expect, it } from "vitest";
import { pageDocument, parseCell, readTable, screenKey } from "@/shared/tableExport";

/** Construit un tableau HTML comme ceux rendus par DataTable. */
function table(html: string): HTMLTableElement {
  const host = document.createElement("div");
  host.innerHTML = `<table class="nx-table">${html}</table>`;
  document.body.appendChild(host);
  return host.querySelector("table")!;
}

describe("tableExport — relire un tableau affiché pour un vrai Excel", () => {
  it("parseCell retrouve montants fr-FR, pourcentages, dates et heures", () => {
    expect(parseCell("4 246,00 HTG")).toEqual({ value: 4246, kind: "money", currency: "HTG" });
    expect(parseCell("−1 250,50 USD")).toEqual({ value: -1250.5, kind: "money", currency: "USD" });
    expect(parseCell("12,5 %")).toEqual({ value: 12.5, kind: "percent" });
    expect(parseCell("42")).toEqual({ value: 42, kind: "integer" });
    expect(parseCell("3,25")).toEqual({ value: 3.25, kind: "number" });
    expect(parseCell("15/09/2026")).toEqual({ value: "2026-09-15", kind: "date" });
    expect(parseCell("24 sept. 2026").value).toBe("2026-09-24");
    const dt = parseCell("24 sept. 2026 à 10:28");
    expect(dt.kind).toBe("datetime");
    expect(new Date(dt.value as string).getHours()).toBe(10);
    expect(parseCell("—")).toEqual({ value: null, kind: "text" });
  });

  it("parseCell laisse en texte les identifiants chiffrés (téléphone, code)", () => {
    expect(parseCell("50934567890")).toEqual({ value: "50934567890", kind: "text" });
    expect(parseCell("00123")).toEqual({ value: "00123", kind: "text" });
    expect(parseCell("CRD-2026-000001")).toEqual({ value: "CRD-2026-000001", kind: "text" });
  });

  it("readTable type les colonnes, ignore les actions et reprend la ligne de totaux", () => {
    const t = table(`
      <thead><tr><th>Crédit</th><th class="nx-num">Reste dû</th><th>Émis le</th><th></th></tr></thead>
      <tbody>
        <tr><td><div>CRD-1</div><div>Vente</div></td><td>4 246,00 HTG</td><td>15/09/2026</td><td><button>Verser</button></td></tr>
        <tr><td>CRD-2</td><td>750,00 HTG</td><td>—</td><td><button>Verser</button></td></tr>
      </tbody>
      <tfoot><tr><td>Total</td><td>4 996,00 HTG</td><td></td><td></td></tr></tfoot>`);
    const r = readTable(t, "Crédits");
    expect(r.columns).toEqual([
      { label: "Crédit", kind: "text" },
      { label: "Reste dû", kind: "money", currency: "HTG" },
      { label: "Émis le", kind: "date" },
    ]);
    expect(r.rows).toEqual([["CRD-1 Vente", 4246, "2026-09-15"], ["CRD-2", 750, null]]);
    expect(r.totals).toEqual(["Total", 4996, null]);
  });

  it("readTable ignore la ligne « Rien à afficher »", () => {
    const t = table(`<thead><tr><th>A</th><th>B</th></tr></thead><tbody><tr><td colspan="2">Rien à afficher.</td></tr></tbody>`);
    expect(readTable(t).rows).toEqual([]);
  });

  it("pageDocument réunit les tableaux (titre de leur carte) et les indicateurs de la page", () => {
    document.body.innerHTML = `
      <main id="nx-main"><h1>Achats &amp; fournisseurs</h1>
        <div class="nx-card nx-kpi"><div class="nx-kpi__label">Dettes ouvertes</div><div class="nx-kpi__value">12 000,00 HTG</div></div>
        <div class="nx-card"><h3>Dettes</h3><table><thead><tr><th>Fournisseur</th><th>Solde</th></tr></thead>
          <tbody><tr><td>Distrimed</td><td>3 000,00 HTG</td></tr></tbody></table></div>
      </main>`;
    const doc = pageDocument();
    expect(doc.title).toBe("Achats & fournisseurs");
    expect(doc.summary).toEqual([{ label: "Dettes ouvertes", value: 12000, kind: "money", currency: "HTG" }]);
    expect(doc.tables?.[0].title).toBe("Dettes");
    expect(doc.tables?.[0].rows).toEqual([["Distrimed", 3000]]);
  });

  it("screenKey dérive une clé sûre de l'écran et de l'onglet", () => {
    window.history.pushState({}, "", "/customers/abc?tab=credits");
    expect(screenKey()).toBe("customers-credits");
    window.history.pushState({}, "", "/");
    expect(screenKey()).toBe("accueil");
  });
});
