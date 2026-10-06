import { expect, test } from "@playwright/test";
import {
  RUN, createProductUi, createSupplierUi, field, forbidNativeDialogs, loginUi, onHand, receiveWithoutOrderUi, verifier,
  watchServerErrors,
} from "./support";

/**
 * Plan maître §42 — SCÉNARIO 3 : inventaire → constater différence → valider → vérifier mouvement → vérifier audit.
 * 20 unités en stock, 17 comptées : l'écart de −3 devient un mouvement de stock valorisé et laisse une trace d'audit.
 */
test("Scénario 3 — inventaire, écart, validation, mouvement, audit", async ({ page, baseURL }) => {
  const natives = forbidNativeDialogs(page);
  const serverErrors = watchServerErrors(page);
  const v = await verifier(baseURL!);
  const product = { sku: `E2E-S3-${RUN}`, name: `Huile 1 L ${RUN}`, salePrice: 500, purchasePrice: 300 };
  const supplier = `Fournisseur Huile ${RUN}`;
  let productId = "";
  let countId = "";
  type EntrySummary = { id: string; description: string | null; totalDebit: number; isBalanced: boolean };
  const varianceEntries = async () => (await v.get<EntrySummary[]>(`/accounting-entries?q=${encodeURIComponent("Écart d'inventaire")}&limit=500`))
    .filter((e) => e.description === "Écart d'inventaire");
  let entriesBefore: EntrySummary[] = [];

  await loginUi(page);
  await test.step("préparer : produit, fournisseur, 20 unités reçues", async () => {
    productId = await createProductUi(page, v, product);
    await createSupplierUi(page, supplier);
    await receiveWithoutOrderUi(page, supplier, { ...product, quantity: 20, unitCost: product.purchasePrice });
    expect(await onHand(v, productId)).toBe(20);
  });

  await test.step("ouvrir un inventaire partiel de l'article", async () => {
    await page.goto("/inventory");
    await page.getByRole("tab", { name: "Inventaires" }).click();
    await page.getByRole("button", { name: "Nouvel inventaire" }).click();
    const d = page.getByRole("dialog", { name: "Nouvel inventaire" });
    await expect(field(d, "Emplacement")).not.toHaveValue("");
    await field(d, "Type").selectOption("partial");
    await field(d, "Ajouter un article").fill(product.sku);
    await d.getByRole("listbox", { name: "Articles trouvés" }).getByText(product.name).click();
    const created = page.waitForResponse((r) => /\/inventory\/counts$/.test(new URL(r.url()).pathname) && r.request().method() === "POST");
    await d.getByRole("button", { name: "Ouvrir l'inventaire" }).click();
    const res = await created;
    expect(res.status(), await res.text()).toBeLessThan(300);
    countId = JSON.parse(await res.text());
    await expect(d).toBeHidden();
  });

  await test.step("compter 17 au lieu de 20, justifier, valider", async () => {
    entriesBefore = await varianceEntries();
    const drawer = page.getByRole("dialog", { name: /^Inventaire / });
    await expect(drawer).toBeVisible();
    await field(drawer, `Quantité comptée — ${product.name}`).fill("17");
    await field(drawer, `Justification — ${product.name}`).fill("Trois bouteilles cassées en réserve");
    await drawer.getByRole("button", { name: "Enregistrer le comptage" }).click();
    await drawer.getByRole("button", { name: "Valider l'inventaire" }).click();
    const confirm = page.getByRole("dialog", { name: "Valider l'inventaire" });
    await field(confirm, "Motif").fill("Inventaire tournant de fin de semaine");
    const validated = page.waitForResponse((r) => r.url().includes(`/inventory/counts/${countId}/validate`));
    await confirm.getByRole("button", { name: "Valider définitivement" }).click();
    const res = await validated;
    expect(res.status(), await res.text()).toBeLessThan(300);
    await expect(confirm).toBeHidden();
  });

  await test.step("stock : 17 ; mouvement d'inventaire de −3 valorisé au coût", async () => {
    expect(await onHand(v, productId)).toBe(17);
    const movements = await v.get<Array<{ quantity: number; unitCost: number; movementType: string; referenceType: string | null }>>(
      `/stock/movements?productId=${productId}`);
    const adjustment = movements.find((m) => m.movementType === "count");
    expect(adjustment, JSON.stringify(movements)).toBeTruthy();
    expect(adjustment!.referenceType).toBe("stock_adjustment");
    expect(adjustment!.quantity).toBe(-3);
    expect(adjustment!.unitCost).toBe(product.purchasePrice);
    // l'écran Mouvements l'affiche
    await page.goto("/inventory");
    await page.getByRole("tab", { name: "Mouvements" }).click();
    await expect(page.getByText(product.sku).first()).toBeVisible();
  });

  await test.step("comptabilité : une écriture d'écart d'inventaire de 3 × 300, équilibrée", async () => {
    const after = await varianceEntries();
    const added = after.filter((e) => !entriesBefore.some((b) => b.id === e.id));
    expect(added).toHaveLength(1);
    expect(added[0].totalDebit).toBe(3 * product.purchasePrice);
    expect(added[0].isBalanced).toBe(true);
  });

  await test.step("audit : l'inventaire et sa validation sont tracés", async () => {
    const audit = await v.get<{ events: Array<{ action: string; entityType: string; userId: string | null }>; total: number }>(
      `/security/audit?entityId=${countId}&pageSize=100`);
    expect(audit.total, "événements d'audit de l'inventaire").toBeGreaterThan(0);
    expect(audit.events.every((e) => e.userId), "chaque événement porte son auteur").toBe(true);
  });

  await v.dispose();
  expect(natives, "boîtes natives").toEqual([]);
  expect(serverErrors, "erreurs serveur").toEqual([]);
});
