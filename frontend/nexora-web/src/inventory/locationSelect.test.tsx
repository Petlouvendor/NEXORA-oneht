import { act, useState } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, describe, expect, it } from "vitest";
import { LocationSelect } from "@/inventory/stockShared";
import type { Location } from "@/inventory/inventoryApi";
import { MATCH_STATUS } from "@/purchasing/purchasingShared";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const place = (id: string, name: string) => ({ id, name, status: "active", locationType: "store" }) as unknown as Location;

function Harness({ locations, required, allowAll }: { locations: Location[]; required?: boolean; allowAll?: boolean }) {
  const [value, setValue] = useState("");
  return (
    <>
      <LocationSelect value={value} onChange={setValue} locations={locations} required={required} allowAll={allowAll} />
      <output>{value}</output>
    </>
  );
}

async function render(node: React.ReactElement): Promise<HTMLElement> {
  const host = document.createElement("div");
  document.body.appendChild(host);
  await act(async () => { createRoot(host).render(node); });
  return host;
}

/** Finalisation phase 19 — défauts révélés par la suite E2E. */
describe("emplacement unique et statuts de rapprochement", () => {
  afterEach(() => { document.body.innerHTML = ""; });

  it("un seul emplacement possible et obligatoire : présélectionné", async () => {
    const host = await render(<Harness required locations={[place("mag", "Magasin")]} />);
    expect(host.querySelector("output")!.textContent).toBe("mag");
  });

  it("plusieurs emplacements, champ facultatif ou « tous » : aucun choix imposé", async () => {
    expect((await render(<Harness required locations={[place("a", "A"), place("b", "B")]} />)).querySelector("output")!.textContent).toBe("");
    expect((await render(<Harness locations={[place("a", "A")]} />)).querySelector("output")!.textContent).toBe("");
    expect((await render(<Harness required allowAll locations={[place("a", "A")]} />)).querySelector("output")!.textContent).toBe("");
  });

  it("chaque statut réel de rapprochement fournisseur (V039) a un libellé français", () => {
    for (const status of ["unmatched", "matched", "variance", "blocked", "waived"]) {
      expect(MATCH_STATUS[status], status).toBeTruthy();
      expect(MATCH_STATUS[status]).not.toBe(status);
    }
  });
});
