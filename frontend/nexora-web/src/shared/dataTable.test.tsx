import { act } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, describe, expect, it } from "vitest";
import { DataTable, type Column } from "@/shared/ui";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

interface Row { id: string; name: string; amount: number; meta: { city: string } }
const rows: Row[] = [
  { id: "1", name: "Épicerie Nord", amount: 300, meta: { city: "Cap-Haïtien" } },
  { id: "2", name: "Boulangerie", amount: 1200, meta: { city: "Jacmel" } },
  { id: "3", name: "Agence Sud", amount: 50, meta: { city: "Les Cayes" } },
  ...Array.from({ length: 6 }, (_, i) => ({ id: `x${i}`, name: `Magasin ${i}`, amount: 100 + i, meta: { city: "Port-au-Prince" } })),
];
const columns: Array<Column<Row>> = [
  { key: "name", header: "Nom", render: (r) => r.name },
  { key: "amount", header: "Montant", numeric: true, render: (r) => r.amount },
  { key: "act", header: "", render: () => <button type="button">Ouvrir</button> },
];

async function mount(): Promise<HTMLElement> {
  const host = document.createElement("div");
  document.body.appendChild(host);
  await act(async () => { createRoot(host).render(<DataTable columns={columns} rows={rows} rowKey={(r) => r.id} />); });
  return host;
}
const firstCells = (host: HTMLElement) => [...host.querySelectorAll("tbody tr")].map((tr) => tr.querySelector("td")?.textContent);

/** Finalisation phase 20 (§46) — filtre rapide et tri du tableau commun. */
describe("tableau commun : filtre et tri", () => {
  afterEach(() => { document.body.innerHTML = ""; });

  it("filtre sans tenir compte des accents ni de la casse, y compris dans les sous-objets", async () => {
    const host = await mount();
    const search = host.querySelector<HTMLInputElement>('input[aria-label="Filtrer ce tableau"]')!;
    const type = async (v: string) => act(async () => {
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!.call(search, v);
      search.dispatchEvent(new Event("input", { bubbles: true }));
    });
    await type("epicerie");
    expect(firstCells(host)).toEqual(["Épicerie Nord"]);
    await type("jacmel");
    expect(firstCells(host)).toEqual(["Boulangerie"]);
    await type("introuvable");
    expect(host.querySelector("tbody")!.textContent).toContain("Aucune ligne ne correspond");
  });

  it("trie au clic (croissant, décroissant, sans tri), annonce le sens, ignore la colonne d'actions", async () => {
    const host = await mount();
    const amountHeader = [...host.querySelectorAll("th")].find((th) => th.textContent?.startsWith("Montant"))!;
    const button = amountHeader.querySelector("button")!;
    await act(async () => { button.click(); });
    expect(amountHeader.getAttribute("aria-sort")).toBe("ascending");
    expect(firstCells(host)[0]).toBe("Agence Sud");
    await act(async () => { button.click(); });
    expect(amountHeader.getAttribute("aria-sort")).toBe("descending");
    expect(firstCells(host)[0]).toBe("Boulangerie");
    await act(async () => { button.click(); });
    expect(amountHeader.getAttribute("aria-sort")).toBe("none");
    const actionHeader = host.querySelectorAll("th")[2];
    expect(actionHeader.querySelector("button")).toBeNull();
  });

  it("peu de lignes : pas de filtre affiché", async () => {
    const host = document.createElement("div");
    document.body.appendChild(host);
    await act(async () => { createRoot(host).render(<DataTable columns={columns} rows={rows.slice(0, 3)} rowKey={(r) => r.id} />); });
    expect(host.querySelector('input[aria-label="Filtrer ce tableau"]')).toBeNull();
  });
});
