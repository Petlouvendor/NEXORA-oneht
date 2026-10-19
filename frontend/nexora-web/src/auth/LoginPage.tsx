import { useEffect, useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { forgotPassword, resetPassword } from "@/auth/accountApi";
import { getSetupStatus } from "@/setup/setupApi";
import { ApiError } from "@/core/apiClient";
import { Button, Field, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";

type Mode = "login" | "forgot" | "reset";

const message = (err: unknown, fallback: string) =>
  err instanceof ApiError ? err.problem.detail ?? err.problem.title : fallback;

/** Connexion, mot de passe oublié et réinitialisation (recette Phase 55 AUTH-008). Le lien reçu
 *  par email ouvre `/reset-password?token=…`, qui arrive directement en mode réinitialisation. */
export function LoginPage() {
  const { login, verifyMfa, cancelMfa, mfaPending } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [mode, setMode] = useState<Mode>(params.get("token") ? "reset" : "login");
  const [email, setEmail] = useState("direction@abc-group.demo");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [resetToken, setResetToken] = useState(params.get("token") ?? "");
  const [newPassword, setNewPassword] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Résumé fonctionnel §20 : tant que l'application n'est pas installée, l'assistant remplace la connexion.
  useEffect(() => {
    getSetupStatus()
      .then((s) => { if (s.setupRequired || s.restartRequired) void navigate("/setup", { replace: true }); })
      .catch(() => { /* serveur injoignable : l'erreur apparaîtra à la connexion */ });
  }, [navigate]);

  function switchTo(next: Mode) {
    setMode(next);
    setError(null);
    setNotice(null);
  }

  async function onSubmit(e: FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      if (mode === "forgot") {
        await forgotPassword(email.trim());
        setNotice("Si un compte correspond à cette adresse, un email contenant un code de réinitialisation vient d'être envoyé.");
        setMode("reset");
        return;
      }
      if (mode === "reset") {
        await resetPassword(resetToken.trim(), newPassword);
        setNewPassword("");
        setPassword("");
        setNotice("Mot de passe réinitialisé. Vous pouvez vous connecter.");
        setMode("login");
        return;
      }
      if (mfaPending) {
        await verifyMfa(code.trim());
      } else {
        await login(email, password);
      }
      void navigate("/", { replace: true });
    } catch (err) {
      if (err instanceof ApiError && err.problem.title === "session_expired") return;
      setError(message(err, mode === "login" ? "Échec de connexion" : "Opération impossible"));
    } finally {
      setBusy(false);
    }
  }

  const link = (label: string, next: Mode) => (
    <button type="button" className="nx-btn nx-btn--ghost" style={{ padding: 0, justifySelf: "center" }} onClick={() => switchTo(next)}>
      {label}
    </button>
  );

  return (
    <div style={{
      display: "grid", placeItems: "center", minHeight: "100vh", padding: 24,
      background: "radial-gradient(1200px 600px at 70% -10%, color-mix(in srgb, var(--nx-brand) 12%, transparent), transparent 60%), var(--nx-bg)",
    }}>
      <form onSubmit={onSubmit} className="nx-card nx-card--pad-lg" style={{ width: 360, display: "grid", gap: 16 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <span style={{ color: "var(--nx-brand)" }}><Icon.Monogram size={26} /></span>
          <div style={{ display: "grid", lineHeight: 1.15 }}>
            <div style={{ fontWeight: 800, fontSize: 22, letterSpacing: 1 }}>NEXORA&nbsp;ONE</div>
            <div style={{ fontSize: 10.5, fontWeight: 650, letterSpacing: "0.16em", color: "var(--nx-text-3)" }}>THE NEXT GROUP</div>
          </div>
        </div>
        <div style={{ color: "var(--nx-text-2)", marginTop: -10 }}>
          {mode === "login" ? "Le système d'exploitation intelligent de votre entreprise."
            : mode === "forgot" ? "Recevoir un code de réinitialisation par email."
            : "Choisir un nouveau mot de passe."}
        </div>
        {notice && <div role="status" style={{ color: "var(--nx-success)", fontSize: 14 }}>{notice}</div>}

        {mode === "login" && !mfaPending && (
          <>
            <Field label="E-mail">
              <TextInput value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username" inputMode="email" />
            </Field>
            <Field label="Mot de passe" error={error}>
              <TextInput type="password" value={password} onChange={(e) => setPassword(e.target.value)}
                         autoComplete="current-password" invalid={Boolean(error)} />
            </Field>
            <Button type="submit" block loading={busy}>{busy ? "Connexion…" : "Se connecter"}</Button>
            {link("Mot de passe oublié ?", "forgot")}
          </>
        )}

        {mode === "login" && mfaPending && (
          <>
            <div style={{ fontSize: 14, color: "var(--nx-text-2)" }}>
              Saisissez le code à 6 chiffres de votre application d'authentification, ou un code de récupération.
            </div>
            <Field label="Code de vérification" error={error}>
              <TextInput value={code} onChange={(e) => setCode(e.target.value)} inputMode="text"
                         autoFocus autoComplete="one-time-code" invalid={Boolean(error)} />
            </Field>
            <Button type="submit" block loading={busy}>{busy ? "Vérification…" : "Vérifier"}</Button>
            <button type="button" className="nx-btn nx-btn--ghost" onClick={() => { cancelMfa(); setCode(""); setError(null); }}>
              Annuler
            </button>
          </>
        )}

        {mode === "forgot" && (
          <>
            <Field label="E-mail du compte" error={error}>
              <TextInput value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="username"
                         inputMode="email" autoFocus required invalid={Boolean(error)} />
            </Field>
            <Button type="submit" block loading={busy}>Envoyer le code</Button>
            {link("J'ai déjà un code", "reset")}
            {link("Retour à la connexion", "login")}
          </>
        )}

        {mode === "reset" && (
          <>
            <Field label="Code reçu par email">
              <TextInput value={resetToken} onChange={(e) => setResetToken(e.target.value)} autoComplete="one-time-code" required />
            </Field>
            <Field label="Nouveau mot de passe" hint="10 caractères minimum, au moins une lettre et un chiffre." error={error}>
              <TextInput type="password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)}
                         autoComplete="new-password" required invalid={Boolean(error)} />
            </Field>
            <Button type="submit" block loading={busy}>Réinitialiser</Button>
            {link("Retour à la connexion", "login")}
          </>
        )}

        <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)", textAlign: "center" }}>
          Connexion sécurisée · TLS · journalisée
        </div>
      </form>
      <div style={{ position: "fixed", bottom: 18, left: 0, right: 0, textAlign: "center", color: "var(--nx-text-3)", fontSize: 12, fontStyle: "italic" }}>
        « Connecter les visions, concrétiser l’avenir. » — THE NEXT GROUP
      </div>
    </div>
  );
}
