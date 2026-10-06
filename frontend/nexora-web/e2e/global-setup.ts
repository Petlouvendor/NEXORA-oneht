import type { FullConfig } from "@playwright/test";
import { CASHIER, MANAGER, verifier } from "./support";

/**
 * Préparation de l'entreprise de test (finalisation phase 19) : un caissier et un responsable de magasin distincts de
 * la direction, comme sur le terrain — personne ne valide la clôture de sa propre caisse (règle des quatre yeux). Idempotente :
 * le compte n'est créé qu'une fois (création d'utilisateur = action critique, confirmation forte par l'API).
 */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const baseURL = config.projects[0].use.baseURL!;
  const direction = await verifier(baseURL);
  try {
    const users = await direction.get<Array<{ email: string }>>("/users");
    const accounts = [
      { ...CASHIER, fullName: "Caissier E2E", role: "cashier" },
      { ...MANAGER, fullName: "Responsable de magasin E2E", role: "manager" },
    ];
    for (const a of accounts) {
      if (users.some((u) => u.email.toLowerCase() === a.email)) continue;
      await direction.post("/users", {
        email: a.email, fullName: a.fullName, password: a.password,
        initialAssignment: { roleName: a.role, scopeType: "COMPANY", companyId: direction.companyId, reason: "Suite E2E" },
      });
    }
  } finally {
    await direction.dispose();
  }
}
