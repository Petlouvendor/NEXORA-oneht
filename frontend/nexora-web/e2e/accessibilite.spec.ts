import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "@playwright/test";
import { CASHIER, loginUi } from "./support";

/**
 * Plan maître §47-48 (finalisation phase 20) : accessibilité et responsive des écrans principaux, sur l'interface
 * réelle. Audit axe-core (WCAG 2.1 A et AA) : aucune violation « grave » ou « critique » ; sur téléphone (390 px),
 * aucun défilement horizontal de la page et des cibles tactiles d'au moins 40 px à la caisse.
 */
const SCREENS: Array<{ path: string; name: string }> = [
  { path: "/", name: "Tableau de bord" },
  { path: "/catalog", name: "Catalogue" },
  { path: "/inventory", name: "Stock" },
  { path: "/purchasing", name: "Achats" },
  { path: "/customers", name: "Clients" },
  { path: "/orders", name: "Ventes & commandes" },
  { path: "/finance", name: "Finance" },
  { path: "/accounting", name: "Comptabilité" },
  { path: "/reports", name: "Rapports" },
  { path: "/operations", name: "Exploitation" },
  { path: "/security", name: "Sécurité" },
  { path: "/admin", name: "Administration" },
];

async function audit(page: import("@playwright/test").Page) {
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  return result.violations
    .filter((v) => v.impact === "serious" || v.impact === "critical")
    .map((v) => `${v.id} (${v.impact}, ${v.nodes.length}×) : ${v.help} — ${v.nodes.slice(0, 2).map((n) => n.target.join(" ")).join(" | ")}`);
}

test.describe("Accessibilité et responsive", () => {
  test("écran de connexion", async ({ page }) => {
    await page.goto("/login");
    await expect(page.getByLabel("E-mail")).toBeVisible();
    expect(await audit(page)).toEqual([]);
  });

  test("écrans principaux : aucune violation grave (WCAG 2.1 AA)", async ({ page }) => {
    await loginUi(page);
    const report: Record<string, string[]> = {};
    for (const s of SCREENS) {
      await page.goto(s.path);
      await page.waitForLoadState("networkidle");
      await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
      const violations = await audit(page);
      if (violations.length) report[s.name] = violations;
    }
    expect(report).toEqual({});
  });

  test("thème sombre (Dark Premium) : aucune violation grave", async ({ page }) => {
    await page.emulateMedia({ colorScheme: "dark" });
    await loginUi(page);
    const report: Record<string, string[]> = {};
    for (const s of SCREENS.slice(0, 6)) {
      await page.goto(s.path);
      await page.waitForLoadState("networkidle");
      const violations = await audit(page);
      if (violations.length) report[s.name] = violations;
    }
    expect(report).toEqual({});
  });

  test("caisse sur téléphone : pas de défilement horizontal, cibles tactiles de 40 px au moins", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true });
    const page = await context.newPage();
    await loginUi(page, CASHIER);
    await page.goto("/pos");
    await page.waitForLoadState("networkidle");
    expect(await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth), "défilement horizontal").toBeLessThanOrEqual(0);
    const small = await page.evaluate(() => [...document.querySelectorAll("main button, main [role=button]")]
      .filter((el) => (el as HTMLElement).offsetParent !== null)
      .map((el) => ({ label: (el.getAttribute("aria-label") || el.textContent || "").trim().slice(0, 40), ...(() => {
        const r = el.getBoundingClientRect(); return { w: Math.round(r.width), h: Math.round(r.height) };
      })() }))
      .filter((b) => b.h < 40 || b.w < 40));
    expect(small, "boutons de caisse trop petits pour le doigt").toEqual([]);
    expect(await audit(page)).toEqual([]);
    await context.close();
  });

  test("écrans principaux sur téléphone : pas de défilement horizontal", async ({ browser }) => {
    const context = await browser.newContext({ viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true });
    const page = await context.newPage();
    await loginUi(page);
    const overflow: Record<string, number> = {};
    for (const s of SCREENS) {
      await page.goto(s.path);
      await page.waitForLoadState("networkidle");
      const extra = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
      if (extra > 0) overflow[s.name] = extra;
    }
    expect(overflow).toEqual({});
    await context.close();
  });
});
