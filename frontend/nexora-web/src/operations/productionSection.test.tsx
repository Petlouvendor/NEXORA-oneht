import { act } from "react";
import { createRoot } from "react-dom/client";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { productionReadiness, type ProductionReadiness } from "@/operations/operationsApi";
import { ProductionSection } from "@/operations/ProductionSection";

(globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }).IS_REACT_ACT_ENVIRONMENT = true;

const sample: ProductionReadiness = {
  environment: "Production", version: "1.18.0", commit: "6ed0b7bd602b478d", startedAt: "2026-10-06T16:00:00Z",
  uptimeSeconds: 7_500, overall: "fail",
  probes: [
    { name: "database", status: "Healthy", description: "PostgreSQL joignable", durationMs: 2 },
    { name: "migrations", status: "Unhealthy", description: "1 migration(s) en attente", durationMs: 4 },
  ],
  migrations: { embedded: 95, applied: 94, latestApplied: "V094", latestEmbedded: "V095", pending: ["V095__future.sql"], drifted: [], unknown: [] },
  checks: [
    { key: "observability", label: "Traces et métriques (OpenTelemetry)", category: "monitoring", status: "warn", detail: "aucun collecteur" },
    { key: "secrets", label: "Secrets de production", category: "security", status: "fail", detail: "Jwt:SigningKey absente" },
    { key: "https", label: "HTTPS", category: "security", status: "pass", detail: "TLS terminé par le proxy" },
  ],
  generatedAt: "2026-10-06T18:05:00Z",
};

const json = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { "Content-Type": "application/json" } });

/** Finalisation phase 18 — vue « Production » de l'exploitation. */
describe("état de production", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => { vi.unstubAllGlobals(); document.body.innerHTML = ""; });

  it("lit /ops/production", async () => {
    const fetchMock = vi.fn(async () => json(sample));
    vi.stubGlobal("fetch", fetchMock);
    expect((await productionReadiness()).version).toBe("1.18.0");
    expect(String((fetchMock.mock.calls[0] as unknown[])[0])).toContain("/ops/production");
  });

  it("affiche version, migrations en attente et bloquants en premier", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => json(sample)));
    const host = document.createElement("div");
    document.body.appendChild(host);
    await act(async () => { createRoot(host).render(<ProductionSection />); });
    await act(async () => { await new Promise((r) => setTimeout(r, 0)); });

    const text = host.textContent ?? "";
    expect(text).toContain("1.18.0");
    expect(text).toContain("6ed0b7bd602b");
    expect(text).toContain("2 h 5 min");
    expect(text).toContain("V095__future.sql");
    expect(text).toContain("bloquants à corriger");
    // Tri : le contrôle bloquant (secrets) avant l'avertissement (observabilité), avant le conforme (HTTPS).
    expect(text.indexOf("Secrets de production")).toBeLessThan(text.indexOf("Traces et métriques"));
    expect(text.indexOf("Traces et métriques")).toBeLessThan(text.indexOf("HTTPS"));
  });

  it("l'écran Exploitation n'utilise plus aucune boîte native du navigateur", () => {
    for (const file of ["OperationsPage.tsx", "MaintenanceSections.tsx", "ProductionSection.tsx"]) {
      const source = readFileSync(resolve(process.cwd(), "src/operations", file), "utf8");
      expect(source, file).not.toMatch(/(^|[^.\w])(prompt|confirm|alert)\(/m);
    }
  });
});
