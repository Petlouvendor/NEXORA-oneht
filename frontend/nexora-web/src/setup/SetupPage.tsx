import { useEffect, useState, type FormEvent, type ReactNode } from "react";
import { useNavigate } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { Button, Field, Select, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import {
  getSetupOptions, getSetupStatus, installNexora, passwordProblem, testDatabase, verifySetupToken,
  type DatabaseSetup, type DatabaseTestResult, type InstallResult, type SetupAdmin, type SetupCompany,
  type SetupOptions, type SetupSettings,
} from "@/setup/setupApi";

/**
 * Assistant d'installation (résumé fonctionnel §20) :
 * INSTALLATION → POSTGRESQL → TEST → MIGRATIONS → ENTREPRISE → ADMIN → CONFIGURATION → APPLICATION.
 * Le navigateur ne parle jamais à PostgreSQL : il transmet les paramètres à l'API, qui teste la
 * connexion, applique les migrations et crée l'entreprise et son administrateur. L'assistant est
 * fermé dès que l'application est configurée.
 */

type Step = "token" | "database" | "company" | "admin" | "settings" | "install" | "done";

const STEPS: { key: Step; label: string }[] = [
  { key: "token", label: "Accès" },
  { key: "database", label: "PostgreSQL" },
  { key: "company", label: "Entreprise" },
  { key: "admin", label: "Administrateur" },
  { key: "settings", label: "Paramètres" },
  { key: "install", label: "Installation" },
  { key: "done", label: "Application" },
];

const errorText = (err: unknown, fallback: string) =>
  err instanceof ApiError ? err.problem.detail ?? err.problem.title : fallback;

const sameDb = (a: DatabaseSetup, b: DatabaseSetup | null) =>
  b !== null && a.host === b.host && a.port === b.port && a.database === b.database
  && a.username === b.username && a.password === b.password && a.sslMode === b.sslMode;

export function SetupPage() {
  const navigate = useNavigate();
  const [step, setStep] = useState<Step>("token");
  const [checking, setChecking] = useState(true);
  const [token, setToken] = useState("");
  const [options, setOptions] = useState<SetupOptions | null>(null);
  const [db, setDb] = useState<DatabaseSetup>({
    host: "localhost", port: 5432, database: "nexora", username: "postgres", password: "", sslMode: "Prefer",
  });
  const [test, setTest] = useState<DatabaseTestResult | null>(null);
  const [testedDb, setTestedDb] = useState<DatabaseSetup | null>(null);
  const [company, setCompany] = useState<SetupCompany>({ name: "", activityType: "boutique", currency: "HTG" });
  const [admin, setAdmin] = useState<SetupAdmin>({ fullName: "", email: "", password: "" });
  const [confirm, setConfirm] = useState("");
  const [settings, setSettings] = useState<SetupSettings>({
    requireCashSession: true, cashVarianceThreshold: 500, maxDiscountPercent: 15, allowNegativeStock: false,
  });
  const [result, setResult] = useState<InstallResult | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Assistant fermé une fois l'application configurée ; « redémarrage attendu » si l'installation vient d'avoir lieu.
  useEffect(() => {
    let cancelled = false;
    getSetupStatus()
      .then((s) => {
        if (cancelled) return;
        if (s.restartRequired) setStep("done");
        else if (!s.setupRequired) void navigate("/login", { replace: true });
      })
      .catch(() => { /* serveur injoignable : on laisse l'assistant afficher l'erreur à la première action */ })
      .finally(() => !cancelled && setChecking(false));
    return () => { cancelled = true; };
  }, [navigate]);

  async function run(action: () => Promise<void>, fallback: string) {
    setError(null);
    setBusy(true);
    try {
      await action();
    } catch (err) {
      setError(errorText(err, fallback));
    } finally {
      setBusy(false);
    }
  }

  const submitToken = (e: FormEvent) => {
    e.preventDefault();
    void run(async () => {
      await verifySetupToken(token);
      setOptions(await getSetupOptions());
      setStep("database");
    }, "Jeton refusé.");
  };

  const runTest = () => void run(async () => {
    const snapshot = { ...db };
    setTest(await testDatabase(token, snapshot));
    setTestedDb(snapshot);
  }, "Test de connexion impossible.");

  const passwordIssue = admin.password ? passwordProblem(admin.password, admin.email) : null;
  const confirmIssue = confirm && confirm !== admin.password ? "Les deux mots de passe diffèrent." : null;
  const adminReady = admin.fullName.trim() !== "" && /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(admin.email.trim())
    && admin.password !== "" && !passwordIssue && confirm === admin.password;
  const dbReady = Boolean(test?.ready) && sameDb(db, testedDb);

  const install = () => void run(async () => {
    try {
      setResult(await installNexora(token, { database: db, company, admin, settings }));
    } catch (err) {
      // La réponse a pu se perdre (réseau, délai) alors que l'installation a abouti côté serveur.
      const s = await getSetupStatus().catch(() => null);
      if (!s || (s.setupRequired && !s.restartRequired)) throw err;
    }
    setStep("done");
  }, "L'installation a échoué.");

  if (checking) {
    return <Shell step={step}><div style={{ color: "var(--nx-text-2)" }}>Vérification de l'état du serveur…</div></Shell>;
  }

  return (
    <Shell step={step}>
      {step === "token" && (
        <form onSubmit={submitToken} style={{ display: "grid", gap: 16 }}>
          <Intro title="Bienvenue — installation de Nexora One">
            Cet assistant crée la base de données, l'entreprise et le premier administrateur. Saisissez le
            jeton d'installation affiché dans le journal de démarrage du serveur.
          </Intro>
          <Field label="Jeton d'installation" error={error}>
            <TextInput value={token} onChange={(e) => setToken(e.target.value)} autoFocus required
                       autoComplete="off" spellCheck={false} invalid={Boolean(error)} />
          </Field>
          <Button type="submit" block loading={busy} disabled={!token.trim()}>Commencer</Button>
        </form>
      )}

      {step === "database" && (
        <div style={{ display: "grid", gap: 14 }}>
          <Intro title="Connexion PostgreSQL">
            Compte d'installation (superutilisateur ou droits CREATEROLE / CREATEDB). L'application utilisera
            ensuite un rôle dédié, sans privilège de contournement, créé automatiquement.
          </Intro>
          <Row>
            <Field label="Hôte" required>
              <TextInput value={db.host} onChange={(e) => setDb({ ...db, host: e.target.value })} required />
            </Field>
            <Field label="Port" required>
              <TextInput type="number" min={1} max={65535} value={db.port}
                         onChange={(e) => setDb({ ...db, port: Number(e.target.value) })} required />
            </Field>
          </Row>
          <Field label="Base de données" required hint="Créée si elle n'existe pas ; une base contenant déjà Nexora One est refusée.">
            <TextInput value={db.database} onChange={(e) => setDb({ ...db, database: e.target.value })} required />
          </Field>
          <Row>
            <Field label="Utilisateur" required>
              <TextInput value={db.username} onChange={(e) => setDb({ ...db, username: e.target.value })}
                         autoComplete="off" required />
            </Field>
            <Field label="Mot de passe" required>
              <TextInput type="password" value={db.password} onChange={(e) => setDb({ ...db, password: e.target.value })}
                         autoComplete="new-password" required />
            </Field>
          </Row>
          <Field label="SSL">
            <Select value={db.sslMode} onChange={(e) => setDb({ ...db, sslMode: e.target.value })}>
              {(options?.sslModes ?? ["Prefer"]).map((m) => <option key={m} value={m}>{SSL_LABEL[m] ?? m}</option>)}
            </Select>
          </Field>
          <Button variant="subtle" onClick={runTest} loading={busy}>Tester la connexion</Button>
          {test && sameDb(db, testedDb) && <TestReport test={test} />}
          {test && !sameDb(db, testedDb) && (
            <div style={{ fontSize: 14, color: "var(--nx-text-2)" }}>Paramètres modifiés : relancez le test.</div>
          )}
          {error && <ErrorLine text={error} />}
          <Nav onBack={() => setStep("token")} onNext={() => setStep("company")} nextDisabled={!dbReady} />
        </div>
      )}

      {step === "company" && (
        <div style={{ display: "grid", gap: 14 }}>
          <Intro title="Entreprise initiale">
            Plan comptable, journaux, caisse, banque, moyens de paiement, succursale « Siège », point de vente
            et caisse enregistreuse sont créés automatiquement : l'entreprise peut vendre dès l'installation.
          </Intro>
          <Field label="Raison sociale" required>
            <TextInput value={company.name} onChange={(e) => setCompany({ ...company, name: e.target.value })} autoFocus required />
          </Field>
          <Row>
            <Field label="Activité principale" required>
              <Select value={company.activityType} onChange={(e) => setCompany({ ...company, activityType: e.target.value })}>
                {options?.activities.map((a) => <option key={a.code} value={a.code}>{a.label}</option>)}
              </Select>
            </Field>
            <Field label="Devise de tenue" required>
              <Select value={company.currency} onChange={(e) => setCompany({ ...company, currency: e.target.value })}>
                {options?.currencies.map((c) => <option key={c.code} value={c.code}>{c.label}</option>)}
              </Select>
            </Field>
          </Row>
          <Nav onBack={() => setStep("database")} onNext={() => setStep("admin")} nextDisabled={!company.name.trim()} />
        </div>
      )}

      {step === "admin" && (
        <div style={{ display: "grid", gap: 14 }}>
          <Intro title="Premier administrateur">
            Compte de la direction (tous les droits sur l'entreprise). Il créera ensuite les autres utilisateurs.
          </Intro>
          <Field label="Nom complet" required>
            <TextInput value={admin.fullName} onChange={(e) => setAdmin({ ...admin, fullName: e.target.value })} autoFocus required />
          </Field>
          <Field label="E-mail" required>
            <TextInput type="email" value={admin.email} onChange={(e) => setAdmin({ ...admin, email: e.target.value })}
                       autoComplete="username" required />
          </Field>
          <Row>
            <Field label="Mot de passe" required hint="10 caractères minimum, au moins une lettre et un chiffre." error={passwordIssue}>
              <TextInput type="password" value={admin.password} onChange={(e) => setAdmin({ ...admin, password: e.target.value })}
                         autoComplete="new-password" required invalid={Boolean(passwordIssue)} />
            </Field>
            <Field label="Confirmation" required error={confirmIssue}>
              <TextInput type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)}
                         autoComplete="new-password" required invalid={Boolean(confirmIssue)} />
            </Field>
          </Row>
          <Nav onBack={() => setStep("company")} onNext={() => setStep("settings")} nextDisabled={!adminReady} />
        </div>
      )}

      {step === "settings" && (
        <div style={{ display: "grid", gap: 14 }}>
          <Intro title="Paramètres de départ">
            Modifiables à tout moment dans Administration → Paramètres.
          </Intro>
          <Check checked={settings.requireCashSession} onChange={(v) => setSettings({ ...settings, requireCashSession: v })}
                 label="Exiger une session de caisse ouverte pour vendre au POS" />
          <Row>
            <Field label={`Seuil d'écart de caisse (${company.currency})`} hint="Au-delà, la clôture exige la validation d'un responsable.">
              <TextInput type="number" min={0} value={settings.cashVarianceThreshold}
                         onChange={(e) => setSettings({ ...settings, cashVarianceThreshold: Number(e.target.value) })} />
            </Field>
            <Field label="Remise maximale (%)" hint="Plafond de l'entreprise, en plus de la limite de chaque rôle.">
              <TextInput type="number" min={0} max={100} value={settings.maxDiscountPercent}
                         onChange={(e) => setSettings({ ...settings, maxDiscountPercent: Number(e.target.value) })} />
            </Field>
          </Row>
          <Check checked={settings.allowNegativeStock} onChange={(v) => setSettings({ ...settings, allowNegativeStock: v })}
                 label="Autoriser la vente en stock négatif" />
          <Nav onBack={() => setStep("admin")} onNext={() => setStep("install")}
               nextDisabled={settings.cashVarianceThreshold < 0 || settings.maxDiscountPercent < 0 || settings.maxDiscountPercent > 100} />
        </div>
      )}

      {step === "install" && (
        <div style={{ display: "grid", gap: 14 }}>
          <Intro title="Récapitulatif">Vérifiez avant de lancer l'installation.</Intro>
          <dl style={{ display: "grid", gridTemplateColumns: "minmax(110px, auto) 1fr", gap: "6px 14px", margin: 0, fontSize: 14 }}>
            <Recap label="Base">{db.username}@{db.host}:{db.port}/{db.database}{testedDb && test && !test.databaseExists ? " (créée)" : ""}</Recap>
            <Recap label="Entreprise">{company.name} · {options?.activities.find((a) => a.code === company.activityType)?.label} · {company.currency}</Recap>
            <Recap label="Administrateur">{admin.fullName} · {admin.email}</Recap>
            <Recap label="Caisse">{settings.requireCashSession ? "session obligatoire" : "session facultative"} · écart toléré {settings.cashVarianceThreshold} {company.currency}</Recap>
            <Recap label="Remises">{settings.maxDiscountPercent} % maximum</Recap>
            <Recap label="Stock">{settings.allowNegativeStock ? "négatif autorisé" : "négatif interdit"}</Recap>
          </dl>
          {busy && (
            <div role="status" style={{ fontSize: 14, color: "var(--nx-text-2)" }}>
              Création des tables (migrations), de l'entreprise et de l'administrateur… Cela peut prendre une à deux minutes.
            </div>
          )}
          {error && <ErrorLine text={error} />}
          <div style={{ display: "flex", gap: 10, justifyContent: "space-between" }}>
            <Button variant="ghost" onClick={() => setStep("settings")} disabled={busy}>Retour</Button>
            <Button onClick={install} loading={busy}>Installer Nexora One</Button>
          </div>
        </div>
      )}

      {step === "done" && <Done result={result} />}
    </Shell>
  );
}

/** Attend le redémarrage du service (arrêt automatique après l'installation, relance par le superviseur). */
function Done({ result }: { result: InstallResult | null }) {
  const navigate = useNavigate();
  const [ready, setReady] = useState(false);
  const [waited, setWaited] = useState(0);

  useEffect(() => {
    if (ready) return;
    const timer = window.setInterval(() => {
      setWaited((w) => w + 3);
      getSetupStatus()
        .then((s) => { if (!s.setupRequired && !s.restartRequired) setReady(true); })
        .catch(() => { /* service en cours de redémarrage */ });
    }, 3000);
    return () => window.clearInterval(timer);
  }, [ready]);

  return (
    <div style={{ display: "grid", gap: 14 }}>
      <Intro title={ready ? "Nexora One est prêt" : "Installation terminée"}>
        {ready
          ? "Le service a redémarré avec la nouvelle configuration. L'assistant d'installation est désormais désactivé."
          : "Le service redémarre pour charger la configuration (connexion de l'application, clés de sécurité)…"}
      </Intro>
      {result && (
        <dl style={{ display: "grid", gridTemplateColumns: "minmax(110px, auto) 1fr", gap: "6px 14px", margin: 0, fontSize: 14 }}>
          <Recap label="Entreprise">{result.companyName}</Recap>
          <Recap label="Administrateur">{result.adminEmail}</Recap>
          <Recap label="Migrations">{result.migrationsApplied} appliquées</Recap>
        </dl>
      )}
      {!ready && waited >= 60 && (
        <div style={{ fontSize: 14, color: "var(--nx-warning)" }}>
          Le service ne semble pas redémarrer automatiquement : relancez-le (service Windows, systemd ou conteneur),
          cette page se mettra à jour d'elle-même.
        </div>
      )}
      <Button block disabled={!ready} onClick={() => navigate("/login", { replace: true })}>
        {ready ? "Aller à la connexion" : "Redémarrage en cours…"}
      </Button>
    </div>
  );
}

const SSL_LABEL: Record<string, string> = {
  Prefer: "Préférer SSL", Require: "Exiger SSL", VerifyFull: "Exiger SSL + vérifier le certificat", Disable: "Sans SSL",
};

function Shell({ step, children }: { step: Step; children: ReactNode }) {
  const current = STEPS.findIndex((s) => s.key === step);
  return (
    <div style={{
      display: "grid", placeItems: "center", minHeight: "100vh", padding: 16,
      background: "radial-gradient(1200px 600px at 70% -10%, color-mix(in srgb, var(--nx-brand) 12%, transparent), transparent 60%), var(--nx-bg)",
    }}>
      <main className="nx-card nx-card--pad-lg" style={{ width: "min(600px, 100%)", display: "grid", gap: 18 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 10 }}>
          <span style={{ color: "var(--nx-brand)" }}><Icon.Monogram size={26} /></span>
          <div style={{ fontWeight: 800, fontSize: 22, letterSpacing: 0.5 }}>Nexora&nbsp;One</div>
          <span style={{ marginLeft: "auto", fontSize: "var(--nx-fs-xs)", color: "var(--nx-text-3)" }}>Installation</span>
        </div>
        <ol aria-label="Étapes de l'installation" style={{ display: "flex", flexWrap: "wrap", gap: 6, listStyle: "none", margin: 0, padding: 0 }}>
          {STEPS.map((s, i) => (
            <li key={s.key} aria-current={i === current ? "step" : undefined} style={{
              fontSize: "var(--nx-fs-xs)", padding: "3px 9px", borderRadius: 999,
              border: "1px solid var(--nx-border)",
              background: i === current ? "var(--nx-brand)" : i < current ? "color-mix(in srgb, var(--nx-brand) 14%, transparent)" : "transparent",
              color: i === current ? "var(--nx-brand-ink)" : i < current ? "var(--nx-text)" : "var(--nx-text-3)",
              fontWeight: i === current ? 700 : 500,
            }}>
              {i + 1}. {s.label}
            </li>
          ))}
        </ol>
        {children}
      </main>
    </div>
  );
}

function Intro({ title, children }: { title: string; children: ReactNode }) {
  return (
    <div style={{ display: "grid", gap: 4 }}>
      <h1 style={{ fontSize: 18, margin: 0 }}>{title}</h1>
      <p style={{ margin: 0, color: "var(--nx-text-2)", fontSize: 14 }}>{children}</p>
    </div>
  );
}

function Row({ children }: { children: ReactNode }) {
  return <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 12 }}>{children}</div>;
}

function Nav({ onBack, onNext, nextDisabled }: { onBack: () => void; onNext: () => void; nextDisabled?: boolean }) {
  return (
    <div style={{ display: "flex", gap: 10, justifyContent: "space-between" }}>
      <Button variant="ghost" onClick={onBack}>Retour</Button>
      <Button onClick={onNext} disabled={nextDisabled}>Continuer</Button>
    </div>
  );
}

function Check({ checked, onChange, label }: { checked: boolean; onChange: (v: boolean) => void; label: string }) {
  return (
    <label style={{ display: "flex", gap: 10, alignItems: "center", fontSize: 14, cursor: "pointer" }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  );
}

function Recap({ label, children }: { label: string; children: ReactNode }) {
  return (
    <>
      <dt style={{ color: "var(--nx-text-2)" }}>{label}</dt>
      <dd style={{ margin: 0, overflowWrap: "anywhere" }}>{children}</dd>
    </>
  );
}

function ErrorLine({ text }: { text: string }) {
  return <div role="alert" style={{ color: "var(--nx-error)", fontSize: 14 }}>{text}</div>;
}

function TestReport({ test }: { test: DatabaseTestResult }) {
  return (
    <div role="status" style={{
      display: "grid", gap: 6, padding: 12, borderRadius: 10, fontSize: 14,
      border: `1px solid ${test.ready ? "var(--nx-success)" : "var(--nx-error)"}`,
      background: `color-mix(in srgb, ${test.ready ? "var(--nx-success)" : "var(--nx-error)"} 8%, transparent)`,
    }}>
      <strong>{test.ready ? "Prêt pour l'installation" : test.connected ? "Installation impossible sur cette base" : "Connexion impossible"}</strong>
      {test.messages.map((m) => <div key={m}>{m}</div>)}
    </div>
  );
}
