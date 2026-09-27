import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Badge, Button, Card, DataTable, SectionLabel, type Column } from "@/shared/ui";
import {
  advanceStop, completeIntervention, completeStop, createIntervention, createRoute, failStop, getMobileConfig,
  listCrashReports, listInterventions, listRoutes, startIntervention,
  type CrashReport, type DeliveryStop, type FieldIntervention,
} from "@/mobile/mobileApi";

// Mobile, terrain & multi-appareils (Phase 44 §12-13, §25, §46, §48).

function useAsync<T>(loader: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = useCallback(() => {
    setError(null);
    loader().then(setData).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(load, [load]);
  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }
  return { data, error, busy, load, run };
}

const stopTone = (s: string) => s === "delivered" ? "success" : s === "failed" || s === "returned" ? "error" : s === "arrived" || s === "en_route" ? "info" : undefined;

// --- §12, §52 Tournées de livraison -----------------------------------------

export function DeliveryRoutesSection({ canManage }: { canManage: boolean }) {
  const routes = useAsync(() => listRoutes());

  const stopCols: Array<Column<DeliveryStop>> = [
    { key: "s", header: "#", numeric: true, render: (s) => s.sequenceNo },
    { key: "a", header: "Adresse", render: (s) => s.address },
    { key: "st", header: "Statut", render: (s) => <Badge tone={stopTone(s.status)}>{s.status}</Badge> },
    { key: "am", header: "Encaissé", numeric: true, render: (s) => s.amountCollected ?? "—" },
    { key: "act", header: "", render: (s) => canManage && !["delivered", "failed", "returned"].includes(s.status) && (
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        {s.status === "pending" && <Button size="sm" variant="ghost" onClick={() => routes.run(() => advanceStop(s.id, "en_route"))}>En route</Button>}
        {s.status !== "arrived" && <Button size="sm" variant="ghost" onClick={() => routes.run(() => advanceStop(s.id, "arrived"))}>Arrivé</Button>}
        <Button size="sm" onClick={() => routes.run(() => completeStop(s.id, {
          operationUuid: crypto.randomUUID(),
          amountCollected: Number(prompt("Montant encaissé (0 si aucun) :", "0") ?? "0") || null,
        }))}>Livré</Button>
        <Button size="sm" variant="ghost" onClick={() => {
          const reason = prompt("Motif de l'échec :"); if (!reason) return;
          void routes.run(() => failStop(s.id, reason));
        }}>Échec</Button>
      </div>
    ) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={canManage && (
          <Button size="sm" disabled={routes.busy} onClick={() => {
            const address = prompt("Adresse de livraison :"); if (!address) return;
            void routes.run(() => createRoute({ routeDate: new Date().toISOString().slice(0, 10), stops: [{ address }] }));
          }}>Nouvelle tournée</Button>
        )}>Tournées de livraison (§12, §52)</SectionLabel>
        {routes.error && <div className="nx-error-text">{routes.error}</div>}
      </Card>
      {(routes.data ?? []).map((r) => (
        <Card key={r.id}>
          <SectionLabel>
            {new Date(r.routeDate).toLocaleDateString()} · {r.vehicle ?? "véhicule non précisé"} · <Badge tone={r.status === "completed" ? "success" : undefined}>{r.status}</Badge>
          </SectionLabel>
          <DataTable columns={stopCols} rows={r.stops} rowKey={(s) => s.id} empty="Aucune étape." />
        </Card>
      ))}
    </div>
  );
}

// --- §13 Interventions techniciens -------------------------------------------

export function InterventionsSection({ canManage }: { canManage: boolean }) {
  const interventions = useAsync(() => listInterventions());
  const cols: Array<Column<FieldIntervention>> = [
    { key: "e", header: "Équipement", render: (i) => i.equipmentDescription },
    { key: "s", header: "Statut", render: (i) => <Badge tone={i.status === "completed" ? "success" : i.status === "cancelled" ? "error" : undefined}>{i.status}</Badge> },
    { key: "t", header: "Temps (min)", numeric: true, render: (i) => i.timeSpentMinutes ?? "—" },
    { key: "r", header: "Rapport", render: (i) => <span style={{ color: "var(--nx-text-2)" }}>{i.reportText || "—"}</span> },
    { key: "a", header: "", render: (i) => canManage && i.status !== "completed" && i.status !== "cancelled" && (
      <div style={{ display: "flex", gap: 6 }}>
        {i.status === "scheduled" && <Button size="sm" variant="ghost" onClick={() => interventions.run(() => startIntervention(i.id))}>Démarrer</Button>}
        <Button size="sm" onClick={() => {
          const reportText = prompt("Rapport d'intervention :", "RAS") ?? "";
          const minutes = Number(prompt("Temps passé (minutes) :", "30") ?? "30");
          void interventions.run(() => completeIntervention(i.id, { operationUuid: crypto.randomUUID(), checklist: [], partsUsed: [], timeSpentMinutes: minutes, reportText }));
        }}>Terminer</Button>
      </div>
    ) },
  ];
  return (
    <Card>
      <SectionLabel action={canManage && (
        <Button size="sm" disabled={interventions.busy} onClick={() => {
          const equipmentDescription = prompt("Équipement à intervenir :"); if (!equipmentDescription) return;
          void interventions.run(() => createIntervention({ equipmentDescription }));
        }}>Nouvelle intervention</Button>
      )}>Interventions techniciens (§13)</SectionLabel>
      {interventions.error && <div className="nx-error-text">{interventions.error}</div>}
      <DataTable columns={cols} rows={interventions.data ?? []} rowKey={(i) => i.id} empty="Aucune intervention." />
    </Card>
  );
}

// --- §25, §46, §48 Config mobile & diagnostics --------------------------------

export function MobileConfigSection() {
  const config = useAsync(getMobileConfig);
  const crashes = useAsync(() => listCrashReports(50));
  const crashCols: Array<Column<CrashReport>> = [
    { key: "v", header: "Version", render: (c) => c.appVersion },
    { key: "o", header: "OS", render: (c) => c.os },
    { key: "m", header: "Message", render: (c) => c.message },
    { key: "d", header: "Date", render: (c) => new Date(c.occurredAt).toLocaleString() },
  ];
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Configuration mobile (§25, §48)</SectionLabel>
        {config.error && <div className="nx-error-text">{config.error}</div>}
        {config.data && (
          <ul style={{ margin: 0, paddingLeft: 18, fontSize: 13, color: "var(--nx-text-2)" }}>
            <li>Ventes hors ligne : {config.data.allowOfflineSales ? "autorisées" : "interdites"}</li>
            <li>Crédit hors ligne : {config.data.allowOfflineCredit ? `autorisé (plafond ${config.data.offlineCreditLimit})` : "interdit"}</li>
            <li>Durée maximale hors ligne : {config.data.maxOfflineMinutes} min</li>
            <li>Opérations rejouables hors ligne : {config.data.supportedOfflineEntityTypes.join(", ")}</li>
            <li>Push : {config.data.pushEnabled ? "activé" : "désactivé"}</li>
          </ul>
        )}
      </Card>
      <Card>
        <SectionLabel>Diagnostics — rapports de crash minimisés (§46)</SectionLabel>
        {crashes.error && <div className="nx-error-text">{crashes.error}</div>}
        <DataTable columns={crashCols} rows={crashes.data ?? []} rowKey={(c) => c.id} empty="Aucun crash signalé." />
      </Card>
    </div>
  );
}
