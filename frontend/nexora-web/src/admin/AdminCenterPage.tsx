import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import {
  Badge, Button, Card, DataTable, EmptyState, Field, SectionLabel, Select, Skeleton, StatePill, Tabs, TextInput, type Column,
} from "@/shared/ui";
import { dateShort, relative } from "@/shared/format";
import {
  activateConfigVersion, activateWorkflow, disableRole, getAutomations, getConfigVersions, getEffectiveSettings,
  createUser, getRoles, getTasks, getWorkflows, listUsers, runAutomation, setSetting, setUserStatus, snapshotConfig, testWorkflow,
  type AdminRole, type AdminTask, type Automation, type ConfigVersion, type EffectiveSetting, type UserSummary, type Workflow,
} from "@/admin/adminApi";

const TABS = [
  { id: "tasks", label: "Tâches" },
  { id: "settings", label: "Paramètres" },
  { id: "users", label: "Utilisateurs" },
  { id: "roles", label: "Rôles" },
  { id: "workflows", label: "Workflows" },
  { id: "automations", label: "Automatisations" },
  { id: "versions", label: "Versions de config" },
];

const SEVERITY_TONE: Record<string, "error" | "warning" | "info"> = { critical: "error", warning: "warning", info: "info" };
const DOMAINS = ["numbering", "accounting_defaults", "taxes", "approvals", "pos", "stock"];

/** Centre d'administration (Phase 14 §4). */
/** `initialTab` : entrée « Automatisation » du menu NEXORA ONE (§5) → onglet Automatisations. */
export function AdminCenterPage({ initialTab = "tasks" }: { initialTab?: string } = {}) {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState(initialTab);

  if (!hasPermission("admin.settings") && !hasPermission("admin.roles") && !hasPermission("admin.workflows")
      && !hasPermission("admin.manage_users") && !hasPermission("admin.automations")) {
    return <Card><EmptyState title="Administration" hint="Vous n'avez pas les permissions d'administration." /></Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Centre d'administration</h1>
      <Tabs tabs={TABS} active={tab} onChange={setTab} />
      {tab === "tasks" && <TasksPanel />}
      {tab === "settings" && <SettingsPanel />}
      {tab === "users" && <UsersPanel />}
      {tab === "roles" && <RolesPanel />}
      {tab === "workflows" && <WorkflowsPanel />}
      {tab === "automations" && <AutomationsPanel />}
      {tab === "versions" && <VersionsPanel />}
    </div>
  );
}

function useLoader<T>(load: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const run = useCallback(() => {
    setError(null);
    load().then(setData).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  useEffect(run, [run]);
  return { data, error, reload: run };
}

function TasksPanel() {
  const { data, error } = useLoader<AdminTask[]>(getTasks);
  if (error) return <div className="nx-error-text">{error}</div>;
  if (!data) return <Card><Skeleton rows={4} /></Card>;
  if (data.length === 0) return <Card><EmptyState title="Configuration complète" hint="Aucune tâche administrative en attente." /></Card>;
  return (
    <div style={{ display: "grid", gap: 8 }}>
      {data.map((t) => (
        <Card key={t.key} style={{ display: "flex", gap: 12, alignItems: "flex-start" }}>
          <Badge tone={SEVERITY_TONE[t.severity]}>{t.count}</Badge>
          <div>
            <div style={{ fontWeight: 650 }}>{t.title}</div>
            <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>{t.detail}</div>
          </div>
          <Badge style={{ marginLeft: "auto" }}>{t.category}</Badge>
        </Card>
      ))}
    </div>
  );
}

function SettingsPanel() {
  const toast = useToast();
  const { data, error, reload } = useLoader<EffectiveSetting[]>(getEffectiveSettings);
  if (error) return <div className="nx-error-text">{error}</div>;
  if (!data) return <Card><Skeleton rows={6} /></Card>;

  const groups = [...new Set(data.map((s) => s.category))];

  async function save(key: string, raw: string) {
    let value: unknown = raw;
    if (raw === "true" || raw === "false") value = raw === "true";
    else if (/^-?\d+(\.\d+)?$/.test(raw)) value = Number(raw);
    try {
      const r = await setSetting({ key, scope: "company", scopeId: null, value, note: "modifié depuis le centre d'administration" });
      r.warnings.forEach((w) => toast.push(w, "warning"));
      toast.success(`Paramètre « ${key} » enregistré.`);
      reload();
    } catch (e) {
      toast.error(e instanceof ApiError ? e.message : "Erreur");
    }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {groups.map((g) => (
        <Card key={g}>
          <SectionLabel>{g}</SectionLabel>
          <div style={{ display: "grid", gap: 10 }}>
            {data.filter((s) => s.category === g).map((s) => (
              <div key={s.key} style={{ display: "flex", gap: 10, alignItems: "center", flexWrap: "wrap" }}>
                <code style={{ fontSize: "var(--nx-fs-sm)", minWidth: 240 }}>{s.key}</code>
                <input className="nx-input" style={{ maxWidth: 180 }} defaultValue={JSON.stringify(s.value).replace(/^"|"$/g, "")}
                       onKeyDown={(e) => e.key === "Enter" && save(s.key, (e.target as HTMLInputElement).value)} />
                <Badge tone={s.source === "definition" ? undefined : "info"}>{s.source}</Badge>
              </div>
            ))}
          </div>
        </Card>
      ))}
      <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>Entrée pour enregistrer une valeur au niveau entreprise. Les paramètres sensibles exigent un motif côté API.</div>
    </div>
  );
}

function RolesPanel() {
  const toast = useToast();
  const { data, error, reload } = useLoader<AdminRole[]>(getRoles);
  if (error) return <div className="nx-error-text">{error}</div>;
  if (!data) return <Card><Skeleton rows={4} /></Card>;

  const cols: Column<AdminRole>[] = [
    { key: "name", header: "Rôle", render: (r) => <b>{r.name}</b> },
    { key: "perms", header: "Permissions", render: (r) => <span style={{ color: "var(--nx-text-2)" }}>{r.permissions.length}</span>, numeric: true },
    { key: "assign", header: "Affectations", render: (r) => r.assignmentCount, numeric: true },
    { key: "status", header: "Statut", render: (r) => <StatePill status={r.status} /> },
    {
      key: "act", header: "", render: (r) => r.status === "active" && !r.systemRole ? (
        <Button size="sm" variant="ghost" onClick={async () => {
          try { await disableRole(r.id, false); toast.success("Rôle désactivé."); reload(); }
          catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
        }}>Désactiver</Button>
      ) : null,
    },
  ];

  return (
    <Card>
      <SectionLabel>Rôles de l'entreprise (§10-11)</SectionLabel>
      {data.length === 0
        ? <EmptyState title="Aucun rôle personnalisé" hint="Créez un rôle via l'API /admin/roles ou dupliquez un rôle système." />
        : <DataTable columns={cols} rows={data} rowKey={(r) => r.id} />}
    </Card>
  );
}

function WorkflowsPanel() {
  const toast = useToast();
  const { data, error, reload } = useLoader<Workflow[]>(getWorkflows);
  const [amount, setAmount] = useState("5000");
  if (error) return <div className="nx-error-text">{error}</div>;
  if (!data) return <Card><Skeleton rows={4} /></Card>;
  if (data.length === 0) return <Card><EmptyState title="Aucun workflow" hint="Créez un workflow d'approbation via /admin/workflows." /></Card>;

  return (
    <div style={{ display: "grid", gap: 12 }}>
      {data.map((w) => (
        <Card key={w.id}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
            <div>
              <div style={{ fontWeight: 650 }}>{w.name} <StatePill status={w.status} /></div>
              <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                {w.triggerEvent} · logique {w.definition.logic} · {w.definition.conditions.length} condition(s)
                {w.definition.approval && ` · ${w.definition.approval.levels} approbation(s)`}
                {w.isTested ? " · testé" : " · non testé"}
              </div>
            </div>
            <div style={{ display: "flex", gap: 6, alignItems: "center" }}>
              <input className="nx-input" style={{ width: 90 }} value={amount} onChange={(e) => setAmount(e.target.value)} />
              <Button size="sm" variant="ghost" onClick={async () => {
                try {
                  const r = await testWorkflow(w.id, { amount: Number(amount) });
                  toast.push(r.matched ? "Correspondance : approbation prévue." : "Aucune correspondance.", r.matched ? "success" : "info");
                  reload();
                } catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
              }}>Tester</Button>
              {w.status !== "active" && (
                <Button size="sm" disabled={!w.isTested} onClick={async () => {
                  try { await activateWorkflow(w.id); toast.success("Workflow activé."); reload(); }
                  catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
                }}>Activer</Button>
              )}
            </div>
          </div>
        </Card>
      ))}
    </div>
  );
}

function AutomationsPanel() {
  const toast = useToast();
  const { data, error, reload } = useLoader<Automation[]>(getAutomations);
  if (error) return <div className="nx-error-text">{error}</div>;
  if (!data) return <Card><Skeleton rows={4} /></Card>;
  if (data.length === 0) return <Card><EmptyState title="Aucune automatisation" hint="Créez-en une via /admin/automations." /></Card>;

  return (
    <div style={{ display: "grid", gap: 12 }}>
      {data.map((a) => (
        <Card key={a.id}>
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12 }}>
            <div>
              <div style={{ fontWeight: 650 }}>{a.name} <Badge>{a.kind}</Badge> <StatePill status={a.status === "active" ? "active" : "draft"} /></div>
              <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                {String(a.schedule.frequency ?? "—")} · prochaine : {relative(a.nextRunAt)} · dernière : {relative(a.lastRunAt)}
              </div>
            </div>
            <div style={{ display: "flex", gap: 6 }}>
              <Button size="sm" variant="ghost" onClick={async () => {
                try { const r = await runAutomation(a.id, true); toast.push(`Prévisualisation : ${r.itemsProcessed} élément(s).`, "info"); }
                catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
              }}>Prévisualiser</Button>
              <Button size="sm" onClick={async () => {
                try { const r = await runAutomation(a.id, false); toast.success(`Exécutée : ${r.itemsProcessed} élément(s) traité(s).`); reload(); }
                catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
              }}>Exécuter</Button>
            </div>
          </div>
        </Card>
      ))}
    </div>
  );
}

function VersionsPanel() {
  const toast = useToast();
  const [domain, setDomain] = useState("numbering");
  const { data, error, reload } = useLoader<ConfigVersion[]>(() => getConfigVersions(domain), [domain]);

  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 6 }}>
          <select className="nx-select" style={{ width: 190 }} value={domain} onChange={(e) => setDomain(e.target.value)}>
            {DOMAINS.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
          <Button size="sm" variant="ghost" onClick={async () => {
            try { await snapshotConfig(domain, "instantané manuel"); toast.success("Instantané créé."); reload(); }
            catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
          }}>Instantané</Button>
        </div>
      }>Versions — {domain} (§39)</SectionLabel>

      {error && <div className="nx-error-text">{error}</div>}
      {!data ? <Skeleton rows={3} /> : data.length === 0 ? (
        <EmptyState title="Aucune version" hint="Créez un premier instantané de configuration." />
      ) : (
        <div style={{ display: "grid", gap: 8 }}>
          {data.map((v) => (
            <div key={v.id} style={{ display: "flex", gap: 10, alignItems: "center", borderTop: "1px solid var(--nx-border)", paddingTop: 8 }}>
              <b>v{v.versionNumber}</b>
              <StatePill status={v.status} />
              <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                {v.effectiveFrom ? `du ${dateShort(v.effectiveFrom)}` : "brouillon"}
                {v.effectiveTo ? ` au ${dateShort(v.effectiveTo)}` : ""}
                {v.note ? ` · ${v.note}` : ""}
              </span>
              {v.status === "draft" && (
                <Button size="sm" style={{ marginLeft: "auto" }} onClick={async () => {
                  try { await activateConfigVersion(v.id); toast.success(`Version v${v.versionNumber} activée.`); reload(); }
                  catch (e) { toast.error(e instanceof ApiError ? e.message : "Erreur"); }
                }}>Activer</Button>
              )}
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}

// Rôles proposés quand la liste des rôles n'est pas lisible (permission admin.roles absente).
const FALLBACK_ROLES = ["salesperson", "cashier", "stock_manager", "purchasing_manager", "accountant", "manager", "auditor"];

/**
 * Utilisateurs (recette Phase 55 RBAC-001, AUTH-009). La création exige une première affectation :
 * le serveur refuse tout rôle portant une permission que le créateur ne détient pas.
 */
function UsersPanel() {
  const { hasPermission, activeCompanyId, user } = useAuth();
  const toast = useToast();
  const canManage = hasPermission("admin.manage_users");
  const [rows, setRows] = useState<UserSummary[] | null>(null);
  const [roles, setRoles] = useState<string[]>(FALLBACK_ROLES);
  const [creating, setCreating] = useState(false);
  const [email, setEmail] = useState("");
  const [fullName, setFullName] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("salesperson");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    listUsers().then(setRows).catch((e) => setError(e instanceof ApiError ? e.problem.detail ?? e.problem.title : "Erreur"));
  }, []);
  useEffect(() => {
    if (!canManage) return;
    load();
    getRoles()
      .then((r) => setRoles(r.filter((x) => x.status === "active" && !["super_administrator", "group_administrator"].includes(x.name)).map((x) => x.name)))
      .catch(() => { /* garde la liste de repli */ });
  }, [canManage, load]);

  if (!canManage) return <Card><EmptyState title="Utilisateurs" hint="Réservé à la permission admin.manage_users." /></Card>;

  async function create() {
    setBusy(true);
    setError(null);
    try {
      await createUser({
        email, fullName, password,
        initialAssignment: { roleName: role, scopeType: "COMPANY", companyId: activeCompanyId, reason: "Création de compte" },
      });
      toast.success(`Compte ${email} créé. Transmettez-lui son mot de passe provisoire par un canal sûr.`);
      setCreating(false);
      setEmail(""); setFullName(""); setPassword("");
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.problem.detail ?? e.problem.title : "Création impossible");
    } finally {
      setBusy(false);
    }
  }

  async function toggle(u: UserSummary) {
    const next = u.status === "active" ? "suspended" : "active";
    if (next === "suspended" && !confirm(`Suspendre ${u.email} ? Ses sessions seront fermées immédiatement.`)) return;
    try {
      await setUserStatus(u.id, next);
      toast.success(next === "suspended" ? "Compte suspendu." : "Compte réactivé.");
      load();
    } catch (e) {
      toast.error(e instanceof ApiError ? e.problem.detail ?? e.problem.title : "Action impossible");
    }
  }

  const columns: Array<Column<UserSummary>> = [
    { key: "email", header: "Email", render: (u) => u.email },
    { key: "name", header: "Nom", render: (u) => u.fullName ?? "—" },
    { key: "status", header: "Statut", render: (u) => <Badge tone={u.status === "active" ? "success" : "warning"}>{u.status}</Badge> },
    { key: "mfa", header: "MFA", render: (u) => (u.mfaEnabled ? "oui" : "non") },
    { key: "last", header: "Dernière connexion", render: (u) => (u.lastLoginAt ? relative(u.lastLoginAt) : "jamais") },
    { key: "act", header: "", render: (u) => u.id === user?.id ? null : (
      <Button variant="ghost" size="sm" onClick={() => toggle(u)}>{u.status === "active" ? "Suspendre" : "Réactiver"}</Button>
    ) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {!creating && <div><Button onClick={() => setCreating(true)}>Nouvel utilisateur</Button></div>}
      {creating && (
        <Card>
          <div style={{ display: "grid", gap: 12, maxWidth: 440 }}>
            <SectionLabel>Nouvel utilisateur — entreprise active</SectionLabel>
            <Field label="Email" required><TextInput type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
            <Field label="Nom complet" required><TextInput value={fullName} onChange={(e) => setFullName(e.target.value)} /></Field>
            <Field label="Mot de passe provisoire" required hint="10 caractères minimum, au moins une lettre et un chiffre.">
              <TextInput type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </Field>
            <Field label="Rôle">
              <Select value={role} onChange={(e) => setRole(e.target.value)}>
                {roles.map((r) => <option key={r} value={r}>{r}</option>)}
              </Select>
            </Field>
            {error && <div role="alert" className="nx-error-text">{error}</div>}
            <div style={{ display: "flex", gap: 8 }}>
              <Button onClick={create} loading={busy} disabled={!email || !fullName || !password}>Créer le compte</Button>
              <Button variant="ghost" onClick={() => setCreating(false)}>Annuler</Button>
            </div>
          </div>
        </Card>
      )}
      <Card>
        {!creating && error && <div role="alert" className="nx-error-text">{error}</div>}
        {rows === null ? <Skeleton rows={4} /> : (
          <DataTable columns={columns} rows={rows} rowKey={(u) => u.id}
                     empty={<EmptyState title="Aucun utilisateur" hint="Aucun compte dans votre périmètre." />} />
        )}
      </Card>
    </div>
  );
}
