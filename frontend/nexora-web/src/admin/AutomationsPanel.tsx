import { useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateTime } from "@/shared/format";
import { Badge, Button, Card, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  getAutomationRuns, getAutomations, runAutomation, setAutomationStatus, upsertAutomation, type Automation,
} from "@/admin/adminApi";
import { AUTOMATION_KINDS, FREQUENCIES, codeFromName } from "@/admin/adminRules";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;

/** Automatisations : QUAND (planification) / SI (critère) / ALORS (notification) — seulement ce que NEXORA exécute vraiment. */
export function AutomationsPanel() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const list = useAsync(getAutomations, [version]);
  const [creating, setCreating] = useState(false);
  const [history, setHistory] = useState<Automation | null>(null);
  const { busy, error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);

  async function run(a: Automation, preview: boolean) {
    const r = await submit(() => runAutomation(a.id, preview));
    if (!r) return;
    if (r.status === "failed") toast.error(`Échec : ${r.error ?? "erreur inconnue"}`);
    else if (preview) toast.push(`Aperçu : ${r.itemsProcessed} élément(s) seraient traités — aucune notification envoyée.`, "info");
    else { toast.success(`Exécutée : ${r.itemsProcessed} élément(s) traité(s).`); reload(); }
  }

  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setCreating(true)}>Nouvelle automatisation</Button>}>Automatisations</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        Exécutées par le planificateur aux dates prévues (s'il est activé sur le serveur), ou à la demande avec « Exécuter ».
        Les notifications sont envoyées à la personne qui a créé l'automatisation.
      </div>
      {(error || list.error) && <div role="alert" className="nx-error-text">{error ?? list.error}</div>}
      {(list.data ?? []).length === 0 && !list.loading && <div style={hint}>Aucune automatisation.</div>}
      <div style={{ display: "grid", gap: 10 }}>
        {(list.data ?? []).map((a) => {
          const k = AUTOMATION_KINDS[a.kind];
          const freq = String(a.schedule.frequency ?? "");
          return (
            <div key={a.id} style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 4 }}>
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <b>{a.name}</b>
                <Badge tone={a.status === "active" ? "success" : "warning"}>{a.status === "active" ? "Active" : "En pause"}</Badge>
                {!k && <Badge tone="error">Type non exécuté par NEXORA</Badge>}
              </div>
              {k && <div style={{ fontSize: 13 }}>
                <b>QUAND</b> {FREQUENCIES[freq]?.toLowerCase() ?? freq}{a.schedule.atTime ? ` à ${String(a.schedule.atTime)}` : ""} ·{" "}
                <b>SI</b> {k.condition}{a.kind === "expiry_alert" && a.parameters.withinDays ? ` (${String(a.parameters.withinDays)} jours)` : ""} ·{" "}
                <b>ALORS</b> {k.then}
              </div>}
              <div style={hint}>Dernière exécution : {a.lastRunAt ? dateTime(a.lastRunAt) : "jamais"} · prochaine : {a.status === "active" && a.nextRunAt ? dateTime(a.nextRunAt) : "—"}</div>
              <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(a, true)}>Aperçu</Button>
                <Button size="sm" disabled={busy} onClick={() => run(a, false)}>Exécuter</Button>
                <Button size="sm" variant="ghost" disabled={busy} onClick={async () => {
                  const next = a.status === "active" ? "paused" : "active";
                  if (await submit(() => setAutomationStatus(a.id, next).then(() => true))) { toast.success(next === "paused" ? "Automatisation en pause." : "Automatisation réactivée."); reload(); }
                }}>{a.status === "active" ? "Mettre en pause" : "Réactiver"}</Button>
                <Button size="sm" variant="ghost" onClick={() => setHistory(a)}>Historique</Button>
              </div>
            </div>
          );
        })}
      </div>
      {creating && <AutomationDialog onClose={() => setCreating(false)} onDone={() => { setCreating(false); reload(); }} />}
      {history && <RunsDialog automation={history} onClose={() => setHistory(null)} />}
    </Card>
  );
}

function AutomationDialog({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [kind, setKind] = useState("low_stock_alert");
  const [name, setName] = useState("");
  const [frequency, setFrequency] = useState("daily");
  const [atTime, setAtTime] = useState("07:00");
  const [days, setDays] = useState("30");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "automation.name": "name", "automation.kind": "kind", "automation.schedule": "frequency" });
  const k = AUTOMATION_KINDS[kind];

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (name.trim().length < 2) return fail("name", "Nom obligatoire.");
    const d = num(days);
    if (kind === "expiry_alert" && (d == null || !Number.isInteger(d) || d < 1 || d > 365)) return fail("days", "Entre 1 et 365 jours.");
    const ok = await submit(() => upsertAutomation({
      code: `${codeFromName(name)}-${Date.now().toString(36)}`.slice(0, 50), name: name.trim(), kind,
      schedule: frequency === "hourly" ? { frequency } : { frequency, atTime },
      parameters: kind === "expiry_alert" ? { withinDays: d } : {},
    }).then(() => true));
    if (ok) { toast.success("Automatisation créée et active."); onDone(); }
  }

  return (
    <Modal title="Nouvelle automatisation" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={120} placeholder="Ex. Stock bas chaque matin" onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Automatisation" required error={fieldErrors.kind}>
          <Select value={kind} onChange={(e) => setKind(e.target.value)}>
            {Object.entries(AUTOMATION_KINDS).map(([key, v]) => <option key={key} value={key}>{v.label}</option>)}
          </Select>
        </Field>
        <fieldset style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 8 }}>
          <legend><b>QUAND</b></legend>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 8 }}>
            <Field label="Fréquence" required error={fieldErrors.frequency}>
              <Select value={frequency} onChange={(e) => setFrequency(e.target.value)}>
                {Object.entries(FREQUENCIES).map(([key, v]) => <option key={key} value={key}>{v}</option>)}
              </Select>
            </Field>
            {frequency !== "hourly" && <Field label="Heure"><TextInput type="time" value={atTime} onChange={(e) => setAtTime(e.target.value)} /></Field>}
          </div>
        </fieldset>
        <fieldset style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 8 }}>
          <legend><b>SI</b></legend>
          <div style={{ fontSize: 13 }}>{k.condition}</div>
          {kind === "expiry_alert" && <Field label="Expire dans (jours)" required error={fieldErrors.days}>
            <TextInput type="number" min={1} max={365} value={days} onChange={(e) => setDays(e.target.value)} /></Field>}
        </fieldset>
        <fieldset style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10 }}>
          <legend><b>ALORS</b></legend>
          <div style={{ fontSize: 13 }}>{k.then}</div>
        </fieldset>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function RunsDialog({ automation, onClose }: { automation: Automation; onClose: () => void }) {
  const runs = useAsync(() => getAutomationRuns(automation.id), [automation.id]);
  return (
    <Modal title={`Historique — ${automation.name}`} onClose={onClose} footer={<Button onClick={onClose}>Fermer</Button>}>
      <div style={{ minWidth: "min(600px, 92vw)" }}>
        {runs.error && <div role="alert" className="nx-error-text">{runs.error}</div>}
        <DataTable exportTitle={`Exécutions — ${automation.name}`}
          columns={[
            { key: "d", header: "Début", render: (r) => dateTime(r.startedAt) },
            { key: "m", header: "Mode", render: (r) => (r.mode === "test" ? "Aperçu" : "Réel") },
            { key: "s", header: "Statut", render: (r) => <Badge tone={r.status === "success" ? "success" : r.status === "failed" ? "error" : "warning"}>
              {r.status === "success" ? "Réussie" : r.status === "failed" ? "Échec" : r.status}</Badge> },
            { key: "n", header: "Éléments", numeric: true, render: (r) => r.itemsProcessed },
            { key: "e", header: "Erreur", render: (r) => r.error ?? "" },
          ]}
          rows={runs.data ?? []} rowKey={(r) => r.id} empty={runs.loading ? "Chargement…" : "Aucune exécution."} />
      </div>
    </Modal>
  );
}
