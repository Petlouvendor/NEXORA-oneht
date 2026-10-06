import { expect, test } from "@playwright/test";
import {
  CASHIER, MANAGER, RUN, closeCashUi, closeLeftoverSessions, createCustomerUi, createProductUi, field, forbidNativeDialogs,
  loginUi, openCashUi, sellUi, verifier, watchServerErrors, type Entry,
} from "./support";

/**
 * Plan maître §42 — SCÉNARIO 2 : créer client → vente à crédit → vérifier créance → enregistrer paiement → vérifier solde.
 * Un versement partiel puis le solde : le compte client, la créance et la comptabilité restent cohérents (§43).
 */
test("Scénario 2 — vente à crédit, créance, versements, solde", async ({ page, browser, baseURL }) => {
  const natives = forbidNativeDialogs(page);
  const serverErrors = watchServerErrors(page);
  const v = await verifier(baseURL!);
  const cashierApi = await verifier(baseURL!, CASHIER);
  const managerApi = await verifier(baseURL!, MANAGER);
  const service = { sku: `E2E-S2-${RUN}`, name: `Installation électrique ${RUN}`, salePrice: 2000, service: true };
  const customerName = `Client Crédit ${RUN}`;
  let customerId = "";

  await loginUi(page);
  await test.step("la direction crée la prestation et le client, avec une limite de crédit de 10 000", async () => {
    await createProductUi(page, v, service);
    customerId = await createCustomerUi(page, v, customerName, 10_000);
    const account = await v.get<{ creditLimit: number; availableCredit: number; balanceDue: number }>(`/customers/${customerId}/account`);
    expect(account.creditLimit).toBe(10_000);
    expect(account.availableCredit).toBe(10_000);
    expect(account.balanceDue).toBe(0);
  });

  const posContext = await browser.newContext();
  const pos = await posContext.newPage();
  const posNatives = forbidNativeDialogs(pos);
  const posErrors = watchServerErrors(pos);
  let sale!: Awaited<ReturnType<typeof sellUi>>;
  await test.step("le caissier vend 2 prestations à crédit, sans rien encaisser", async () => {
    await closeLeftoverSessions([{ owner: cashierApi, validator: v }, { owner: v, validator: managerApi }]);
    await loginUi(pos, CASHIER);
    await openCashUi(pos, 500);
    sale = await sellUi(pos, { lines: [{ ...service, quantity: 2 }], customer: customerName, credit: true });
    expect(sale.subtotal).toBe(2 * service.salePrice);
    expect(sale.amountPaid).toBe(0);
    expect(sale.creditAmount).toBe(sale.total);
    expect(sale.receivableId).toBeTruthy();
  });

  await test.step("créance : le compte client doit le total, la comptabilité porte la créance", async () => {
    const account = await v.get<{ balanceDue: number; creditPurchases: number; availableCredit: number }>(`/customers/${customerId}/account`);
    expect(account.balanceDue).toBe(sale.total);
    expect(account.creditPurchases).toBe(sale.total);
    expect(account.availableCredit).toBe(10_000 - sale.total);
    const credits = await v.get<Array<{ status: string; amount?: number; remaining?: number; balance?: number }>>(`/customers/${customerId}/credits`);
    expect(credits).toHaveLength(1);
    const entry = await v.get<Entry>(`/accounting-entries/${sale.journalEntryId}`);
    expect(entry.isBalanced).toBe(true);
    expect(entry.lines.filter((l) => l.debit > 0).map((l) => l.debit), "créance client au débit").toContain(sale.total);
  });

  await test.step("caisse : aucune vente au comptant, clôture au seul fond initial", async () => {
    const summary = await closeCashUi(pos, 500);
    expect(summary.cashSales).toBe(0);
    expect(summary.expectedBalance).toBe(500);
    expect(summary.difference).toBe(0);
  });

  const pay = async (amount: number) => {
    await page.goto(`/customers/${customerId}`);
    await page.getByRole("button", { name: "Enregistrer un versement" }).click();
    const d = page.getByRole("dialog", { name: `Versement — ${customerName}` });
    await field(d, "Montant").fill(String(amount));
    const account = field(d, "Compte d'encaissement");
    if (await account.count()) {
      if (!(await account.inputValue())) await account.selectOption({ index: 1 });
    }
    await d.getByRole("button", { name: /^Enregistrer/ }).click();
    const done = page.getByRole("dialog", { name: /^Versement .* enregistré$/ });
    await expect(done).toBeVisible();
    await done.getByRole("button", { name: "Fermer", exact: true }).last().click();
  };

  await test.step("versement partiel de 1 500 : le solde baisse d'autant", async () => {
    await pay(1500);
    const account = await v.get<{ balanceDue: number; totalPaid: number }>(`/customers/${customerId}/account`);
    expect(account.totalPaid).toBe(1500);
    expect(account.balanceDue).toBe(sale.total - 1500);
  });

  await test.step("versement du solde : plus rien à payer, crédit disponible rétabli", async () => {
    await pay(sale.total - 1500);
    const account = await v.get<{ balanceDue: number; totalPaid: number; availableCredit: number }>(`/customers/${customerId}/account`);
    expect(account.balanceDue).toBe(0);
    expect(account.totalPaid).toBe(sale.total);
    expect(account.availableCredit).toBe(10_000);
    // et l'écran du compte client l'affiche
    await page.goto(`/customers/${customerId}`);
    await expect(page.getByText("Solde dû").first()).toBeVisible();
  });

  await Promise.all([v.dispose(), cashierApi.dispose(), managerApi.dispose(), posContext.close()]);
  expect([...natives, ...posNatives], "boîtes natives").toEqual([]);
  expect([...serverErrors, ...posErrors], "erreurs serveur").toEqual([]);
});
