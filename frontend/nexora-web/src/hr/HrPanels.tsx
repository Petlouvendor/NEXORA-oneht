import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, DataTable, EmptyState, Money, SectionLabel, Select, Spinner, Tabs, type Column } from "@/shared/ui";
import { dateShort, dateTime, todayIso } from "@/shared/format";
import {
  ADVANCE_KINDS, ADVANCE_STATUS, ATTENDANCE_STATUS, EXPENSE_CATEGORIES, LEAVE_TYPES, OVERTIME_TYPES, RECOVERY_METHODS, REQUEST_STATUS,
  cancelAdvance, cancelVariable, decideAdjustment, decideAttendance, decideLeave, decideOvertime, decideVariable, listAdjustments,
  listAdvances, listAttendance, listExpenseClaims, listLeaves, listOvertime, listPeriods, listVariables, approveAdvance, approveExpenseClaim,
  paymentAccounts, payExpenseClaim, pendingApprovals, rejectAdvance, rejectExpenseClaim,
  type Advance, type ApprovalItem, type Attendance, type ExpenseClaim, type Leave, type Overtime, type PaymentAccount,
  type PayrollAdjustment, type PayrollPeriod, type PayrollVariable,
} from "@/hr/hrApi";
import {
  AdvanceApproveDialog, AdvanceMoneyDialog, AttendanceDialog, AttendanceImportDialog, DecisionDialog, ExpenseClaimDialog, LeaveDialog,
  OvertimeDialog, PromptDialog, RescheduleDialog, VariableDialog, errorText,
} from "@/hr/HrDialogs";
import { statusTone } from "@/hr/HrPage";

const reqBadge = (s: string) => <Badge tone={statusTone(s)}>{REQUEST_STATUS[s] ?? s}</Badge>;

// ================================================================== Avances, acomptes, prêts (§12-§17)

export function AdvancesPanel({ employeeId }: { employeeId?: string }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [status, setStatus] = useState("");
  const [rows, setRows] = useState<Advance[] | null>(null);
  const [dialog, setDialog] = useState<{ kind: "approve" | "pay" | "repay" | "schedule" | "reject" | "cancel"; advance: Advance } | null>(null);
  const load = useCallback(() => { listAdvances({ employeeId, status: status || undefined }).then(setRows).catch(() => setRows([])); }, [employeeId, status]);
  useEffect(load, [load]);
  const done = (msg: string) => { setDialog(null); toast.success(msg); load(); };
  const columns: Array<Column<Advance>> = [
    { key: "num", header: "Référence", render: (a) => <Link to={`/hr/advances/${a.id}`}>{a.advanceNumber}</Link> },
    ...(employeeId ? [] : [{ key: "emp", header: "Employé", render: (a: Advance) => <Link to={`/hr/employees/${a.employeeId}`}>{a.employeeName}</Link> }]),
    { key: "kind", header: "Type", render: (a) => ADVANCE_KINDS[a.kind] },
    { key: "req", header: "Demandé", numeric: true, render: (a) => <Money value={a.amountRequested} currency={a.currency} /> },
    { key: "paid", header: "Versé", numeric: true, render: (a) => <Money value={a.amountPaid} currency={a.currency} /> },
    { key: "rec", header: "Récupéré", numeric: true, render: (a) => <Money value={a.recoveredAmount} currency={a.currency} /> },
    { key: "bal", header: "Solde", numeric: true, render: (a) => <b><Money value={a.balance} currency={a.currency} /></b> },
    { key: "method", header: "Récupération", render: (a) => RECOVERY_METHODS[a.recoveryMethod] },
    { key: "status", header: "Statut", render: (a) => <Badge tone={statusTone(a.status)}>{ADVANCE_STATUS[a.status]}</Badge> },
    { key: "act", header: "", render: (a) => (
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        {a.status === "requested" && hasPermission("payroll.advance_approve") && <>
          <Button size="sm" onClick={() => setDialog({ kind: "approve", advance: a })}>Approuver</Button>
          <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "reject", advance: a })}>Refuser</Button>
        </>}
        {a.status === "approved" && hasPermission("payroll.advance_pay") && <Button size="sm" onClick={() => setDialog({ kind: "pay", advance: a })}>Verser</Button>}
        {["paid", "in_repayment"].includes(a.status) && hasPermission("payroll.advance_pay") &&
          <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "repay", advance: a })}>Remboursement</Button>}
        {["paid", "in_repayment"].includes(a.status) && hasPermission("payroll.advance_schedule") &&
          <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "schedule", advance: a })}>Échéancier</Button>}
        {["requested", "approved"].includes(a.status) && <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "cancel", advance: a })}>Annuler</Button>}
      </div>
    ) },
  ];
  return (
    <Card>
      <SectionLabel action={
        <Select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">Toutes</option>
          <option value="requested">À approuver</option>
          <option value="approved">À verser</option>
          <option value="open">En cours de récupération</option>
          <option value="settled">Soldées</option>
        </Select>
      }>Avances, acomptes et prêts — chaque avance garde sa référence, son solde et son échéancier (§16)</SectionLabel>
      {rows ? <DataTable columns={columns} rows={rows} rowKey={(a) => a.id} empty="Aucune avance." exportTitle="Avances sur salaire" /> : <Spinner />}
      {dialog?.kind === "approve" && <AdvanceApproveDialog advance={dialog.advance} onClose={() => setDialog(null)} onDone={() => done("Avance approuvée")} />}
      {(dialog?.kind === "pay" || dialog?.kind === "repay") && (
        <AdvanceMoneyDialog advance={dialog.advance} mode={dialog.kind} onClose={() => setDialog(null)}
                            onDone={() => done(dialog.kind === "pay" ? "Avance versée — créance sur l'employé créée" : "Remboursement enregistré")} />
      )}
      {dialog?.kind === "schedule" && <RescheduleDialog advance={dialog.advance} onClose={() => setDialog(null)} onDone={() => done("Échéancier remplacé")} />}
      {dialog?.kind === "reject" && (
        <PromptDialog title={`Refuser ${dialog.advance.advanceNumber}`} label="Motif du refus" type="textarea" submitLabel="Refuser"
                      onClose={() => setDialog(null)} onSubmit={async (note) => { await rejectAdvance(dialog.advance.id, note); done("Demande refusée"); }} />
      )}
      {dialog?.kind === "cancel" && (
        <PromptDialog title={`Annuler ${dialog.advance.advanceNumber}`} label="Motif d'annulation" submitLabel="Annuler la demande"
                      onClose={() => setDialog(null)} onSubmit={async (reason) => { await cancelAdvance(dialog.advance.id, reason); done("Demande annulée"); }} />
      )}
    </Card>
  );
}

// ================================================================== Temps : congés, heures sup., présences (§18, §19)

export function TimePanel() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [sub, setSub] = useState("leaves");
  const [leaves, setLeaves] = useState<Leave[] | null>(null);
  const [overtime, setOvertime] = useState<Overtime[] | null>(null);
  const [attendance, setAttendance] = useState<Attendance[] | null>(null);
  const [dialog, setDialog] = useState<"leave" | "overtime" | "attendance" | "import" | null>(null);
  const [deciding, setDeciding] = useState<{ kind: "leave" | "overtime" | "attendance"; id: string; label: string } | null>(null);
  const load = useCallback(() => {
    listLeaves().then(setLeaves).catch(() => setLeaves([]));
    listOvertime().then(setOvertime).catch(() => setOvertime([]));
    const from = todayIso(new Date(Date.now() - 45 * 86400_000));
    listAttendance({ from }).then(setAttendance).catch(() => setAttendance([]));
  }, []);
  useEffect(load, [load]);
  const canDecide = hasPermission("hr.approve_requests");
  const canManage = hasPermission("hr.manage_time");
  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {canManage && <Button size="sm" onClick={() => setDialog("leave")}>Congé / absence</Button>}
          {canManage && <Button size="sm" variant="ghost" onClick={() => setDialog("overtime")}>Heures sup.</Button>}
          {canManage && <Button size="sm" variant="ghost" onClick={() => setDialog("attendance")}>Présence</Button>}
          {canManage && <Button size="sm" variant="ghost" onClick={() => setDialog("import")}>Importer des présences</Button>}
        </div>
      }>Temps et absences — validation par une autre personne, verrouillés une fois la paie validée</SectionLabel>
      <Tabs tabs={[{ id: "leaves", label: "Congés & absences" }, { id: "overtime", label: "Heures supplémentaires" }, { id: "attendance", label: "Présences (45 j)" }]}
            active={sub} onChange={setSub} />
      <div style={{ marginTop: 12 }}>
        {sub === "leaves" && (leaves ? <DataTable<Leave> rows={leaves} rowKey={(l) => l.id} exportTitle="Congés et absences" columns={[
          { key: "emp", header: "Employé", render: (l) => <Link to={`/hr/employees/${l.employeeId}`}>{l.employeeName}</Link> },
          { key: "type", header: "Type", render: (l) => LEAVE_TYPES[l.leaveType] },
          { key: "dates", header: "Dates", render: (l) => `${dateShort(l.startDate)} → ${dateShort(l.endDate)}` },
          { key: "days", header: "Jours", numeric: true, render: (l) => l.days },
          { key: "paid", header: "Rémunéré", render: (l) => (l.paid ? "Oui" : <Badge tone="warning">Non</Badge>) },
          { key: "by", header: "Demandé par", render: (l) => l.requestedByName ?? "—" },
          { key: "st", header: "Statut", render: (l) => reqBadge(l.status) },
          { key: "dec", header: "Décision", render: (l) => (l.decidedByName ? `${l.decidedByName}, ${dateTime(l.decidedAt)}` : "—") },
          { key: "act", header: "", render: (l) => l.status === "requested" && canDecide
              ? <Button size="sm" onClick={() => setDeciding({ kind: "leave", id: l.id, label: `${l.employeeName} — ${LEAVE_TYPES[l.leaveType]}` })}>Décider</Button> : null },
        ]} /> : <Spinner />)}
        {sub === "overtime" && (overtime ? <DataTable<Overtime> rows={overtime} rowKey={(o) => o.id} exportTitle="Heures supplémentaires" columns={[
          { key: "emp", header: "Employé", render: (o) => o.employeeName },
          { key: "date", header: "Date", render: (o) => dateShort(o.workDate) },
          { key: "h", header: "Heures", numeric: true, render: (o) => o.hours },
          { key: "type", header: "Type", render: (o) => `${OVERTIME_TYPES[o.overtimeType]} × ${o.coefficient}` },
          { key: "st", header: "Statut", render: (o) => reqBadge(o.status) },
          { key: "proof", header: "Approbation", render: (o) => (o.decidedByName ? `${o.decidedByName}, ${dateTime(o.decidedAt)}` : "—") },
          { key: "paid", header: "Payée", render: (o) => (o.payrollItemId ? "Oui" : "—") },
          { key: "act", header: "", render: (o) => o.status === "requested" && canDecide
              ? <Button size="sm" onClick={() => setDeciding({ kind: "overtime", id: o.id, label: `${o.employeeName} — ${o.hours} h le ${dateShort(o.workDate)}` })}>Décider</Button> : null },
        ]} /> : <Spinner />)}
        {sub === "attendance" && (attendance ? <DataTable<Attendance> rows={attendance} rowKey={(a) => a.id} exportTitle="Présences" columns={[
          { key: "emp", header: "Employé", render: (a) => a.employeeName },
          { key: "date", header: "Date", render: (a) => dateShort(a.workDate) },
          { key: "st", header: "Statut", render: (a) => ATTENDANCE_STATUS[a.status] },
          { key: "h", header: "Heures", numeric: true, render: (a) => a.hoursWorked ?? "—" },
          { key: "j", header: "Justifiée", render: (a) => (a.justified ? "Oui" : "Non") },
          { key: "src", header: "Source", render: (a) => a.source },
          { key: "val", header: "Validation", render: (a) => reqBadge(a.validationStatus) },
          { key: "act", header: "", render: (a) => a.validationStatus === "pending" && canDecide
              ? <Button size="sm" onClick={() => setDeciding({ kind: "attendance", id: a.id, label: `${a.employeeName} — ${ATTENDANCE_STATUS[a.status]} le ${dateShort(a.workDate)}` })}>Valider</Button> : null },
        ]} /> : <Spinner />)}
      </div>
      {dialog === "leave" && <LeaveDialog onClose={() => setDialog(null)} onDone={() => { setDialog(null); toast.success("Demande enregistrée"); load(); }} />}
      {dialog === "overtime" && <OvertimeDialog onClose={() => setDialog(null)} onDone={() => { setDialog(null); toast.success("Heures envoyées pour validation"); load(); }} />}
      {(dialog === "attendance" || dialog === "import") && (() => {
        const Dialog = dialog === "import" ? AttendanceImportDialog : AttendanceDialog;
        return <Dialog onClose={() => setDialog(null)} onDone={(r) => {
          setDialog(null);
          toast.success(`${r.created} créée(s), ${r.updated} mise(s) à jour${r.errors.length ? ` — ${r.errors.length} ligne(s) refusée(s)` : ""}`);
          if (r.errors.length) toast.error(r.errors.slice(0, 3).join(" · "));
          load();
        }} />;
      })()}
      {deciding && (
        <DecisionDialog title={deciding.label} approveLabel={deciding.kind === "attendance" ? "Valider" : "Approuver"} onClose={() => setDeciding(null)}
                        onDecide={async (approve, note) => {
                          if (deciding.kind === "leave") await decideLeave(deciding.id, approve, note || null);
                          else if (deciding.kind === "overtime") await decideOvertime(deciding.id, approve, note || null);
                          else await decideAttendance(deciding.id, approve, note || null);
                          setDeciding(null);
                          load();
                        }} />
      )}
    </Card>
  );
}

// ================================================================== Variables (§20, §21)

export function VariablesPanel() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [periods, setPeriods] = useState<PayrollPeriod[]>([]);
  const [periodId, setPeriodId] = useState("");
  const [rows, setRows] = useState<PayrollVariable[] | null>(null);
  const [creating, setCreating] = useState(false);
  const [deciding, setDeciding] = useState<PayrollVariable | null>(null);
  const [cancelling, setCancelling] = useState<PayrollVariable | null>(null);
  useEffect(() => { listPeriods().then((p) => setPeriods(p.filter((x) => ["draft", "calculated", "in_validation"].includes(x.status)))).catch(() => {}); }, []);
  const load = useCallback(() => { listVariables({ periodId: periodId || undefined }).then(setRows).catch(() => setRows([])); }, [periodId]);
  useEffect(load, [load]);
  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 8 }}>
          <Select value={periodId} onChange={(e) => setPeriodId(e.target.value)}>
            <option value="">Toutes les périodes</option>
            {periods.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
          </Select>
          {(hasPermission("hr.manage_time") || hasPermission("hr.approve_requests")) &&
            <Button size="sm" onClick={() => setCreating(true)} disabled={periods.length === 0}>Nouvel élément</Button>}
        </div>
      }>Primes, commissions, avantages et retenues ponctuels — validés avant intégration (§20)</SectionLabel>
      {rows ? <DataTable<PayrollVariable> rows={rows} rowKey={(v) => v.id} exportTitle="Éléments variables" columns={[
        { key: "emp", header: "Employé", render: (v) => v.employeeName },
        { key: "per", header: "Période", render: (v) => v.periodLabel },
        { key: "comp", header: "Élément", render: (v) => `${v.componentName}${v.kind === "deduction" ? " (retenue)" : ""}` },
        { key: "amt", header: "Montant", numeric: true, render: (v) => <Money value={v.amount} /> },
        { key: "desc", header: "Source", render: (v) => v.description + (v.legalBasis ? ` — base légale : ${v.legalBasis}` : "") },
        { key: "by", header: "Saisi par", render: (v) => v.submittedByName ?? "—" },
        { key: "st", header: "Statut", render: (v) => reqBadge(v.status) },
        { key: "act", header: "", render: (v) => (
          <div style={{ display: "flex", gap: 6 }}>
            {v.status === "submitted" && hasPermission("hr.approve_requests") && <Button size="sm" onClick={() => setDeciding(v)}>Décider</Button>}
            {["submitted", "approved"].includes(v.status) && !v.payrollItemId && <Button size="sm" variant="ghost" onClick={() => setCancelling(v)}>Annuler</Button>}
          </div>
        ) },
      ]} /> : <Spinner />}
      {creating && <VariableDialog periods={periods} onClose={() => setCreating(false)} onDone={() => { setCreating(false); toast.success("Élément soumis pour validation"); load(); }} />}
      {deciding && (
        <DecisionDialog title={`${deciding.employeeName} — ${deciding.componentName}`} onClose={() => setDeciding(null)}
                        onDecide={async (approve, note) => { await decideVariable(deciding.id, approve, note || null); setDeciding(null); load(); }} />
      )}
      {cancelling && (
        <PromptDialog title="Annuler l'élément variable" label="Motif" submitLabel="Annuler l'élément" onClose={() => setCancelling(null)}
                      onSubmit={async (reason) => { await cancelVariable(cancelling.id, reason); setCancelling(null); load(); }} />
      )}
    </Card>
  );
}

// ================================================================== Notes de frais (§17, §32)

export function ExpensesPanel({ employeeId }: { employeeId?: string }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [rows, setRows] = useState<ExpenseClaim[] | null>(null);
  const [creating, setCreating] = useState(false);
  const [deciding, setDeciding] = useState<ExpenseClaim | null>(null);
  const [paying, setPaying] = useState<ExpenseClaim | null>(null);
  const [accounts, setAccounts] = useState<PaymentAccount[]>([]);
  const load = useCallback(() => { listExpenseClaims({ employeeId }).then(setRows).catch(() => setRows([])); }, [employeeId]);
  useEffect(load, [load]);
  useEffect(() => { if (hasPermission("payroll.expense_approve")) paymentAccounts().then(setAccounts).catch(() => {}); }, [hasPermission]);
  return (
    <Card>
      <SectionLabel action={hasPermission("hr.manage_time") && <Button size="sm" onClick={() => setCreating(true)}>Nouvelle note de frais</Button>}>
        Remboursements de frais — dette de l'entreprise envers l'employé (§17)
      </SectionLabel>
      {rows ? <DataTable<ExpenseClaim> rows={rows} rowKey={(c) => c.id} exportTitle="Notes de frais" columns={[
        { key: "num", header: "Référence", render: (c) => c.claimNumber },
        { key: "emp", header: "Employé", render: (c) => c.employeeName },
        { key: "date", header: "Date", render: (c) => dateShort(c.expenseDate) },
        { key: "cat", header: "Catégorie", render: (c) => EXPENSE_CATEGORIES[c.category] },
        { key: "desc", header: "Description", render: (c) => c.description },
        { key: "amt", header: "Montant", numeric: true, render: (c) => <Money value={c.amount} currency={c.currency} /> },
        { key: "set", header: "Règlement", render: (c) => (c.settlement === "payroll" ? "Avec la paie" : "Direct") },
        { key: "st", header: "Statut", render: (c) => reqBadge(c.status) },
        { key: "act", header: "", render: (c) => (
          <div style={{ display: "flex", gap: 6 }}>
            {c.status === "submitted" && hasPermission("payroll.expense_approve") && <Button size="sm" onClick={() => setDeciding(c)}>Décider</Button>}
            {c.status === "approved" && hasPermission("payroll.expense_approve") && <Button size="sm" variant="ghost" onClick={() => setPaying(c)}>Payer directement</Button>}
          </div>
        ) },
      ]} /> : <Spinner />}
      {creating && <ExpenseClaimDialog employeeId={employeeId} onClose={() => setCreating(false)} onDone={() => { setCreating(false); toast.success("Note de frais soumise"); load(); }} />}
      {deciding && (
        <DecisionDialog title={`${deciding.claimNumber} — ${deciding.employeeName}`} onClose={() => setDeciding(null)}
                        onDecide={async (approve, note) => {
                          if (approve) await approveExpenseClaim(deciding.id, note || null); else await rejectExpenseClaim(deciding.id, note);
                          setDeciding(null); load();
                        }} />
      )}
      {paying && (
        <PayClaimDialog claim={paying} accounts={accounts.filter((a) => a.currency === paying.currency)} onClose={() => setPaying(null)}
                        onDone={() => { setPaying(null); toast.success("Frais remboursés"); load(); }} />
      )}
    </Card>
  );
}

function PayClaimDialog({ claim, accounts, onClose, onDone }: { claim: ExpenseClaim; accounts: PaymentAccount[]; onClose: () => void; onDone: () => void }) {
  const [operation] = useState(() => crypto.randomUUID());
  const [account, setAccount] = useState(accounts[0]?.id ?? "");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  return (
    <DecisionDialog title={`Rembourser ${claim.claimNumber}`} approveLabel="Rembourser" onClose={onClose}
                    onDecide={async (approve, note) => {
                      if (!approve) { onClose(); return; }
                      setBusy(true);
                      try { await payExpenseClaim(claim.id, account, note || null, operation); onDone(); } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
                    }}>
      <p style={{ margin: 0 }}>{claim.employeeName} — <Money value={claim.amount} currency={claim.currency} /> ({claim.description}).</p>
      <Select value={account} onChange={(e) => setAccount(e.target.value)} disabled={busy}>
        {accounts.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
      </Select>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
    </DecisionDialog>
  );
}

// ================================================================== Régularisations (§36)

export function AdjustmentsPanel() {
  const { hasPermission } = useAuth();
  const [rows, setRows] = useState<PayrollAdjustment[] | null>(null);
  const [deciding, setDeciding] = useState<PayrollAdjustment | null>(null);
  const load = useCallback(() => { listAdjustments().then(setRows).catch(() => setRows([])); }, []);
  useEffect(load, [load]);
  return (
    <Card>
      <SectionLabel>Régularisations — reliées à la paie d'origine, jamais de modification silencieuse (§36)</SectionLabel>
      {rows ? <DataTable<PayrollAdjustment> rows={rows} rowKey={(a) => a.id} exportTitle="Régularisations de paie" columns={[
        { key: "num", header: "Référence", render: (a) => a.adjustmentNumber },
        { key: "emp", header: "Employé", render: (a) => a.employeeName },
        { key: "orig", header: "Paie d'origine", render: (a) => <Link to={`/hr/payroll/${a.originalRunId}`}>{a.originalRunNumber} — {a.originalPeriodLabel}</Link> },
        { key: "kind", header: "Nature", render: (a) => (a.kind === "earning" ? "Rappel" : "Trop-perçu") },
        { key: "amt", header: "Montant", numeric: true, render: (a) => <Money value={a.amount} currency={a.currency} /> },
        { key: "reason", header: "Justification", render: (a) => a.reason },
        { key: "st", header: "Statut", render: (a) => reqBadge(a.status) },
        { key: "applied", header: "Intégrée dans", render: (a) => a.appliedRunNumber ?? "—" },
        { key: "act", header: "", render: (a) => a.status === "requested" && hasPermission("payroll.approve_adjustment")
            ? <Button size="sm" onClick={() => setDeciding(a)}>Décider</Button> : null },
      ]} /> : <Spinner />}
      {deciding && (
        <DecisionDialog title={`${deciding.adjustmentNumber} — ${deciding.employeeName}`} onClose={() => setDeciding(null)}
                        onDecide={async (approve, note) => { await decideAdjustment(deciding.id, approve, note || null); setDeciding(null); load(); }} />
      )}
    </Card>
  );
}

// ================================================================== File de validation

export function ApprovalsPanel() {
  const [rows, setRows] = useState<ApprovalItem[] | null>(null);
  const [deciding, setDeciding] = useState<ApprovalItem | null>(null);
  const load = useCallback(() => { pendingApprovals().then(setRows).catch(() => setRows([])); }, []);
  useEffect(load, [load]);
  const decide = async (item: ApprovalItem, approve: boolean, note: string) => {
    const n = note || null;
    switch (item.kind) {
      case "leave": await decideLeave(item.id, approve, n); break;
      case "overtime": await decideOvertime(item.id, approve, n); break;
      case "variable": await decideVariable(item.id, approve, n); break;
      case "attendance": await decideAttendance(item.id, approve, n); break;
      case "expense_claim": if (approve) await approveExpenseClaim(item.id, n); else await rejectExpenseClaim(item.id, note); break;
      case "adjustment": await decideAdjustment(item.id, approve, n); break;
      case "advance": if (approve) await approveAdvance(item.id, { note: n }); else await rejectAdvance(item.id, note); break;
    }
    setDeciding(null);
    load();
  };
  const KIND: Record<string, string> = {
    leave: "Congé", overtime: "Heures sup.", variable: "Variable", attendance: "Présence", advance: "Avance", expense_claim: "Note de frais", adjustment: "Régularisation",
  };
  if (!rows) return <Card><Spinner /></Card>;
  return (
    <Card>
      <SectionLabel>Demandes en attente — une demande ne se valide jamais par son auteur (§39)</SectionLabel>
      {rows.length === 0 ? <EmptyState title="Rien à valider" /> : (
        <DataTable<ApprovalItem> rows={rows} rowKey={(r) => `${r.kind}-${r.id}`} exportTitle={false} columns={[
          { key: "kind", header: "Type", render: (r) => <Badge tone="info">{KIND[r.kind] ?? r.kind}</Badge> },
          { key: "emp", header: "Employé", render: (r) => r.employeeName },
          { key: "sum", header: "Demande", render: (r) => r.summary },
          { key: "amt", header: "Montant", numeric: true, render: (r) => (r.amount == null ? "—" : <Money value={r.amount} currency={r.currency ?? "HTG"} />) },
          { key: "by", header: "Par", render: (r) => `${r.requestedByName ?? "—"}, ${dateTime(r.requestedAt)}` },
          { key: "act", header: "", render: (r) => r.canDecide
              ? <Button size="sm" onClick={() => setDeciding(r)}>Décider</Button>
              : <span style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>{r.cannotDecideReason}</span> },
        ]} />
      )}
      {deciding && <DecisionDialog title={`${KIND[deciding.kind]} — ${deciding.employeeName}`} onClose={() => setDeciding(null)}
                                   onDecide={(approve, note) => decide(deciding, approve, note)}>
        <p style={{ margin: 0 }}>{deciding.summary}</p>
        {deciding.kind === "advance" && <p style={{ margin: 0, color: "var(--nx-text-2)" }}>Montant accordé = montant demandé ; pour accorder moins, passer par l'onglet Avances & prêts.</p>}
      </DecisionDialog>}
    </Card>
  );
}
