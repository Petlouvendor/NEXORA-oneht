import { useCallback, useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, DataTable, EmptyState, KpiCard, Money, SectionLabel, Spinner, Tabs } from "@/shared/ui";
import { dateShort, dateTime, money } from "@/shared/format";
import {
  ADVANCE_KINDS, ADVANCE_STATUS, CONTRACT_TYPES, EXPENSE_CATEGORIES, LEAVE_TYPES, OVERTIME_TYPES, RECOVERY_METHODS, REQUEST_STATUS,
  advanceHistory, cancelAdvance, cancelLeave, listAdvances, listExpenseClaims, listLeaves, listOvertime, myProfile,
  type Advance, type AdvanceHistory, type ExpenseClaim, type Leave, type MyProfile, type Overtime,
} from "@/hr/hrApi";
import { AdvanceRequestDialog, ExpenseClaimDialog, LeaveDialog, OvertimeDialog, PromptDialog, errorText } from "@/hr/HrDialogs";
import { DocumentsPanel, HistoryPanel, PayslipsPanel } from "@/hr/EmployeePage";
import { statusTone } from "@/hr/HrPage";

const EVENT: Record<string, string> = {
  requested: "Demande", approval_step: "Étape d'approbation", approved: "Approbation", rejected: "Refus", paid: "Versement", schedule_set: "Échéancier",
  schedule_changed: "Échéancier modifié", recovered_payroll: "Retenue sur paie", recovered_manual: "Remboursement direct", settled: "Soldée", cancelled: "Annulation",
};

/** Historique complet d'une avance (§13.10, §29 « GET /salary-advances/{id}/history »). */
export function AdvanceHistoryView({ advanceId, self }: { advanceId: string; self?: boolean }) {
  const [h, setH] = useState<AdvanceHistory | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { advanceHistory(advanceId, self).then(setH).catch((e) => setError(errorText(e, "Avance introuvable"))); }, [advanceId, self]);
  if (error) return <Card><EmptyState title="Avance" hint={error} /></Card>;
  if (!h) return <Card><Spinner /></Card>;
  const a = h.advance;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))" }}>
        <KpiCard label="Demandé" value={money(a.amountRequested, a.currency)} />
        <KpiCard label="Approuvé" value={a.amountApproved == null ? "—" : money(a.amountApproved, a.currency)} />
        <KpiCard label="Versé" value={money(a.amountPaid, a.currency)} />
        <KpiCard label="Récupéré" value={money(a.recoveredAmount, a.currency)} />
        <KpiCard label="Solde" value={money(a.balance, a.currency)} tone={a.balance > 0 ? "warning" : undefined} />
      </div>
      <Card>
        <SectionLabel>{a.advanceNumber} — {ADVANCE_KINDS[a.kind]} <Badge tone={statusTone(a.status)}>{ADVANCE_STATUS[a.status]}</Badge></SectionLabel>
        <div style={{ display: "grid", gap: 4 }}>
          <div>Récupération : {RECOVERY_METHODS[a.recoveryMethod]}{a.recoveryMethod === "installments" ? ` en ${a.installmentsCount} échéance(s)` : ""}{a.recoveryPercent ? ` — ${a.recoveryPercent} % du net` : ""}</div>
          {a.reason && <div>Motif : {a.reason}</div>}
          {a.approvedByName && <div>Approuvée par {a.approvedByName} le {dateTime(a.approvedAt)}</div>}
          {a.paidByName && <div>Versée par {a.paidByName} le {dateTime(a.paidAt)} — {a.financialAccountName} ({a.paymentReference ?? "sans référence"})</div>}
        </div>
      </Card>
      <Card>
        <SectionLabel>Échéancier</SectionLabel>
        <DataTable rows={a.schedule} rowKey={(s) => s.id} empty="Pas d'échéancier (pourcentage du net ou remboursement direct)." exportTitle={false} columns={[
          { key: "v", header: "Version", render: (s) => `v${s.scheduleVersion}` },
          { key: "n", header: "N°", render: (s) => s.seqNo },
          { key: "due", header: "Échéance", render: (s) => dateShort(s.dueDate) },
          { key: "amt", header: "Montant", numeric: true, render: (s) => <Money value={s.amount} currency={a.currency} /> },
          { key: "rec", header: "Récupéré", numeric: true, render: (s) => <Money value={s.recoveredAmount} currency={a.currency} /> },
          { key: "st", header: "Statut", render: (s) => ({ pending: "À venir", partial: "Partielle", recovered: "Récupérée", replaced: "Remplacée" }[s.status] ?? s.status) },
        ]} />
      </Card>
      <Card>
        <SectionLabel>Récupérations</SectionLabel>
        <DataTable rows={h.repayments} rowKey={(r) => r.id} empty="Aucune récupération." exportTitle={false} columns={[
          { key: "at", header: "Date", render: (r) => dateTime(r.repaidAt) },
          { key: "src", header: "Source", render: (r) => (r.source === "payroll" ? `Paie ${r.runNumber ?? ""} — ${r.periodLabel ?? ""}` : `Remboursement direct${r.reference ? ` (${r.reference})` : ""}`) },
          { key: "amt", header: "Montant", numeric: true, render: (r) => <Money value={r.amount} currency={r.currency} /> },
          { key: "by", header: "Par", render: (r) => r.createdByName ?? "—" },
        ]} />
      </Card>
      <Card>
        <SectionLabel>Toutes les étapes (§13.10)</SectionLabel>
        <DataTable rows={h.events} rowKey={(e) => e.id} exportTitle={false} columns={[
          { key: "at", header: "Date", render: (e) => dateTime(e.occurredAt) },
          { key: "type", header: "Étape", render: (e) => EVENT[e.eventType] ?? e.eventType },
          { key: "amt", header: "Montant", numeric: true, render: (e) => (e.amount == null ? "—" : <Money value={e.amount} currency={a.currency} />) },
          { key: "bal", header: "Solde après", numeric: true, render: (e) => (e.balanceAfter == null ? "—" : <Money value={e.balanceAfter} currency={a.currency} />) },
          { key: "note", header: "Détail", render: (e) => [e.reference, e.note].filter(Boolean).join(" — ") || "—" },
          { key: "by", header: "Par", render: (e) => e.actorName ?? "—" },
        ]} />
      </Card>
    </div>
  );
}

export function AdvancePage() {
  const { id = "" } = useParams();
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}><Link to="/hr?tab=advances">Avances & prêts</Link></div>
      <AdvanceHistoryView advanceId={id} />
    </div>
  );
}

/** Espace employé (§5, §6) : l'employé voit SES bulletins, avances, congés et frais, et fait ses demandes. */
export function MyHrPage() {
  const toast = useToast();
  const [params, setParams] = useSearchParams();
  const [profile, setProfile] = useState<MyProfile | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dialog, setDialog] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const reload = useCallback(() => {
    myProfile().then(setProfile).catch((e) => setError(errorText(e, "Aucun dossier employé")));
    setVersion((v) => v + 1);
  }, []);
  useEffect(reload, [reload]);
  if (error && !profile) return <Card><EmptyState title="Mon espace RH" hint={error} /></Card>;
  if (!profile) return <Card><Spinner /></Card>;
  const tab = params.get("tab") ?? "payslips";
  const done = (msg: string) => { setDialog(null); toast.success(msg); reload(); };
  const cur = profile.currency ?? "HTG";
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
        <div>
          <h1 style={{ margin: 0 }}>Mon espace RH</h1>
          <div style={{ color: "var(--nx-text-2)" }}>{profile.fullName} · {profile.employeeNumber} · {[profile.jobTitle, profile.departmentName].filter(Boolean).join(" — ")}</div>
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <Button onClick={() => setDialog("advance")}>Demander une avance</Button>
          <Button variant="ghost" onClick={() => setDialog("leave")}>Demander un congé</Button>
          <Button variant="ghost" onClick={() => setDialog("overtime")}>Déclarer des heures sup.</Button>
          <Button variant="ghost" onClick={() => setDialog("expense")}>Note de frais</Button>
        </div>
      </div>
      <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))" }}>
        <KpiCard label="Salaire de base" value={profile.baseSalary == null ? "—" : money(profile.baseSalary, cur)} />
        <KpiCard label="Contrat" value={profile.contractType ? CONTRACT_TYPES[profile.contractType] : "—"} />
        <KpiCard label="Avances à rembourser" value={money(profile.advanceBalance, cur)} />
        <KpiCard label="Prêt à rembourser" value={money(profile.loanBalance, cur)} />
        <KpiCard label="Congés annuels pris / droit" value={`${profile.annualLeaveTaken} / ${profile.annualLeaveEntitlement} j`} />
      </div>
      <Tabs tabs={[{ id: "payslips", label: "Mes bulletins" }, { id: "advances", label: "Mes avances" }, { id: "leaves", label: "Congés & heures sup." },
                   { id: "expenses", label: "Mes frais" }, { id: "history", label: "Mon historique" }, { id: "documents", label: "Mes documents" }]}
            active={tab} onChange={(t) => setParams((p) => { p.set("tab", t); return p; }, { replace: true })} />
      {tab === "payslips" && <PayslipsPanel employeeId={profile.employeeId} version={version} self />}
      {tab === "advances" && <MyAdvances version={version} onChanged={reload} />}
      {tab === "leaves" && <MyTime version={version} onChanged={reload} />}
      {tab === "expenses" && <MyExpenses version={version} />}
      {tab === "history" && <HistoryPanel employeeId={profile.employeeId} version={version} self />}
      {tab === "documents" && <DocumentsPanel employee={{ id: profile.employeeId }} version={version} self />}

      {dialog === "advance" && <AdvanceRequestDialog employeeId={profile.employeeId} self onClose={() => setDialog(null)} onDone={() => done("Demande envoyée pour validation")} />}
      {dialog === "leave" && <LeaveDialog employeeId={profile.employeeId} self onClose={() => setDialog(null)} onDone={() => done("Demande de congé envoyée")} />}
      {dialog === "overtime" && <OvertimeDialog employeeId={profile.employeeId} self onClose={() => setDialog(null)} onDone={() => done("Heures envoyées pour validation")} />}
      {dialog === "expense" && <ExpenseClaimDialog employeeId={profile.employeeId} self onClose={() => setDialog(null)} onDone={() => done("Note de frais envoyée")} />}
    </div>
  );
}

function MyAdvances({ version, onChanged }: { version: number; onChanged: () => void }) {
  const [rows, setRows] = useState<Advance[] | null>(null);
  const [open, setOpen] = useState<string | null>(null);
  const [cancelling, setCancelling] = useState<Advance | null>(null);
  useEffect(() => { listAdvances({}, true).then(setRows).catch(() => setRows([])); }, [version]);
  if (open) return <div style={{ display: "grid", gap: 8 }}><div><Button size="sm" variant="ghost" onClick={() => setOpen(null)}>← Retour</Button></div><AdvanceHistoryView advanceId={open} self /></div>;
  return (
    <Card>
      <SectionLabel>Mes avances, acomptes et prêts</SectionLabel>
      {rows ? <DataTable<Advance> rows={rows} rowKey={(a) => a.id} empty="Aucune avance." exportTitle={false} columns={[
        { key: "num", header: "Référence", render: (a) => <button className="nx-linkbtn" onClick={() => setOpen(a.id)}>{a.advanceNumber}</button> },
        { key: "kind", header: "Type", render: (a) => ADVANCE_KINDS[a.kind] },
        { key: "req", header: "Demandé", numeric: true, render: (a) => <Money value={a.amountRequested} currency={a.currency} /> },
        { key: "paid", header: "Versé", numeric: true, render: (a) => <Money value={a.amountPaid} currency={a.currency} /> },
        { key: "bal", header: "Reste dû", numeric: true, render: (a) => <b><Money value={a.balance} currency={a.currency} /></b> },
        { key: "next", header: "Prochaine retenue", render: (a) => { const n = a.schedule.find((s) => ["pending", "partial"].includes(s.status)); return n ? `${dateShort(n.dueDate)} — ${money(n.amount - n.recoveredAmount, a.currency)}` : "—"; } },
        { key: "st", header: "Statut", render: (a) => <Badge tone={statusTone(a.status)}>{ADVANCE_STATUS[a.status]}</Badge> },
        { key: "act", header: "", render: (a) => a.status === "requested" ? <Button size="sm" variant="ghost" onClick={() => setCancelling(a)}>Annuler</Button> : null },
      ]} /> : <Spinner />}
      {cancelling && (
        <PromptDialog title={`Annuler ${cancelling.advanceNumber}`} label="Motif" submitLabel="Annuler ma demande" onClose={() => setCancelling(null)}
                      onSubmit={async (reason) => { await cancelAdvance(cancelling.id, reason, true); setCancelling(null); onChanged(); }} />
      )}
    </Card>
  );
}

function MyTime({ version, onChanged }: { version: number; onChanged: () => void }) {
  const [leaves, setLeaves] = useState<Leave[] | null>(null);
  const [overtime, setOvertime] = useState<Overtime[] | null>(null);
  const [cancelling, setCancelling] = useState<Leave | null>(null);
  useEffect(() => {
    listLeaves({}, true).then(setLeaves).catch(() => setLeaves([]));
    listOvertime({}, true).then(setOvertime).catch(() => setOvertime([]));
  }, [version]);
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Congés et absences</SectionLabel>
        {leaves ? <DataTable<Leave> rows={leaves} rowKey={(l) => l.id} empty="Aucune demande." exportTitle={false} columns={[
          { key: "type", header: "Type", render: (l) => LEAVE_TYPES[l.leaveType] },
          { key: "dates", header: "Dates", render: (l) => `${dateShort(l.startDate)} → ${dateShort(l.endDate)} (${l.days} j)` },
          { key: "paid", header: "Rémunéré", render: (l) => (l.paid ? "Oui" : "Non") },
          { key: "st", header: "Statut", render: (l) => <Badge tone={statusTone(l.status)}>{REQUEST_STATUS[l.status]}</Badge> },
          { key: "note", header: "Décision", render: (l) => [l.decidedByName, l.decisionNote].filter(Boolean).join(" — ") || "—" },
          { key: "act", header: "", render: (l) => ["requested", "approved"].includes(l.status) ? <Button size="sm" variant="ghost" onClick={() => setCancelling(l)}>Annuler</Button> : null },
        ]} /> : <Spinner />}
      </Card>
      <Card>
        <SectionLabel>Heures supplémentaires déclarées</SectionLabel>
        {overtime ? <DataTable<Overtime> rows={overtime} rowKey={(o) => o.id} empty="Aucune déclaration." exportTitle={false} columns={[
          { key: "date", header: "Date", render: (o) => dateShort(o.workDate) },
          { key: "h", header: "Heures", numeric: true, render: (o) => o.hours },
          { key: "type", header: "Type", render: (o) => OVERTIME_TYPES[o.overtimeType] },
          { key: "st", header: "Statut", render: (o) => <Badge tone={statusTone(o.status)}>{REQUEST_STATUS[o.status]}</Badge> },
          { key: "paid", header: "Payées", render: (o) => (o.payrollItemId ? "Oui" : "—") },
        ]} /> : <Spinner />}
      </Card>
      {cancelling && (
        <PromptDialog title="Annuler ma demande de congé" label="Motif" submitLabel="Annuler" onClose={() => setCancelling(null)}
                      onSubmit={async (reason) => { await cancelLeave(cancelling.id, reason, true); setCancelling(null); onChanged(); }} />
      )}
    </div>
  );
}

function MyExpenses({ version }: { version: number }) {
  const [rows, setRows] = useState<ExpenseClaim[] | null>(null);
  useEffect(() => { listExpenseClaims({}, true).then(setRows).catch(() => setRows([])); }, [version]);
  return (
    <Card>
      <SectionLabel>Mes notes de frais</SectionLabel>
      {rows ? <DataTable<ExpenseClaim> rows={rows} rowKey={(c) => c.id} empty="Aucune note de frais." exportTitle={false} columns={[
        { key: "num", header: "Référence", render: (c) => c.claimNumber },
        { key: "date", header: "Date", render: (c) => dateShort(c.expenseDate) },
        { key: "cat", header: "Catégorie", render: (c) => EXPENSE_CATEGORIES[c.category] },
        { key: "desc", header: "Description", render: (c) => c.description },
        { key: "amt", header: "Montant", numeric: true, render: (c) => <Money value={c.amount} currency={c.currency} /> },
        { key: "st", header: "Statut", render: (c) => <Badge tone={statusTone(c.status)}>{REQUEST_STATUS[c.status]}</Badge> },
        { key: "note", header: "Décision", render: (c) => c.decisionNote ?? "—" },
      ]} /> : <Spinner />}
    </Card>
  );
}
