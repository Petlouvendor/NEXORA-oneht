import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { Card, Tabs } from "@/shared/ui";
import { DeliveryRoutesSection, InterventionsSection, MobileConfigSection } from "@/mobile/FieldOpsSections";

/** Mobile, terrain & multi-appareils (Phase 44 §12-13, §25, §46, §48). */
export function FieldOpsPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("deliveries");

  const canView = hasPermission("field_ops.view");
  const canManage = hasPermission("field_ops.manage");

  if (!canView) {
    return <Card>Le terrain requiert la permission <code>field_ops.view</code>.</Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Terrain</h1>
      <Tabs
        tabs={[
          { id: "deliveries", label: "Livraisons" },
          { id: "interventions", label: "Interventions" },
          { id: "mobile", label: "Mobile" },
        ]}
        active={tab}
        onChange={setTab}
      />
      {tab === "deliveries" && <DeliveryRoutesSection canManage={canManage} />}
      {tab === "interventions" && <InterventionsSection canManage={canManage} />}
      {tab === "mobile" && <MobileConfigSection />}
    </div>
  );
}
