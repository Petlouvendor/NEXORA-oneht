import { useCallback, useEffect, useState } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, DataTable, EmptyState, KpiCard, Money, SectionLabel, Spinner, Tabs, type Column } from "@/shared/ui";
import { ActionMenu } from "@/shared/ExportMenu";
import { Icon } from "@/shared/icons";
import { dateShort, dateTime, money, todayIso } from "@/shared/format";
import {
  CONTRACT_TYPES, DOCUMENT_TYPES, EMPLOYEE_STATUS, FREQUENCIES, PAY_BASES, PAYMENT_METHODS, downloadEmployeeDocument, downloadPayslip,
  employeeHistory, employeePayslips, endContract, endPayElement, getEmployee, getSensitive, listContracts, listDocuments, listPayElements,
  printPayslip, salaryHistory, setEmployeeStatus,
  type Contract, type Employee, type EmployeeDocument, type HistoryEntry, type PayElement, type Payslip, type SalaryHistoryEntry,
} from "@/hr/hrApi";
import {
  AdvanceRequestDialog, ContractDialog, DocumentDialog, EmployeeDialog, ExpenseClaimDialog, LeaveDialog, OvertimeDialog, PayElementDialog,
  PromptDialog, SalaryChangeDialog, errorText,
} from "@/hr/HrDialogs";
import { AdvancesPanel } from "@/hr/HrPanels";
import { statusTone } from "@/hr/HrPage";

/**
 * Dossier d'un employé (§7, §40) : un responsable autorisé y lit son contrat, sa rémunération applicable et son
 * historique, chaque paie, ses avances et récupérations, le solde restant, ses paiements et ses bulletins.
 * Sans <c>payroll.view_salaries</c>, le serveur ne renvoie aucun montant — l'écran n'affiche que ce qu'il reçoit.
 */
export function EmployeePage() {
  const { id = "" } = useParams();
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [params, setParams] = useSearchParams();
  const [employee, setEmployee] = useState<Employee | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [dialog, setDialog] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  const reload = useCallback(() => {
    getEmployee(id).then(setEmployee).catch((e) => setError(errorText(e, "Employé introuvable")));
    setVersion((v) => v + 1);
  }, [id]);
  useEffect(reload, [reload]);
  if (error && !employee) return <Card><EmptyState title="Dossier employé" hint={error} /></Card>;
  if (!employee) return <Card><Spinner /></Card>;

  const salaries = employee.canViewSalaries;
  const tab = params.get("tab") ?? "file";
  const tabs = [
    { id: "file", label: "Dossier" },
    { id: "contracts", label: "Contrats & rémunération" },
    ...(salaries ? [{ id: "elements", label: "Éléments de paie" }, { id: "history", label: "Historique financier" }, { id: "payslips", label: "Bulletins" }] : []),
    { id: "advances", label: "Avances & prêts" },
    ...(employee.canManage || salaries ? [{ id: "documents", label: "Documents" }] : []),
  ];
  const done = (msg: string) => { setDialog(null); toast.success(msg); reload(); };
  const comp = employee.compensation;

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "flex", justifyContent: "space-between", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}><Link to="/hr?tab=employees">Employés</Link> · {employee.employeeNumber}</div>
          <h1 style={{ margin: "4px 0 0" }}>{employee.firstName} {employee.lastName}{" "}
            <Badge tone={statusTone(employee.status)}>{EMPLOYEE_STATUS[employee.status]}</Badge></h1>
          <div style={{ color: "var(--nx-text-2)" }}>{[employee.jobTitle, employee.departmentName].filter(Boolean).join(" — ") || "—"}</div>
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {employee.canManage && <Button variant="ghost" onClick={() => setDialog("edit")}>Modifier</Button>}
          {hasPermission("payroll.advance_request") && employee.status !== "terminated" && <Button onClick={() => setDialog("advance")}>Demander une avance</Button>}
          {hasPermission("hr.manage_time") && (
            <ActionMenu label="Saisir" items={[
              { label: "Congé / absence", run: () => setDialog("leave") },
              { label: "Heures supplémentaires", run: () => setDialog("overtime") },
              { label: "Note de frais", run: () => setDialog("expense") },
            ]} />
          )}
          {employee.canManage && employee.status !== "terminated" && (
            <ActionMenu label="Statut" items={[
              ...(employee.status !== "active" ? [{ label: "Réactiver", run: () => setEmployeeStatus(id, "active").then(reload) }] : []),
              ...(employee.status !== "suspended" ? [{ label: "Suspendre", run: () => setEmployeeStatus(id, "suspended").then(reload) }] : []),
              { label: "Sortie de l'entreprise…", run: () => setDialog("terminate") },
            ]} />
          )}
        </div>
      </div>

      {salaries && comp && (
        <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(190px, 1fr))" }}>
          <KpiCard label={`Salaire (${PAY_BASES[comp.payBasis ?? "monthly"] ?? "—"})`} value={comp.baseSalary == null ? "—" : money(comp.baseSalary, comp.currency ?? undefined)} />
          <KpiCard label="Contrat" value={comp.contractType ? CONTRACT_TYPES[comp.contractType] : "—"} />
          <KpiCard label="Avances à récupérer" value={money(comp.advanceBalance, comp.currency ?? undefined)} tone={comp.advanceBalance > 0 ? "warning" : undefined} />
          <KpiCard label="Prêts à récupérer" value={money(comp.loanBalance, comp.currency ?? undefined)} />
        </div>
      )}

      <Tabs tabs={tabs} active={tab} onChange={(t) => setParams((p) => { p.set("tab", t); return p; }, { replace: true })} />
      {tab === "file" && <FilePanel employee={employee} />}
      {tab === "contracts" && <ContractsPanel employee={employee} version={version} onAction={setDialog} onChanged={reload} />}
      {tab === "elements" && <ElementsPanel employee={employee} version={version} onAdd={() => setDialog("element")} />}
      {tab === "history" && <HistoryPanel employeeId={id} version={version} />}
      {tab === "payslips" && <PayslipsPanel employeeId={id} version={version} />}
      {tab === "advances" && <AdvancesPanel employeeId={id} key={version} />}
      {tab === "documents" && <DocumentsPanel employee={employee} version={version} onAdd={() => setDialog("document")} />}

      {dialog === "edit" && <EmployeeDialog employee={employee} canSetSalary={false} onClose={() => setDialog(null)} onDone={() => done("Dossier mis à jour")} />}
      {dialog === "advance" && <AdvanceRequestDialog employeeId={id} onClose={() => setDialog(null)} onDone={() => done("Demande d'avance envoyée pour validation")} />}
      {dialog === "leave" && <LeaveDialog employeeId={id} onClose={() => setDialog(null)} onDone={() => done("Absence enregistrée")} />}
      {dialog === "overtime" && <OvertimeDialog employeeId={id} onClose={() => setDialog(null)} onDone={() => done("Heures envoyées pour validation")} />}
      {dialog === "expense" && <ExpenseClaimDialog employeeId={id} onClose={() => setDialog(null)} onDone={() => done("Note de frais soumise")} />}
      {dialog === "contract" && <ContractDialog employeeId={id} canSetSalary={hasPermission("payroll.manage_compensation")} onClose={() => setDialog(null)} onDone={() => done("Contrat créé")} />}
      {dialog === "salary" && <SalaryChangeDialog employeeId={id} current={comp?.baseSalary ?? null} onClose={() => setDialog(null)} onDone={() => done("Nouveau salaire enregistré")} />}
      {dialog === "element" && <PayElementDialog employeeId={id} onClose={() => setDialog(null)} onDone={() => done("Élément ajouté")} />}
      {dialog === "document" && <DocumentDialog employeeId={id} onClose={() => setDialog(null)} onDone={() => done("Document déposé")} />}
      {dialog === "terminate" && (
        <PromptDialog title="Sortie de l'employé" label="Date de sortie" type="date" initial={todayIso()}
                      submitLabel="Continuer" onClose={() => setDialog(null)}
                      onSubmit={async (date) => { setDialog(`terminate:${date}`); }} />
      )}
      {dialog?.startsWith("terminate:") && (
        <PromptDialog title="Motif de sortie" label="Motif" type="textarea" minLength={3} submitLabel="Enregistrer la sortie" onClose={() => setDialog(null)}
                      onSubmit={async (reason) => { await setEmployeeStatus(id, "terminated", dialog.slice(10), reason); done("Sortie enregistrée — dossier et historique conservés"); }} />
      )}
    </div>
  );
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div style={{ display: "grid", gridTemplateColumns: "200px 1fr", gap: 8, padding: "6px 0", borderBottom: "1px solid var(--nx-border)" }}>
      <span style={{ color: "var(--nx-text-2)" }}>{label}</span><span>{value ?? "—"}</span>
    </div>
  );
}

function FilePanel({ employee: e }: { employee: Employee }) {
  const [sensitive, setSensitive] = useState<Awaited<ReturnType<typeof getSensitive>> | null>(null);
  const [error, setError] = useState<string | null>(null);
  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "repeat(auto-fit, minmax(340px, 1fr))" }}>
      <Card>
        <SectionLabel>Identité & contact</SectionLabel>
        <Row label="Matricule" value={e.employeeNumber} />
        <Row label="Date de naissance" value={e.birthDate ? dateShort(e.birthDate) : null} />
        <Row label="Téléphone" value={e.phone} />
        <Row label="Email" value={e.email} />
        <Row label="Adresse" value={[e.address, e.city].filter(Boolean).join(", ") || null} />
        <Row label="Contact d'urgence" value={[e.emergencyContactName, e.emergencyContactPhone].filter(Boolean).join(" — ") || null} />
        <Row label="Compte utilisateur" value={e.userEmail ?? "Non rattaché (pas d'espace employé)"} />
      </Card>
      <Card>
        <SectionLabel>Emploi</SectionLabel>
        <Row label="Poste" value={e.jobTitle} />
        <Row label="Département" value={e.departmentName} />
        <Row label="Responsable" value={e.managerName} />
        <Row label="Lieu de travail" value={e.establishmentName} />
        <Row label="Entrée" value={dateShort(e.hireDate)} />
        {e.terminationDate && <Row label="Sortie" value={`${dateShort(e.terminationDate)} — ${e.terminationReason ?? ""}`} />}
        <Row label="Créé par" value={`${e.createdByName ?? "—"}, ${dateTime(e.createdAt)}`} />
      </Card>
      <Card>
        <SectionLabel action={e.canViewSensitive && !sensitive && (
          <Button size="sm" variant="ghost" onClick={() => getSensitive(e.id).then(setSensitive).catch((x) => setError(errorText(x)))}>Afficher en clair (journalisé)</Button>
        )}>Paiement & identifiants (protégés)</SectionLabel>
        <Row label="Moyen de paiement" value={PAYMENT_METHODS[e.paymentMethod]} />
        <Row label="Banque" value={e.bankName} />
        <Row label="Compte bancaire" value={sensitive?.bankAccount ?? e.bankAccountMasked} />
        <Row label="Mobile money" value={sensitive?.mobileMoney ?? e.mobileMoneyMasked} />
        <Row label="Identifiant fiscal" value={sensitive?.taxId ?? e.taxIdMasked} />
        <Row label="Sécurité sociale" value={sensitive?.socialId ?? e.socialIdMasked} />
        {error && <div role="alert" className="nx-error-text">{error}</div>}
      </Card>
    </div>
  );
}

function ContractsPanel({ employee, version, onAction, onChanged }: {
  employee: Employee; version: number; onAction: (d: string) => void; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [contracts, setContracts] = useState<Contract[] | null>(null);
  const [history, setHistory] = useState<SalaryHistoryEntry[] | null>(null);
  const [ending, setEnding] = useState<Contract | null>(null);
  useEffect(() => {
    listContracts(employee.id).then(setContracts).catch(() => setContracts([]));
    if (employee.canViewSalaries) salaryHistory(employee.id).then(setHistory).catch(() => setHistory([]));
  }, [employee.id, employee.canViewSalaries, version]);
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={employee.canManage && employee.status !== "terminated" && <Button size="sm" onClick={() => onAction("contract")}>Nouveau contrat</Button>}>
          Contrats — un contrat terminé n'est jamais supprimé (§8)
        </SectionLabel>
        {contracts ? <DataTable<Contract> rows={contracts} rowKey={(c) => c.id} exportTitle="Contrats" columns={[
          { key: "num", header: "N°", render: (c) => c.contractNumber },
          { key: "type", header: "Type", render: (c) => CONTRACT_TYPES[c.contractType] },
          { key: "dates", header: "Période", render: (c) => `${dateShort(c.startDate)} → ${c.endDate ? dateShort(c.endDate) : "…"}` },
          { key: "freq", header: "Paie", render: (c) => `${FREQUENCIES[c.payFrequency]} — ${PAY_BASES[c.payBasis]} (${c.currency})` },
          { key: "h", header: "h/sem.", numeric: true, render: (c) => c.weeklyHours },
          { key: "st", header: "Statut", render: (c) => <Badge tone={statusTone(c.status === "ended" ? "closed" : c.status)}>{c.status}</Badge> },
          { key: "end", header: "Fin", render: (c) => c.endReason ?? "—" },
          { key: "act", header: "", render: (c) => c.status === "active" && employee.canManage ? <Button size="sm" variant="ghost" onClick={() => setEnding(c)}>Terminer</Button> : null },
        ]} /> : <Spinner />}
      </Card>
      {employee.canViewSalaries && (
        <Card>
          <SectionLabel action={hasPermission("payroll.manage_compensation") && <Button size="sm" onClick={() => onAction("salary")}>Modifier le salaire</Button>}>
            Historique de rémunération — ancien et nouveau salaire, date d'effet, auteur et motif (§8)
          </SectionLabel>
          {history ? <DataTable<SalaryHistoryEntry> rows={history} rowKey={(h) => h.id} exportTitle="Historique de rémunération" columns={[
            { key: "eff", header: "Date d'effet", render: (h) => dateShort(h.effectiveDate) },
            { key: "amt", header: "Nouveau", numeric: true, render: (h) => <b><Money value={h.amount} currency={h.currency} /></b> },
            { key: "prev", header: "Ancien", numeric: true, render: (h) => (h.previousAmount == null ? "—" : <Money value={h.previousAmount} currency={h.currency} />) },
            { key: "basis", header: "Base", render: (h) => PAY_BASES[h.payBasis] },
            { key: "ctr", header: "Contrat", render: (h) => h.contractNumber },
            { key: "reason", header: "Motif", render: (h) => h.reason },
            { key: "by", header: "Par", render: (h) => `${h.createdByName ?? "—"}, ${dateTime(h.createdAt)}` },
          ]} /> : <Spinner />}
        </Card>
      )}
      {ending && (
        <PromptDialog title={`Terminer ${ending.contractNumber}`} label="Motif de fin" type="textarea" minLength={3} submitLabel="Terminer le contrat"
                      onClose={() => setEnding(null)}
                      onSubmit={async (reason) => {
                        await endContract(ending.id, todayIso(), reason);
                        setEnding(null); toast.success("Contrat terminé — conservé dans l'historique"); onChanged();
                      }} />
      )}
    </div>
  );
}

function ElementsPanel({ employee, version, onAdd }: { employee: Employee; version: number; onAdd: () => void }) {
  const { hasPermission } = useAuth();
  const [rows, setRows] = useState<PayElement[] | null>(null);
  const load = useCallback(() => { listPayElements(employee.id).then(setRows).catch(() => setRows([])); }, [employee.id]);
  useEffect(load, [load, version]);
  const method = (e: PayElement) => ({ fixed: "Fixe par paie", per_day_worked: "Par jour travaillé", percent_of_base: "% du salaire", percent_of_sales: "% des ventes" }[e.calcMethod] ?? e.calcMethod);
  return (
    <Card>
      <SectionLabel action={hasPermission("payroll.manage_compensation") && <Button size="sm" onClick={onAdd}>Ajouter</Button>}>Primes récurrentes, indemnités, commissions, avantages (§20)</SectionLabel>
      {rows ? <DataTable<PayElement> rows={rows} rowKey={(e) => e.id} exportTitle="Éléments de paie" columns={[
        { key: "name", header: "Élément", render: (e) => `${e.componentName}${e.kind === "deduction" ? " (retenue)" : ""}` },
        { key: "method", header: "Calcul", render: method },
        { key: "value", header: "Valeur", numeric: true, render: (e) => (e.rate != null ? `${e.rate} %` : <Money value={e.amount ?? 0} currency={e.currency} />) },
        { key: "dates", header: "Validité", render: (e) => `${dateShort(e.startDate)} → ${e.endDate ? dateShort(e.endDate) : "…"}` },
        { key: "st", header: "Statut", render: (e) => <Badge tone={e.status === "active" ? "success" : undefined}>{e.status === "active" ? "Actif" : "Terminé"}</Badge> },
        { key: "act", header: "", render: (e) => e.status === "active" && hasPermission("payroll.manage_compensation")
            ? <Button size="sm" variant="ghost" onClick={() => endPayElement(e.id, todayIso()).then(load)}>Terminer</Button> : null },
      ]} /> : <Spinner />}
    </Card>
  );
}

export function HistoryPanel({ employeeId, version, self }: { employeeId: string; version?: number; self?: boolean }) {
  const [rows, setRows] = useState<HistoryEntry[] | null>(null);
  useEffect(() => { employeeHistory(employeeId, self).then(setRows).catch(() => setRows([])); }, [employeeId, version, self]);
  const columns: Array<Column<HistoryEntry>> = [
    { key: "at", header: "Date", render: (h) => dateTime(h.occurredAt) },
    { key: "title", header: "Opération", render: (h) => <b>{h.title}</b> },
    { key: "detail", header: "Détail", render: (h) => h.detail ?? "—" },
    { key: "amt", header: "Montant", numeric: true, render: (h) => (h.amount == null ? "—" : <Money value={h.amount} currency={h.currency ?? "HTG"} />) },
    { key: "ref", header: "Référence", render: (h) => h.reference ?? "—" },
    { key: "by", header: "Par", render: (h) => h.actorName ?? "—" },
  ];
  return (
    <Card>
      <SectionLabel>Historique financier — salaires, paiements, avances, récupérations, frais, régularisations, bulletins (§26)</SectionLabel>
      {rows ? <DataTable columns={columns} rows={rows} rowKey={(h) => `${h.type}-${h.sourceId}-${h.occurredAt}`} empty="Aucune opération." exportTitle="Historique financier" /> : <Spinner />}
    </Card>
  );
}

export function PayslipsPanel({ employeeId, version, self }: { employeeId: string; version?: number; self?: boolean }) {
  const toast = useToast();
  const [rows, setRows] = useState<Payslip[] | null>(null);
  useEffect(() => { employeePayslips(employeeId, self).then(setRows).catch(() => setRows([])); }, [employeeId, version, self]);
  return (
    <Card>
      <SectionLabel>Bulletins de paie — identifiant unique et empreinte vérifiable (§25)</SectionLabel>
      {rows ? <DataTable<Payslip> rows={rows} rowKey={(p) => p.id} empty="Aucun bulletin émis." exportTitle={false} columns={[
        { key: "num", header: "Bulletin", render: (p) => p.payslipNumber },
        { key: "period", header: "Période", render: (p) => p.periodLabel },
        { key: "gross", header: "Brut", numeric: true, render: (p) => <Money value={p.gross} currency={p.currency} /> },
        { key: "net", header: "Net", numeric: true, render: (p) => <b><Money value={p.net} currency={p.currency} /></b> },
        { key: "pay", header: "Paiement", render: (p) => p.paymentReference ?? "—" },
        { key: "st", header: "Statut", render: (p) => <Badge tone={p.status === "issued" ? "success" : "error"}>{p.status === "issued" ? "Émis" : "Annulé"}</Badge> },
        { key: "act", header: "", render: (p) => (
          <ActionMenu label="Bulletin" icon={<Icon.Printer size={14} />} items={[
            { label: "Imprimer (PDF)", run: () => printPayslip(p.id, self).catch((e) => toast.error(errorText(e))) },
            { label: "Télécharger le PDF", run: () => downloadPayslip(p.id, "pdf", self) },
            ...(self ? [] : [{ label: "Télécharger en Excel", run: () => downloadPayslip(p.id, "xlsx") }]),
          ]} />
        ) },
      ]} /> : <Spinner />}
    </Card>
  );
}

export function DocumentsPanel({ employee, version, onAdd, self }: { employee: { id: string; canManage?: boolean }; version?: number; onAdd?: () => void; self?: boolean }) {
  const [rows, setRows] = useState<EmployeeDocument[] | null>(null);
  useEffect(() => { listDocuments(employee.id, self).then(setRows).catch(() => setRows([])); }, [employee.id, version, self]);
  return (
    <Card>
      <SectionLabel action={employee.canManage && onAdd && <Button size="sm" onClick={onAdd}>Déposer</Button>}>Documents RH confidentiels</SectionLabel>
      {rows ? <DataTable<EmployeeDocument> rows={rows} rowKey={(d) => d.id} empty="Aucun document." exportTitle={false} columns={[
        { key: "type", header: "Type", render: (d) => DOCUMENT_TYPES[d.documentType] },
        { key: "title", header: "Titre", render: (d) => d.title },
        { key: "file", header: "Fichier", render: (d) => `${d.fileName} (${Math.ceil(d.sizeBytes / 1024)} Ko)` },
        { key: "vis", header: "Visible employé", render: (d) => (d.visibleToEmployee ? "Oui" : "Non") },
        { key: "by", header: "Déposé", render: (d) => `${d.uploadedByName ?? "—"}, ${dateTime(d.createdAt)}` },
        { key: "st", header: "", render: (d) => (d.archivedAt ? <Badge>Archivé</Badge> : null) },
        { key: "act", header: "", render: (d) => <Button size="sm" variant="ghost" onClick={() => downloadEmployeeDocument(d.id, self)}>Télécharger</Button> },
      ]} /> : <Spinner />}
    </Card>
  );
}
