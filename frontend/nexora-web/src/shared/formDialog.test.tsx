import { act, useEffect } from "react";
import { createRoot } from "react-dom/client";
import { afterEach, describe, expect, it } from "vitest";
import { useFormDialog, type FormDialogOptions } from "@/shared/FormDialog";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

/** Ouvre la fenêtre au montage et expose sa promesse de résultat. */
function Harness({ options, onResult }: { options: FormDialogOptions; onResult: (v: Record<string, string> | null) => void }) {
  const form = useFormDialog();
  useEffect(() => { void form.ask(options).then(onResult); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  return <>{form.dialog}</>;
}

const flush = () => act(async () => { await new Promise((r) => setTimeout(r, 0)); });
const button = (label: string) => [...document.querySelectorAll("button")].find((b) => b.textContent?.includes(label))!;
async function type(input: HTMLInputElement, value: string) {
  await act(async () => {
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, "value")!.set!.call(input, value);
    input.dispatchEvent(new Event("input", { bubbles: true }));
  });
}

/** Analyse 2026-10, §8.5 : la fenêtre reste ouverte jusqu'à la réponse du serveur et garde la saisie en cas de refus. */
describe("fenêtre de formulaire avec enregistrement intégré", () => {
  afterEach(() => { document.body.innerHTML = ""; });

  it("refus du serveur : la fenêtre reste ouverte, affiche le motif et garde la saisie ; succès : elle se ferme", async () => {
    let attempts = 0;
    const results: Array<Record<string, string> | null> = [];
    const host = document.createElement("div");
    document.body.appendChild(host);
    await act(async () => {
      createRoot(host).render(<Harness onResult={(v) => results.push(v)} options={{
        title: "Résoudre", confirmLabel: "Résoudre",
        fields: [{ name: "cause", label: "Cause", required: true }],
        onSubmit: async () => { attempts += 1; if (attempts === 1) throw new Error("Période close : impossible d'enregistrer."); },
      }} />);
    });

    const input = document.querySelector<HTMLInputElement>("input")!;
    await type(input, "Fibre coupée");
    await act(async () => { button("Résoudre").click(); });
    await flush();

    expect(document.querySelector('[role="alert"]')?.textContent).toContain("Période close");
    expect(document.querySelector<HTMLInputElement>("input")!.value).toBe("Fibre coupée");
    expect(results).toEqual([]);

    await act(async () => { button("Résoudre").click(); });
    await flush();
    expect(attempts).toBe(2);
    expect(results).toEqual([{ cause: "Fibre coupée" }]);
    expect(document.querySelector("input")).toBeNull();
  });
});
