import { useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateTime, money } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  activateWorkflow, getWorkflowCatalog, getWorkflows, setWorkflowStatus, testWorkflow, upsertWorkflow,
  type Workflow, type WorkflowCatalog, type WorkflowTestResult,
} from "@/admin/adminApi";
import {
  codeFromName, definitionFromForm, describeWorkflow, emptyWorkflowForm, formFromWorkflow, validateWorkflowForm, type WorkflowForm,
} from "@/admin/adminRules";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const STATUS: Record<string, { label: string; tone: "success" | "warning" | "info" }> = {
  draft: { label: "Brouillon", tone: "warning" }, active: { label: "Actif", tone: "success" }, inactive: { label: "Désactivé", tone: "info" },
};

/** Workflows d'approbation : QUAND (opération) / SI (seuil de montant) / ALORS (approbations), testés avant activation. */
export function WorkflowsPanel() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const list = useAsync(getWorkflows, [version]);
  const catalog = useAsync(getWorkflowCatalog, []);
  const [editing, setEditing] = useState<Workflow | "new" | null>(null);
  const [testing, setTesting] = useState<Workflow | null>(null);
  const [deactivating, setDeactivating] = useState<Workflow | null>(null);
  const { busy, error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);
  const label = (key: string) => catalog.data?.triggers.find((t) => t.key === key)?.label ?? key;
  const automatic = (key: string) => catalog.data?.triggers.find((t) => t.key === key)?.automatic ?? false;

  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setEditing("new")}>Nouveau workflow</Button>}>Workflows d'approbation</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        Un workflow impose des approbations à un type d'opération. Il doit être testé avant d'être activé ; un workflow actif
        ne se modifie pas sans être désactivé d'abord.
      </div>
      {(error || list.error) && <div role="alert" className="nx-error-text">{error ?? list.error}</div>}
      {(list.data ?? []).length === 0 && !list.loading && <div style={hint}>Aucun workflow.</div>}
      <div style={{ display: "grid", gap: 10 }}>
        {(list.data ?? []).map((w) => (
          <div key={w.id} style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 6 }}>
            <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <b>{w.name}</b>
              <Badge tone={STATUS[w.status]?.tone}>{STATUS[w.status]?.label ?? w.status}</Badge>
              <Badge tone={w.isTested ? "success" : "warning"}>{w.isTested ? `Testé ${w.lastTestedAt ? dateTime(w.lastTestedAt) : ""}` : "Non testé"}</Badge>
              {automatic(w.triggerEvent) && <Badge tone="info">Appliqué automatiquement</Badge>}
            </div>
            <div>{describeWorkflow(w.definition, label(w.triggerEvent))}</div>
            <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
              <Button size="sm" variant="subtle" onClick={() => setTesting(w)}>Tester</Button>
              {w.status !== "active" && <Button size="sm" variant="ghost" onClick={() => setEditing(w)}>Modifier</Button>}
              {w.status !== "active" && <Button size="sm" disabled={busy || !w.isTested} title={w.isTested ? undefined : "Testez d'abord le workflow"}
                onClick={async () => { if (await submit(() => activateWorkflow(w.id).then(() => true))) { toast.success("Workflow activé : il s'applique désormais."); reload(); } }}>Activer</Button>}
              {w.status === "active" && <Button size="sm" variant="ghost" onClick={() => setDeactivating(w)}>Désactiver</Button>}
            </div>
          </div>
        ))}
      </div>
      {editing && catalog.data && <WorkflowDialog workflow={editing === "new" ? null : editing} catalog={catalog.data}
                                                  onClose={() => setEditing(null)} onDone={() => { setEditing(null); reload(); }} />}
      {testing && <TestDialog workflow={testing} triggerLabel={label(testing.triggerEvent)} onClose={() => { setTesting(null); reload(); }} />}
      {deactivating && <ConfirmDialog title={`Désactiver « ${deactivating.name} »`} confirmLabel="Désactiver"
        message="Les opérations concernées ne demanderont plus ces approbations. Les demandes déjà ouvertes restent à décider."
        onCancel={() => setDeactivating(null)}
        onConfirm={async () => {
          const w = deactivating; setDeactivating(null);
          if (await submit(() => setWorkflowStatus(w.id, "inactive").then(() => true))) { toast.success("Workflow désactivé."); reload(); }
        }} />}
    </Card>
  );
}

function WorkflowDialog({ workflow, catalog, onClose, onDone }: {
  workflow: Workflow | null; catalog: WorkflowCatalog; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [f, setF] = useState<WorkflowForm>(workflow ? formFromWorkflow(workflow) : emptyWorkflowForm());
  const set = (patch: Partial<WorkflowForm>) => setF((x) => ({ ...x, ...patch }));
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "workflow.name": "name", "workflow.trigger": "trigger", "workflow.levels": "levels", "workflow.approver_role": "role", "workflow.condition": "amount",
  });
  const trigger = catalog.triggers.find((t) => t.key === f.trigger);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const problem = validateWorkflowForm(f);
    if (problem) return fail(problem[0], problem[1]);
    const ok = await submit(() => upsertWorkflow({
      code: workflow?.code ?? codeFromName(f.name), name: f.name.trim(), triggerEvent: f.trigger, definition: definitionFromForm(f),
    }).then(() => true));
    if (ok) { toast.success("Workflow enregistré : testez-le avant de l'activer."); onDone(); }
  }

  return (
    <Modal title={workflow ? `Modifier « ${workflow.name} »` : "Nouveau workflow"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 12, minWidth: "min(560px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nom" required error={fieldErrors.name}><TextInput value={f.name} maxLength={120} placeholder="Ex. Crédit au-delà de la limite" onChange={(e) => set({ name: e.target.value })} /></Field>
        <fieldset style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 8 }}>
          <legend><b>QUAND</b> — l'opération</legend>
          <Field label="Opération" required error={fieldErrors.trigger}>
            <Select value={f.trigger} onChange={(e) => set({ trigger: e.target.value })}>
              <option value="">— Choisir —</option>
              {catalog.triggers.map((t) => <option key={t.key} value={t.key}>{t.label}{t.automatic ? " (appliqué automatiquement)" : ""}</option>)}
            </Select>
          </Field>
          {trigger && <div style={hint}>{trigger.detail}</div>}
        </fieldset>
        <fieldset style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 8 }}>
          <legend><b>SI</b> — le montant</legend>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 8 }}>
            <Field label="Condition">
              <Select value={f.threshold} onChange={(e) => set({ threshold: e.target.value as WorkflowForm["threshold"] })}>
                <option value="none">Toujours</option>
                <option value="gte">Montant supérieur ou égal à</option>
                <option value="gt">Montant supérieur à</option>
              </Select>
            </Field>
            {f.threshold !== "none" && <Field label="Montant" required error={fieldErrors.amount}>
              <TextInput type="number" min={0} step="0.01" value={f.amount} onChange={(e) => set({ amount: e.target.value })} /></Field>}
          </div>
        </fieldset>
        <fieldset style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 8 }}>
          <legend><b>ALORS</b> — l'approbation</legend>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 8 }}>
            <Field label="Nombre d'approbations" required error={fieldErrors.levels} hint="Approbateurs distincts, différents du demandeur.">
              <Select value={f.levels} onChange={(e) => set({ levels: e.target.value })}>
                {[1, 2, 3, 4, 5].map((n) => <option key={n} value={n}>{n}</option>)}
              </Select>
            </Field>
            <Field label="Rôle qui approuve" required error={fieldErrors.role}>
              <Select value={f.role} onChange={(e) => set({ role: e.target.value })}>
                <option value="">— Choisir —</option>
                {catalog.approverRoles.map((r) => <option key={r.name} value={r.name}>{r.name}{r.description ? ` — ${r.description}` : ""}</option>)}
              </Select>
            </Field>
          </div>
        </fieldset>
        {f.trigger && validateWorkflowForm(f) === null && (
          <div style={{ fontSize: 13 }}>Résultat : {describeWorkflow(definitionFromForm(f), trigger?.label ?? f.trigger)}</div>
        )}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Test à blanc : un montant d'exemple, le détail de l'évaluation, aucun effet. */
function TestDialog({ workflow, triggerLabel, onClose }: { workflow: Workflow; triggerLabel: string; onClose: () => void }) {
  const [amount, setAmount] = useState("");
  const [result, setResult] = useState<WorkflowTestResult | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit();

  async function run(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    if (a == null || a < 0) return fail("amount", "Montant d'exemple requis.");
    const r = await submit(() => testWorkflow(workflow.id, { amount: a }));
    if (r) setResult(r);
  }

  return (
    <Modal title={`Tester « ${workflow.name} »`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Fermer</Button><Button disabled={busy} onClick={() => run()}>Tester</Button></>}>
      <form onSubmit={run} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={hint}>{describeWorkflow(workflow.definition, triggerLabel)} Le test n'a aucun effet.</div>
        <Field label="Montant de l'opération" required error={fieldErrors.amount}>
          <TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        {result && (
          <div role="status" style={{ display: "grid", gap: 4 }}>
            <Badge tone={result.matched ? "warning" : "success"}>
              {result.matched ? "La règle s'applique" : "La règle ne s'applique pas"}
            </Badge>
            {result.conditions.map((c, i) => (
              <div key={i} style={hint}>
                Montant {money(Number(c.actual ?? 0))} {c.op === "gt" ? ">" : "≥"} {money(Number(c.expected))} : {c.passed ? "oui" : "non"}
              </div>
            ))}
            {result.matched && result.plannedApprovals.map((p) => <div key={p}>À exiger : {p}</div>)}
          </div>
        )}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
