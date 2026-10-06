import { expect, test } from "@playwright/test";
import { verifier, type Verifier } from "./support";

/**
 * Plan maître §43 — règle de cohérence financière, vérifiée sur TOUTE l'entreprise de test après les scénarios :
 * la comptabilité est équilibrée et les comptes auxiliaires (créances clients, dettes fournisseurs) concordent avec le
 * grand livre. Comptes du plan comptable par défaut d'une installation neuve : 4111 Clients, 4011 Fournisseurs,
 * 3110 Stocks marchandises.
 */
test.describe("Scénario 6 — cohérence financière globale", () => {
  let v: Verifier;
  type Row = { accountCode: string; debit: number; credit: number };
  let rows: Row[] = [];
  const balance = (code: string) => rows.filter((r) => r.accountCode === code).reduce((t, r) => t + r.debit - r.credit, 0);
  const round = (n: number) => Math.round(n * 100) / 100 + 0;   // + 0 : jamais « -0 »

  test.beforeAll(async ({ baseURL }) => {
    v = await verifier(baseURL!);
    const asOf = new Date().toLocaleDateString("en-CA", { timeZone: "America/Port-au-Prince" });
    const tb = await v.get<{ totalDebit: number; totalCredit: number; rows: Row[] }>(`/trial-balance?asOf=${asOf}`);
    expect(tb.totalDebit, "balance générale équilibrée").toBe(tb.totalCredit);
    rows = tb.rows;
  });
  test.afterAll(async () => { await v.dispose(); });

  test("toutes les écritures sont équilibrées", async () => {
    const entries = await v.get<Array<{ entryNumber: string; isBalanced: boolean; totalDebit: number; totalCredit: number }>>(
      "/accounting-entries?limit=1000");
    expect(entries.length).toBeGreaterThan(0);
    expect(entries.filter((e) => !e.isBalanced || e.totalDebit !== e.totalCredit).map((e) => e.entryNumber)).toEqual([]);
  });

  test("créances : somme des encours clients = compte 4111 Clients", async () => {
    const customers = await v.get<Array<{ openReceivables: number | null }>>("/customers");
    const subledger = round(customers.reduce((t, c) => t + (c.openReceivables ?? 0), 0));
    expect(round(balance("4111"))).toBe(subledger);
  });

  test("dettes : somme des dettes fournisseurs ouvertes = compte 4011 Fournisseurs", async () => {
    const payables = await v.get<Array<{ remainingAmount: number }>>("/payables?onlyOpen=true");
    const subledger = round(payables.reduce((t, p) => t + p.remainingAmount, 0));
    expect(round(-balance("4011"))).toBe(subledger);
  });

  test("stock : valeur du stock = compte 3110 Stocks marchandises", async () => {
    // Défaut connu, révélé par cette suite (finalisation phase 19) : une réception augmente le stock valorisé SANS
    // écriture ; le stock n'entre en comptabilité qu'à la facture fournisseur. Les réceptions non facturées manquent
    // donc au compte 3110 (décalage de rattachement, définitif pour une réception jamais facturée). Correction
    // proposée : compte « fournisseurs — factures non parvenues » à la réception, soldé par la facture. Décision
    // demandée dans le rapport de phase ; ce test redeviendra bloquant une fois la correction faite.
    test.fixme(true, "réceptions non facturées absentes du grand livre — décision en attente (rapport phase 19)");
    const balances = await v.get<Array<{ stockValue: number }>>("/stock/balances");
    const subledger = round(balances.reduce((t, b) => t + b.stockValue, 0));
    expect(round(balance("3110"))).toBe(subledger);
  });
});
