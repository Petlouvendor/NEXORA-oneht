import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { configureApiClient } from "@/core/apiClient";
import { downloadPayslip, exportReport, payAdvance, payRun, repayAdvance, requestAdvance, requestLeave, runStep } from "@/hr/hrApi";

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("hrApi (module paie §5, §22, §24, §27, §39)", () => {
  beforeEach(() => configureApiClient({ token: () => "t", company: () => "c", onUnauthorized: () => {} }));
  afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

  it("payRun sends the operation UUID as idempotency key: a resend never pays twice (§39)", async () => {
    const seen: RequestInit[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      expect(url).toContain("/payroll/run-1/pay");
      seen.push(init);
      return jsonResponse(200, { paid: 1, alreadyPaid: 0, skipped: 0, payments: [], errors: [], runStatus: "paid" });
    }));

    await payRun("run-1", { financialAccountId: "fa", itemIds: null }, "op-fixed");
    await payRun("run-1", { financialAccountId: "fa", itemIds: null }, "op-fixed");

    expect(seen).toHaveLength(2);
    for (const init of seen) {
      expect(new Headers(init.headers).get("Idempotency-Key")).toBe("op-fixed");
      expect(JSON.parse(init.body as string)).toMatchObject({ operationUuid: "op-fixed", financialAccountId: "fa" });
    }
  });

  it("advance payment and repayment carry their operation UUID; amounts are never computed client-side", async () => {
    const seen: Array<{ url: string; init: RequestInit }> = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seen.push({ url, init });
      return jsonResponse(200, { id: "a1" });
    }));
    await payAdvance("a1", { financialAccountId: "fa", paymentMethod: "cash" }, "op-pay");
    await repayAdvance("a1", { amount: 1000, financialAccountId: "fa", paymentMethod: "cash" }, "op-repay");

    expect(seen[0].url).toContain("/salary-advances/a1/pay");
    expect(new Headers(seen[0].init.headers).get("Idempotency-Key")).toBe("op-pay");
    expect(JSON.parse(seen[0].init.body as string)).not.toHaveProperty("amount");
    expect(seen[1].url).toContain("/salary-advances/a1/repay");
    expect(new Headers(seen[1].init.headers).get("Idempotency-Key")).toBe("op-repay");
  });

  it("self-service requests go to /me/hr and never name another employee in the URL (§5)", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => { urls.push(url); return jsonResponse(200, {}); }));
    await requestAdvance("emp-1", { kind: "salary_advance", amount: 5000 }, true);
    await requestLeave({ employeeId: "emp-1", leaveType: "annual", startDate: "2026-10-01", endDate: "2026-10-02" }, true);
    await requestAdvance("emp-2", { kind: "salary_advance", amount: 5000 });

    expect(urls[0]).toMatch(/\/me\/hr\/advances$/);
    expect(urls[1]).toMatch(/\/me\/hr\/leaves$/);
    expect(urls[2]).toContain("/employees/emp-2/salary-advances");
  });

  it("workflow steps post the note and the warnings acknowledgement", async () => {
    let seen: { url: string; body: unknown } | null = null;
    vi.stubGlobal("fetch", vi.fn(async (url: string, init: RequestInit) => {
      seen = { url, body: JSON.parse(init.body as string) };
      return jsonResponse(200, { id: "run-1", status: "reviewed" });
    }));
    await runStep("run-1", "review", "Contrôlé", true);
    expect(seen).toEqual({ url: expect.stringContaining("/payroll/run-1/review"), body: { note: "Contrôlé", acknowledgeWarnings: true } });
  });

  it("payslips and reports download real files with the server file name", async () => {
    const urls: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (url: string) => {
      urls.push(url);
      return new Response(new Uint8Array([0x25, 0x50, 0x44, 0x46]), {
        status: 200, headers: { "Content-Type": "application/pdf", "Content-Disposition": 'attachment; filename="bulletin-BUL-2026-000001.pdf"' },
      });
    }));
    const anchor = document.createElement("a");
    const click = vi.spyOn(anchor, "click").mockImplementation(() => {});
    vi.spyOn(document, "createElement").mockReturnValue(anchor);
    URL.createObjectURL = vi.fn(() => "blob:x");
    URL.revokeObjectURL = vi.fn();

    await downloadPayslip("slip-1", "pdf", true);
    expect(urls[0]).toContain("/me/hr/payslips/slip-1/file?format=pdf");
    expect(anchor.download).toBe("bulletin-BUL-2026-000001.pdf");

    await exportReport("staff_debt", "xlsx", { from: "2026-01-01", to: "2026-09-24" });
    expect(urls[1]).toContain("/hr/reports/staff_debt/export?");
    expect(urls[1]).toContain("format=xlsx");
    expect(click).toHaveBeenCalledTimes(2);
  });
});
