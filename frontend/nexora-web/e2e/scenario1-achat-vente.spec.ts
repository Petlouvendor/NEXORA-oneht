import { expect, test } from "@playwright/test";
import {
  CASHIER, MANAGER, RUN, closeCashUi, closeLeftoverSessions, confirmIdentityIfAsked, createCustomerUi, createProductUi,
  createSupplierUi, field, forbidNativeDialogs, loginUi, onHand, openCashUi, sellUi, verifier, watchServerErrors,
  type CheckoutResult, type Entry, type Verifier,
} from "./support";

/**
 * Plan maître §42 — SCÉNARIO 1 : créer produit → créer fournisseur → commande fournisseur → recevoir → vérifier stock
 * → vendre → vérifier stock → vérifier caisse → vérifier client → vérifier comptabilité.
 * Toutes les actions passent par l'interface ; les effets croisés sont vérifiés par l'API (§43).
 */
test.describe.serial("Scénario 1 — du produit à la comptabilité", () => {
  const product = { sku: `E2E-S1-${RUN}`, name: `Riz parfumé 5 kg ${RUN}`, salePrice: 1250, purchasePrice: 800 };
  const supplierName = `Fournisseur Riz ${RUN}`;
  let v: Verifier;
  let productId = "";

  test.beforeAll(async ({ baseURL }) => { v = await verifier(baseURL!); });

  test("achat : produit, fournisseur, commande, réception, stock", async ({ page }) => {
    const natives = forbidNativeDialogs(page);
    const serverErrors = watchServerErrors(page);
    await loginUi(page);

    await test.step("créer le produit et le fournisseur", async () => {
      productId = await createProductUi(page, v, product);
      await createSupplierUi(page, supplierName);
    });

    await test.step("commander 40 unités", async () => {
      await page.goto("/purchasing");
      await page.getByRole("button", { name: "Nouvelle commande" }).click();
      const d = page.getByRole("dialog", { name: "Nouvelle commande fournisseur" });
      await field(d, "Fournisseur").selectOption({ label: supplierName });
      await field(d, "Ajouter un article").fill(product.sku);
      await d.getByRole("listbox", { name: "Articles trouvés" }).getByText(product.name).click();
      await field(d, `Quantité — ${product.name}`).fill("40");
      await field(d, `Prix unitaire — ${product.name}`).fill(String(product.purchasePrice));
      await d.getByRole("button", { name: "Soumettre à approbation" }).click();
      await expect(d).toBeHidden();
    });

    await test.step("approuver puis réceptionner les 40 unités", async () => {
      const drawer = page.getByRole("dialog", { name: /^Commande / });
      await expect(drawer).toBeVisible();
      await drawer.getByRole("button", { name: "Approuver", exact: true }).click();
      const confirm = page.getByRole("dialog", { name: "Approuver la commande" });
      await confirm.getByRole("button", { name: "Approuver", exact: true }).click();
      await confirmIdentityIfAsked(page);
      await expect(confirm).toBeHidden();
      await drawer.getByRole("button", { name: "Réceptionner" }).click();
      const r = page.getByRole("dialog", { name: /^Réception de la commande/ });
      await expect(field(r, "Emplacement de réception")).not.toHaveValue("");   // seul emplacement : présélectionné
      await field(r, `Quantité acceptée — ${product.name}`).fill("40");
      await r.getByRole("button", { name: "Valider la réception" }).click();
      await expect(r).toBeHidden();
    });

    await test.step("vérifier le stock : 40 unités valorisées au coût d'achat", async () => {
      const balances = await v.get<Array<{ productId: string; onHand: number; averageCost: number; stockValue: number }>>("/stock/balances");
      const mine = balances.filter((b) => b.productId === productId);
      expect(mine.reduce((t, b) => t + b.onHand, 0)).toBe(40);
      expect(mine[0].averageCost).toBe(product.purchasePrice);
      expect(mine.reduce((t, b) => t + b.stockValue, 0)).toBe(40 * product.purchasePrice);
      await page.goto("/inventory");
      await expect(page.getByText(product.name).first()).toBeVisible();
    });

    expect(natives, "boîtes natives").toEqual([]);
    expect(serverErrors, "erreurs serveur").toEqual([]);
  });

  test("vente : client, caisse, stock, compte client, comptabilité, clôture", async ({ page, browser, baseURL }) => {
    const natives = forbidNativeDialogs(page);
    const serverErrors = watchServerErrors(page);
    // Le caissier tient la caisse dans sa propre session de navigateur ; la direction crée le client et vérifie.
    const cashierContext = await browser.newContext();
    const pos = await cashierContext.newPage();
    const posNatives = forbidNativeDialogs(pos);
    const posErrors = watchServerErrors(pos);
    const cashierApi = await verifier(baseURL!, CASHIER);
    const managerApi = await verifier(baseURL!, MANAGER);
    const customerName = `Client Comptant ${RUN}`;
    const opening = 1000;
    let customerId = "";
    let sale!: CheckoutResult;
    await loginUi(page);

    await test.step("la direction crée le client", async () => {
      customerId = await createCustomerUi(page, v, customerName);
    });

    await test.step("le caissier ouvre sa caisse et vend 3 unités au client, en espèces", async () => {
      await closeLeftoverSessions([{ owner: cashierApi, validator: v }, { owner: v, validator: managerApi }]);
      await loginUi(pos, CASHIER);
      await openCashUi(pos, opening);
      sale = await sellUi(pos, { lines: [{ ...product, quantity: 3 }], customer: customerName });
      expect(sale.total).toBe(sale.subtotal + sale.taxTotal);
      expect(sale.subtotal).toBe(3 * product.salePrice);
    });

    await test.step("stock : 40 − 3 = 37", async () => {
      expect(await onHand(v, productId)).toBe(37);
    });

    await test.step("comptabilité : écriture équilibrée, chiffre d'affaires, taxe et coût des ventes", async () => {
      const entry = await v.get<Entry>(`/accounting-entries/${sale.journalEntryId}`);
      expect(entry.isBalanced).toBe(true);
      expect(entry.totalDebit).toBe(entry.totalCredit);
      const credits = entry.lines.filter((l) => l.credit > 0).map((l) => l.credit);
      const debits = entry.lines.filter((l) => l.debit > 0).map((l) => l.debit);
      expect(credits, "produit des ventes (HT)").toContain(sale.subtotal);
      if (sale.taxTotal > 0) expect(credits, "taxe collectée").toContain(sale.taxTotal);
      expect(debits, "encaissement").toContain(sale.total);
      // Coût des ventes au coût moyen d'achat : 3 × 800, en débit (charge) et en crédit (sortie de stock).
      expect(debits, "coût des ventes").toContain(3 * product.purchasePrice);
      expect(credits, "sortie de stock").toContain(3 * product.purchasePrice);
    });

    await test.step("client : l'achat figure sur son compte, rien à payer", async () => {
      const account = await v.get<{ totalPurchases: number; purchaseCount: number; balanceDue: number }>(`/customers/${customerId}/account`);
      expect(account.purchaseCount).toBe(1);
      expect(account.totalPurchases).toBe(sale.total);
      expect(account.balanceDue).toBe(0);
    });

    await test.step("caisse : clôture au montant attendu, sans écart", async () => {
      const summary = await closeCashUi(pos, opening + sale.total);
      expect(summary.expectedBalance).toBe(opening + sale.total);
      expect(summary.cashSales).toBe(sale.total);
      expect(summary.difference).toBe(0);
    });

    await cashierContext.close();
    expect([...natives, ...posNatives], "boîtes natives").toEqual([]);
    expect([...serverErrors, ...posErrors], "erreurs serveur").toEqual([]);
  });
});
