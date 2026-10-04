import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useOutbox } from "@/shared/useOutbox";
import { Card, Tabs } from "@/shared/ui";
import { kindsFor } from "@/shared/syncOperations";
import { MyInterventionsSection, MyRouteSection, SupervisionSection, SyncSection } from "@/mobile/FieldOpsSections";

/** Terrain : assigné → en route → sur place → terminé → preuve → synchronisé. Écran pensé d'abord pour le téléphone. */
export function FieldOpsPage() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") ?? "mine";
  const setTab = (t: string) => setParams((p) => { p.set("tab", t); return p; }, { replace: true });
  const { entries } = useOutbox();
  const waiting = entries.filter((e) => kindsFor("field").includes(e.kind) && e.status !== "synced").length;

  if (!hasPermission("field_ops.view")) return <Card>Le terrain n'est pas accessible avec vos permissions.</Card>;

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Terrain</h1>
      <Tabs active={tab} onChange={setTab} tabs={[
        { id: "mine", label: "Ma tournée" },
        { id: "interventions", label: "Mes interventions" },
        { id: "supervision", label: "Supervision" },
        { id: "sync", label: waiting ? `Synchronisation (${waiting})` : "Synchronisation" },
      ]} />
      {tab === "mine" && <MyRouteSection />}
      {tab === "interventions" && <MyInterventionsSection />}
      {tab === "supervision" && <SupervisionSection />}
      {tab === "sync" && <SyncSection />}
    </div>
  );
}
