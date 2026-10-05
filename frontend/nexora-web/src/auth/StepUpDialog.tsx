import { useEffect, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { ApiError, apiFetch, setStepUpHandler } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { Button, Field, Modal, TextInput } from "@/shared/ui";
import { TopLayer } from "@/shared/TopLayer";

/** Libellés des catégories d'actions critiques (mêmes clés que le serveur). */
export const STEP_UP_LABELS: Record<string, string> = {
  payment: "Paiement",
  cancellation: "Annulation",
  accounting_validation: "Validation comptable",
  permission_change: "Changement de droits",
  deletion: "Suppression",
  sensitive_data: "Données sensibles",
};

interface Pending { purpose: string; detail: string; resolve: (token: string | null) => void }

/**
 * Confirmation forte (finalisation phase 17) : toute action critique refusée par le serveur avec
 * « security.step_up_required » ouvre cette fenêtre ; une fois l'identité confirmée (mot de passe, plus le code
 * de double authentification si elle est active), l'action est rejouée automatiquement. La confirmation vaut
 * quelques minutes pour la même catégorie d'actions.
 */
export function StepUpHost() {
  const { user } = useAuth();
  const [pending, setPending] = useState<Pending | null>(null);
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [needsCode, setNeedsCode] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [enrollment, setEnrollment] = useState(false);
  const [busy, setBusy] = useState(false);
  const pendingRef = useRef<Pending | null>(null);
  pendingRef.current = pending;

  useEffect(() => {
    setStepUpHandler((purpose, detail) => new Promise<string | null>((resolve) => {
      pendingRef.current?.resolve(null);   // une seule demande à la fois
      setPassword(""); setCode(""); setError(null); setEnrollment(false);
      setNeedsCode(!!user?.mfaEnabled);
      setPending({ purpose, detail, resolve });
    }));
    return () => setStepUpHandler(null);
  }, [user?.mfaEnabled]);

  if (!pending) return null;

  const close = (token: string | null) => { pending.resolve(token); setPending(null); };

  async function confirm(e?: FormEvent) {
    e?.preventDefault();
    if (!password) { setError("Saisissez votre mot de passe."); return; }
    if (needsCode && !code.trim()) { setError("Saisissez le code de votre application d'authentification."); return; }
    setBusy(true); setError(null);
    try {
      const r = await apiFetch<{ token: string }>("/security/step-up", {
        method: "POST", body: JSON.stringify({ password, purpose: pending!.purpose, code: code.trim() || null }),
      });
      close(r.token);
    } catch (err) {
      const title = err instanceof ApiError ? err.problem.title : "";
      if (title === "security.step_up_code_required") setNeedsCode(true);
      if (title === "security.mfa_enrollment_required") setEnrollment(true);
      setError(err instanceof Error ? err.message : "Confirmation impossible.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <TopLayer>
    <Modal title="Confirmation de sécurité" onClose={() => close(null)}
      footer={<><Button variant="ghost" onClick={() => close(null)}>Annuler</Button>
        <Button disabled={busy || enrollment} onClick={() => confirm()}>Confirmer</Button></>}>
      <form noValidate onSubmit={confirm} style={{ display: "grid", gap: 10 }}>
        <div><b>{STEP_UP_LABELS[pending.purpose] ?? "Action critique"}</b> — {pending.detail}</div>
        <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
          Action sensible : confirmez votre identité. La confirmation vaut quelques minutes pour ce type d'action.
        </div>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {enrollment && <div><Link to="/settings" onClick={() => close(null)}>Activer la double authentification</Link></div>}
        <Field label="Mot de passe" required>
          <TextInput type="password" autoComplete="current-password" autoFocus value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        {needsCode && (
          <Field label="Code de double authentification" required hint="Code à 6 chiffres de votre application, ou un code de récupération.">
            <TextInput inputMode="numeric" autoComplete="one-time-code" value={code} onChange={(e) => setCode(e.target.value)} />
          </Field>
        )}
        <button type="submit" hidden />
      </form>
    </Modal>
    </TopLayer>
  );
}
