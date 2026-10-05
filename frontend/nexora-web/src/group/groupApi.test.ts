import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import {
  archiveCompany, canReconcile, counterpartCandidates, getControlCenter, reconcileIntercompany, runActions, setMapping,
  type IntercompanyTx,
} from "@/group/groupApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

const tx = (over: Partial<IntercompanyTx>): IntercompanyTx => ({
  id: "x", sourceCompanyId: "A", sourceCompany: "A", targetCompanyId: "B", targetCompany: "B", transactionType: "sale_purchase",
  amount: 100, currency: "HTG", occurredAt: "2026-10-01", status: "confirmed", matchStatus: "unmatched",
  sourceDocument: null, targetDocument: null, counterpartId: null, matchDifference: null, ...over,
});

/** Finalisation phase 14 — groupe : en-tête de périmètre, rapprochement intersociétés, cycle de consolidation. */
describe("groupApi", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c1", onUnauthorized: () => {} }));
  afterEach(() => vi.unstubAllGlobals());

  it("toutes les routes groupe portent X-Group-Scope", async () => {
    const fetchMock = vi.fn(async (_u: string, _i?: RequestInit) => jsonResponse(200, {}));
    vi.stubGlobal("fetch", fetchMock);
    await getControlCenter("g1");
    await setMapping("g1", "c1", "7011", "GA-REV");
    await archiveCompany("g1", "c2", "Cessation");
    for (const call of fetchMock.mock.calls) expect(new Headers(call[1]?.headers).get("X-Group-Scope")).toBe("true");
    expect(fetchMock.mock.calls[1][0]).toContain("/groups/g1/accounts/mappings");
    expect(fetchMock.mock.calls[1][1]?.method).toBe("PUT");
    expect(JSON.parse(String(fetchMock.mock.calls[2][1]?.body))).toEqual({ reason: "Cessation" });
  });

  it("rapprochement : contrepartie choisie ou recherche automatique (null)", async () => {
    const fetchMock = vi.fn(async (_u: string, _i?: RequestInit) => jsonResponse(200, { status: "matched" }));
    vi.stubGlobal("fetch", fetchMock);
    await reconcileIntercompany("g1", "s1", null);
    expect(JSON.parse(String(fetchMock.mock.calls[0][1]?.body))).toEqual({ sourceTransactionId: "s1", counterpartTransactionId: null });
  });

  it("contreparties : sens inverse, confirmées, non rapprochées, les plus proches en montant d'abord", () => {
    const source = tx({ id: "s", amount: 1000 });
    const all = [
      source,
      tx({ id: "same-way", amount: 1000 }),                                                        // même sens : jamais
      tx({ id: "draft", sourceCompanyId: "B", targetCompanyId: "A", amount: 1000, status: "draft" }), // brouillon : jamais
      tx({ id: "matched", sourceCompanyId: "B", targetCompanyId: "A", amount: 1000, matchStatus: "matched" }),
      tx({ id: "far", sourceCompanyId: "B", targetCompanyId: "A", amount: 700 }),
      tx({ id: "near", sourceCompanyId: "B", targetCompanyId: "A", amount: 990, matchStatus: "asymmetric" }),
    ];
    expect(counterpartCandidates(source, all).map((t) => t.id)).toEqual(["near", "far"]);
    expect(canReconcile(tx({ status: "draft" }))).toBe(false);
    expect(canReconcile(tx({ matchStatus: "asymmetric" }))).toBe(true);
    expect(canReconcile(tx({ matchStatus: "matched", status: "reconciled" }))).toBe(false);
  });

  it("cycle de consolidation : validation seulement prête ET cohérente", () => {
    expect(runActions({ status: "ready", coherenceOk: false })).toEqual({ execute: true, validate: false, publish: false, reopen: false });
    expect(runActions({ status: "ready", coherenceOk: true }).validate).toBe(true);
    expect(runActions({ status: "validated", coherenceOk: true })).toEqual({ execute: false, validate: false, publish: true, reopen: true });
    expect(runActions({ status: "published", coherenceOk: true })).toEqual({ execute: false, validate: false, publish: false, reopen: true });
  });
});
