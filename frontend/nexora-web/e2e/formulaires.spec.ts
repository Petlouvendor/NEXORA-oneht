import { expect, test } from "@playwright/test";
import { RUN, field, forbidNativeDialogs, loginUi, verifier, watchServerErrors } from "./support";

/**
 * Plan maître §45 (finalisation phase 20) : les anciennes suites de `window.prompt` sont devenues de vrais
 * formulaires — validation par champ, listes de choix, annulation — et créent de vraies données.
 */
test("formulaires : incident de continuité (ouverture, post-mortem) et budget", async ({ page, baseURL }) => {
  const natives = forbidNativeDialogs(page);
  const serverErrors = watchServerErrors(page);
  const v = await verifier(baseURL!);
  const incident = `Coupure du lien Internet ${RUN}`;
  await loginUi(page);

  await test.step("ouvrir un incident : validation, puis gravité et catégorie choisies dans des listes", async () => {
    await page.goto("/continuity");
    await page.getByRole("button", { name: "Ouvrir un incident" }).click();
    const d = page.getByRole("dialog", { name: "Ouvrir un incident" });
    await d.getByRole("button", { name: "Ouvrir" }).click();
    await expect(d.getByText("Obligatoire.")).toBeVisible();          // rien n'est envoyé sans titre
    await field(d, "Titre").fill(incident);
    await field(d, "Gravité").selectOption("p3");
    await field(d, "Catégorie").selectOption({ label: "Perte de réseau" });
    await d.getByRole("button", { name: "Ouvrir" }).click();
    await expect(d).toBeHidden();
    await expect(page.getByText(`P3 · ${incident}`)).toBeVisible();
  });

  await test.step("résoudre avec un post-mortem complet", async () => {
    const card = page.locator("div").filter({ hasText: `P3 · ${incident}` }).filter({ has: page.getByRole("button", { name: "Résoudre" }) }).last();
    await card.getByRole("button", { name: "Résoudre" }).click();
    const d = page.getByRole("dialog", { name: "Résoudre l'incident — post-mortem" });
    await field(d, "Cause").fill("Fibre coupée par des travaux de voirie");
    await field(d, "Impact").fill("Caisse passée hors ligne 40 minutes, ventes synchronisées ensuite");
    await field(d, "Actions correctives").fill("Bascule sur la ligne 4G de secours");
    await field(d, "Prévention").fill("Second opérateur sur le routeur");
    await field(d, "Responsable du suivi").fill("Direction E2E");
    await field(d, "Durée d'interruption observée (RTO)").fill("40");
    await d.getByRole("button", { name: "Résoudre" }).click();
    await expect(d).toBeHidden();
    const incidents = await v.get<Array<{ title: string; status: string; rtoActualMinutes?: number | null; postMortem?: { cause: string } | null }>>(
      "/continuity/incidents");
    const mine = incidents.find((i) => i.title === incident)!;
    expect(mine.status).toBe("resolved");
    expect(mine.postMortem?.cause).toContain("Fibre coupée");
  });

  await test.step("budget : exercice borné, devise choisie, puis créé", async () => {
    await page.goto("/reports");
    await page.getByRole("tab", { name: "Budgets" }).click();
    await page.getByRole("button", { name: "Nouveau budget" }).click();
    const d = page.getByRole("dialog", { name: "Nouveau budget" });
    await field(d, "Nom").fill(`Budget magasin ${RUN}`);
    await field(d, "Exercice").fill("1990");
    await d.getByRole("button", { name: "Créer" }).click();
    await expect(d.getByText(/^Au moins /)).toBeVisible();             // exercice hors bornes refusé
    await field(d, "Exercice").fill(String(new Date().getFullYear()));
    await d.getByRole("button", { name: "Créer" }).click();
    await expect(d).toBeHidden();
    await expect(page.getByRole("cell", { name: `Budget magasin ${RUN}` })).toBeVisible();
  });

  expect(natives, "boîtes natives").toEqual([]);
  expect(serverErrors, "erreurs serveur").toEqual([]);
});
