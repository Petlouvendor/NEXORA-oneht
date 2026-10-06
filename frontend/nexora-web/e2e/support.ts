import { expect, request, type APIRequestContext, type Locator, type Page } from "@playwright/test";

/** Comptes de l'entreprise de test : la direction (créée par « bootstrap ») et un caissier (créé par global-setup). */
export const E2E = {
  email: process.env.E2E_EMAIL ?? "direction@e2e.ht",
  password: process.env.E2E_PASSWORD ?? "E2e!Nexora2026",
};
export const CASHIER = {
  email: process.env.E2E_CASHIER_EMAIL ?? "caisse@e2e.ht",
  password: process.env.E2E_CASHIER_PASSWORD ?? "Tiroir!Nexora2026",
};
export const MANAGER = {
  email: process.env.E2E_MANAGER_EMAIL ?? "responsable@e2e.ht",
  password: process.env.E2E_MANAGER_PASSWORD ?? "Magasin!Nexora2026",
};
export type Account = { email: string; password: string };

/** Suffixe unique par exécution : chaque scénario crée ses propres données, la suite se rejoue sur la même base. */
export const RUN = Date.now().toString(36).slice(-6).toUpperCase();

/** Montant affiché (« 1 234,50 HTG », espaces fines) → nombre. */
export function amount(text: string): number {
  const cleaned = text.replace(/[^\d,.-]/g, "").replace(/\.(?=\d{3}(\D|$))/g, "").replace(",", ".");
  return Number(cleaned);
}

// ---------------------------------------------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------------------------------------------

/** Champ par son libellé exact, astérisque d'obligation toléré (« Nom* »). */
export function field(scope: Page | Locator, label: string): Locator {
  const escaped = label.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  return scope.getByLabel(new RegExp(`^${escaped}\\s*\\*?$`));
}

/** Connexion par l'écran de connexion, comme un utilisateur. */
export async function loginUi(page: Page, who: Account = E2E): Promise<void> {
  await page.goto("/login");
  await page.getByLabel("E-mail").fill(who.email);
  await page.getByLabel("Mot de passe").fill(who.password);
  await page.getByRole("button", { name: "Se connecter" }).click();
  await expect(page).not.toHaveURL(/\/login/);
}

/** Confirmation forte (phase 17) : si l'action est critique, la fenêtre s'ouvre — on confirme avec le mot de passe. */
export async function confirmIdentityIfAsked(page: Page, timeout = 3_000, who: Account = E2E): Promise<boolean> {
  const dialog = page.getByRole("dialog", { name: "Confirmation de sécurité" });
  try {
    await dialog.waitFor({ timeout });
  } catch {
    return false;
  }
  await dialog.getByLabel("Mot de passe").fill(who.password);
  await dialog.getByRole("button", { name: "Confirmer" }).click();
  await expect(dialog).toBeHidden();
  return true;
}

/** Aucune boîte native du navigateur (plan maître : interdites) — échoue le test si une apparaît. */
export function forbidNativeDialogs(page: Page): string[] {
  const seen: string[] = [];
  page.on("dialog", async (d) => { seen.push(`${d.type()}: ${d.message()}`); await d.dismiss(); });
  return seen;
}

/** Erreurs serveur (5xx) observées pendant le scénario : il ne doit y en avoir aucune. */
export function watchServerErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on("response", (r) => { if (r.status() >= 500) errors.push(`${r.status()} ${r.request().method()} ${r.url()}`); });
  return errors;
}

// ---------------------------------------------------------------------------------------------------------------
// API de vérification : les actions passent par l'interface ; l'API sert à vérifier précisément les effets croisés
// (stock, caisse, créances, dettes, comptabilité), avec la même identité que l'utilisateur.
// ---------------------------------------------------------------------------------------------------------------

export interface Verifier {
  companyId: string;
  get<T = unknown>(path: string): Promise<T>;
  post<T = unknown>(path: string, body?: unknown): Promise<T>;
  dispose(): Promise<void>;
}

/**
 * Une connexion par compte et par processus de test (la connexion est limitée par adresse : jamais une connexion par
 * vérification). Le jeton d'accès dure 15 minutes : sur 401, une seule reconnexion puis nouvel essai.
 */
const verifiers = new Map<string, Promise<Verifier>>();

export function verifier(baseURL: string, who: Account = E2E): Promise<Verifier> {
  const key = `${baseURL}|${who.email}`;
  if (!verifiers.has(key)) verifiers.set(key, createVerifier(baseURL, who).catch((e) => { verifiers.delete(key); throw e; }));
  return verifiers.get(key)!;
}

async function createVerifier(baseURL: string, who: Account): Promise<Verifier> {
  const ctx: APIRequestContext = await request.newContext({ baseURL, ignoreHTTPSErrors: true });
  let headers: Record<string, string> = {};
  let companyId = "";
  let stepUp: string | null = null;

  async function login(): Promise<void> {
    const res = await ctx.post("/api/v1/auth/login", { data: { email: who.email, password: who.password } });
    expect(res.ok(), `connexion API de ${who.email} : ${res.status()}`).toBeTruthy();
    const token = (await res.json()).accessToken as string;
    const me = await (await ctx.get("/api/v1/me", { headers: { Authorization: `Bearer ${token}` } })).json();
    companyId = me.companies?.[0]?.id ?? me.companyIds?.[0] ?? me.activeCompanyId;
    expect(companyId, "entreprise de l'utilisateur").toBeTruthy();
    headers = { Authorization: `Bearer ${token}`, "X-Company-Id": companyId };
    stepUp = null;
  }
  await login();

  async function send<T>(method: "GET" | "POST", path: string, body?: unknown): Promise<T> {
    const call = () => ctx.fetch(`/api/v1${path}`, {
      method, data: body, headers: stepUp ? { ...headers, "X-Step-Up-Token": stepUp } : headers,
    });
    let res = await call();
    if (res.status() === 401) {
      await login();
      res = await call();
    }
    if (res.status() === 403) {
      const problem = await res.json().catch(() => ({}));
      if (problem.title === "security.step_up_required") {
        const su = await ctx.post("/api/v1/security/step-up", {
          headers, data: { password: who.password, purpose: problem.purpose },
        });
        stepUp = (await su.json()).token;
        res = await call();
      }
    }
    const text = await res.text();
    expect(res.ok(), `${method} ${path} → ${res.status()} ${text.slice(0, 300)}`).toBeTruthy();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  return {
    get companyId() { return companyId; },
    get: (path) => send("GET", path),
    post: (path, body) => send("POST", path, body ?? {}),
    // Partagé par tous les tests du processus : libéré à la fin du processus, jamais par un test.
    dispose: async () => undefined,
  };
}

/**
 * Remise à zéro des caisses : une session restée ouverte par un passage interrompu est comptée par son titulaire
 * (comptage à l'aveugle), puis l'écart est validé par une AUTRE personne (règle des quatre yeux) — chaque scénario de
 * caisse part d'une caisse libre et connaît son fond initial.
 */
export async function closeLeftoverSessions(holders: Array<{ owner: Verifier; validator: Verifier }>): Promise<void> {
  for (const { owner, validator } of holders) {
    const current = await owner.get<{ sessionId: string; status: string } | undefined>("/pos/sessions/current");
    if (!current?.sessionId) continue;
    let status = current.status;
    if (status === "open") {
      status = (await owner.post<{ status: string }>("/pos/sessions/close",
        { sessionId: current.sessionId, countedBalance: 0, notes: "E2E : remise à zéro" })).status;
    }
    if (status === "closing") {
      await validator.post(`/pos/sessions/${current.sessionId}/validate-close`, { note: "E2E : remise à zéro d'une session interrompue" });
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Gestes métier réutilisés par les scénarios (toujours par l'interface)
// ---------------------------------------------------------------------------------------------------------------

export interface ProductSpec { sku: string; name: string; salePrice: number; purchasePrice?: number; service?: boolean }

/** Catalogue → Nouveau produit. Rend l'identifiant du produit créé (lu par l'API). */
export async function createProductUi(page: Page, v: Verifier, p: ProductSpec): Promise<string> {
  await page.goto("/catalog");
  await page.getByRole("button", { name: "Nouveau produit" }).click();
  const d = page.getByRole("dialog", { name: "Nouveau produit" });
  await field(d, "SKU").fill(p.sku);
  await field(d, "Nom").fill(p.name);
  if (p.service) await field(d, "Type").selectOption({ label: "Service" });
  await field(d, "Prix de vente").fill(String(p.salePrice));
  if (p.purchasePrice != null) await field(d, "Prix d'achat").fill(String(p.purchasePrice));
  await d.getByRole("button", { name: "Créer le produit" }).click();
  await expect(d).toBeHidden();
  const found = await v.get<Array<{ id: string; sku: string; status: string }>>(`/catalog/products?search=${encodeURIComponent(p.sku)}`);
  const created = found.find((x) => x.sku === p.sku);
  expect(created?.status, `produit ${p.sku}`).toBe("active");
  return created!.id;
}

/** Clients → Nouveau client (limite de crédit facultative). Rend l'identifiant du client. */
export async function createCustomerUi(page: Page, v: Verifier, name: string, creditLimit?: number): Promise<string> {
  await page.goto("/customers");
  await page.getByRole("button", { name: "Nouveau client" }).click();
  await field(page, "Nom").fill(name);
  if (creditLimit != null) await field(page, "Limite de crédit").fill(String(creditLimit));
  await page.getByRole("button", { name: "Enregistrer", exact: true }).click();
  await expect(page.getByRole("cell", { name }).first()).toBeVisible();
  const found = await v.get<Array<{ id: string; displayName: string }>>(`/customers/lookup?q=${encodeURIComponent(name)}&limit=5`);
  return found.find((c) => c.displayName === name)!.id;
}

/** POS : ouvre la caisse avec un fond initial (la caisse doit être libre, cf. closeLeftoverSessions). */
export async function openCashUi(page: Page, opening: number): Promise<void> {
  await page.goto("/pos");
  const openButton = page.getByRole("button", { name: "Ouvrir la caisse" });
  await field(page, "Fond de caisse initial").fill(String(opening));
  await openButton.click();
  await expect(openButton).toBeHidden();
}

export interface CheckoutResult {
  saleId: string; documentNumber: string; subtotal: number; taxTotal: number; total: number; amountPaid: number;
  creditAmount: number; journalEntryId: string; receivableId: string | null;
}

/** POS : ajoute des articles, choisit le client, encaisse (au comptant ou à crédit). Rend le résultat du serveur. */
export async function sellUi(page: Page, sale: {
  lines: Array<{ sku: string; name: string; quantity: number }>; customer?: string; credit?: boolean;
}): Promise<CheckoutResult> {
  const search = page.getByLabel("Rechercher ou scanner un produit");
  for (const l of sale.lines) {
    await search.fill(l.sku);
    await search.press("Enter");
    await field(page, `Quantité — ${l.name}`).fill(String(l.quantity));
  }
  if (sale.customer) {
    await page.getByLabel("Rechercher un client").fill(sale.customer);
    await page.getByRole("option", { name: new RegExp(sale.customer) }).first().click();
  }
  if (sale.credit) await page.getByLabel(/Vente à crédit/).check();
  await page.getByRole("button", { name: /^Encaisser/ }).click();
  const confirm = page.getByRole("dialog", { name: "Confirmer l'encaissement" });
  const checkout = page.waitForResponse((r) => r.url().includes("/pos/checkout") && r.request().method() === "POST");
  await confirm.getByRole("button", { name: "Valider la vente" }).click();
  const res = await checkout;
  expect(res.status(), await res.text()).toBe(200);
  return res.json();
}

/** POS : clôture à l'aveugle avec le montant compté. Rend la synthèse calculée par le serveur. */
export async function closeCashUi(page: Page, counted: number): Promise<{ expectedBalance: number; cashSales: number; difference: number; status: string }> {
  await page.getByRole("button", { name: "Clôturer" }).click();
  const closing = page.waitForResponse((r) => r.url().includes("/pos/sessions/close"));
  await field(page, `Montant compté (HTG)`).fill(String(counted));
  await page.getByRole("button", { name: "Valider le comptage" }).click();
  return (await closing).json();
}

/** Écriture comptable lue par l'API, avec ses lignes. */
export interface Entry {
  isBalanced: boolean; totalDebit: number; totalCredit: number; status: string;
  lines: Array<{ accountCode: string; accountName: string; debit: number; credit: number }>;
}

/** Fournisseurs → Nouveau fournisseur. */
export async function createSupplierUi(page: Page, name: string): Promise<void> {
  await page.goto("/suppliers");
  await page.getByRole("button", { name: "Nouveau fournisseur" }).click();
  await field(page, "Raison sociale").fill(name);
  await page.getByRole("button", { name: "Créer", exact: true }).click();
  await expect(page.getByRole("cell", { name }).first()).toBeVisible();
}

/** Achats → Réceptions → Réception sans commande, à l'emplacement unique présélectionné. */
export async function receiveWithoutOrderUi(page: Page, supplier: string, line: { sku: string; name: string; quantity: number; unitCost: number }): Promise<void> {
  await page.goto("/purchasing");
  await page.getByRole("tab", { name: "Réceptions" }).click();
  await page.getByRole("button", { name: "Réception sans commande" }).click();
  const d = page.getByRole("dialog", { name: "Réception sans commande" });
  await field(d, "Fournisseur").selectOption({ label: supplier });
  await expect(field(d, "Emplacement de réception")).not.toHaveValue("");
  await field(d, "Ajouter un article").fill(line.sku);
  await d.getByRole("listbox", { name: "Articles trouvés" }).getByText(line.name).click();
  await field(d, `Quantité acceptée — ${line.name}`).fill(String(line.quantity));
  await field(d, `Coût unitaire — ${line.name}`).fill(String(line.unitCost));
  await d.getByRole("button", { name: "Valider la réception" }).click();
  await expect(d).toBeHidden();
}

/** Quantité en stock d'un produit, tous emplacements. */
export async function onHand(v: Verifier, productId: string): Promise<number> {
  const balances = await v.get<Array<{ productId: string; onHand: number }>>("/stock/balances");
  return balances.filter((b) => b.productId === productId).reduce((t, b) => t + b.onHand, 0);
}
