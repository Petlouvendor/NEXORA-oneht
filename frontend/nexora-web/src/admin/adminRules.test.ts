import { describe, expect, it } from "vitest";
import type { PermissionEntry } from "@/admin/adminApi";
import {
  codeFromName, definitionFromForm, describeWorkflow, emptyWorkflowForm, groupPermissions, notGrantable, validateWorkflowForm,
} from "@/admin/adminRules";

const p = (code: string, grantable = true): PermissionEntry => ({ code, name: code, module: code.split(".")[0], action: null, grantable });

describe("administration — règles de l'écran (finalisation phase 11)", () => {
  it("groupe les permissions par module et filtre", () => {
    const cat = [p("sales.view"), p("sales.create"), p("inventory.transfer"), p("accounting.view")];
    expect(groupPermissions(cat).map((g) => g.label)).toEqual(["Comptabilité", "Stock", "Ventes"]);
    expect(groupPermissions(cat, "transfer").flatMap((g) => g.entries.map((e) => e.code))).toEqual(["inventory.transfer"]);
  });

  it("signale les permissions qu'on ne détient pas, sauf celles que le rôle avait déjà", () => {
    const cat = [p("sales.view"), p("platform.manage_billing", false)];
    expect(notGrantable(["sales.view", "platform.manage_billing"], cat)).toEqual(["platform.manage_billing"]);
    expect(notGrantable(["platform.manage_billing"], cat, ["platform.manage_billing"])).toEqual([]);
  });

  it("construit un workflow QUAND / SI / ALORS et le décrit", () => {
    const f = { ...emptyWorkflowForm(), name: "Crédit hors limite", trigger: "credit_sale", threshold: "gte" as const, amount: "50000", levels: "2", role: "manager" };
    expect(validateWorkflowForm({ ...f, role: "" })).toEqual(["role", "Choisissez le rôle qui approuve."]);
    expect(validateWorkflowForm({ ...f, levels: "6" })?.[0]).toBe("levels");
    expect(validateWorkflowForm({ ...f, amount: "-1" })?.[0]).toBe("amount");
    expect(validateWorkflowForm(f)).toBeNull();
    const def = definitionFromForm(f);
    expect(def).toEqual({ logic: "all", conditions: [{ field: "amount", op: "gte", value: 50000 }], approval: { levels: 2, approverRoleCode: "manager" }, actions: [], notifications: [] });
    expect(describeWorkflow(def, "Vente à crédit au-delà de la limite")).toContain("2 approbations distinctes par un utilisateur ayant le rôle « manager »");
    expect(definitionFromForm({ ...f, threshold: "none" }).conditions).toEqual([]);
  });

  it("dérive un code technique stable du nom", () => {
    expect(codeFromName("Crédit au-delà de la limite !")).toBe("credit-au-dela-de-la-limite");
    expect(codeFromName("???")).toBe("workflow");
  });
});
