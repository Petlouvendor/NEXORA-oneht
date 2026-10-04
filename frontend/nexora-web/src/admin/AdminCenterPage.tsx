import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, EmptyState, SectionLabel, Skeleton, StatePill, Tabs } from "@/shared/ui";
import { dateShort } from "@/shared/format";
import { activateConfigVersion, getConfigVersions, getTasks, snapshotConfig, type AdminTask, type ConfigVersion } from "@/admin/adminApi";
import { AutomationsPanel } from "@/admin/AutomationsPanel";
import { RolesPanel } from "@/admin/RolesPanel";
import { SettingsPanel } from "@/admin/SettingsPanel";
import { UsersPanel } from "@/admin/UsersPanel";
import { WorkflowsPanel } from "@/admin/WorkflowsPanel";

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
