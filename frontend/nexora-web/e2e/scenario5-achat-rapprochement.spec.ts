import { expect, test } from "@playwright/test";
import {
  RUN, confirmIdentityIfAsked, createProductUi, createSupplierUi, field, forbidNativeDialogs, loginUi, onHand, verifier,
  watchServerErrors, type Entry,
} from "./support";

/**
 * Plan maître §42 — SCÉNARIO 5 : achat → commande → réception partielle → facture → rapprochement → paiement.
 * 50 commandées, 30 reçues et facturées ; le rapprochement à trois voies est conforme ; le paiement solde la dette ;
 * stock, dette fournisseur et comptabilité restent cohérents (§43).
 */
test("Scénario 5 — commande, réception partielle, facture, rapprochement, paiement", async ({ page, baseURL }) => {
  const natives = forbidNativeDialogs(page);
  const serverErrors = watchServerErrors(page);
  const v = await verifier(baseURL!);
  const product = { sku: `E2E-S5-${RUN}`, name: `Ciment 42,5 kg ${RUN}`, salePrice: 900, purchasePrice: 500 };
  const supplier = `Cimenterie ${RUN}`;
  const supplierRef = `FA-${RUN}`;
  let productId = "";
  let invoiceId = "";
  let invoiceEntryId = "";

  await loginUi(page);
  await test.step("préparer : produit et fournisseur", async () => {
    productId = await createProductUi(page, v, product);
    await createSupplierUi(page, supplier);
  });

  const drawer = page.getByRole("dialog", { name: /^Commande / });
  await test.step("commander 50, approuver", async () => {
    await page.goto("/purchasing");
    await page.getByRole("button", { name: "Nouvelle commande" }).click();
    const d = page.getByRole("dialog", { name: "Nouvelle commande fournisseur" });
    await field(d, "Fournisseur").selectOption({ label: supplier });
    await field(d, "Ajouter un article").fill(product.sku);
    await d.getByRole("listbox", { name: "Articles trouvés" }).getByText(product.name).click();
    await field(d, `Quantité — ${product.name}`).fill("50");
    await field(d, `Prix unitaire — ${product.name}`).fill(String(product.purchasePrice));
    await d.getByRole("button", { name: "Soumettre à approbation" }).click();
    await expect(d).toBeHidden();
    await drawer.getByRole("button", { name: "Approuver", exact: true }).click();
    await page.getByRole("dialog", { name: "Approuver la commande" }).getByRole("button", { name: "Approuver", exact: true }).click();
    await confirmIdentityIfAsked(page);
    await expect(drawer.getByText("Approuvée")).toBeVisible();
  });

  await test.step("réception partielle : 30 sur 50", async () => {
    await drawer.getByRole("button", { name: "Réceptionner" }).click();
    const r = page.getByRole("dialog", { name: /^Réception de la commande/ });
    await expect(field(r, "Emplacement de réception")).not.toHaveValue("");
    await field(r, `Quantité acceptée — ${product.name}`).fill("30");
    await r.getByRole("button", { name: "Valider la réception" }).click();
    await expect(r).toBeHidden();
    expect(await onHand(v, productId)).toBe(30);
    await expect(drawer.getByText(/partiellement/i).first()).toBeVisible();
  });

  await test.step("facture du fournisseur pour les 30 reçues, à crédit", async () => {
    await drawer.getByRole("button", { name: "Saisir la facture" }).click();
    const f = page.getByRole("dialog", { name: /^Facture de la commande/ });
    await field(f, "N° de facture du fournisseur").fill(supplierRef);
    await field(f, `Quantité — ${product.name}`).fill("30");
    await field(f, `Prix unitaire — ${product.name}`).fill(String(product.purchasePrice));
    const created = page.waitForResponse((r) => /\/purchasing\/invoices$/.test(new URL(r.url()).pathname) && r.request().method() === "POST");
    await f.getByRole("button", { name: "Comptabiliser la facture" }).click();
    const res = await created;
    expect(res.status(), await res.text()).toBeLessThan(300);
    const recorded = await res.json() as { invoiceId: string; payableId: string | null; journalEntryId: string };
    invoiceId = recorded.invoiceId;
    invoiceEntryId = recorded.journalEntryId;
    expect(recorded.payableId, "facture à crédit = dette").toBeTruthy();
    await expect(f).toBeHidden();
  });

  await test.step("rapprochement commande ↕ réception ↕ facture : conforme", async () => {
    await page.goto("/purchasing");
    await page.getByRole("tab", { name: "Factures" }).click();
    const row = page.getByRole("row", { name: new RegExp(supplierRef) });
    await expect(row.getByText("À rapprocher")).toBeVisible();   // libellé français, jamais le code brut
    const number = (await v.get<{ invoice: { documentNumber: string } }>(`/purchasing/invoices/${invoiceId}`)).invoice.documentNumber;
    await row.getByText(number).click();
    const inv = page.getByRole("dialog", { name: /^Facture / });
    const matched = page.waitForResponse((r) => r.url().includes(`/purchasing/invoices/${invoiceId}/match`));
    await inv.getByRole("button", { name: /^Rapprocher/ }).click();
    expect((await matched).status()).toBeLessThan(300);
    const detail = await v.get<{ invoice: { matchStatus: string; total: number } }>(`/purchasing/invoices/${invoiceId}`);
    expect(detail.invoice.matchStatus).toBe("matched");
    expect(detail.invoice.total).toBeGreaterThanOrEqual(30 * product.purchasePrice);
    await page.goto("/purchasing");
    await page.getByRole("tab", { name: "Factures" }).click();
    await expect(page.getByRole("row", { name: new RegExp(supplierRef) }).getByText("Rapprochée")).toBeVisible();
  });

  type Payable = { id: string; supplierName: string; remainingAmount: number; status: string; amount?: number };
  let payable!: Payable;
  await test.step("dette fournisseur ouverte du montant de la facture", async () => {
    const payables = await v.get<Payable[]>("/payables?onlyOpen=true");
    payable = payables.find((p) => p.supplierName === supplier)!;
    expect(payable, "dette du fournisseur").toBeTruthy();
    const detail = await v.get<{ invoice: { total: number } }>(`/purchasing/invoices/${invoiceId}`);
    expect(payable.remainingAmount).toBe(detail.invoice.total);
  });

  let paymentEntryId = "";
  await test.step("payer la dette (confirmation forte)", async () => {
    await page.goto("/purchasing");
    await page.getByRole("tab", { name: /^Dettes/ }).click();
    await page.getByRole("row", { name: new RegExp(supplier) }).getByRole("button", { name: "Payer" }).click();
    const d = page.getByRole("dialog", { name: `Payer — ${supplier}` });
    const account = field(d, "Payer depuis");
    if (!(await account.inputValue())) await account.selectOption({ index: 1 });
    const paid = page.waitForResponse((r) => r.url().includes(`/payables/${payable.id}/payments`) && r.status() !== 403);
    await d.getByRole("button", { name: /^Payer/ }).click();
    await confirmIdentityIfAsked(page);
    const res = await paid;
    expect(res.status(), await res.text()).toBeLessThan(300);
    paymentEntryId = (await res.json()).journalEntryId;
  });

  await test.step("cohérence : dette soldée, écritures équilibrées, stock et commande", async () => {
    const all = await v.get<Payable[]>("/payables?onlyOpen=false");
    expect(all.find((p) => p.id === payable.id)!.remainingAmount).toBe(0);
    const payment = await v.get<Entry>(`/accounting-entries/${paymentEntryId}`);
    expect(payment.isBalanced).toBe(true);
    expect(payment.totalDebit, "paiement = montant de la dette").toBe(payable.remainingAmount);
    const invoiceEntry = await v.get<Entry>(`/accounting-entries/${invoiceEntryId}`);
    expect(invoiceEntry.isBalanced).toBe(true);
    expect(invoiceEntry.totalCredit, "facture comptabilisée = dette").toBe(payable.remainingAmount);
    // la dette créditée par la facture est débitée par le paiement : même compte, même montant
    const payableCredit = invoiceEntry.lines.find((l) => l.credit === payable.remainingAmount)!;
    expect(payment.lines.find((l) => l.accountCode === payableCredit.accountCode)?.debit).toBe(payable.remainingAmount);
    expect(await onHand(v, productId)).toBe(30);
    const orders = await v.get<Array<{ supplierName: string; status: string }>>("/purchasing/orders");
    expect(orders.find((o) => o.supplierName === supplier)!.status).toBe("partially_received");
  });

  await v.dispose();
  expect(natives, "boîtes natives").toEqual([]);
  expect(serverErrors, "erreurs serveur").toEqual([]);
});
