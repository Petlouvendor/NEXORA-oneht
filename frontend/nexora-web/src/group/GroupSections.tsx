import { useMemo, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateShort, money, number, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, KpiCard, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  ACCOUNT_TYPES, ACTIVITY_LABELS, FX_METHODS, IC_STATUS, IC_TYPES, MATCH_STATUS, PRIORITIES, RUN_STATUS, TASK_STATUS,
  archiveCompany, canReconcile, confirmIntercompany, counterpartCandidates, createRun, createTask, executeRun, getControlCenter,
  getPolicy, getStatements, listGroupAccounts, listIntercompany, listMappings, listRuns, listTasks, mappingCoverage,
  provisionCompany, publishRun, reconcileIntercompany, recordIntercompany, reopenRun, runActions, setMapping, setPolicy,
  updateTask, upsertGroupAccount, validateRun,
  type ConsolidationRun, type GroupAccount, type GroupOptions, type GroupTask, type IntercompanyTx, type LocalAccountMapping,
} from "@/group/groupApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const row = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 8 } as const;
const yesNo = (v: boolean) => <Badge tone={v ? "success" : "warning"}>{v ? "Oui" : "Non"}</Badge>;

interface Props { gid: string; options: GroupOptions; onChanged: () => void }

// ================================================================== Pilotage

export function OverviewSection({ gid, options }: Props) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const cc = useAsync(() => getControlCenter(gid), [gid, version]);
  const tasks = useAsync(() => (hasPermission("group.tasks") ? listTasks(gid) : Promise.resolve([] as GroupTask[])), [gid, version]);
  const [newTask, setNewTask] = useState(false);
  const [editing, setEditing] = useState<GroupTask | null>(null);
  const userName = (id: string | null) => options.users.find((u) => u.id === id)?.name ?? "—";
  const c = cc.data;

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      {cc.error && <div role="alert" className="nx-error-text">{cc.error}</div>}
      {c && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(190px, 1fr))", gap: 12 }}>
            <KpiCard label="Sociétés" value={c.companies.filter((x) => x.status !== "archived").length} />
            <KpiCard label="Intersociétés à rapprocher" value={c.unreconciledIntercompany} tone={c.unreconciledIntercompany ? "warning" : undefined} />
            <KpiCard label="Consolidations à valider" value={c.runsReadyForValidation} />
            <KpiCard label="Tâches ouvertes" value={c.openTasks} />
            <KpiCard label="Créances clients (groupe)" value={money(c.groupReceivables, c.currency)} />
            <KpiCard label="Dettes fournisseurs (groupe)" value={money(c.groupPayables, c.currency)} />
          </div>
          <Card>
            <SectionLabel>Centre de contrôle</SectionLabel>
            <DataTable exportTitle="Centre de contrôle du groupe"
              columns={[
                { key: "n", header: "Société", render: (x) => <>{x.companyName}{x.status === "archived" && <> <Badge>Archivée</Badge></>}</> },
                { key: "p", header: "Dernière période", render: (x) => `${x.latestPeriod} · ${x.latestPeriodStatus === "open" ? "ouverte" : x.latestPeriodStatus === "closed" || x.latestPeriodStatus === "locked" ? "clôturée" : x.latestPeriodStatus}` },
                { key: "m", header: "Comptes sans correspondance", numeric: true, render: (x) => <span style={{ color: x.unmappedAccounts ? "var(--nx-warning)" : undefined }}>{x.unmappedAccounts}</span> },
                { key: "i", header: "Intersociétés à rapprocher", numeric: true, render: (x) => <span style={{ color: x.unreconciledIntercompany ? "var(--nx-warning)" : undefined }}>{x.unreconciledIntercompany}</span> },
                { key: "r", header: "Créances", numeric: true, render: (x) => money(x.receivables, c.currency) },
                { key: "d", header: "Dettes", numeric: true, render: (x) => money(x.payables, c.currency) },
              ]}
              rows={c.companies} rowKey={(x) => x.companyId} />
            <div style={hint}>Montants dans la devise de chaque société, sans conversion ; les totaux de consolidation sont dans l'onglet Consolidation.</div>
          </Card>
        </>
      )}
      {hasPermission("group.tasks") && (
        <Card>
          <SectionLabel action={<Button size="sm" onClick={() => setNewTask(true)}>Nouvelle tâche</Button>}>Tâches de contrôle</SectionLabel>
          {tasks.error && <div role="alert" className="nx-error-text">{tasks.error}</div>}
          <DataTable exportTitle="Tâches du groupe"
            columns={[
              { key: "t", header: "Tâche", render: (t) => <button type="button" className="nx-linkbtn" onClick={() => setEditing(t)}>{t.title}</button> },
              { key: "c", header: "Société", render: (t) => t.companyName ?? "Groupe" },
              { key: "a", header: "Responsable", render: (t) => userName(t.assigneeUserId) },
              { key: "p", header: "Priorité", render: (t) => <Badge tone={t.priority === "urgent" ? "error" : t.priority === "high" ? "warning" : undefined}>{PRIORITIES[t.priority] ?? t.priority}</Badge> },
              { key: "d", header: "Échéance", render: (t) => (t.dueDate ? dateShort(t.dueDate) : "—") },
              { key: "s", header: "État", render: (t) => TASK_STATUS[t.status] ?? t.status },
            ]}
            rows={tasks.data ?? []} rowKey={(t) => t.id} empty="Aucune tâche." />
        </Card>
      )}
      {newTask && <TaskDialog gid={gid} options={options} onClose={() => setNewTask(false)}
        onDone={() => { setNewTask(false); setVersion((v) => v + 1); toast.success("Tâche créée."); }} />}
      {editing && <TaskStatusDialog gid={gid} task={editing} onClose={() => setEditing(null)}
        onDone={() => { setEditing(null); setVersion((v) => v + 1); toast.success("Tâche mise à jour."); }} />}
    </div>
  );
}

function TaskDialog({ gid, options, onClose, onDone }: { gid: string; options: GroupOptions; onClose: () => void; onDone: () => void }) {
  const [title, setTitle] = useState("");
  const [description, setDescription] = useState("");
  const [company, setCompany] = useState("");
  const [assignee, setAssignee] = useState("");
  const [priority, setPriority] = useState("normal");
  const [due, setDue] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "group.task_title": "title", "group.task_due": "due", "group.task_assignee": "assignee" });
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!title.trim()) return fail("title", "Titre obligatoire.");
    if (due && due < todayIso()) return fail("due", "L'échéance ne peut pas être passée.");
    const ok = await submit(() => createTask(gid, { title: title.trim(), description: description.trim() || null, companyId: company || null,
      assigneeUserId: assignee || null, priority, dueDate: due || null }).then(() => true));
    if (ok) onDone();
  }
  return (
    <Modal title="Nouvelle tâche de contrôle" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Titre" required error={fieldErrors.title}><TextInput value={title} maxLength={200} onChange={(e) => setTitle(e.target.value)} /></Field>
        <Field label="Description"><TextInput value={description} maxLength={2000} onChange={(e) => setDescription(e.target.value)} /></Field>
        <div style={row}>
          <Field label="Société"><Select value={company} onChange={(e) => setCompany(e.target.value)}>
            <option value="">Tout le groupe</option>
            {options.companies.filter((c) => c.status !== "archived").map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </Select></Field>
          <Field label="Responsable" error={fieldErrors.assignee}><Select value={assignee} onChange={(e) => setAssignee(e.target.value)}>
            <option value="">Non assignée</option>
            {options.users.map((u) => <option key={u.id} value={u.id}>{u.name}</option>)}
          </Select></Field>
          <Field label="Priorité"><Select value={priority} onChange={(e) => setPriority(e.target.value)}>
            {Object.entries(PRIORITIES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select></Field>
          <Field label="Échéance" error={fieldErrors.due}><TextInput type="date" min={todayIso()} value={due} onChange={(e) => setDue(e.target.value)} /></Field>
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function TaskStatusDialog({ gid, task, onClose, onDone }: { gid: string; task: GroupTask; onClose: () => void; onDone: () => void }) {
  const [status, setStatus] = useState(task.status);
  const [notes, setNotes] = useState(task.notes ?? "");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "group.task_notes_required": "notes" });
  const needsNote = status === "blocked" || status === "cancelled";
  async function save() {
    if (needsNote && !notes.trim()) return fail("notes", "Indiquez pourquoi la tâche est bloquée ou annulée.");
    if (await submit(() => updateTask(gid, task.id, status, notes.trim() || null).then(() => true))) onDone();
  }
  return (
    <Modal title={task.title} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Fermer</Button><Button disabled={busy} onClick={save}>Enregistrer</Button></>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(480px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {task.description && <div>{task.description}</div>}
        <Field label="État"><Select value={status} onChange={(e) => setStatus(e.target.value)}>
          {Object.entries(TASK_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </Select></Field>
        <Field label="Notes" required={needsNote} error={fieldErrors.notes}><TextInput value={notes} maxLength={2000} onChange={(e) => setNotes(e.target.value)} /></Field>
      </div>
    </Modal>
  );
}

// ================================================================== Intersociétés

export function IntercompanySection({ gid, options }: Props) {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const list = useAsync(() => listIntercompany(gid), [gid, version]);
  const [onlyOpen, setOnlyOpen] = useState(true);
  const [recording, setRecording] = useState(false);
  const [reconciling, setReconciling] = useState<IntercompanyTx | null>(null);
  const { busy, error, submit } = useSubmit();
  const all = list.data ?? [];
  const rows = onlyOpen ? all.filter((t) => t.status !== "eliminated" && t.matchStatus !== "matched") : all;
  const reload = () => setVersion((v) => v + 1);

  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setRecording(true)}>Nouvelle opération</Button>}>Opérations intersociétés</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        Chaque société enregistre sa propre écriture (la vente chez l'une, l'achat chez l'autre). Une opération confirmée se rapproche de
        l'opération inverse ; les montants concordants (à la tolérance près) sont éliminés en consolidation. Les livres des sociétés ne sont jamais modifiés.
      </div>
      <label style={{ display: "inline-flex", gap: 6, alignItems: "center", marginBottom: 8 }}>
        <input type="checkbox" checked={onlyOpen} onChange={(e) => setOnlyOpen(e.target.checked)} /> Seulement les opérations à traiter
      </label>
      {(list.error || error) && <div role="alert" className="nx-error-text">{list.error ?? error}</div>}
      <DataTable exportTitle="Opérations intersociétés"
        columns={[
          { key: "d", header: "Date", render: (t) => dateShort(t.occurredAt) },
          { key: "s", header: "De → vers", render: (t) => <>{t.sourceCompany} → {t.targetCompany}</> },
          { key: "t", header: "Nature", render: (t) => IC_TYPES[t.transactionType] ?? t.transactionType },
          { key: "p", header: "Pièces", render: (t) => [t.sourceDocument, t.targetDocument].filter(Boolean).join(" / ") || "—" },
          { key: "a", header: "Montant", numeric: true, render: (t) => money(t.amount, t.currency) },
          { key: "st", header: "État", render: (t) => <Badge tone={IC_STATUS[t.status]?.tone}>{IC_STATUS[t.status]?.label ?? t.status}</Badge> },
          { key: "m", header: "Rapprochement", render: (t) => <>
            <Badge tone={MATCH_STATUS[t.matchStatus]?.tone}>{MATCH_STATUS[t.matchStatus]?.label ?? t.matchStatus}</Badge>
            {t.matchStatus === "asymmetric" && t.matchDifference != null && <span style={hint}> écart {money(t.matchDifference, t.currency)}</span>}
          </> },
          { key: "x", header: "", render: (t) => (
            <span style={{ display: "inline-flex", gap: 4 }}>
              {t.status === "draft" && <Button size="sm" variant="subtle" disabled={busy}
                onClick={async () => { if (await submit(() => confirmIntercompany(gid, t.id).then(() => true))) { toast.success("Opération confirmée."); reload(); } }}>Confirmer</Button>}
              {canReconcile(t) && <Button size="sm" variant="subtle" disabled={busy} onClick={() => setReconciling(t)}>Rapprocher</Button>}
            </span>) },
        ]}
        rows={rows} rowKey={(t) => t.id} empty={list.loading ? "Chargement…" : "Aucune opération à traiter."} />
      {recording && <RecordDialog gid={gid} options={options} onClose={() => setRecording(false)}
        onDone={() => { setRecording(false); reload(); toast.success("Opération enregistrée (brouillon) : confirmez-la quand la pièce est vérifiée."); }} />}
      {reconciling && <ReconcileDialog gid={gid} source={reconciling} all={all} onClose={() => setReconciling(null)}
        onDone={(msg) => { setReconciling(null); reload(); toast.success(msg); }} />}
    </Card>
  );
}

function RecordDialog({ gid, options, onClose, onDone }: { gid: string; options: GroupOptions; onClose: () => void; onDone: () => void }) {
  const active = options.companies.filter((c) => c.status !== "archived");
  const [source, setSource] = useState(active[0]?.id ?? "");
  const [target, setTarget] = useState(active[1]?.id ?? "");
  const [type, setType] = useState("sale_purchase");
  const [amount, setAmount] = useState("");
  const [currency, setCurrency] = useState(active[0]?.currency ?? "HTG");
  const [date, setDate] = useState(todayIso());
  const [srcDoc, setSrcDoc] = useState("");
  const [tgtDoc, setTgtDoc] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "intercompany.amount": "amount", "intercompany.same_company": "target" });
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!source || !target) return fail("target", "Choisissez les deux sociétés.");
    if (source === target) return fail("target", "Les deux sociétés doivent être différentes.");
    const a = num(amount);
    if (a == null || a <= 0) return fail("amount", "Montant positif obligatoire.");
    if (!date) return fail("date", "Date obligatoire.");
    const ok = await submit(() => recordIntercompany(gid, { sourceCompanyId: source, targetCompanyId: target, transactionType: type, amount: a,
      currency, occurredAt: date, sourceReference: srcDoc.trim() || null, targetReference: tgtDoc.trim() || null }).then(() => true));
    if (ok) onDone();
  }
  return (
    <Modal title="Nouvelle opération intersociétés" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(560px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={row}>
          <Field label="Société d'origine" required><Select value={source} onChange={(e) => { setSource(e.target.value); setCurrency(active.find((c) => c.id === e.target.value)?.currency ?? currency); }}>
            {active.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </Select></Field>
          <Field label="Société destinataire" required error={fieldErrors.target}><Select value={target} onChange={(e) => setTarget(e.target.value)}>
            <option value="">— Choisir —</option>
            {active.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </Select></Field>
          <Field label="Nature"><Select value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(IC_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select></Field>
        </div>
        <div style={row}>
          <Field label="Montant" required error={fieldErrors.amount}><TextInput inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
          <Field label="Devise"><Select value={currency} onChange={(e) => setCurrency(e.target.value)}>
            {options.currencies.map((c) => <option key={c} value={c}>{c}</option>)}
          </Select></Field>
          <Field label="Date" required error={fieldErrors.date}><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        </div>
        <div style={row}>
          <Field label="Pièce chez l'origine" hint="N° de facture, de virement…"><TextInput value={srcDoc} maxLength={60} onChange={(e) => setSrcDoc(e.target.value)} /></Field>
          <Field label="Pièce chez le destinataire"><TextInput value={tgtDoc} maxLength={60} onChange={(e) => setTgtDoc(e.target.value)} /></Field>
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function ReconcileDialog({ gid, source, all, onClose, onDone }: { gid: string; source: IntercompanyTx; all: IntercompanyTx[]; onClose: () => void; onDone: (msg: string) => void }) {
  const candidates = useMemo(() => counterpartCandidates(source, all), [source, all]);
  const [choice, setChoice] = useState<string>("");
  const { busy, error, submit } = useSubmit();
  async function go() {
    const m = await submit(() => reconcileIntercompany(gid, source.id, choice || null));
    if (!m) return;
    onDone(m.status === "matched" ? "Rapprochement concordant : l'opération sera éliminée en consolidation."
      : m.status === "asymmetric" ? `Écart constaté (${money(m.amountDifference, source.currency)}${m.currencyMatch ? "" : ", devises différentes"}) : à corriger dans les livres.`
      : "Aucune contrepartie trouvée : l'autre société doit enregistrer et confirmer son opération.");
  }
  return (
    <Modal title="Rapprocher une opération" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={go}>Rapprocher</Button></>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(560px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div><b>{source.sourceCompany} → {source.targetCompany}</b> · {money(source.amount, source.currency)} · {dateShort(source.occurredAt)}</div>
        <Field label="Contrepartie" hint="Opérations inverses confirmées et non rapprochées, les plus proches en montant d'abord.">
          <Select value={choice} onChange={(e) => setChoice(e.target.value)}>
            <option value="">Recherche automatique</option>
            {candidates.map((c) => <option key={c.id} value={c.id}>
              {dateShort(c.occurredAt)} · {money(c.amount, c.currency)}{c.sourceDocument ? ` · ${c.sourceDocument}` : ""}
            </option>)}
          </Select>
        </Field>
        {candidates.length === 0 && <div style={hint}>Aucune opération inverse confirmée : le rapprochement constatera l'absence de contrepartie.</div>}
      </div>
    </Modal>
  );
}

// ================================================================== Correspondances

export function MappingSection({ gid, options }: Props) {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const accounts = useAsync(() => listGroupAccounts(gid), [gid, version]);
  const coverage = useAsync(() => mappingCoverage(gid), [gid, version]);
  const active = options.companies.filter((c) => c.status !== "archived");
  const [company, setCompany] = useState(active[0]?.id ?? "");
  const mappings = useAsync(() => (company ? listMappings(gid, company) : Promise.resolve([] as LocalAccountMapping[])), [gid, company, version]);
  const [onlyMissing, setOnlyMissing] = useState(false);
  const [editAccount, setEditAccount] = useState<GroupAccount | "new" | null>(null);
  const { error, submit, busy } = useSubmit();
  const groupAccounts = accounts.data ?? [];
  const rows = (mappings.data ?? []).filter((m) => !onlyMissing || !m.groupAccountCode);

  async function map(m: LocalAccountMapping, code: string) {
    if (!code || code === m.groupAccountCode) return;
    if (await submit(() => setMapping(gid, company, m.code, code).then(() => true))) {
      toast.success(`${m.code} → ${code} : nouvelle version de correspondance.`);
      setVersion((v) => v + 1);
    }
  }

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <Card>
        <SectionLabel action={<Button size="sm" onClick={() => setEditAccount("new")}>Nouveau compte groupe</Button>}>Plan comptable du groupe</SectionLabel>
        {accounts.error && <div role="alert" className="nx-error-text">{accounts.error}</div>}
        <DataTable exportTitle="Plan comptable du groupe"
          columns={[
            { key: "c", header: "Code", render: (a) => <button type="button" className="nx-linkbtn" onClick={() => setEditAccount(a)}>{a.code}</button> },
            { key: "n", header: "Libellé", render: (a) => a.name },
            { key: "t", header: "Type", render: (a) => ACCOUNT_TYPES[a.accountType] ?? a.accountType },
            { key: "e", header: "Intragroupe (éliminable)", render: (a) => (a.isEliminable ? "Oui" : "—") },
          ]}
          rows={groupAccounts} rowKey={(a) => a.id} empty="Aucun compte groupe." />
      </Card>
      <Card>
        <SectionLabel>Couverture des correspondances</SectionLabel>
        <DataTable exportTitle="Couverture des correspondances"
          columns={[
            { key: "c", header: "Société", render: (r) => r.companyName },
            { key: "m", header: "Comptes rattachés", numeric: true, render: (r) => r.mappedAccounts },
            { key: "u", header: "Mouvementés sans correspondance", numeric: true, render: (r) => <span style={{ color: r.unmappedWithActivity ? "var(--nx-warning)" : undefined }}>{r.unmappedWithActivity}</span> },
            { key: "l", header: "Comptes concernés", render: (r) => r.unmappedCodes.slice(0, 8).join(", ") || "—" },
          ]}
          rows={coverage.data ?? []} rowKey={(r) => r.companyId} />
        <div style={hint}>Un compte mouvementé sans correspondance est absent de la consolidation.</div>
      </Card>
      <Card>
        <SectionLabel>Correspondance des comptes d'une société</SectionLabel>
        <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "flex-end", marginBottom: 8 }}>
          <Field label="Société"><Select value={company} onChange={(e) => setCompany(e.target.value)}>
            {active.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
          </Select></Field>
          <label style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
            <input type="checkbox" checked={onlyMissing} onChange={(e) => setOnlyMissing(e.target.checked)} /> Seulement les comptes sans correspondance
          </label>
        </div>
        {(mappings.error || error) && <div role="alert" className="nx-error-text">{mappings.error ?? error}</div>}
        <DataTable exportTitle="Correspondance des comptes"
          columns={[
            { key: "c", header: "Compte local", render: (m) => <>{m.code} — {m.name}{m.hasActivity && !m.groupAccountCode && <> <Badge tone="warning">mouvementé</Badge></>}</> },
            { key: "t", header: "Type", render: (m) => ACCOUNT_TYPES[m.accountType] ?? m.accountType },
            { key: "g", header: "Compte groupe", render: (m) => (
              <Select aria-label={`Compte groupe pour ${m.code}`} value={m.groupAccountCode ?? ""} disabled={busy} onChange={(e) => map(m, e.target.value)}>
                <option value="">— Sans correspondance —</option>
                {groupAccounts.filter((a) => a.accountType === m.accountType && a.status === "active")
                  .map((a) => <option key={a.id} value={a.code}>{a.code} — {a.name}</option>)}
              </Select>) },
            { key: "v", header: "Depuis", render: (m) => (m.effectiveFrom ? `${dateShort(m.effectiveFrom)}${m.versions > 1 ? ` (v${m.versions})` : ""}` : "—") },
          ]}
          rows={rows} rowKey={(m) => m.accountId} empty={mappings.loading ? "Chargement…" : "Aucun compte."} />
        <div style={hint}>Seuls les comptes groupe du même type sont proposés. Chaque changement crée une nouvelle version ; l'ancienne reste dans l'historique.</div>
      </Card>
      {editAccount && <GroupAccountDialog gid={gid} account={editAccount === "new" ? null : editAccount} onClose={() => setEditAccount(null)}
        onDone={() => { setEditAccount(null); setVersion((v) => v + 1); toast.success("Compte groupe enregistré."); }} />}
    </div>
  );
}

function GroupAccountDialog({ gid, account, onClose, onDone }: { gid: string; account: GroupAccount | null; onClose: () => void; onDone: () => void }) {
  const [code, setCode] = useState(account?.code ?? "");
  const [name, setName] = useState(account?.name ?? "");
  const [type, setType] = useState(account?.accountType ?? "asset");
  const [elim, setElim] = useState(account?.isEliminable ?? false);
  const [order, setOrder] = useState(String(account?.sortOrder ?? 0));
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "group.account_code": "code", "group.account_name": "name", "group.account_type_in_use": "type" });
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!code.trim()) return fail("code", "Code obligatoire.");
    if (!name.trim()) return fail("name", "Libellé obligatoire.");
    const o = num(order);
    if (o == null || !Number.isInteger(o) || o < 0) return fail("order", "Nombre entier positif.");
    if (await submit(() => upsertGroupAccount(gid, { code: code.trim(), name: name.trim(), accountType: type, isEliminable: elim, sortOrder: o }).then(() => true))) onDone();
  }
  return (
    <Modal title={account ? `Compte groupe ${account.code}` : "Nouveau compte groupe"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(480px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={row}>
          <Field label="Code" required error={fieldErrors.code}><TextInput value={code} maxLength={20} disabled={!!account} onChange={(e) => setCode(e.target.value)} /></Field>
          <Field label="Type" required error={fieldErrors.type}><Select value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(ACCOUNT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select></Field>
          <Field label="Ordre" error={fieldErrors.order}><TextInput inputMode="numeric" value={order} onChange={(e) => setOrder(e.target.value)} /></Field>
        </div>
        <Field label="Libellé" required error={fieldErrors.name}><TextInput value={name} maxLength={120} onChange={(e) => setName(e.target.value)} /></Field>
        <label style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
          <input type="checkbox" checked={elim} onChange={(e) => setElim(e.target.checked)} /> Compte intragroupe : ses montants rapprochés sont éliminés en consolidation
        </label>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

// ================================================================== Consolidation

export function ConsolidationSection({ gid, options }: Props) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const runs = useAsync(() => listRuns(gid), [gid, version]);
  const [creating, setCreating] = useState(false);
  const [opened, setOpened] = useState<ConsolidationRun | null>(null);
  const reload = () => setVersion((v) => v + 1);
  return (
    <Card>
      <SectionLabel action={hasPermission("consolidation.run") ? <Button size="sm" onClick={() => setCreating(true)}>Nouvelle consolidation</Button> : undefined}>Consolidations</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        Brouillon → exécution (agrégation des balances converties au taux de clôture, éliminations) → validation → publication. Les livres des sociétés ne sont jamais modifiés.
      </div>
      {runs.error && <div role="alert" className="nx-error-text">{runs.error}</div>}
      <DataTable exportTitle="Consolidations"
        columns={[
          { key: "l", header: "Période", render: (r) => <button type="button" className="nx-linkbtn" onClick={() => setOpened(r)}>{r.periodLabel}</button> },
          { key: "d", header: "Du / au", render: (r) => `${dateShort(r.fromDate)} → ${dateShort(r.toDate)}` },
          { key: "c", header: "Sociétés", numeric: true, render: (r) => r.companies.length },
          { key: "s", header: "État", render: (r) => <><Badge tone={RUN_STATUS[r.status]?.tone}>{RUN_STATUS[r.status]?.label ?? r.status}</Badge>
            {r.generatedAt && !r.coherenceOk && <> <Badge tone="error">incohérente</Badge></>}</> },
          { key: "t", header: "Total", numeric: true, render: (r) => (r.generatedAt ? money(r.totalDebit, r.currency) : "—") },
          { key: "e", header: "Éliminations", numeric: true, render: (r) => (r.generatedAt ? money(r.eliminationTotal, r.currency) : "—") },
        ]}
        rows={runs.data ?? []} rowKey={(r) => r.id} empty={runs.loading ? "Chargement…" : "Aucune consolidation."} />
      {creating && <RunDialog gid={gid} options={options} onClose={() => setCreating(false)}
        onDone={(r) => { setCreating(false); reload(); setOpened(r); toast.success("Consolidation créée : exécutez-la pour calculer les états."); }} />}
      {opened && <RunDrawer key={opened.id} gid={gid} run={opened} onClose={() => setOpened(null)} onChanged={reload} />}
    </Card>
  );
}

function RunDialog({ gid, options, onClose, onDone }: { gid: string; options: GroupOptions; onClose: () => void; onDone: (r: ConsolidationRun) => void }) {
  const active = options.companies.filter((c) => c.status !== "archived");
  const today = todayIso();
  const [label, setLabel] = useState("");
  const [from, setFrom] = useState(`${today.slice(0, 8)}01`);
  const [to, setTo] = useState(today);
  const [selected, setSelected] = useState<string[]>(active.map((c) => c.id));
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "consolidation.label": "label", "consolidation.period": "to", "consolidation.period_length": "to" });
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!label.trim()) return fail("label", "Libellé obligatoire (ex. « Septembre 2026 »).");
    if (!from || !to || to < from) return fail("to", "La fin doit suivre le début.");
    if (selected.length === 0) return fail("companies", "Choisissez au moins une société.");
    const r = await submit(() => createRun(gid, { periodLabel: label.trim(), fromDate: from, toDate: to, companyIds: selected, profileId: null }));
    if (r) onDone(r);
  }
  return (
    <Modal title="Nouvelle consolidation" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Libellé" required error={fieldErrors.label}><TextInput value={label} maxLength={60} placeholder="Septembre 2026" onChange={(e) => setLabel(e.target.value)} /></Field>
        <div style={row}>
          <Field label="Du" required><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
          <Field label="Au" required error={fieldErrors.to}><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
        </div>
        <Field label="Sociétés consolidées" error={fieldErrors.companies}>
          <div style={{ display: "grid", gap: 4 }}>
            {active.map((c) => (
              <label key={c.id} style={{ display: "inline-flex", gap: 6, alignItems: "center" }}>
                <input type="checkbox" checked={selected.includes(c.id)}
                  onChange={(e) => setSelected((s) => (e.target.checked ? [...s, c.id] : s.filter((x) => x !== c.id)))} />
                {c.name} <span style={hint}>({c.currency})</span>
              </label>
            ))}
          </div>
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function RunDrawer({ gid, run, onClose, onChanged }: { gid: string; run: ConsolidationRun; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [current, setCurrent] = useState(run);
  const [reopening, setReopening] = useState(false);
  const statements = useAsync(() => (current.generatedAt ? getStatements(gid, current.id) : Promise.resolve(null)), [gid, current.id, current.generatedAt, current.status]);
  const { busy, error, submit } = useSubmit();
  const can = runActions(current);
  const cur = current.currency;
  async function act(fn: () => Promise<ConsolidationRun>, msg: string) {
    const r = await submit(fn);
    if (r) { setCurrent(r); onChanged(); toast.success(msg); }
  }
  const st = statements.data;
  return (
    <Drawer title={`Consolidation — ${current.periodLabel}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 12, gridTemplateColumns: "minmax(0, 1fr)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          <Badge tone={RUN_STATUS[current.status]?.tone}>{RUN_STATUS[current.status]?.label ?? current.status}</Badge>
          <span style={hint}>{dateShort(current.fromDate)} → {dateShort(current.toDate)} · devise {cur}</span>
        </div>
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {can.execute && hasPermission("consolidation.run") && <Button disabled={busy} onClick={() => act(() => executeRun(gid, current.id), "Consolidation calculée.")}>{current.generatedAt ? "Recalculer" : "Exécuter"}</Button>}
          {can.validate && hasPermission("consolidation.validate") && <Button variant="subtle" disabled={busy} onClick={() => act(() => validateRun(gid, current.id), "Consolidation validée.")}>Valider</Button>}
          {can.publish && hasPermission("consolidation.publish") && <Button variant="subtle" disabled={busy} onClick={() => act(() => publishRun(gid, current.id), "Consolidation publiée : les opérations rapprochées sont éliminées.")}>Publier</Button>}
          {can.reopen && hasPermission("consolidation.validate") && <Button variant="ghost" disabled={busy} onClick={() => setReopening(true)}>Rouvrir</Button>}
        </div>
        {current.status === "ready" && !current.coherenceOk && <div className="nx-error-text">Contrôle de cohérence non satisfait : la consolidation ne peut pas être validée (voir les points ci-dessous).</div>}
        {current.diagnostics.length > 0 && (
          <Card style={{ background: "var(--nx-warning-bg)", borderColor: "transparent" }}>
            <SectionLabel>Points de contrôle</SectionLabel>
            <ul style={{ margin: 0, paddingLeft: 18 }}>{current.diagnostics.map((d, i) => <li key={i}>{d}</li>)}</ul>
          </Card>
        )}
        <DataTable exportTitle={`Sociétés — ${current.periodLabel}`}
          columns={[
            { key: "n", header: "Société", render: (c) => c.companyName },
            { key: "f", header: "Taux", numeric: true, render: (c) => `${c.functionalCurrency} × ${number(c.fxRate, 6)}` },
            { key: "p", header: "Période clôturée", render: (c) => yesNo(c.periodClosed) },
            { key: "m", header: "Correspondances complètes", render: (c) => yesNo(c.mappingComplete) },
            { key: "d", header: `Contribution (${cur})`, numeric: true, render: (c) => money(c.contributedDebit, cur) },
          ]}
          rows={current.companies} rowKey={(c) => c.companyId} />
        {st && (
          <>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 8 }}>
              <KpiCard label="Actif" value={money(st.totalAssets, cur)} />
              <KpiCard label="Passif" value={money(st.totalLiabilities, cur)} />
              <KpiCard label="Capitaux propres" value={money(st.totalEquity, cur)} />
              <KpiCard label="Produits" value={money(st.totalRevenue, cur)} />
              <KpiCard label="Charges" value={money(st.totalExpense, cur)} />
              <KpiCard label="Résultat consolidé" value={money(st.netResult, cur)} />
            </div>
            <div>Bilan équilibré : {yesNo(st.balanceSheetBalanced)} · Balance équilibrée : {yesNo(st.trialBalanceBalanced)}</div>
            <DataTable exportTitle={`Balance consolidée — ${current.periodLabel}`}
              columns={[
                { key: "c", header: "Compte groupe", render: (l) => `${l.groupAccountCode} — ${l.groupAccountName}` },
                { key: "ad", header: "Agrégé débit", numeric: true, render: (l) => money(l.aggregatedDebit, cur) },
                { key: "ac", header: "Agrégé crédit", numeric: true, render: (l) => money(l.aggregatedCredit, cur) },
                { key: "e", header: "Éliminations", numeric: true, render: (l) => (l.eliminationDebit || l.eliminationCredit ? money(l.eliminationDebit + l.eliminationCredit, cur) : "—") },
                { key: "cd", header: "Consolidé débit", numeric: true, render: (l) => money(l.consolidatedDebit, cur) },
                { key: "cc", header: "Consolidé crédit", numeric: true, render: (l) => money(l.consolidatedCredit, cur) },
              ]}
              rows={st.trialBalance} rowKey={(l) => l.groupAccountCode} />
          </>
        )}
      </div>
      {reopening && <ConfirmDialog title={`Rouvrir — ${current.periodLabel}`} requireReason confirmLabel="Rouvrir"
        message="La consolidation repasse en préparation : elle devra être recalculée, validée et publiée de nouveau."
        onCancel={() => setReopening(false)}
        onConfirm={(reason) => { setReopening(false); void act(() => reopenRun(gid, current.id, reason), "Consolidation rouverte."); }} />}
    </Drawer>
  );
}

// ================================================================== Politique & sociétés

export function PolicySection({ gid, options, onChanged }: Props) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const policy = useAsync(() => getPolicy(gid), [gid, version]);
  const [currency, setCurrency] = useState<string | null>(null);
  const [tolerance, setTolerance] = useState<string | null>(null);
  const [archiving, setArchiving] = useState<GroupOptions["companies"][number] | null>(null);
  const [creating, setCreating] = useState(false);
  const save = useSubmit({ "group.tolerance": "tolerance", "group.currency": "currency" });
  const arch = useSubmit();
  const p = policy.data;
  const cur = currency ?? p?.consolidationCurrency ?? "";
  const tol = tolerance ?? (p ? String(p.intercompanyTolerance) : "");
  const canManage = hasPermission("group.manage");

  async function savePolicy(e: FormEvent) {
    e.preventDefault();
    const t = num(tol);
    if (t == null || t < 0) return save.fail("tolerance", "Montant positif ou nul.");
    if (await save.submit(() => setPolicy(gid, { consolidationCurrency: cur, fxMethod: "closing_rate", intercompanyTolerance: t }).then(() => true))) {
      toast.success("Politique du groupe enregistrée."); setCurrency(null); setTolerance(null); setVersion((v) => v + 1);
    }
  }

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <Card>
        <SectionLabel>Politique du groupe</SectionLabel>
        {(policy.error || save.error) && <div role="alert" className="nx-error-text">{policy.error ?? save.error}</div>}
        {p && (
          <form noValidate onSubmit={savePolicy} style={{ display: "grid", gap: 10 }}>
            <div style={row}>
              <Field label="Devise de consolidation" error={save.fieldErrors.currency}>
                <Select value={cur} disabled={!canManage} onChange={(e) => setCurrency(e.target.value)}>
                  {options.currencies.map((c) => <option key={c} value={c}>{c}</option>)}
                </Select>
              </Field>
              <Field label="Conversion" hint="Taux de la dernière date de la période. Le taux moyen et le taux historique ne sont pas appliqués par le moteur.">
                <Select value="closing_rate" disabled>
                  {options.supportedFxMethods.map((m) => <option key={m} value={m}>{FX_METHODS[m] ?? m}</option>)}
                </Select>
              </Field>
              <Field label={`Tolérance de rapprochement (${cur})`} error={save.fieldErrors.tolerance} hint="Écart accepté entre les deux écritures d'une opération intersociétés.">
                <TextInput inputMode="decimal" value={tol} disabled={!canManage} onChange={(e) => setTolerance(e.target.value)} />
              </Field>
            </div>
            {canManage && <div><Button type="submit" size="sm" disabled={save.busy}>Enregistrer la politique</Button></div>}
          </form>
        )}
      </Card>
      <Card>
        <SectionLabel action={hasPermission("group.companies") ? <Button size="sm" onClick={() => setCreating(true)}>Ajouter une société</Button> : undefined}>Sociétés du groupe</SectionLabel>
        {arch.error && <div role="alert" className="nx-error-text">{arch.error}</div>}
        <DataTable exportTitle="Sociétés du groupe"
          columns={[
            { key: "n", header: "Société", render: (c) => c.name },
            { key: "s", header: "Activité", render: (c) => (c.sector ? ACTIVITY_LABELS[c.sector] ?? c.sector : "—") },
            { key: "c", header: "Devise", render: (c) => c.currency },
            { key: "st", header: "État", render: (c) => (c.status === "archived"
              ? <><Badge>Archivée</Badge> <span style={hint}>{c.archivedAt ? dateShort(c.archivedAt) : ""}{c.archiveReason ? ` — ${c.archiveReason}` : ""}</span></>
              : <Badge tone="success">Active</Badge>) },
            { key: "a", header: "", render: (c) => (c.status !== "archived" && hasPermission("group.companies")
              ? <Button size="sm" variant="ghost" disabled={arch.busy} onClick={() => setArchiving(c)}>Archiver</Button> : null) },
          ]}
          rows={options.companies} rowKey={(c) => c.id} />
        <div style={hint}>L'archivage arrête les nouvelles opérations et conserve tout l'historique.</div>
      </Card>
      {archiving && <ConfirmDialog title={`Archiver ${archiving.name}`} requireReason danger confirmLabel="Archiver"
        message="Ses modules sont désactivés : plus aucune vente, achat ni écriture. Son historique reste consultable et consolidable sur les périodes passées."
        onCancel={() => setArchiving(null)}
        onConfirm={async (reason) => {
          const c = archiving; setArchiving(null);
          if (await arch.submit(() => archiveCompany(gid, c.id, reason).then(() => true))) { toast.success(`${c.name} archivée.`); onChanged(); }
        }} />}
      {creating && <CompanyDialog gid={gid} options={options} onClose={() => setCreating(false)}
        onDone={(name) => { setCreating(false); onChanged(); toast.success(`${name} créée : plan comptable, journaux, exercice et caisse sont prêts.`); }} />}
    </div>
  );
}

function CompanyDialog({ gid, options, onClose, onDone }: { gid: string; options: GroupOptions; onClose: () => void; onDone: (name: string) => void }) {
  const [name, setName] = useState("");
  const [activity, setActivity] = useState(options.activities[0] ?? "services");
  const [currency, setCurrency] = useState(options.currencies.includes("HTG") ? "HTG" : options.currencies[0] ?? "");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "company.name": "name", "company.name_exists": "name" });
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!name.trim()) return fail("name", "Nom obligatoire.");
    if (await submit(() => provisionCompany(gid, { name: name.trim(), activityType: activity, currency }).then(() => true))) onDone(name.trim());
  }
  return (
    <Modal title="Ajouter une société au groupe" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer la société</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(480px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={120} onChange={(e) => setName(e.target.value)} /></Field>
        <div style={row}>
          <Field label="Activité"><Select value={activity} onChange={(e) => setActivity(e.target.value)}>
            {options.activities.map((a) => <option key={a} value={a}>{ACTIVITY_LABELS[a] ?? a}</option>)}
          </Select></Field>
          <Field label="Devise"><Select value={currency} onChange={(e) => setCurrency(e.target.value)}>
            {options.currencies.map((c) => <option key={c} value={c}>{c}</option>)}
          </Select></Field>
        </div>
        <div style={hint}>Les personnes qui ont un rôle au niveau du groupe y ont accès immédiatement ; les autres doivent recevoir un rôle dans cette société.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
