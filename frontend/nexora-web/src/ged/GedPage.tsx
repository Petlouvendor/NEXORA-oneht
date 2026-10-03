import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useAsync } from "@/shared/useAsync";
import { Card, KpiCard, Tabs } from "@/shared/ui";
import { listSignatureRequests, listWorkflowQueue } from "@/ged/gedApi";
import { GedAuditSection, LibrarySection, RetentionSection, ReviewSection, SignaturesSection } from "@/ged/GedSections";

/** GED : bibliothèque versionnée, signatures (document → demande → destinataire → signature / refus → archivage),
 * revue, rétention, audit. L'onglet suit l'URL (`?tab=signatures` depuis une notification). */
export function GedPage() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const tab = params.get("tab") ?? "library";
  const setTab = (t: string) => setParams((p) => { p.set("tab", t); return p; }, { replace: true });
  const [version, setVersion] = useState(0);
  const changed = () => setVersion((v) => v + 1);
  const mine = useAsync(() => listSignatureRequests({ mine: true }), [version]);
  const pending = useAsync(() => listSignatureRequests({ status: "pending" }), [version]);
  const review = useAsync(listWorkflowQueue, [version]);

  if (!hasPermission("ged.view")) return <Card>La GED n'est pas accessible avec vos permissions.</Card>;

  const toSign = mine.data?.length ?? 0;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Documents (GED)</h1>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 12 }}>
        <KpiCard label="À signer par moi" value={String(toSign)} tone={toSign ? "warning" : undefined} />
        <KpiCard label="Signatures en cours" value={String(pending.data?.length ?? 0)} />
        <KpiCard label="Documents en revue" value={String(review.data?.length ?? 0)} />
      </div>
      <Tabs active={tab} onChange={setTab} tabs={[
        { id: "library", label: "Bibliothèque" },
        { id: "signatures", label: toSign ? `Signatures (${toSign} à signer)` : "Signatures" },
        { id: "review", label: "Revue" },
        { id: "retention", label: "Rétention & suppression" },
        { id: "audit", label: "Audit" },
      ]} />
      {tab === "library" && <LibrarySection onChanged={changed} />}
      {tab === "signatures" && <SignaturesSection onChanged={changed} />}
      {tab === "review" && <ReviewSection onChanged={changed} />}
      {tab === "retention" && <RetentionSection />}
      {tab === "audit" && <GedAuditSection />}
    </div>
  );
}
