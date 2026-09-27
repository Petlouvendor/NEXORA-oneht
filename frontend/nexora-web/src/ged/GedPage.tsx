import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { Card, Tabs } from "@/shared/ui";
import { DocumentsSection, GedAuditSection, RetentionSection, SignaturesSection, WorkflowSection } from "@/ged/GedSections";

/** GED, documents, archivage & signature (Phase 45 §3-6, §13-31, §39, §45). */
export function GedPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("library");

  const canView = hasPermission("ged.view");
  const canUpload = hasPermission("ged.upload");
  const canManage = hasPermission("ged.manage");
  const canSign = hasPermission("ged.sign");
  const canManageRetention = hasPermission("ged.manage_retention");

  if (!canView) {
    return <Card>La GED requiert la permission <code>ged.view</code>.</Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Documents (GED)</h1>
      <Tabs
        tabs={[
          { id: "library", label: "Bibliothèque" },
          { id: "workflow", label: "Revue & approbation" },
          { id: "signatures", label: "Signatures" },
          { id: "retention", label: "Rétention & suppression" },
          { id: "audit", label: "Audit" },
        ]}
        active={tab}
        onChange={setTab}
      />
      {tab === "library" && <DocumentsSection canUpload={canUpload} canManage={canManage} />}
      {tab === "workflow" && <WorkflowSection canManage={canManage} />}
      {tab === "signatures" && <SignaturesSection canSign={canSign} />}
      {tab === "retention" && <RetentionSection canManage={canManageRetention} />}
      {tab === "audit" && <GedAuditSection />}
    </div>
  );
}
