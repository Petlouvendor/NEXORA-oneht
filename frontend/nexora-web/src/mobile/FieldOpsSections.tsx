import { useCallback, useEffect, useMemo, useState, type FormEvent } from "react";
import { apiDownload } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useOnline } from "@/shared/useOnline";
import { useOutbox } from "@/shared/useOutbox";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { autoFlushOnReconnect, enqueue, flushOutbox, pruneSynced, type BatchSender } from "@/shared/offlineQueue";
import {
  isOfflineFailure, kindsFor, toSyncOperations, type FieldDeliveryPayload, type FieldInterventionPayload, type PendingImage,
} from "@/shared/syncOperations";
import { pushBatch } from "@/sync/syncApi";
import {
  INTERVENTION_STATUS, ROUTE_STATUS, STOP_STATUS, advanceStop, completeIntervention, completeStop, createIntervention, createRoute,
  failStop, fieldDeviceId, getFieldOptions, getMobileConfig, listInterventions, listRoutes, myInterventions, myStops,
  startIntervention, uploadProof, type DeliveryStop, type FieldIntervention, type FieldOptions,
} from "@/mobile/mobileApi";
import { PhotoInput, SignaturePad, currentPosition } from "@/mobile/FieldCapture";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const big = { minHeight: 44, flex: "1 1 140px" } as const;
const FIELD_KINDS = kindsFor("field");

/** Envoi des opérations terrain en attente : preuves d'abord, puis le lot de synchronisation. */
export const fieldSender: BatchSender = async (batch) => {
  const device = await fieldDeviceId();
  const res = await pushBatch(device, await toSyncOperations(batch, uploadProof));
  return res.operations.map((o) => ({
    id: o.operationUuid, status: o.status === "SYNCED" ? "synced" : o.status === "CONFLICT" ? "conflict" : "failed", message: o.message,
  }));
};

/** Opérations terrain encore en file, par arrêt / intervention (pour afficher « en attente de synchronisation »). */
function usePendingFieldOps() {
  const { entries } = useOutbox();
  return useMemo(() => {
    const byStop = new Map<string, string>();
    const byIntervention = new Map<string, string>();
    for (const e of entries) {
      if (e.status === "synced") continue;
      if (e.kind === "field.delivery") byStop.set((e.payload as FieldDeliveryPayload).stopId, e.status);
      if (e.kind === "field.intervention") byIntervention.set((e.payload as FieldInterventionPayload).interventionId, e.status);
    }
    return { byStop, byIntervention };
  }, [entries]);
}

function PendingBadge({ status }: { status: string | undefined }) {
  if (!status) return null;
  return status === "conflict"
    ? <Badge tone="error">Conflit — à vérifier</Badge>
    : <Badge tone="warning">En attente de synchronisation</Badge>;
}

// ------------------------------------------------------------------ Ma tournée (livreur)

export function MyRouteSection() {
  const toast = useToast();
  const online = useOnline();
  const [version, setVersion] = useState(0);
  const stops = useAsync(myStops, [version]);
  const options = useAsync(getFieldOptions, []);
  const pending = usePendingFieldOps();
  const [delivering, setDelivering] = useState<DeliveryStop | null>(null);
  const [failing, setFailing] = useState<DeliveryStop | null>(null);
  const { busy, error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);

  useEffect(() => autoFlushOnReconnect(fieldSender, FIELD_KINDS), []);

  async function advance(s: DeliveryStop, status: "en_route" | "arrived") {
    if (!online) { toast.push("Hors ligne : cette étape ne s'enregistre qu'en ligne. Vous pouvez livrer directement, la livraison sera synchronisée.", "warning"); return; }
    if (await submit(() => advanceStop(s.id, status))) reload();
  }

  const list = stops.data ?? [];
  const todo = list.filter((s) => !["delivered", "failed", "returned"].includes(s.status) && !pending.byStop.has(s.id));
  return (
    <div style={{ display: "grid", gap: 12 }}>
      {(error || stops.error) && <div role="alert" className="nx-error-text">{error ?? stops.error}</div>}
      {!online && <Card><b>Hors ligne.</b> <span style={hint}>Les livraisons sont gardées sur cet appareil et envoyées au retour du réseau.</span></Card>}
      <div style={hint}>{todo.length} arrêt(s) à faire aujourd'hui.</div>
      {list.length === 0 && !stops.loading && <Card>Aucune livraison assignée.</Card>}
      {list.map((s) => {
        const p = pending.byStop.get(s.id);
        const open = !["delivered", "failed", "returned"].includes(s.status) && !p;
        return (
          <Card key={s.id} style={{ display: "grid", gap: 8 }}>
            <div style={{ display: "flex", justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
              <b>#{s.sequenceNo} · {s.customerName ?? "Client non précisé"}</b>
              <span><Badge tone={STOP_STATUS[s.status].tone}>{STOP_STATUS[s.status].label}</Badge> <PendingBadge status={p} /></span>
            </div>
            <div>{s.address || "Adresse non précisée"}</div>
            {s.contactPhone && <a href={`tel:${s.contactPhone}`}>{s.contactPhone}</a>}
            {s.orderNumber && <div style={hint}>Commande {s.orderNumber} · total {money(s.orderTotal ?? 0, s.currency ?? undefined)}{s.orderDeposit ? ` · acompte ${money(s.orderDeposit, s.currency ?? undefined)}` : ""}</div>}
            {s.failureReason && <div style={hint}>Échec : {s.failureReason}</div>}
            {open && (
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {(s.status === "pending" || s.status === "failed") && <Button style={big} variant="subtle" disabled={busy} onClick={() => advance(s, "en_route")}>En route</Button>}
                {s.status !== "arrived" && <Button style={big} variant="subtle" disabled={busy} onClick={() => advance(s, "arrived")}>Sur place</Button>}
                <Button style={big} disabled={busy} onClick={() => setDelivering(s)}>Livrer</Button>
                <Button style={big} variant="ghost" disabled={busy} onClick={() => setFailing(s)}>Échec</Button>
              </div>
            )}
          </Card>
        );
      })}
      {delivering && <DeliverDialog stop={delivering} options={options.data} onClose={() => setDelivering(null)} onDone={() => { setDelivering(null); reload(); }} />}
      {failing && <ConfirmDialog title={`Échec de livraison — ${failing.customerName ?? failing.address}`} requireReason danger confirmLabel="Enregistrer l'échec"
        message="L'arrêt pourra être relivré plus tard. Indiquez la raison (absent, adresse introuvable, refus…)."
        onCancel={() => setFailing(null)}
        onConfirm={async (reason) => {
          const s = failing; setFailing(null);
          if (!online) { toast.push("Hors ligne : un échec ne s'enregistre qu'en ligne.", "warning"); return; }
          if (await submit(() => failStop(s.id, reason))) { toast.success("Échec enregistré."); reload(); }
        }} />}
    </div>
  );
}

/** Livraison : preuve (signature et/ou photo), encaissement éventuel, position si prévue ; hors ligne → mise en file. */
function DeliverDialog({ stop, options, onClose, onDone }: { stop: DeliveryStop; options: FieldOptions | null; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const online = useOnline();
  const [operationUuid] = useState(newOperationId);
  const [signature, setSignature] = useState<PendingImage | null>(null);
  const [photo, setPhoto] = useState<PendingImage | null>(null);
  const [amount, setAmount] = useState("");
  const [account, setAccount] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "delivery_stop.proof": "proof", "delivery_stop.amount": "amount", "delivery_stop.account": "account",
  });
  const accounts = (options?.cashAccounts ?? []).filter((a) => !stop.currency || a.currency === stop.currency);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!signature && !photo) return fail("proof", "Preuve requise : faites signer le client ou prenez une photo.");
    const a = num(amount);
    if (amount.trim() && (a == null || a < 0)) return fail("amount", "Montant invalide.");
    if (a && a > 0 && !account) return fail("account", "Choisissez la caisse qui reçoit l'argent.");
    const pos = options?.captureLocation ? await currentPosition() : null;
    const payload: FieldDeliveryPayload = {
      stopId: stop.id, latitude: pos?.latitude ?? null, longitude: pos?.longitude ?? null,
      signatureAttachmentId: null, photoAttachmentId: null,
      amountCollected: a && a > 0 ? a : null, paymentFinancialAccountId: a && a > 0 ? account : null,
      pendingSignature: signature, pendingPhoto: photo,
    };
    const queue = () => {
      enqueue({ id: operationUuid, kind: "field.delivery", payload });
      toast.push("Livraison gardée sur l'appareil : elle sera envoyée au retour du réseau.", "warning");
      onDone();
    };
    if (!online) return queue();
    const r = await submit(async () => {
      try {
        const sigId = signature ? await uploadProof(signature, "signature", "delivery_stop", stop.id) : null;
        const photoId = photo ? await uploadProof(photo, "photo", "delivery_stop", stop.id) : null;
        return await completeStop(stop.id, {
          operationUuid, latitude: payload.latitude, longitude: payload.longitude, signatureAttachmentId: sigId, photoAttachmentId: photoId,
          amountCollected: payload.amountCollected, paymentFinancialAccountId: payload.paymentFinancialAccountId,
        });
      } catch (err) {
        if (isOfflineFailure(err, navigator.onLine)) { queue(); return undefined; }
        throw err;
      }
    });
    if (r) { toast.success(r.amountCollected ? `Livré — ${money(r.amountCollected, r.currency ?? undefined)} encaissé.` : "Livraison confirmée."); onDone(); }
  }

  return (
    <Modal title={`Livrer — ${stop.customerName ?? stop.address}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Confirmer la livraison</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(480px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Signature du client" error={fieldErrors.proof}><SignaturePad onChange={setSignature} /></Field>
        <Field label="Photo (colis remis, bon signé…)"><PhotoInput onChange={setPhoto} /></Field>
        {stop.salesOrderId && (
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 8 }}>
            <Field label="Montant encaissé" error={fieldErrors.amount} hint={`Commande ${stop.orderNumber} : ${money(stop.orderTotal ?? 0, stop.currency ?? undefined)}${stop.orderDeposit ? `, acompte ${money(stop.orderDeposit, stop.currency ?? undefined)}` : ""}`}>
              <TextInput type="number" min={0} step="0.01" inputMode="decimal" value={amount} placeholder="0 si rien encaissé" onChange={(e) => setAmount(e.target.value)} />
            </Field>
            <Field label="Caisse" error={fieldErrors.account}>
              <Select value={account} onChange={(e) => setAccount(e.target.value)}>
                <option value="">—</option>
                {accounts.map((x) => <option key={x.id} value={x.id}>{x.name} ({x.currency})</option>)}
              </Select>
            </Field>
          </div>
        )}
        <div style={hint}>
          {options?.captureLocation
            ? "La position de livraison sera enregistrée (votre appareil vous demandera l'autorisation)."
            : "Aucune position n'est enregistrée (non prévu par l'entreprise)."}
          {!online && " Hors ligne : la livraison sera envoyée au retour du réseau."}
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

// ------------------------------------------------------------------ Mes interventions (technicien)

export function MyInterventionsSection() {
  const toast = useToast();
  const online = useOnline();
  const [version, setVersion] = useState(0);
  const list = useAsync(myInterventions, [version]);
  const pending = usePendingFieldOps();
  const [completing, setCompleting] = useState<FieldIntervention | null>(null);
  const { busy, error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);
  useEffect(() => autoFlushOnReconnect(fieldSender, FIELD_KINDS), []);

  return (
    <div style={{ display: "grid", gap: 12 }}>
      {(error || list.error) && <div role="alert" className="nx-error-text">{error ?? list.error}</div>}
      {(list.data ?? []).length === 0 && !list.loading && <Card>Aucune intervention assignée.</Card>}
      {(list.data ?? []).map((i) => {
        const p = pending.byIntervention.get(i.id);
        const open = (i.status === "scheduled" || i.status === "in_progress") && !p;
        return (
          <Card key={i.id} style={{ display: "grid", gap: 8 }}>
            <div style={{ display: "flex", justifyContent: "space-between", gap: 8, flexWrap: "wrap" }}>
              <b>{i.equipmentDescription}</b>
              <span><Badge tone={INTERVENTION_STATUS[i.status].tone}>{INTERVENTION_STATUS[i.status].label}</Badge> <PendingBadge status={p} /></span>
            </div>
            <div style={hint}>{i.customerName ?? "Client non précisé"}{i.scheduledAt ? ` · prévue le ${dateTime(i.scheduledAt)}` : ""}</div>
            {i.status === "completed" && <div style={hint}>Terminée {i.completedAt ? dateTime(i.completedAt) : ""} — {i.reportText}</div>}
            {open && (
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                {i.status === "scheduled" && <Button style={big} variant="subtle" disabled={busy}
                  onClick={async () => {
                    if (!online) { toast.push("Hors ligne : terminez directement l'intervention, elle sera synchronisée.", "warning"); return; }
                    if (await submit(() => startIntervention(i.id))) reload();
                  }}>Sur place</Button>}
                <Button style={big} disabled={busy} onClick={() => setCompleting(i)}>Terminer</Button>
              </div>
            )}
          </Card>
        );
      })}
      {completing && <CompleteInterventionDialog intervention={completing} onClose={() => setCompleting(null)} onDone={() => { setCompleting(null); reload(); }} />}
    </div>
  );
}

function CompleteInterventionDialog({ intervention, onClose, onDone }: { intervention: FieldIntervention; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const online = useOnline();
  const [operationUuid] = useState(newOperationId);
  const [checklist, setChecklist] = useState<Array<{ label: string; done: boolean }>>(
    intervention.checklist.length ? intervention.checklist : [{ label: "", done: false }]);
  const [report, setReport] = useState("");
  const [minutes, setMinutes] = useState(intervention.startedAt
    ? String(Math.max(1, Math.round((Date.now() - new Date(intervention.startedAt).getTime()) / 60000))) : "");
  const [signature, setSignature] = useState<PendingImage | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "field_intervention.report": "report", "field_intervention.time": "minutes" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const m = num(minutes);
    if (m == null || !Number.isInteger(m) || m < 0 || m > 1440) return fail("minutes", "Temps passé en minutes (0 à 1440).");
    if (report.trim().length < 3) return fail("report", "Compte rendu obligatoire.");
    const items = checklist.filter((c) => c.label.trim()).map((c) => ({ label: c.label.trim(), done: c.done }));
    const payload: FieldInterventionPayload = {
      interventionId: intervention.id, checklist: items, partsUsed: [], timeSpentMinutes: m, reportText: report.trim(),
      signatureAttachmentId: null, pendingSignature: signature,
    };
    const queue = () => {
      enqueue({ id: operationUuid, kind: "field.intervention", payload });
      toast.push("Intervention gardée sur l'appareil : elle sera envoyée au retour du réseau.", "warning");
      onDone();
    };
    if (!online) return queue();
    const r = await submit(async () => {
      try {
        const sigId = signature ? await uploadProof(signature, "signature", "field_intervention", intervention.id) : null;
        return await completeIntervention(intervention.id, { operationUuid, checklist: items, partsUsed: [], timeSpentMinutes: m, reportText: report.trim(), signatureAttachmentId: sigId });
      } catch (err) {
        if (isOfflineFailure(err, navigator.onLine)) { queue(); return undefined; }
        throw err;
      }
    });
    if (r) { toast.success("Intervention terminée."); onDone(); }
  }

  return (
    <Modal title={`Terminer — ${intervention.equipmentDescription}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Terminer l'intervention</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(480px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Points de contrôle">
          <div style={{ display: "grid", gap: 6 }}>
            {checklist.map((c, idx) => (
              <div key={idx} style={{ display: "flex", gap: 6, alignItems: "center" }}>
                <input type="checkbox" aria-label={`Point ${idx + 1} fait`} checked={c.done}
                  onChange={(e) => setChecklist((l) => l.map((x, j) => (j === idx ? { ...x, done: e.target.checked } : x)))} />
                <TextInput aria-label={`Point ${idx + 1}`} value={c.label} maxLength={120} placeholder="Ex. filtre nettoyé"
                  onChange={(e) => setChecklist((l) => l.map((x, j) => (j === idx ? { ...x, label: e.target.value } : x)))} />
              </div>
            ))}
            <Button type="button" size="sm" variant="ghost" onClick={() => setChecklist((l) => [...l, { label: "", done: false }])}>+ Point</Button>
          </div>
        </Field>
        <Field label="Compte rendu" required error={fieldErrors.report}><TextInput value={report} maxLength={4000} placeholder="Travaux effectués, observations" onChange={(e) => setReport(e.target.value)} /></Field>
        <Field label="Temps passé (minutes)" required error={fieldErrors.minutes}><TextInput type="number" min={0} max={1440} inputMode="numeric" value={minutes} onChange={(e) => setMinutes(e.target.value)} /></Field>
        <Field label="Signature du client (facultative)"><SignaturePad onChange={setSignature} /></Field>
        <div style={hint}>Les pièces utilisées ne sont pas encore décomptées du stock par NEXORA : notez-les dans le compte rendu.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

// ------------------------------------------------------------------ Supervision

export function SupervisionSection() {
  const { hasPermission } = useAuth();
  const [date, setDate] = useState(todayIso());
  const [version, setVersion] = useState(0);
  const routes = useAsync(() => listRoutes(date || undefined), [date, version]);
  const interventions = useAsync(() => listInterventions(), [version]);
  const options = useAsync(getFieldOptions, [version]);
  const [creating, setCreating] = useState<"route" | "intervention" | null>(null);
  const [proof, setProof] = useState<DeliveryStop | null>(null);
  const reload = () => setVersion((v) => v + 1);
  const canManage = hasPermission("field_ops.manage");

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={canManage && <Button size="sm" onClick={() => setCreating("route")}>Nouvelle tournée</Button>}>Tournées</SectionLabel>
        <Field label="Date"><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        {routes.error && <div role="alert" className="nx-error-text">{routes.error}</div>}
        {(routes.data ?? []).length === 0 && !routes.loading && <div style={hint}>Aucune tournée ce jour.</div>}
        <div style={{ display: "grid", gap: 10, marginTop: 8 }}>
          {(routes.data ?? []).map((r) => (
            <div key={r.id} style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 6 }}>
              <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
                <b>{r.driverName ?? "Livreur non assigné"}</b>
                <Badge tone={r.status === "completed" ? "success" : r.status === "in_progress" ? "info" : undefined}>{ROUTE_STATUS[r.status]}</Badge>
                <span style={hint}>{dateShort(r.routeDate)}{r.vehicle ? ` · ${r.vehicle}` : ""} · {r.stops.filter((s) => s.status === "delivered").length}/{r.stops.length} livré(s)</span>
              </div>
              <DataTable exportTitle={`Tournée ${dateShort(r.routeDate)} — ${r.driverName ?? ""}`}
                columns={[
                  { key: "n", header: "#", numeric: true, render: (s) => s.sequenceNo },
                  { key: "c", header: "Client", render: (s) => s.customerName ?? "—" },
                  { key: "a", header: "Adresse", render: (s) => s.address || "—" },
                  { key: "o", header: "Commande", render: (s) => s.orderNumber ?? "—" },
                  { key: "s", header: "Statut", render: (s) => <Badge tone={STOP_STATUS[s.status].tone}>{STOP_STATUS[s.status].label}</Badge> },
                  { key: "t", header: "Livré le", render: (s) => (s.deliveredAt ? dateTime(s.deliveredAt) : s.failureReason ?? "—") },
                  { key: "m", header: "Encaissé", numeric: true, render: (s) => (s.amountCollected ? money(s.amountCollected, s.currency ?? undefined) : "—") },
                  { key: "p", header: "", render: (s) => (s.signatureAttachmentId || s.photoAttachmentId)
                    ? <Button size="sm" variant="ghost" onClick={() => setProof(s)}>Preuve</Button> : null },
                ]}
                rows={r.stops} rowKey={(s) => s.id} />
            </div>
          ))}
        </div>
      </Card>
      <Card>
        <SectionLabel action={canManage && <Button size="sm" onClick={() => setCreating("intervention")}>Nouvelle intervention</Button>}>Interventions</SectionLabel>
        {interventions.error && <div role="alert" className="nx-error-text">{interventions.error}</div>}
        <DataTable exportTitle="Interventions"
          columns={[
            { key: "e", header: "Intervention", render: (i) => i.equipmentDescription },
            { key: "t", header: "Technicien", render: (i) => i.technicianName ?? "—" },
            { key: "d", header: "Prévue", render: (i) => (i.scheduledAt ? dateTime(i.scheduledAt) : "—") },
            { key: "s", header: "Statut", render: (i) => <Badge tone={INTERVENTION_STATUS[i.status].tone}>{INTERVENTION_STATUS[i.status].label}</Badge> },
            { key: "m", header: "Temps", numeric: true, render: (i) => (i.timeSpentMinutes != null ? `${i.timeSpentMinutes} min` : "—") },
            { key: "r", header: "Compte rendu", render: (i) => i.reportText || "—" },
          ]}
          rows={interventions.data ?? []} rowKey={(i) => i.id} empty={interventions.loading ? "Chargement…" : "Aucune intervention."} />
      </Card>
      {creating === "route" && options.data && <RouteDialog options={options.data} onClose={() => setCreating(null)} onDone={() => { setCreating(null); reload(); }} />}
      {creating === "intervention" && options.data && <InterventionDialog options={options.data} onClose={() => setCreating(null)} onDone={() => { setCreating(null); reload(); }} />}
      {proof && <ProofDialog stop={proof} onClose={() => setProof(null)} />}
    </div>
  );
}

type StopDraft = { key: number; orderId: string; address: string; phone: string };

function RouteDialog({ options, onClose, onDone }: { options: FieldOptions; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [driver, setDriver] = useState("");
  const [date, setDate] = useState(todayIso());
  const [vehicle, setVehicle] = useState("");
  const [stops, setStops] = useState<StopDraft[]>([{ key: 1, orderId: "", address: "", phone: "" }]);
  const { busy, error, fieldErrors, submit, fail, setError } = useSubmit({ "delivery_route.driver": "driver", "delivery_route.date": "date" });
  const setStop = (key: number, patch: Partial<StopDraft>) => setStops((l) => l.map((s) => (s.key === key ? { ...s, ...patch } : s)));
  const used = new Set(stops.map((s) => s.orderId).filter(Boolean));

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!driver) return fail("driver", "Choisissez le livreur.");
    if (!date) return fail("date", "Date obligatoire.");
    const filled = stops.filter((s) => s.orderId || s.address.trim());
    if (filled.length === 0) { setError("Ajoutez au moins un arrêt (commande ou adresse)."); return; }
    const r = await submit(() => createRoute({
      driverUserId: driver, routeDate: date, vehicle: vehicle.trim() || null,
      stops: filled.map((s) => ({ salesOrderId: s.orderId || null, customerId: null, address: s.address.trim(), latitude: null, longitude: null, contactPhone: s.phone.trim() || null })),
    }));
    if (r) { toast.success(`Tournée créée : ${r.stops.length} arrêt(s) assigné(s) à ${r.driverName ?? "ce livreur"}.`); onDone(); }
  }

  return (
    <Modal title="Nouvelle tournée" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer la tournée</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(640px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 8 }}>
          <Field label="Livreur" required error={fieldErrors.driver}>
            <Select value={driver} onChange={(e) => setDriver(e.target.value)}>
              <option value="">— Choisir —</option>
              {options.agents.map((a) => <option key={a.id} value={a.id}>{a.fullName}</option>)}
            </Select>
          </Field>
          <Field label="Date" required error={fieldErrors.date}><TextInput type="date" min={todayIso()} value={date} onChange={(e) => setDate(e.target.value)} /></Field>
          <Field label="Véhicule"><TextInput value={vehicle} maxLength={80} placeholder="Ex. Moto 2" onChange={(e) => setVehicle(e.target.value)} /></Field>
        </div>
        {stops.map((s, i) => (
          <fieldset key={s.key} style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 8, display: "grid", gap: 6 }}>
            <legend style={{ fontSize: 13 }}>Arrêt {i + 1}</legend>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 6 }}>
              <Field label="Commande à livrer">
                <Select aria-label={`Commande arrêt ${i + 1}`} value={s.orderId} onChange={(e) => {
                  const o = options.orders.find((x) => x.id === e.target.value);
                  setStop(s.key, { orderId: e.target.value, address: o?.deliveryAddress ?? s.address });
                }}>
                  <option value="">Sans commande</option>
                  {options.orders.filter((o) => o.id === s.orderId || !used.has(o.id)).map((o) =>
                    <option key={o.id} value={o.id}>{o.documentNumber} — {o.customerName ?? "client"} ({money(o.total, o.currency)})</option>)}
                </Select>
              </Field>
              <Field label="Adresse"><TextInput aria-label={`Adresse arrêt ${i + 1}`} value={s.address} maxLength={300} onChange={(e) => setStop(s.key, { address: e.target.value })} /></Field>
              <Field label="Téléphone"><TextInput type="tel" value={s.phone} maxLength={30} onChange={(e) => setStop(s.key, { phone: e.target.value })} /></Field>
            </div>
            {stops.length > 1 && <div><Button type="button" size="sm" variant="ghost" onClick={() => setStops((l) => l.filter((x) => x.key !== s.key))}>Retirer</Button></div>}
          </fieldset>
        ))}
        <Button type="button" size="sm" variant="subtle" onClick={() => setStops((l) => [...l, { key: Math.max(...l.map((x) => x.key)) + 1, orderId: "", address: "", phone: "" }])}>+ Arrêt</Button>
        <div style={hint}>Seules les commandes confirmées, pas encore prévues dans une tournée, sont proposées. Le livreur est prévenu sur ses appareils.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function InterventionDialog({ options, onClose, onDone }: { options: FieldOptions; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [technician, setTechnician] = useState("");
  const [equipment, setEquipment] = useState("");
  const [when, setWhen] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "field_intervention.technician": "technician", "field_intervention.equipment": "equipment" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!technician) return fail("technician", "Choisissez le technicien.");
    if (equipment.trim().length < 3) return fail("equipment", "Décrivez l'équipement ou le motif.");
    const r = await submit(() => createIntervention({ technicianUserId: technician, customerId: null, equipmentDescription: equipment.trim(),
      scheduledAt: when ? new Date(when).toISOString() : null }));
    if (r) { toast.success(`Intervention assignée à ${r.technicianName ?? "ce technicien"}.`); onDone(); }
  }

  return (
    <Modal title="Nouvelle intervention" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Technicien" required error={fieldErrors.technician}>
          <Select value={technician} onChange={(e) => setTechnician(e.target.value)}>
            <option value="">— Choisir —</option>
            {options.agents.map((a) => <option key={a.id} value={a.id}>{a.fullName}</option>)}
          </Select>
        </Field>
        <Field label="Équipement / motif" required error={fieldErrors.equipment}><TextInput value={equipment} maxLength={300} placeholder="Ex. climatiseur salle 2 — ne refroidit plus" onChange={(e) => setEquipment(e.target.value)} /></Field>
        <Field label="Prévue le"><TextInput type="datetime-local" value={when} onChange={(e) => setWhen(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Preuve de livraison : signature et photo, chargées avec authentification. */
function ProofDialog({ stop, onClose }: { stop: DeliveryStop; onClose: () => void }) {
  const [urls, setUrls] = useState<{ signature?: string; photo?: string }>({});
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    const made: string[] = [];
    const load = async (id: string | null) => {
      if (!id) return undefined;
      const { blob } = await apiDownload(`/attachments/${id}`);
      const u = URL.createObjectURL(blob);
      made.push(u);
      return u;
    };
    Promise.all([load(stop.signatureAttachmentId), load(stop.photoAttachmentId)])
      .then(([signature, photo]) => setUrls({ signature, photo }))
      .catch(() => setError("Preuve illisible."));
    return () => made.forEach((u) => URL.revokeObjectURL(u));
  }, [stop.signatureAttachmentId, stop.photoAttachmentId]);
  return (
    <Modal title={`Preuve de livraison — ${stop.customerName ?? stop.address}`} onClose={onClose} footer={<Button onClick={onClose}>Fermer</Button>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(460px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={hint}>Livré {stop.deliveredAt ? dateTime(stop.deliveredAt) : ""}{stop.completedByName ? ` par ${stop.completedByName}` : ""}
          {stop.latitude != null ? ` · position ${stop.latitude}, ${stop.longitude}` : ""}</div>
        {urls.signature && <figure style={{ margin: 0 }}><img src={urls.signature} alt="Signature du client" style={{ maxWidth: "100%", background: "#fff", borderRadius: 8 }} /><figcaption style={hint}>Signature</figcaption></figure>}
        {urls.photo && <figure style={{ margin: 0 }}><img src={urls.photo} alt="Photo de livraison" style={{ maxWidth: "100%", borderRadius: 8 }} /><figcaption style={hint}>Photo</figcaption></figure>}
      </div>
    </Modal>
  );
}

// ------------------------------------------------------------------ Synchronisation

export function SyncSection() {
  const toast = useToast();
  const online = useOnline();
  const { entries } = useOutbox();
  const config = useAsync(getMobileConfig, []);
  const [busy, setBusy] = useState(false);
  const field = entries.filter((e) => FIELD_KINDS.includes(e.kind));

  const syncNow = useCallback(async () => {
    setBusy(true);
    try {
      const r = await flushOutbox(fieldSender, FIELD_KINDS);
      if (r.synced) toast.success(`${r.synced} opération(s) synchronisée(s).`);
      if (r.conflict) toast.push(`${r.conflict} conflit(s) : l'opération avait déjà été faite autrement — à vérifier avec un responsable.`, "warning");
      if (r.failed) toast.push(`${r.failed} opération(s) refusée(s) : voir le détail.`, "warning");
      if (!r.synced && !r.conflict && !r.failed) toast.push(online ? "Rien à envoyer, ou envoi impossible pour l'instant." : "Toujours hors ligne.", "info");
    } finally { setBusy(false); }
  }, [online, toast]);

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={<div style={{ display: "flex", gap: 6 }}>
          <Button size="sm" disabled={busy || !online || field.every((e) => e.status === "synced")} onClick={syncNow}>Synchroniser</Button>
          <Button size="sm" variant="ghost" disabled={!field.some((e) => e.status === "synced")} onClick={pruneSynced}>Effacer les confirmées</Button>
        </div>}>Opérations de cet appareil</SectionLabel>
        <div style={{ ...hint, marginBottom: 8 }}>{online ? "En ligne." : "Hors ligne."} Une opération n'est confirmée qu'après accord du serveur ; un rejeu ne la compte jamais deux fois.</div>
        <DataTable exportTitle="Opérations terrain en file"
          columns={[
            { key: "k", header: "Opération", render: (e) => (e.kind === "field.delivery" ? "Livraison" : "Intervention") },
            { key: "d", header: "Saisie", render: (e) => dateTime(e.createdAt) },
            { key: "s", header: "État", render: (e) => <Badge tone={e.status === "synced" ? "success" : e.status === "conflict" || e.status === "failed" ? "error" : "warning"}>
              {e.status === "synced" ? "Synchronisée" : e.status === "conflict" ? "Conflit" : e.status === "failed" ? "Refusée" : e.status === "syncing" ? "Envoi…" : "En attente"}</Badge> },
            { key: "e", header: "Détail", render: (e) => e.lastError ?? "" },
          ]}
          rows={field} rowKey={(e) => e.id} empty="Aucune opération en attente." />
      </Card>
      {config.data && (
        <Card>
          <SectionLabel>Paramètres hors ligne</SectionLabel>
          <ul style={{ margin: 0, paddingLeft: 18, ...hint, fontSize: 13 }}>
            <li>Opérations rejouables hors ligne : livraisons, interventions{config.data.allowOfflineSales ? ", ventes" : ""}.</li>
            <li>Notifications sur l'appareil : {config.data.pushEnabled ? "activées" : "non configurées sur le serveur"}.</li>
          </ul>
        </Card>
      )}
    </div>
  );
}
