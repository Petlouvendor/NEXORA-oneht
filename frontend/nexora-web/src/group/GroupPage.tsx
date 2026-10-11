import { useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { Card, Tabs } from "@/shared/ui";
import { getOptions } from "@/group/groupApi";
import { ConsolidationSection, IntercompanySection, MappingSection, OverviewSection, PolicySection } from "@/group/GroupSections";

/**
 * Groupe & consolidation (Phase 9 ; finalisation phase 14) : pilotage, opérations intersociétés,
 * correspondance des comptes, consolidation, politique et sociétés. Accessible seulement avec un rôle
 * attribué au niveau du groupe — le serveur le vérifie sur chaque route.
 */
export function GroupPage() {
  const { user, hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const groupId = useMemo(() => user?.companies.find((c) => c.groupId)?.groupId ?? null, [user]);
  const [version, setVersion] = useState(0);
  const options = useAsync(() => (groupId && hasPermission("group.view") ? getOptions(groupId) : Promise.resolve(null)), [groupId, version]);

  const tabs = [
    { id: "overview", label: "Pilotage", show: hasPermission("group.view") },
    { id: "intercompany", label: "Intersociétés", show: hasPermission("group.intercompany") },
    { id: "mappings", label: "Correspondances", show: hasPermission("group.mappings") },
    { id: "consolidation", label: "Consolidation", show: hasPermission("group.view") },
    { id: "policy", label: "Politique & sociétés", show: hasPermission("group.view") },
  ].filter((t) => t.show);
  const tab = tabs.some((t) => t.id === params.get("tab")) ? params.get("tab")! : tabs[0]?.id ?? "overview";
  const setTab = (t: string) => setParams((p) => { p.set("tab", t); return p; }, { replace: true });

  if (!hasPermission("group.view"))
    return <Card>La vue groupe demande un rôle attribué au niveau du groupe (Administration → utilisateurs, périmètre « Groupe »).</Card>;
  if (!groupId) return <Card>Cette installation ne contient pas de groupe multi-sociétés.</Card>;

  const o = options.data;
  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <h1 style={{ margin: 0 }}>Groupe{o ? ` — ${o.groupName}` : ""}</h1>
      {options.error && <div role="alert" className="nx-error-text">{options.error}</div>}
      <Tabs active={tab} onChange={setTab} tabs={tabs.map(({ id, label }) => ({ id, label }))} />
      {o && (() => {
        const props = { gid: groupId, options: o, onChanged: () => setVersion((v) => v + 1) };
        switch (tab) {
          case "intercompany": return <IntercompanySection {...props} />;
          case "mappings": return <MappingSection {...props} />;
          case "consolidation": return <ConsolidationSection {...props} />;
          case "policy": return <PolicySection {...props} />;
          default: return <OverviewSection {...props} />;
        }
      })()}
    </div>
  );
}
