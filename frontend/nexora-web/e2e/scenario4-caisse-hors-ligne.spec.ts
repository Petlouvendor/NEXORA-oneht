import { expect, test } from "@playwright/test";
import {
  CASHIER, MANAGER, RUN, closeCashUi, closeLeftoverSessions, createProductUi, createSupplierUi, field, forbidNativeDialogs,
  loginUi, onHand, openCashUi, receiveWithoutOrderUi, verifier, watchServerErrors,
} from "./support";

/**
 * Plan maître §42 — SCÉNARIO 4 : POS offline → synchroniser catalogue → couper réseau → vendre → reconnecter →
 * synchroniser → vérifier absence de doublon. Le lot synchronisé est ensuite REJOUÉ tel quel : le serveur le
 * reconnaît (identifiant d'opération) et ne crée pas de seconde vente.
 */
test("Scénario 4 — caisse hors ligne, synchronisation, absence de doublon", async ({ page, browser, baseURL }) => {
  const natives = forbidNativeDialogs(page);
  const serverErrors = watchServerErrors(page);
  const v = await verifier(baseURL!);
  const cashierApi = await verifier(baseURL!, CASHIER);
  const managerApi = await verifier(baseURL!, MANAGER);
  const product = { sku: `E2E-S4-${RUN}`, name: `Savon 200 g ${RUN}`, salePrice: 650, purchasePrice: 400 };
  let productId = "";

  await loginUi(page);
  await test.step("préparer : produit et 10 unités en stock", async () => {
    productId = await createProductUi(page, v, product);
    await createSupplierUi(page, `Savonnerie ${RUN}`);
    await receiveWithoutOrderUi(page, `Savonnerie ${RUN}`, { ...product, quantity: 10, unitCost: product.purchasePrice });
    expect(await onHand(v, productId)).toBe(10);
  });

  const posContext = await browser.newContext();
  const pos = await posContext.newPage();
  const posNatives = forbidNativeDialogs(pos);
  await test.step("synchroniser le catalogue : la caisse ouverte garde son profil hors ligne", async () => {
    await closeLeftoverSessions([{ owner: cashierApi, validator: v }, { owner: v, validator: managerApi }]);
    await loginUi(pos, CASHIER);
    const profile = pos.waitForResponse(async (r) => r.url().includes("/pos/offline-profile") && r.ok()
      && (await r.text()).includes(product.sku));
    await openCashUi(pos, 300);
    await profile;
  });

  let pushBody: unknown = null;
  await test.step("couper le réseau et vendre 2 unités : la vente part en file d'attente", async () => {
    await posContext.setOffline(true);
    await expect(pos.getByText("Hors ligne", { exact: true }).first()).toBeVisible();
    await expect(pos.getByText(/^Catalogue du /)).toBeVisible();
    const search = pos.getByLabel("Rechercher ou scanner un produit");
    await search.fill(product.sku);
    await search.press("Enter");
    await field(pos, `Quantité — ${product.name}`).fill("2");
    await pos.getByRole("button", { name: /^Encaisser/ }).click();
    await pos.getByRole("dialog", { name: "Confirmer l'encaissement" }).getByRole("button", { name: "Valider la vente" }).click();
    await expect(pos.getByText(/vente mise en file d'attente/)).toBeVisible();
    await expect(pos.getByText("1 non synchronisée(s)")).toBeVisible();
  });

  await test.step("pendant la coupure, le serveur n'a rien reçu", async () => {
    expect(await onHand(v, productId)).toBe(10);
  });

  await test.step("reconnecter : synchronisation automatique, vente confirmée", async () => {
    const pushed = pos.waitForResponse((r) => r.url().includes("/sync/batch") && r.request().method() === "POST");
    await posContext.setOffline(false);
    const res = await pushed;
    expect(res.status(), await res.text()).toBe(200);
    pushBody = res.request().postDataJSON();
    const result = await res.json() as { operations: Array<{ status: string; message: string | null }> };
    expect(result.operations.map((o) => o.status)).toEqual(["SYNCED"]);
    await expect(pos.getByText("1 non synchronisée(s)")).toBeHidden();
    expect(await onHand(v, productId)).toBe(8);
  });

  await test.step("rejouer le même lot : aucune seconde vente (absence de doublon)", async () => {
    const replay = await cashierApi.post<{ operations: Array<{ status: string }> }>("/sync/batch", pushBody);
    expect(replay.operations).toHaveLength(1);
    expect(["SYNCED", "DUPLICATE"]).toContain(replay.operations[0].status);
    expect(await onHand(v, productId), "stock inchangé par le rejeu").toBe(8);
    const session = await cashierApi.get<{ saleCount: number }>("/pos/sessions/current");
    expect(session.saleCount, "une seule vente dans la session").toBe(1);
  });

  await test.step("clôture : la caisse attend le fond initial plus cette seule vente", async () => {
    await pos.reload();
    const firstCount = await closeCashUi(pos, 300 + 2 * product.salePrice);
    expect(firstCount.cashSales).toBeGreaterThanOrEqual(2 * product.salePrice);
    expect(firstCount.cashSales, "une seule vente encaissée").toBeLessThan(4 * product.salePrice);
    expect(firstCount.expectedBalance).toBe(300 + firstCount.cashSales);
  });

  await Promise.all([v.dispose(), cashierApi.dispose(), managerApi.dispose()]);
  await posContext.close();
  expect([...natives, ...posNatives], "boîtes natives").toEqual([]);
  expect(serverErrors, "erreurs serveur").toEqual([]);
});
