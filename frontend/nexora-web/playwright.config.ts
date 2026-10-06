import { tmpdir } from "node:os";
import { join } from "node:path";
import { defineConfig, devices } from "@playwright/test";

// Traces et rapports hors du dépôt : un dossier synchronisé (OneDrive…) verrouille les traces pendant leur écriture.
const artifacts = process.env.E2E_ARTIFACTS ?? join(tmpdir(), "nexora-e2e");

/**
 * Suite E2E globale (finalisation phase 19, plan maître §41-43) : scénarios métier complets menés dans l'interface
 * comme un utilisateur, contre une installation réelle (API + base + proxy), avec vérification croisée stock, caisse,
 * créances, dettes et comptabilité.
 *
 *   E2E_BASE_URL   adresse de l'installation (défaut http://localhost:8090 — infrastructure/docker-compose.e2e.yml)
 *   E2E_EMAIL      compte de direction de l'entreprise de test (défaut direction@e2e.ht)
 *   E2E_PASSWORD   son mot de passe
 *
 * Chaque scénario crée ses propres données (suffixe unique par exécution) : la suite se rejoue sur la même base.
 */
export default defineConfig({
  testDir: "./e2e",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  expect: { timeout: 15_000 },
  reporter: [["list"], ["html", { open: "never", outputFolder: join(artifacts, "report") }]],
  outputDir: join(artifacts, "results"),
  globalSetup: "./e2e/global-setup.ts",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:8090",
    locale: "fr-FR",
    timezoneId: "America/Port-au-Prince",
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    ignoreHTTPSErrors: true,
    actionTimeout: 15_000,
    navigationTimeout: 30_000,
  },
  projects: [{ name: "chromium", use: { ...devices["Desktop Chrome"], viewport: { width: 1440, height: 900 } } }],
});
