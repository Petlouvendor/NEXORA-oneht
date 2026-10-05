import { useState, type FormEvent } from "react";
import { apiFetch } from "@/core/apiClient";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { Button, Card, Field, SectionLabel, TextInput } from "@/shared/ui";

export interface SecurityPolicy {
  companyId: string; mfaRequiredRoles: string[]; sessionTtlMinutes: number; stepUpTtlMinutes: number;
  sensitiveExportRequiresApproval: boolean; exportWatermark: boolean; auditRetentionDays: number; enforceMfaForCritical: boolean;
}
export const getSecurityPolicy = () => apiFetch<SecurityPolicy>("/security/policy");
export const setSecurityPolicy = (body: Partial<Omit<SecurityPolicy, "companyId">>) =>
  apiFetch<void>("/security/policy", { method: "PUT", body: JSON.stringify(body) });

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;

/** Politique de sécurité de l'entreprise (finalisation phase 17) : inactivité, confirmation forte, double authentification. */
export function SecurityPolicyPanel() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const policy = useAsync(getSecurityPolicy, [version]);
  const [idle, setIdle] = useState<string | null>(null);
  const [stepUp, setStepUp] = useState<string | null>(null);
  const [roles, setRoles] = useState<string | null>(null);
  const [enforce, setEnforce] = useState<boolean | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "security.ttl": "idle" });
  const p = policy.data;
  if (!p) return <Card>{policy.error ? <div role="alert" className="nx-error-text">{policy.error}</div> : "Chargement…"}</Card>;
  const idleV = idle ?? String(p.sessionTtlMinutes);
  const stepUpV = stepUp ?? String(p.stepUpTtlMinutes);
  const rolesV = roles ?? p.mfaRequiredRoles.join(", ");
  const enforceV = enforce ?? p.enforceMfaForCritical;

  async function save(e: FormEvent) {
    e.preventDefault();
    const i = num(idleV), su = num(stepUpV);
    if (i == null || !Number.isInteger(i) || i < 5 || i > 1440) return fail("idle", "Entre 5 et 1 440 minutes.");
    if (su == null || !Number.isInteger(su) || su < 1 || su > 60) return fail("stepUp", "Entre 1 et 60 minutes.");
    const roleList = rolesV.split(",").map((r) => r.trim()).filter(Boolean);
    if (await submit(() => setSecurityPolicy({ sessionTtlMinutes: i, stepUpTtlMinutes: su, mfaRequiredRoles: roleList, enforceMfaForCritical: enforceV }).then(() => true))) {
      toast.success("Politique de sécurité enregistrée.");
      setIdle(null); setStepUp(null); setRoles(null); setEnforce(null); setVersion((v) => v + 1);
    }
  }

  return (
    <Card>
      <SectionLabel>Politique de sécurité</SectionLabel>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 12, maxWidth: 720 }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: 10 }}>
          <Field label="Déconnexion après inactivité (minutes)" error={fieldErrors.idle}
            hint="Sans activité au-delà, la session n'est plus renouvelée.">
            <TextInput inputMode="numeric" value={idleV} onChange={(e) => setIdle(e.target.value)} />
          </Field>
          <Field label="Validité d'une confirmation forte (minutes)" error={fieldErrors.stepUp}
            hint="Paiements, annulations, validations comptables, droits, suppressions, données sensibles.">
            <TextInput inputMode="numeric" value={stepUpV} onChange={(e) => setStepUp(e.target.value)} />
          </Field>
        </div>
        <Field label="Rôles soumis à la double authentification" hint="Noms des rôles, séparés par des virgules.">
          <TextInput value={rolesV} onChange={(e) => setRoles(e.target.value)} />
        </Field>
        <label style={{ display: "flex", gap: 8, alignItems: "flex-start" }}>
          <input type="checkbox" checked={enforceV} onChange={(e) => setEnforce(e.target.checked)} />
          <span>
            <b>Imposer la double authentification pour les actions critiques</b>
            <div style={hint}>Activé : ces rôles ne peuvent plus confirmer une action critique sans double authentification active.
              Désactivé : la règle reste indicative (l'utilisateur est invité à l'activer). Si elle est active, son code est toujours demandé.</div>
          </span>
        </label>
        <div style={hint}>Durée maximale d'une session : fixée par le serveur (12 h par défaut). Le jeton d'accès dure 15 minutes et se renouvelle seul tant que la session vit.</div>
        <div><Button type="submit" size="sm" disabled={busy}>Enregistrer</Button></div>
      </form>
    </Card>
  );
}
