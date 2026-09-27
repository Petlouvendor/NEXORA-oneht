import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, DataTable, Drawer, EmptyState, KpiCard, Money, SectionLabel, Spinner, Tabs } from "@/shared/ui";
import { ActionMenu } from "@/shared/ExportMenu";
import { Icon } from "@/shared/icons";
import { dateShort, dateTime, money } from "@/shared/format";
import {
  PAYMENT_METHODS, RUN_STATUS, calculatePeriod, cancelRun, cancelSalaryPayment, downloadPayslip, excludeItem, generatePayslips, getItem, getRun,
  includeItem, printPayslip, reconcileRun, runPayments, runPayslips, runStep,
  type PayrollItemDetail, type PayrollItemSummary, type PayrollRun, type Payslip, type Reconciliation, type SalaryPayment, type WorkflowStep,
} from "@/hr/hrApi";
import { AdjustmentDialog, PayRunDialog, PromptDialog, errorText } from "@/hr/HrDialogs";
import { statusTone } from "@/hr/HrPage";

const STEPS = ["calculated", "reviewed", "validated", "approved", "paid", "closed"];
const STEP_LABEL: Record<string, string> = {
  calculated: "Calcul", reviewed: "Contrôle RH / comptable", validated: "Validation", approved: "Approbation finale", paid: "Paiement & bulletins", closed: "Clôture",
};

/** Une paie (§22) : chaque étape affiche qui l'a faite et quand ; les anomalies bloquantes empêchent d'avancer (§23). */
export function PayrollRunPage() {
  const { runId = "" } = useParams();
  const toast = useToast();
  const [run, setRun] = useState<PayrollRun | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState("items");
  const [detail, setDetail] = useState<string | null>(null);
  const [dialog, setDialog] = useState<{ kind: string; item?: PayrollItemSummary } | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const reload = useCallback(() => { getRun(runId).then(setRun).catch((e) => setError(errorText(e, "Paie introuvable"))); setVersion((v) => v + 1); }, [runId]);
  useEffect(reload, [reload]);
  if (error && !run) return <Card><EmptyState title="Paie" hint={error} /></Card>;
  if (!run) return <Card><Spinner /></Card>;

  const can = (a: string) => run.allowedActions.includes(a);
  const step = async (s: WorkflowStep, note: string | null, ack = false) => {
    setBusy(s);
    try { setRun(await runStep(run.id, s, note, ack)); toast.success("Étape enregistrée"); } catch (e) { toast.error(errorText(e)); } finally { setBusy(null); }
  };
  const recalc = async () => {
    setBusy("calculate");
    try { setRun(await calculatePeriod(run.periodId)); toast.success("Paie recalculée"); } catch (e) { toast.error(errorText(e)); } finally { setBusy(null); }
  };
  const unpaid = run.items.filter((i) => i.status === "approved" && i.net > 0);
  const currency = run.totals[0]?.currency ?? "HTG";
  const reached = STEPS.indexOf(run.status);
  const who: Record<string, string | null> = {
    calculated: run.calculatedByName && `${run.calculatedByName}, ${dateTime(run.calculatedAt)}`,
    reviewed: run.reviewedByName && `${run.reviewedByName}, ${dateTime(run.reviewedAt)}`,
    validated: run.validatedByName && `${run.validatedByName}, ${dateTime(run.validatedAt)}`,
    approved: run.approvedByName && `${run.approvedByName}, ${dateTime(run.approvedAt)}`,
    paid: run.paidAt && dateTime(run.paidAt),
    closed: run.closedByName && `${run.closedByName}, ${dateTime(run.closedAt)}`,
  };

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}><Link to="/hr?tab=payroll">Paie</Link> · {run.runNumber}</div>
          <h1 style={{ margin: "4px 0 0" }}>{run.periodLabel} <Badge tone={statusTone(run.status)}>{RUN_STATUS[run.status]}</Badge></h1>
          <div style={{ color: "var(--nx-text-2)" }}>
            {dateShort(run.startDate)} → {dateShort(run.endDate)} · variables jusqu'au {dateShort(run.cutoffDate)} · paiement prévu le {dateShort(run.paymentDate)}
            {run.journalEntryNumber && <> · écriture {run.journalEntryNumber}</>}
          </div>
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {can("calculate") && <Button variant="ghost" loading={busy === "calculate"} onClick={recalc}>Recalculer</Button>}
          {can("review") && <Button loading={busy === "review"} onClick={() => setDialog({ kind: "review" })}>Contrôler</Button>}
          {can("validate") && <Button loading={busy === "validate"} onClick={() => setDialog({ kind: "validate" })}>Valider (verrouiller)</Button>}
          {can("approve") && <Button loading={busy === "approve"} onClick={() => setDialog({ kind: "approve" })}>Approbation finale</Button>}
          {can("pay") && unpaid.length > 0 && <Button onClick={() => setDialog({ kind: "pay" })}>Payer ({unpaid.length})</Button>}
          {can("close") && <Button variant="ghost" onClick={() => step("close", null)}>Clôturer</Button>}
          {can("return") && <Button variant="ghost" onClick={() => setDialog({ kind: "return" })}>Renvoyer en correction</Button>}
          {can("cancel") && <Button variant="danger" onClick={() => setDialog({ kind: "cancel" })}>Annuler</Button>}
        </div>
      </div>

      <Card>
        <ol style={{ display: "flex", gap: 8, listStyle: "none", padding: 0, margin: 0, flexWrap: "wrap" }}>
          {STEPS.map((s, i) => (
            <li key={s} style={{ flex: "1 1 140px", padding: 10, borderRadius: 8, border: "1px solid var(--nx-border)",
                                 background: i <= reached && run.status !== "cancelled" ? "var(--nx-surface-2)" : undefined }}>
              <div style={{ fontWeight: 600 }}>{i <= reached && run.status !== "cancelled" ? "✓ " : ""}{STEP_LABEL[s]}</div>
              <div style={{ fontSize: "var(--nx-fs-xs)", color: "var(--nx-text-2)" }}>{i <= reached ? who[s] ?? "" : ""}</div>
            </li>
          ))}
        </ol>
        {run.stale && <p className="nx-error-text" style={{ marginBottom: 0 }}>Des présences, absences, variables ou salaires ont changé depuis le calcul : recalculer avant de contrôler.</p>}
        {run.returnedNote && run.status === "calculated" && <p style={{ marginBottom: 0 }}><Badge tone="warning">Renvoyée</Badge> {run.returnedNote}</p>}
        {run.cancelReason && <p style={{ marginBottom: 0 }}><Badge tone="error">Annulée</Badge> {run.cancelReason}</p>}
      </Card>

      <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))" }}>
        <KpiCard label="Employés" value={`${run.itemCount}${run.excludedCount ? ` (+${run.excludedCount} exclus)` : ""}`} />
        {run.totals.map((t) => (
          <KpiCard key={t.currency} label={`Brut / net (${t.currency})`} value={`${money(t.gross, t.currency)} / ${money(t.net, t.currency)}`} />
        ))}
        {run.totals.map((t) => <KpiCard key={`c${t.currency}`} label={`Coût employeur (${t.currency})`} value={money(t.employerCost, t.currency)} />)}
        {run.totals.map((t) => <KpiCard key={`u${t.currency}`} label={`Reste à payer (${t.currency})`} value={money(t.unpaid, t.currency)} tone={t.unpaid > 0 && run.status === "approved" ? "warning" : undefined} />)}
        <KpiCard label="Anomalies" value={`${run.blockingCount} bloquante(s) · ${run.warningCount} avert.`} tone={run.blockingCount ? "warning" : undefined} />
      </div>

      <Tabs tabs={[{ id: "items", label: "Employés" }, { id: "payments", label: "Paiements" }, { id: "payslips", label: "Bulletins" },
                   { id: "reconciliation", label: "Rapprochement Finance" }]} active={tab} onChange={setTab} />

      {tab === "items" && (
        <Card>
          <DataTable<PayrollItemSummary> rows={run.items} rowKey={(i) => i.id} exportTitle={`Paie ${run.periodLabel}`} columns={[
            { key: "emp", header: "Employé", render: (i) => <button className="nx-linkbtn" onClick={() => setDetail(i.id)}>{i.employeeNumber} — {i.employeeName}</button> },
            { key: "dept", header: "Département", render: (i) => i.departmentName ?? "—" },
            { key: "gross", header: "Brut", numeric: true, render: (i) => <Money value={i.gross} currency={i.currency} /> },
            { key: "ded", header: "Retenues", numeric: true, render: (i) => <Money value={i.totalDeductions} currency={i.currency} /> },
            { key: "adv", header: "dont avances/prêts", numeric: true, render: (i) => (i.advanceRecovery + i.loanRecovery ? <Money value={i.advanceRecovery + i.loanRecovery} currency={i.currency} /> : "—") },
            { key: "net", header: "Net à payer", numeric: true, render: (i) => <b><Money value={i.net} currency={i.currency} /></b> },
            { key: "anom", header: "Contrôles", render: (i) => (
              <span title={i.anomalies.map((a) => a.message).join("\n")}>
                {i.blockingCount > 0 && <Badge tone="error">{i.blockingCount} bloquante(s)</Badge>} {i.warningCount > 0 && <Badge tone="warning">{i.warningCount}</Badge>}
                {i.blockingCount + i.warningCount === 0 && "✓"}
              </span>
            ) },
            { key: "st", header: "Statut", render: (i) => <Badge tone={statusTone(i.status)}>{i.status === "excluded" ? `Exclu : ${i.excludedReason}` : RUN_STATUS[i.status] ?? i.status}</Badge> },
            { key: "act", header: "", render: (i) => (
              <div style={{ display: "flex", gap: 6 }}>
                {can("exclude") && i.status === "calculated" && <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "exclude", item: i })}>Exclure</Button>}
                {can("exclude") && i.status === "excluded" && <Button size="sm" variant="ghost" onClick={() => includeItem(i.id).then(setRun).catch((e) => toast.error(errorText(e)))}>Réintégrer</Button>}
                {can("pay") && i.status === "approved" && i.net > 0 && <Button size="sm" onClick={() => setDialog({ kind: "payOne", item: i })}>Payer</Button>}
                {can("adjust") && ["approved", "paid"].includes(i.status) && <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "adjust", item: i })}>Régulariser</Button>}
              </div>
            ) },
          ]} />
        </Card>
      )}
      {tab === "payments" && <PaymentsTab runId={run.id} version={version} onChanged={reload} />}
      {tab === "payslips" && <PayslipsTab run={run} version={version} />}
      {tab === "reconciliation" && <ReconciliationTab run={run} version={version} />}

      {detail && <ItemDrawer itemId={detail} onClose={() => setDetail(null)} />}
      {dialog?.kind === "review" && (
        <PromptDialog title="Contrôle RH / comptable" label="Observations du contrôle" type="textarea" minLength={0}
                      hint={run.warningCount ? `${run.warningCount} avertissement(s) : les valider revient à confirmer qu'ils ont été examinés.` : undefined}
                      submitLabel={run.warningCount ? "Contrôlé — avertissements examinés" : "Contrôlé"} onClose={() => setDialog(null)}
                      onSubmit={async (note) => { setDialog(null); await step("review", note || null, true); }} />
      )}
      {(dialog?.kind === "validate" || dialog?.kind === "approve") && (
        <PromptDialog title={dialog.kind === "validate" ? "Valider la paie" : "Approbation finale"} label="Note" type="textarea" minLength={0}
                      hint={dialog.kind === "validate" ? "La paie devient verrouillée : toute correction passera par une régularisation."
                        : "Crée les écritures de paie et récupère les avances. Irréversible."}
                      submitLabel={dialog.kind === "validate" ? "Valider" : "Approuver"} onClose={() => setDialog(null)}
                      onSubmit={async (note) => { const k = dialog.kind as WorkflowStep; setDialog(null); await step(k, note || null); }} />
      )}
      {dialog?.kind === "return" && (
        <PromptDialog title="Renvoyer en correction" label="Ce qui doit être corrigé" type="textarea" submitLabel="Renvoyer" onClose={() => setDialog(null)}
                      onSubmit={async (note) => { setDialog(null); await step("return", note); }} />
      )}
      {dialog?.kind === "cancel" && (
        <PromptDialog title="Annuler la paie" label="Motif" type="textarea" minLength={5} submitLabel="Annuler la paie" onClose={() => setDialog(null)}
                      onSubmit={async (reason) => { setRun(await cancelRun(run.id, reason)); setDialog(null); }} />
      )}
      {dialog?.kind === "exclude" && dialog.item && (
        <PromptDialog title={`Exclure ${dialog.item.employeeName}`} label="Motif d'exclusion" minLength={5} submitLabel="Exclure" onClose={() => setDialog(null)}
                      onSubmit={async (reason) => { setRun(await excludeItem(dialog.item!.id, reason)); setDialog(null); }} />
      )}
      {(dialog?.kind === "pay" || dialog?.kind === "payOne") && (
        <PayRunDialog runId={run.id} currency={dialog.item?.currency ?? currency} itemIds={dialog.item ? [dialog.item.id] : null}
                      count={dialog.item ? 1 : unpaid.length} total={dialog.item ? dialog.item.net : unpaid.reduce((s, i) => s + i.net, 0)}
                      onClose={() => setDialog(null)}
                      onDone={(r) => {
                        setDialog(null);
                        toast.success(`${r.paid} salaire(s) payé(s)${r.alreadyPaid ? `, ${r.alreadyPaid} déjà payé(s)` : ""}`);
                        if (r.errors.length) toast.error(r.errors.join(" · "));
                        reload();
                      }} />
      )}
      {dialog?.kind === "adjust" && dialog.item && (
        <AdjustmentDialog itemId={dialog.item.id} employeeName={dialog.item.employeeName} runNumber={run.runNumber} currency={dialog.item.currency}
                          onClose={() => setDialog(null)} onDone={() => { setDialog(null); toast.success("Régularisation envoyée pour approbation"); }} />
      )}
    </div>
  );
}

/** Détail explicable d'une ligne de paie (§11, §40) : chaque gain et retenue avec sa source et sa règle, puis le calcul pas à pas. */
export function ItemDrawer({ itemId, onClose, self }: { itemId: string; onClose: () => void; self?: boolean }) {
  const [d, setD] = useState<PayrollItemDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { getItem(itemId, self).then(setD).catch((e) => setError(errorText(e))); }, [itemId, self]);
  const cur = d?.summary.currency ?? "HTG";
  return (
    <Drawer title={d ? `${d.summary.employeeName} — ${d.periodLabel}` : "Ligne de paie"} onClose={onClose}>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {!d ? <Spinner /> : (
        <div style={{ display: "grid", gap: 14 }}>
          {d.summary.anomalies.length > 0 && (
            <ul style={{ margin: 0, paddingLeft: 18 }}>
              {d.summary.anomalies.map((a, i) => <li key={i} style={{ color: a.severity === "blocking" ? "var(--nx-error)" : "var(--nx-warning)" }}>{a.message}</li>)}
            </ul>
          )}
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
            Salaire de référence {money(d.baseSalary, cur)} · {d.daysEmployed} j sur {d.periodDays} · absences {d.absenceDays} j ·
            taux journalier {money(d.dailyRate, cur)} · taux horaire {d.hourlyRate.toLocaleString("fr-FR")}
          </div>
          <SectionLabel>Gains</SectionLabel>
          <table className="nx-table"><tbody>
            {d.earnings.map((l) => <tr key={l.id}><td>{l.label}</td><td className="nx-num"><Money value={l.amount} currency={cur} /></td></tr>)}
            <tr><td><b>Brut</b></td><td className="nx-num"><b><Money value={d.summary.gross} currency={cur} /></b></td></tr>
          </tbody></table>
          <SectionLabel>Retenues</SectionLabel>
          <table className="nx-table"><tbody>
            {d.deductions.filter((l) => l.borneBy !== "employer").map((l) => (
              <tr key={l.id}>
                <td>{l.label}{l.plannedAmount != null && l.plannedAmount !== l.amount && <span style={{ color: "var(--nx-warning)" }}> (prévu {money(l.plannedAmount, cur)})</span>}</td>
                <td className="nx-num"><Money value={-l.amount} currency={cur} /></td>
              </tr>
            ))}
            <tr><td><b>Net à payer</b></td><td className="nx-num"><b><Money value={d.summary.net} currency={cur} /></b></td></tr>
          </tbody></table>
          {d.deductions.some((l) => l.borneBy === "employer") && (
            <>
              <SectionLabel>Cotisations patronales (hors net)</SectionLabel>
              <table className="nx-table"><tbody>
                {d.deductions.filter((l) => l.borneBy === "employer").map((l) => <tr key={l.id}><td>{l.label}</td><td className="nx-num"><Money value={l.amount} currency={cur} /></td></tr>)}
              </tbody></table>
            </>
          )}
          <SectionLabel>Calcul pas à pas</SectionLabel>
          <ol style={{ margin: 0, paddingLeft: 18, display: "grid", gap: 4 }}>
            {d.trace.map((t, i) => <li key={i}><b>{t.step}</b> — {t.detail}{t.amount != null && <> : <Money value={t.amount} currency={cur} /></>}</li>)}
          </ol>
          {d.payments.length > 0 && (
            <div>{d.payments.map((p) => <div key={p.id}>Paiement {p.paymentNumber} — {money(p.amount, p.currency)} le {dateTime(p.paidAt)} ({p.status === "completed" ? "effectué" : "annulé"})</div>)}</div>
          )}
        </div>
      )}
    </Drawer>
  );
}

function PaymentsTab({ runId, version, onChanged }: { runId: string; version: number; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [rows, setRows] = useState<SalaryPayment[] | null>(null);
  const [cancelling, setCancelling] = useState<SalaryPayment | null>(null);
  useEffect(() => { runPayments(runId).then(setRows).catch(() => setRows([])); }, [runId, version]);
  return (
    <Card>
      <SectionLabel>Paiements — référence, montant, date, bénéficiaire, moyen, statut (§24)</SectionLabel>
      {rows ? <DataTable<SalaryPayment> rows={rows} rowKey={(p) => p.id} exportTitle="Paiements de salaires" columns={[
        { key: "num", header: "Paiement", render: (p) => p.paymentNumber },
        { key: "emp", header: "Bénéficiaire", render: (p) => p.employeeName },
        { key: "amt", header: "Montant", numeric: true, render: (p) => <Money value={p.amount} currency={p.currency} /> },
        { key: "method", header: "Moyen", render: (p) => `${PAYMENT_METHODS[p.paymentMethod]}${p.beneficiaryAccount ? ` — ${p.beneficiaryAccount}` : ""}` },
        { key: "acct", header: "Compte", render: (p) => p.financialAccountName },
        { key: "ref", header: "Référence", render: (p) => p.reference ?? "—" },
        { key: "at", header: "Date", render: (p) => dateTime(p.paidAt) },
        { key: "st", header: "Statut", render: (p) => <Badge tone={p.status === "completed" ? "success" : "error"}>{p.status === "completed" ? "Effectué" : `Annulé : ${p.cancelReason}`}</Badge> },
        { key: "act", header: "", render: (p) => p.status === "completed" && hasPermission("payroll.pay")
            ? <Button size="sm" variant="ghost" onClick={() => setCancelling(p)}>Annuler (extourne)</Button> : null },
      ]} /> : <Spinner />}
      {cancelling && (
        <PromptDialog title={`Annuler ${cancelling.paymentNumber}`} label="Motif (virement rejeté, erreur de compte…)" type="textarea" minLength={5}
                      hint="L'écriture est extournée, les fonds reviennent sur le compte, le bulletin est annulé ; rien n'est supprimé."
                      submitLabel="Annuler le paiement" onClose={() => setCancelling(null)}
                      onSubmit={async (reason) => { await cancelSalaryPayment(cancelling.id, reason); setCancelling(null); onChanged(); }} />
      )}
    </Card>
  );
}

function PayslipsTab({ run, version }: { run: PayrollRun; version: number }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [rows, setRows] = useState<Payslip[] | null>(null);
  const load = useCallback(() => { runPayslips(run.id).then(setRows).catch(() => setRows([])); }, [run.id]);
  useEffect(load, [load, version]);
  return (
    <Card>
      <SectionLabel action={hasPermission("payroll.pay") && ["approved", "paid", "closed"].includes(run.status) &&
        <Button size="sm" variant="ghost" onClick={() => generatePayslips(run.id).then(setRows).catch((e) => toast.error(errorText(e)))}>Émettre les bulletins manquants</Button>}>
        Bulletins émis au paiement de chaque salaire (§25)
      </SectionLabel>
      {rows ? <DataTable<Payslip> rows={rows} rowKey={(p) => p.id} empty="Aucun bulletin : ils sont émis au paiement." exportTitle={false} columns={[
        { key: "num", header: "Bulletin", render: (p) => p.payslipNumber },
        { key: "emp", header: "Employé", render: (p) => p.employeeName },
        { key: "net", header: "Net", numeric: true, render: (p) => <Money value={p.net} currency={p.currency} /> },
        { key: "pay", header: "Paiement", render: (p) => p.paymentReference ?? "—" },
        { key: "hash", header: "Empreinte", render: (p) => <code title={p.sha256}>{p.sha256.slice(0, 12)}…</code> },
        { key: "st", header: "Statut", render: (p) => <Badge tone={p.status === "issued" ? "success" : "error"}>{p.status === "issued" ? "Émis" : "Annulé"}</Badge> },
        { key: "act", header: "", render: (p) => (
          <ActionMenu label="Bulletin" icon={<Icon.Printer size={14} />} items={[
            { label: "Imprimer (PDF)", run: () => printPayslip(p.id).catch((e) => toast.error(errorText(e))) },
            { label: "Télécharger le PDF", run: () => downloadPayslip(p.id, "pdf") },
            { label: "Télécharger en Excel", run: () => downloadPayslip(p.id, "xlsx") },
          ]} />
        ) },
      ]} /> : <Spinner />}
    </Card>
  );
}

function ReconciliationTab({ run, version }: { run: PayrollRun; version: number }) {
  const [r, setR] = useState<Reconciliation | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { reconcileRun(run.id).then(setR).catch((e) => setError(errorText(e))); }, [run.id, version]);
  if (error) return <Card><EmptyState title="Rapprochement" hint={error} /></Card>;
  if (!r) return <Card><Spinner /></Card>;
  return (
    <Card>
      <SectionLabel>
        Rapprochement RH ↔ Finance (§32) — {r.reconciled ? <Badge tone="success">Concordant</Badge> : <Badge tone="error">Écart à analyser</Badge>}
      </SectionLabel>
      <DataTable rows={r.lines} rowKey={(l) => `${l.key}-${l.currency}`} exportTitle="Rapprochement paie" columns={[
        { key: "label", header: "Poste", render: (l) => l.label },
        { key: "cur", header: "Devise", render: (l) => l.currency },
        { key: "hr", header: "Dossier RH", numeric: true, render: (l) => <Money value={l.hr} currency={l.currency} /> },
        { key: "ledger", header: "Grand livre", numeric: true, render: (l) => <Money value={l.ledger} currency={l.currency} /> },
        { key: "diff", header: "Écart", numeric: true, render: (l) => (l.difference === 0 ? "✓" : <b style={{ color: "var(--nx-error)" }}><Money value={l.difference} currency={l.currency} /></b>) },
      ]} />
      <SectionLabel>Par employé — net approuvé, payé, reste dû au grand livre</SectionLabel>
      <DataTable rows={r.employees} rowKey={(e) => e.employeeId} exportTitle={false} columns={[
        { key: "emp", header: "Employé", render: (e) => e.employeeName },
        { key: "net", header: "Net approuvé", numeric: true, render: (e) => <Money value={e.netApproved} currency={e.currency} /> },
        { key: "paid", header: "Payé", numeric: true, render: (e) => <Money value={e.paid} currency={e.currency} /> },
        { key: "ledger", header: "Dû au grand livre", numeric: true, render: (e) => <Money value={e.ledgerPayable} currency={e.currency} /> },
        { key: "ok", header: "", render: (e) => (e.ok ? "✓" : <Badge tone="error">Écart</Badge>) },
      ]} />
    </Card>
  );
}
