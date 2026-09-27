import { useEffect, useMemo, useState, type ReactNode } from "react";
import { ApiError } from "@/core/apiClient";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import { money } from "@/shared/format";
import {
  ADVANCE_KINDS, CONTRACT_TYPES, DOCUMENT_TYPES, EXPENSE_CATEGORIES, FREQUENCIES, LEAVE_TYPES, OVERTIME_TYPES, PAY_BASES, PAYMENT_METHODS,
  RECOVERY_METHODS, RULE_BASES, RULE_TYPES, addPayElement, approveAdvance, changeSalary, createContract, createEmployee, createPeriod,
  createRule, fileToBase64, listComponents, listDepartments, listEmployees, newRuleVersion, payAdvance, paymentAccounts, payRun,
  previewAdvance, recordAttendance, recordOvertime, repayAdvance, requestAdjustment, requestAdvance, requestLeave, rescheduleAdvance,
  submitExpenseClaim, submitVariable, updateEmployee, updateRule, uploadDocument,
  type Advance, type AdvanceCheck, type ContractInput, type Department, type Employee, type EmployeeInput, type EmployeeListItem,
  type PayComponent, type PaymentAccount, type PayrollPeriod, type PayrollRule, type PayRunResult, type RuleBracket, type RuleInput,
} from "@/hr/hrApi";

export const errorText = (e: unknown, fallback = "Opération refusée") =>
  e instanceof ApiError ? e.problem.detail ?? e.problem.title : fallback;

const today = () => new Date().toISOString().slice(0, 10);
const num = (v: string) => (v.trim() === "" ? null : Number(v.replace(",", ".")));
const Grid = ({ children, cols = 2 }: { children: ReactNode; cols?: number }) => (
  <div style={{ display: "grid", gridTemplateColumns: `repeat(auto-fit, minmax(${cols > 2 ? 160 : 220}px, 1fr))`, gap: 12 }}>{children}</div>
);

/** Formulaire modal standard : erreur serveur affichée telle quelle, bouton occupé pendant l'envoi. */
function FormModal({ title, onClose, submitLabel, onSubmit, disabled, children, wide }: {
  title: string; onClose: () => void; submitLabel: string; onSubmit: () => Promise<void>; disabled?: boolean; children: ReactNode; wide?: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submit = async () => {
    setBusy(true);
    setError(null);
    try { await onSubmit(); } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  };
  return (
    <Modal title={title} onClose={onClose} footer={<>
      <Button variant="ghost" onClick={onClose}>Annuler</Button>
      <Button onClick={submit} loading={busy} disabled={disabled}>{submitLabel}</Button>
    </>}>
      <div style={{ display: "grid", gap: 12, minWidth: wide ? "min(760px, 92vw)" : "min(520px, 92vw)" }}>
        {children}
        {error && <div role="alert" className="nx-error-text">{error}</div>}
      </div>
    </Modal>
  );
}

function useAccounts() {
  const [accounts, setAccounts] = useState<PaymentAccount[]>([]);
  useEffect(() => { paymentAccounts().then(setAccounts).catch(() => setAccounts([])); }, []);
  return accounts;
}

// ================================================================== Employé, contrat, rémunération (§7, §8)

export function EmployeeDialog({ employee, canSetSalary, onClose, onDone }: {
  employee?: Employee; canSetSalary: boolean; onClose: () => void; onDone: (e: Employee) => void;
}) {
  const [f, setF] = useState<EmployeeInput>(() => employee ? {
    employeeNumber: employee.employeeNumber, firstName: employee.firstName, lastName: employee.lastName, birthDate: employee.birthDate,
    phone: employee.phone, email: employee.email, address: employee.address, city: employee.city,
    emergencyContactName: employee.emergencyContactName, emergencyContactPhone: employee.emergencyContactPhone, jobTitle: employee.jobTitle,
    departmentId: employee.departmentId, managerEmployeeId: employee.managerEmployeeId, hireDate: employee.hireDate,
    paymentMethod: employee.paymentMethod, bankName: employee.bankName, notes: employee.notes,
  } : { firstName: "", lastName: "", hireDate: today(), paymentMethod: "bank_transfer" });
  const [withContract, setWithContract] = useState(!employee);
  const [c, setC] = useState<ContractInput>({ contractType: "permanent", startDate: today(), payFrequency: "monthly", payBasis: "monthly", currency: "HTG", weeklyHours: 48 });
  const [departments, setDepartments] = useState<Department[]>([]);
  const [managers, setManagers] = useState<EmployeeListItem[]>([]);
  useEffect(() => {
    listDepartments().then(setDepartments).catch(() => {});
    listEmployees({ status: "active" }).then(setManagers).catch(() => {});
  }, []);
  const set = (k: keyof EmployeeInput) => (e: { target: { value: string } }) => setF((x) => ({ ...x, [k]: e.target.value || null }));
  const setContract = (k: keyof ContractInput) => (e: { target: { value: string } }) => setC((x) => ({ ...x, [k]: e.target.value || null }));

  return (
    <FormModal wide title={employee ? `Modifier ${employee.firstName} ${employee.lastName}` : "Nouvel employé"} onClose={onClose}
               submitLabel={employee ? "Enregistrer" : "Créer le dossier"} disabled={!f.firstName || !f.lastName || !f.hireDate}
               onSubmit={async () => {
                 const body = { ...f, contract: !employee && withContract ? { ...c, startDate: c.startDate || f.hireDate } : null };
                 onDone(employee ? await updateEmployee(employee.id, body) : await createEmployee(body));
               }}>
      <Grid cols={3}>
        <Field label="Prénom" required><TextInput value={f.firstName} onChange={set("firstName")} /></Field>
        <Field label="Nom" required><TextInput value={f.lastName} onChange={set("lastName")} /></Field>
        <Field label="Matricule" hint="Vide = numéro automatique"><TextInput value={f.employeeNumber ?? ""} onChange={set("employeeNumber")} /></Field>
        <Field label="Date de naissance"><TextInput type="date" value={f.birthDate ?? ""} onChange={set("birthDate")} /></Field>
        <Field label="Téléphone"><TextInput value={f.phone ?? ""} onChange={set("phone")} /></Field>
        <Field label="Email"><TextInput type="email" value={f.email ?? ""} onChange={set("email")} /></Field>
        <Field label="Adresse"><TextInput value={f.address ?? ""} onChange={set("address")} /></Field>
        <Field label="Ville"><TextInput value={f.city ?? ""} onChange={set("city")} /></Field>
        <Field label="Contact d'urgence"><TextInput value={f.emergencyContactName ?? ""} onChange={set("emergencyContactName")} /></Field>
        <Field label="Téléphone d'urgence"><TextInput value={f.emergencyContactPhone ?? ""} onChange={set("emergencyContactPhone")} /></Field>
        <Field label="Poste"><TextInput value={f.jobTitle ?? ""} onChange={set("jobTitle")} /></Field>
        <Field label="Département">
          <Select value={f.departmentId ?? ""} onChange={set("departmentId")}>
            <option value="">—</option>
            {departments.map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </Select>
        </Field>
        <Field label="Responsable">
          <Select value={f.managerEmployeeId ?? ""} onChange={set("managerEmployeeId")}>
            <option value="">—</option>
            {managers.filter((m) => m.id !== employee?.id).map((m) => <option key={m.id} value={m.id}>{m.firstName} {m.lastName}</option>)}
          </Select>
        </Field>
        <Field label="Date d'entrée" required><TextInput type="date" value={f.hireDate} onChange={set("hireDate")} /></Field>
        <Field label="Moyen de paiement">
          <Select value={f.paymentMethod ?? "bank_transfer"} onChange={set("paymentMethod")}>
            {Object.entries(PAYMENT_METHODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Banque"><TextInput value={f.bankName ?? ""} onChange={set("bankName")} /></Field>
        <Field label="N° de compte bancaire" hint={employee?.bankAccountMasked ? `Actuel : ${employee.bankAccountMasked} — vide = inchangé` : "Chiffré, jamais affiché en clair"}>
          <TextInput value={f.bankAccount ?? ""} onChange={set("bankAccount")} autoComplete="off" />
        </Field>
        <Field label="N° mobile money" hint={employee?.mobileMoneyMasked ? `Actuel : ${employee.mobileMoneyMasked}` : undefined}>
          <TextInput value={f.mobileMoney ?? ""} onChange={set("mobileMoney")} autoComplete="off" />
        </Field>
        <Field label="Identifiant fiscal (NIF)" hint={employee?.taxIdMasked ? `Actuel : ${employee.taxIdMasked}` : undefined}>
          <TextInput value={f.taxId ?? ""} onChange={set("taxId")} autoComplete="off" />
        </Field>
        <Field label="N° de sécurité sociale" hint={employee?.socialIdMasked ? `Actuel : ${employee.socialIdMasked}` : undefined}>
          <TextInput value={f.socialId ?? ""} onChange={set("socialId")} autoComplete="off" />
        </Field>
      </Grid>
      <Field label="Notes"><textarea className="nx-textarea" rows={2} value={f.notes ?? ""} onChange={set("notes")} /></Field>
      {!employee && (
        <>
          <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <input type="checkbox" checked={withContract} onChange={(e) => setWithContract(e.target.checked)} /> Créer le premier contrat
          </label>
          {withContract && <ContractFields c={c} set={setContract} canSetSalary={canSetSalary} />}
        </>
      )}
    </FormModal>
  );
}

function ContractFields({ c, set, canSetSalary }: {
  c: ContractInput; set: (k: keyof ContractInput) => (e: { target: { value: string } }) => void; canSetSalary: boolean;
}) {
  return (
    <Grid cols={3}>
      <Field label="Type de contrat">
        <Select value={c.contractType} onChange={set("contractType")}>
          {Object.entries(CONTRACT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </Select>
      </Field>
      <Field label="Début" required><TextInput type="date" value={c.startDate} onChange={set("startDate")} /></Field>
      <Field label="Fin" hint="Obligatoire pour un CDD"><TextInput type="date" value={c.endDate ?? ""} onChange={set("endDate")} /></Field>
      <Field label="Fin de période d'essai"><TextInput type="date" value={c.trialEndDate ?? ""} onChange={set("trialEndDate")} /></Field>
      <Field label="Fréquence de paie">
        <Select value={c.payFrequency} onChange={set("payFrequency")}>
          {Object.entries(FREQUENCIES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </Select>
      </Field>
      <Field label="Base de rémunération">
        <Select value={c.payBasis} onChange={set("payBasis")}>
          {Object.entries(PAY_BASES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </Select>
      </Field>
      <Field label="Devise"><TextInput value={c.currency} onChange={set("currency")} maxLength={3} /></Field>
      <Field label="Heures / semaine"><TextInput type="number" value={c.weeklyHours ?? ""} onChange={set("weeklyHours")} /></Field>
      <Field label="Poste au contrat"><TextInput value={c.jobTitle ?? ""} onChange={set("jobTitle")} /></Field>
      {canSetSalary && (
        <Field label="Salaire de base" hint="Selon la base choisie (mois, jour ou heure)">
          <TextInput type="number" min={0} value={c.baseSalary ?? ""} onChange={set("baseSalary")} />
        </Field>
      )}
      <Field label="Avantages"><TextInput value={c.benefits ?? ""} onChange={set("benefits")} /></Field>
      <Field label="Conditions"><TextInput value={c.conditions ?? ""} onChange={set("conditions")} /></Field>
    </Grid>
  );
}

export function ContractDialog({ employeeId, canSetSalary, onClose, onDone }: {
  employeeId: string; canSetSalary: boolean; onClose: () => void; onDone: () => void;
}) {
  const [c, setC] = useState<ContractInput>({ contractType: "permanent", startDate: today(), payFrequency: "monthly", payBasis: "monthly", currency: "HTG", weeklyHours: 48 });
  const set = (k: keyof ContractInput) => (e: { target: { value: string } }) => setC((x) => ({ ...x, [k]: e.target.value || null }));
  return (
    <FormModal wide title="Nouveau contrat" onClose={onClose} submitLabel="Créer le contrat"
               onSubmit={async () => {
                 await createContract(employeeId, { ...c, baseSalary: c.baseSalary ? Number(c.baseSalary) : null, weeklyHours: c.weeklyHours ? Number(c.weeklyHours) : null });
                 onDone();
               }}>
      <p style={{ margin: 0, color: "var(--nx-text-2)" }}>Le contrat actif à durée indéterminée précédent est clos la veille : il n'est jamais supprimé (§8).</p>
      <ContractFields c={c} set={set} canSetSalary={canSetSalary} />
    </FormModal>
  );
}

export function SalaryChangeDialog({ employeeId, current, onClose, onDone }: {
  employeeId: string; current: number | null; onClose: () => void; onDone: () => void;
}) {
  const [date, setDate] = useState(today());
  const [amount, setAmount] = useState("");
  const [hourly, setHourly] = useState("");
  const [reason, setReason] = useState("");
  return (
    <FormModal title="Modifier le salaire" onClose={onClose} submitLabel="Enregistrer le nouveau salaire"
               disabled={!amount || reason.trim().length < 3}
               onSubmit={async () => { await changeSalary(employeeId, { effectiveDate: date, amount: Number(amount), hourlyRate: num(hourly), reason }); onDone(); }}>
      <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
        L'ancien salaire{current != null ? ` (${money(current)})` : ""} est conservé avec sa date d'effet : l'historique n'est jamais écrasé (§8).
        Une date d'effet dans une paie déjà validée est refusée — utiliser une régularisation.
      </p>
      <Grid>
        <Field label="Date d'effet" required><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="Nouveau montant" required><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <Field label="Taux horaire imposé" hint="Vide = dérivé du salaire"><TextInput type="number" min={0} value={hourly} onChange={(e) => setHourly(e.target.value)} /></Field>
      </Grid>
      <Field label="Motif" required hint="Consigné dans l'historique et l'audit"><textarea className="nx-textarea" rows={2} value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
    </FormModal>
  );
}

export function PayElementDialog({ employeeId, onClose, onDone }: { employeeId: string; onClose: () => void; onDone: () => void }) {
  const [components, setComponents] = useState<PayComponent[]>([]);
  const [code, setCode] = useState("TRANSPORT");
  const [method, setMethod] = useState("fixed");
  const [value, setValue] = useState("");
  const [start, setStart] = useState(today());
  const [end, setEnd] = useState("");
  const [note, setNote] = useState("");
  useEffect(() => { listComponents().then(setComponents).catch(() => {}); }, []);
  const eligible = components.filter((c) => c.status === "active" && ["bonus", "commission", "allowance", "benefit_in_kind", "other_deduction"].includes(c.category) && !c.requiresLegalBasis);
  const isRate = method === "percent_of_base" || method === "percent_of_sales";
  return (
    <FormModal title="Élément de paie récurrent" onClose={onClose} submitLabel="Ajouter" disabled={!value}
               onSubmit={async () => {
                 await addPayElement(employeeId, { componentCode: code, calcMethod: method, amount: isRate ? null : Number(value), rate: isRate ? Number(value) : null, startDate: start, endDate: end || null, note: note || null });
                 onDone();
               }}>
      <Grid>
        <Field label="Composant">
          <Select value={code} onChange={(e) => setCode(e.target.value)}>
            {eligible.map((c) => <option key={c.id} value={c.code}>{c.name}{c.kind === "deduction" ? " (retenue)" : ""}</option>)}
          </Select>
        </Field>
        <Field label="Calcul">
          <Select value={method} onChange={(e) => setMethod(e.target.value)}>
            <option value="fixed">Montant fixe par paie</option>
            <option value="per_day_worked">Montant par jour travaillé</option>
            <option value="percent_of_base">% du salaire de base</option>
            <option value="percent_of_sales">% des ventes (commission)</option>
          </Select>
        </Field>
        <Field label={isRate ? "Taux (%)" : "Montant"} required><TextInput type="number" min={0} value={value} onChange={(e) => setValue(e.target.value)} /></Field>
        <Field label="Début"><TextInput type="date" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
        <Field label="Fin"><TextInput type="date" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
      </Grid>
      <Field label="Note"><TextInput value={note} onChange={(e) => setNote(e.target.value)} /></Field>
    </FormModal>
  );
}

export function DocumentDialog({ employeeId, onClose, onDone }: { employeeId: string; onClose: () => void; onDone: () => void }) {
  const [type, setType] = useState("contract");
  const [title, setTitle] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const [visible, setVisible] = useState(false);
  return (
    <FormModal title="Déposer un document RH" onClose={onClose} submitLabel="Déposer" disabled={!file || !title.trim()}
               onSubmit={async () => {
                 await uploadDocument(employeeId, { documentType: type, title, fileName: file!.name, contentType: file!.type || "application/octet-stream",
                   contentBase64: await fileToBase64(file!), visibleToEmployee: visible });
                 onDone();
               }}>
      <Grid>
        <Field label="Type">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(DOCUMENT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Titre" required><TextInput value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
      </Grid>
      <Field label="Fichier (10 Mo au plus)" required><input type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></Field>
      <label style={{ display: "flex", gap: 8 }}><input type="checkbox" checked={visible} onChange={(e) => setVisible(e.target.checked)} /> Visible par l'employé dans son espace</label>
    </FormModal>
  );
}

// ================================================================== Avances (§12-§17)

function CheckList({ checks }: { checks: AdvanceCheck[] }) {
  if (checks.length === 0) return null;
  return (
    <ul style={{ margin: 0, paddingLeft: 18, display: "grid", gap: 4 }}>
      {checks.map((c) => (
        <li key={c.code} style={{ color: c.passed ? "var(--nx-text-2)" : c.blocking ? "var(--nx-error)" : "var(--nx-warning)" }}>
          {c.passed ? "✓" : c.blocking ? "✗" : "!"} {c.message}
        </li>
      ))}
    </ul>
  );
}

export function AdvanceRequestDialog({ employeeId, self, onClose, onDone }: { employeeId: string; self?: boolean; onClose: () => void; onDone: () => void }) {
  const [kind, setKind] = useState("salary_advance");
  const [amount, setAmount] = useState("");
  const [reason, setReason] = useState("");
  const [method, setMethod] = useState("next_payroll");
  const [installments, setInstallments] = useState("2");
  const [percent, setPercent] = useState("");
  const [first, setFirst] = useState("");
  const [conditions, setConditions] = useState("");
  const [checks, setChecks] = useState<AdvanceCheck[]>([]);
  const body = useMemo(() => ({
    kind, amount: Number(amount), reason: reason || null, recoveryMethod: kind === "acompte" ? "next_payroll" : method,
    installments: method === "installments" ? Number(installments) : null, recoveryPercent: method === "percent_of_net" ? num(percent) : null,
    firstRecoveryDate: first || null, conditions: conditions || null,
  }), [kind, amount, reason, method, installments, percent, first, conditions]);
  useEffect(() => {
    if (!amount || Number(amount) <= 0) { setChecks([]); return; }
    const t = setTimeout(() => previewAdvance(employeeId, body, self).then(setChecks).catch(() => setChecks([])), 400);
    return () => clearTimeout(t);
  }, [employeeId, body, amount, self]);
  const blocked = checks.some((c) => c.blocking && !c.passed);
  return (
    <FormModal title="Demande d'avance" onClose={onClose} submitLabel="Envoyer la demande" disabled={!amount || blocked}
               onSubmit={async () => { await requestAdvance(employeeId, body, self); onDone(); }}>
      <Grid>
        <Field label="Type">
          <Select value={kind} onChange={(e) => setKind(e.target.value)}>
            {Object.entries(ADVANCE_KINDS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Montant demandé" required><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        {kind !== "acompte" && (
          <Field label="Récupération">
            <Select value={method} onChange={(e) => setMethod(e.target.value)}>
              {Object.entries(RECOVERY_METHODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
        )}
        {method === "installments" && kind !== "acompte" && (
          <Field label="Nombre d'échéances"><TextInput type="number" min={1} max={120} value={installments} onChange={(e) => setInstallments(e.target.value)} /></Field>
        )}
        {method === "percent_of_net" && kind !== "acompte" && (
          <Field label="% du net retenu"><TextInput type="number" min={1} max={100} value={percent} onChange={(e) => setPercent(e.target.value)} /></Field>
        )}
        <Field label="Première retenue" hint="Vide = paie suivant le versement"><TextInput type="date" value={first} onChange={(e) => setFirst(e.target.value)} /></Field>
      </Grid>
      <Field label="Motif" required={kind === "loan"}><TextInput value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
      {kind === "loan" && <Field label="Conditions du prêt"><textarea className="nx-textarea" rows={2} value={conditions} onChange={(e) => setConditions(e.target.value)} /></Field>}
      <CheckList checks={checks} />
    </FormModal>
  );
}

export function AdvanceApproveDialog({ advance, onClose, onDone }: { advance: Advance; onClose: () => void; onDone: () => void }) {
  const [amount, setAmount] = useState(String(advance.amountApproved ?? advance.amountRequested));
  const [note, setNote] = useState("");
  return (
    <FormModal title={`Approuver ${advance.advanceNumber}`} onClose={onClose} submitLabel="Approuver"
               onSubmit={async () => { await approveAdvance(advance.id, { amountApproved: Number(amount), note: note || null }); onDone(); }}>
      <p style={{ margin: 0 }}>{advance.employeeName} — {ADVANCE_KINDS[advance.kind]} de {money(advance.amountRequested, advance.currency)} ({RECOVERY_METHODS[advance.recoveryMethod]}).
        {advance.requiredLevels > 1 && ` Niveau ${advance.approvalLevel}/${advance.requiredLevels}.`}</p>
      <CheckList checks={advance.checks} />
      <Grid>
        <Field label="Montant approuvé" hint="Au plus le montant demandé"><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <Field label="Note"><TextInput value={note} onChange={(e) => setNote(e.target.value)} /></Field>
      </Grid>
    </FormModal>
  );
}

/** Versement ou remboursement direct : l'identifiant d'opération est fixé à l'ouverture (§39). */
export function AdvanceMoneyDialog({ advance, mode, onClose, onDone }: { advance: Advance; mode: "pay" | "repay"; onClose: () => void; onDone: () => void }) {
  const accounts = useAccounts().filter((a) => a.currency === advance.currency);
  const [operation] = useState(() => crypto.randomUUID());
  const [account, setAccount] = useState("");
  const [method, setMethod] = useState("cash");
  const [reference, setReference] = useState("");
  const [amount, setAmount] = useState(String(advance.balance));
  useEffect(() => { if (!account && accounts[0]) setAccount(accounts[0].id); }, [accounts, account]);
  return (
    <FormModal title={mode === "pay" ? `Verser ${advance.advanceNumber}` : `Remboursement direct — ${advance.advanceNumber}`} onClose={onClose}
               submitLabel={mode === "pay" ? `Verser ${money(advance.amountApproved ?? 0, advance.currency)}` : "Enregistrer le remboursement"}
               disabled={!account || (mode === "repay" && !amount)}
               onSubmit={async () => {
                 if (mode === "pay") await payAdvance(advance.id, { financialAccountId: account, paymentMethod: method, reference: reference || null }, operation);
                 else await repayAdvance(advance.id, { amount: Number(amount), financialAccountId: account, paymentMethod: method, reference: reference || null }, operation);
                 onDone();
               }}>
      {mode === "repay" && <p style={{ margin: 0 }}>Solde restant : <b>{money(advance.balance, advance.currency)}</b> — un montant supérieur est refusé.</p>}
      <Grid>
        <Field label={mode === "pay" ? "Compte de sortie" : "Compte d'encaissement"} required>
          <Select value={account} onChange={(e) => setAccount(e.target.value)}>
            {accounts.map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </Select>
        </Field>
        <Field label="Moyen">
          <Select value={method} onChange={(e) => setMethod(e.target.value)}>
            {Object.entries(PAYMENT_METHODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        {mode === "repay" && <Field label="Montant" required><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>}
        <Field label="Référence"><TextInput value={reference} onChange={(e) => setReference(e.target.value)} /></Field>
      </Grid>
    </FormModal>
  );
}

export function RescheduleDialog({ advance, onClose, onDone }: { advance: Advance; onClose: () => void; onDone: () => void }) {
  const [rows, setRows] = useState<Array<{ dueDate: string; amount: string }>>([{ dueDate: today(), amount: String(advance.balance) }]);
  const [reason, setReason] = useState("");
  const total = rows.reduce((s, r) => s + (Number(r.amount) || 0), 0);
  return (
    <FormModal title={`Échéancier — ${advance.advanceNumber}`} onClose={onClose} submitLabel="Remplacer l'échéancier"
               disabled={Math.abs(total - advance.balance) > 0.001 || reason.trim().length < 5}
               onSubmit={async () => { await rescheduleAdvance(advance.id, rows.map((r) => ({ dueDate: r.dueDate, amount: Number(r.amount) })), reason); onDone(); }}>
      <p style={{ margin: 0 }}>Solde à répartir : <b>{money(advance.balance, advance.currency)}</b> — total saisi : {money(total, advance.currency)}.</p>
      {rows.map((r, i) => (
        <Grid key={i} cols={3}>
          <Field label={`Échéance ${i + 1}`}><TextInput type="date" value={r.dueDate} onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, dueDate: e.target.value } : x))} /></Field>
          <Field label="Montant"><TextInput type="number" value={r.amount} onChange={(e) => setRows(rows.map((x, j) => j === i ? { ...x, amount: e.target.value } : x))} /></Field>
          <div style={{ alignSelf: "end" }}><Button variant="ghost" size="sm" onClick={() => setRows(rows.filter((_, j) => j !== i))} disabled={rows.length === 1}>Retirer</Button></div>
        </Grid>
      ))}
      <div><Button variant="ghost" size="sm" onClick={() => setRows([...rows, { dueDate: today(), amount: "0" }])}>Ajouter une échéance</Button></div>
      <Field label="Motif" required hint="Soumis à permission et tracé (§15)"><TextInput value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
    </FormModal>
  );
}

// ================================================================== Périodes et paiements (§9, §24)

export function PeriodDialog({ onClose, onDone }: { onClose: () => void; onDone: (p: PayrollPeriod) => void }) {
  const now = new Date();
  const firstDay = new Date(now.getFullYear(), now.getMonth(), 1).toISOString().slice(0, 10);
  const lastDay = new Date(now.getFullYear(), now.getMonth() + 1, 0).toISOString().slice(0, 10);
  const [frequency, setFrequency] = useState("monthly");
  const [kind, setKind] = useState("regular");
  const [start, setStart] = useState(firstDay);
  const [end, setEnd] = useState(lastDay);
  const [cutoff, setCutoff] = useState(lastDay);
  const [payment, setPayment] = useState(lastDay);
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  const [scope, setScope] = useState<string[]>([]);
  useEffect(() => { if (kind === "off_cycle") listEmployees().then(setEmployees).catch(() => {}); }, [kind]);
  return (
    <FormModal title="Nouvelle période de paie" onClose={onClose} submitLabel="Créer la période"
               onSubmit={async () => onDone(await createPeriod({ frequency, startDate: start, endDate: end, cutoffDate: cutoff, paymentDate: payment, kind, employeeIds: scope }))}>
      <Grid>
        <Field label="Type">
          <Select value={kind} onChange={(e) => setKind(e.target.value)}>
            <option value="regular">Paie régulière</option>
            <option value="off_cycle">Paie hors cycle (régularisations)</option>
          </Select>
        </Field>
        <Field label="Fréquence">
          <Select value={frequency} onChange={(e) => setFrequency(e.target.value)}>
            {Object.entries(FREQUENCIES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Début"><TextInput type="date" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
        <Field label="Fin"><TextInput type="date" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
        <Field label="Clôture des variables" hint="Après cette date, variables verrouillées"><TextInput type="date" value={cutoff} onChange={(e) => setCutoff(e.target.value)} /></Field>
        <Field label="Date de paiement prévue"><TextInput type="date" value={payment} onChange={(e) => setPayment(e.target.value)} /></Field>
      </Grid>
      {kind === "off_cycle" && (
        <Field label="Employés visés" hint="Vide = employés ayant une régularisation, une note de frais ou une variable approuvée">
          <select className="nx-select" multiple size={6} value={scope} onChange={(e) => setScope(Array.from(e.target.selectedOptions).map((o) => o.value))}>
            {employees.map((x) => <option key={x.id} value={x.id}>{x.employeeNumber} — {x.firstName} {x.lastName}</option>)}
          </select>
        </Field>
      )}
    </FormModal>
  );
}

export function PayRunDialog({ runId, currency, itemIds, count, total, onClose, onDone }: {
  runId: string; currency: string; itemIds: string[] | null; count: number; total: number; onClose: () => void; onDone: (r: PayRunResult) => void;
}) {
  const accounts = useAccounts().filter((a) => a.currency === currency);
  const [operation] = useState(() => crypto.randomUUID());
  const [account, setAccount] = useState("");
  const [method, setMethod] = useState("");
  const [reference, setReference] = useState("");
  const [paidOn, setPaidOn] = useState(today());
  useEffect(() => { if (!account && accounts[0]) setAccount(accounts[0].id); }, [accounts, account]);
  return (
    <FormModal title={itemIds?.length === 1 ? "Payer ce salaire" : "Paiement groupé des salaires"} onClose={onClose}
               submitLabel={`Payer ${count} salaire(s) — ${money(total, currency)}`} disabled={!account}
               onSubmit={async () => onDone(await payRun(runId, { financialAccountId: account, paymentMethod: method || null, itemIds, reference: reference || null, paidOn }, operation))}>
      <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
        Une instruction = un paiement : renvoyer ce formulaire ne paie jamais deux fois (§39). Chaque paiement génère son écriture et son bulletin.
      </p>
      <Grid>
        <Field label="Compte de paiement" required>
          <Select value={account} onChange={(e) => setAccount(e.target.value)}>
            {accounts.map((a) => <option key={a.id} value={a.id}>{a.name} ({a.currency})</option>)}
          </Select>
        </Field>
        <Field label="Moyen" hint="Vide = moyen du dossier de chaque employé">
          <Select value={method} onChange={(e) => setMethod(e.target.value)}>
            <option value="">Selon le dossier</option>
            {Object.entries(PAYMENT_METHODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Date de paiement"><TextInput type="date" value={paidOn} onChange={(e) => setPaidOn(e.target.value)} /></Field>
        <Field label="Référence (ordre de virement…)"><TextInput value={reference} onChange={(e) => setReference(e.target.value)} /></Field>
      </Grid>
    </FormModal>
  );
}

// ================================================================== Temps, variables, frais, régularisations (§17-§20, §36)

function EmployeePicker({ value, onChange }: { value: string; onChange: (id: string) => void }) {
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  useEffect(() => { listEmployees().then((e) => setEmployees(e.filter((x) => x.status !== "terminated"))).catch(() => {}); }, []);
  return (
    <Select value={value} onChange={(e) => onChange(e.target.value)}>
      <option value="">— Choisir —</option>
      {employees.map((x) => <option key={x.id} value={x.id}>{x.employeeNumber} — {x.firstName} {x.lastName}</option>)}
    </Select>
  );
}

export function LeaveDialog({ employeeId, self, onClose, onDone }: { employeeId?: string; self?: boolean; onClose: () => void; onDone: () => void }) {
  const [employee, setEmployee] = useState(employeeId ?? "");
  const [type, setType] = useState("annual");
  const [start, setStart] = useState(today());
  const [end, setEnd] = useState(today());
  const [days, setDays] = useState("");
  const [reason, setReason] = useState("");
  return (
    <FormModal title="Congé / absence" onClose={onClose} submitLabel="Envoyer" disabled={!self && !employee}
               onSubmit={async () => { await requestLeave({ employeeId: employee || employeeId || "", leaveType: type, startDate: start, endDate: end, days: num(days), reason: reason || null }, self); onDone(); }}>
      {!self && !employeeId && <Field label="Employé" required><EmployeePicker value={employee} onChange={setEmployee} /></Field>}
      <Grid>
        <Field label="Type">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(LEAVE_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Jours" hint="Vide = selon les dates ; demi-journées permises"><TextInput type="number" step="0.5" value={days} onChange={(e) => setDays(e.target.value)} /></Field>
        <Field label="Du"><TextInput type="date" value={start} onChange={(e) => setStart(e.target.value)} /></Field>
        <Field label="Au"><TextInput type="date" value={end} onChange={(e) => setEnd(e.target.value)} /></Field>
      </Grid>
      <Field label="Motif"><TextInput value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
    </FormModal>
  );
}

export function OvertimeDialog({ employeeId, self, onClose, onDone }: { employeeId?: string; self?: boolean; onClose: () => void; onDone: () => void }) {
  const [employee, setEmployee] = useState(employeeId ?? "");
  const [date, setDate] = useState(today());
  const [hours, setHours] = useState("");
  const [type, setType] = useState("standard");
  const [reason, setReason] = useState("");
  return (
    <FormModal title="Heures supplémentaires" onClose={onClose} submitLabel="Envoyer pour validation" disabled={!hours || (!self && !employee)}
               onSubmit={async () => { await recordOvertime({ employeeId: employee || employeeId || "", workDate: date, hours: Number(hours), overtimeType: type, reason: reason || null }, self); onDone(); }}>
      {!self && !employeeId && <Field label="Employé" required><EmployeePicker value={employee} onChange={setEmployee} /></Field>}
      <Grid>
        <Field label="Date"><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="Heures" required><TextInput type="number" step="0.25" min={0} value={hours} onChange={(e) => setHours(e.target.value)} /></Field>
        <Field label="Type" hint="Coefficient configuré, figé à la saisie">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(OVERTIME_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </Grid>
      <Field label="Motif"><TextInput value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
    </FormModal>
  );
}

export function AttendanceDialog({ onClose, onDone }: { onClose: () => void; onDone: (r: { created: number; updated: number; errors: string[] }) => void }) {
  const [employee, setEmployee] = useState("");
  const [date, setDate] = useState(today());
  const [status, setStatus] = useState("present");
  const [hours, setHours] = useState("");
  const [justified, setJustified] = useState(false);
  return (
    <FormModal title="Saisir une présence" onClose={onClose} submitLabel="Enregistrer" disabled={!employee}
               onSubmit={async () => onDone(await recordAttendance([{ employeeId: employee, workDate: date, status, hoursWorked: num(hours), justified }]))}>
      <Field label="Employé" required><EmployeePicker value={employee} onChange={setEmployee} /></Field>
      <Grid cols={3}>
        <Field label="Date"><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            {Object.entries({ present: "Présent", absent: "Absent", late: "Retard", half_day: "Demi-journée", holiday: "Férié", rest: "Repos" })
              .map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Heures travaillées"><TextInput type="number" step="0.25" value={hours} onChange={(e) => setHours(e.target.value)} /></Field>
      </Grid>
      <label style={{ display: "flex", gap: 8 }}><input type="checkbox" checked={justified} onChange={(e) => setJustified(e.target.checked)} /> Absence justifiée</label>
    </FormModal>
  );
}

/** Import de présences par lot : une ligne « matricule;date;statut;heures;justifiée(o/n) » par employé et par jour. */
export function AttendanceImportDialog({ onClose, onDone }: { onClose: () => void; onDone: (r: { created: number; updated: number; errors: string[] }) => void }) {
  const [text, setText] = useState("");
  const [employees, setEmployees] = useState<EmployeeListItem[]>([]);
  useEffect(() => { listEmployees().then(setEmployees).catch(() => {}); }, []);
  return (
    <FormModal title="Importer des présences" onClose={onClose} submitLabel="Importer" disabled={!text.trim()}
               onSubmit={async () => {
                 const byNumber = new Map(employees.map((e) => [e.employeeNumber.toUpperCase(), e.id]));
                 const records = text.split(/\r?\n/).map((l) => l.trim()).filter(Boolean).map((l) => l.split(/[;,\t]/).map((x) => x.trim()))
                   .filter((c) => c[0] && c[0].toLowerCase() !== "matricule")
                   .map((c) => ({ employeeId: byNumber.get(c[0].toUpperCase()) ?? "00000000-0000-0000-0000-000000000000", workDate: c[1], status: c[2] || "present",
                     hoursWorked: num(c[3] ?? ""), justified: /^(o|oui|y|yes|1|true)$/i.test(c[4] ?? "") }));
                 onDone(await recordAttendance(records, "import"));
               }}>
      <p style={{ margin: 0, color: "var(--nx-text-2)" }}>Une ligne par employé et par jour : <code>matricule;AAAA-MM-JJ;statut;heures;justifiée</code>. Statuts : present, absent, late, half_day, holiday, rest. Chaque ligne en erreur est signalée sans bloquer les autres.</p>
      <textarea className="nx-textarea" rows={10} value={text} onChange={(e) => setText(e.target.value)} placeholder={"EMP-2026-000001;2026-09-01;present;8;\nEMP-2026-000002;2026-09-01;absent;;n"} />
    </FormModal>
  );
}

export function VariableDialog({ periods, onClose, onDone }: { periods: PayrollPeriod[]; onClose: () => void; onDone: () => void }) {
  const [components, setComponents] = useState<PayComponent[]>([]);
  const [employee, setEmployee] = useState("");
  const [period, setPeriod] = useState(periods[0]?.id ?? "");
  const [code, setCode] = useState("BONUS");
  const [amount, setAmount] = useState("");
  const [description, setDescription] = useState("");
  const [legalBasis, setLegalBasis] = useState("");
  useEffect(() => { listComponents().then(setComponents).catch(() => {}); }, []);
  const eligible = components.filter((c) => c.status === "active" && ["bonus", "commission", "allowance", "benefit_in_kind", "other_deduction"].includes(c.category));
  const selected = eligible.find((c) => c.code === code);
  return (
    <FormModal title="Élément variable" onClose={onClose} submitLabel="Soumettre pour validation"
               disabled={!employee || !period || !amount || !description.trim() || (selected?.requiresLegalBasis === true && !legalBasis.trim())}
               onSubmit={async () => { await submitVariable({ employeeId: employee, periodId: period, componentCode: code, amount: Number(amount), description, legalBasis: legalBasis || null }); onDone(); }}>
      <Field label="Employé" required><EmployeePicker value={employee} onChange={setEmployee} /></Field>
      <Grid>
        <Field label="Période">
          <Select value={period} onChange={(e) => setPeriod(e.target.value)}>
            {periods.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
          </Select>
        </Field>
        <Field label="Élément">
          <Select value={code} onChange={(e) => setCode(e.target.value)}>
            {eligible.map((c) => <option key={c.id} value={c.code}>{c.name}{c.kind === "deduction" ? " (retenue)" : ""}</option>)}
          </Select>
        </Field>
        <Field label="Montant" required><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
      </Grid>
      <Field label="Description / source" required hint="Chaque élément a une source documentée (§21)"><TextInput value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
      {selected?.requiresLegalBasis && (
        <Field label="Base légale et contractuelle" required hint="Retenue disciplinaire uniquement si légalement autorisée (§21)">
          <textarea className="nx-textarea" rows={2} value={legalBasis} onChange={(e) => setLegalBasis(e.target.value)} />
        </Field>
      )}
    </FormModal>
  );
}

export function ExpenseClaimDialog({ employeeId, self, onClose, onDone }: { employeeId?: string; self?: boolean; onClose: () => void; onDone: () => void }) {
  const [employee, setEmployee] = useState(employeeId ?? "");
  const [date, setDate] = useState(today());
  const [category, setCategory] = useState("transport");
  const [description, setDescription] = useState("");
  const [amount, setAmount] = useState("");
  const [settlement, setSettlement] = useState("payroll");
  const [file, setFile] = useState<File | null>(null);
  return (
    <FormModal title="Note de frais" onClose={onClose} submitLabel="Soumettre" disabled={!amount || !description.trim() || (!self && !employee)}
               onSubmit={async () => {
                 const receipt = file ? { documentType: "expense_receipt", title: `Justificatif ${description}`.slice(0, 80), fileName: file.name,
                   contentType: file.type || "application/octet-stream", contentBase64: await fileToBase64(file) } : null;
                 await submitExpenseClaim({ employeeId: employee || employeeId || "", expenseDate: date, category, description, amount: Number(amount), settlement, receipt }, self);
                 onDone();
               }}>
      {!self && !employeeId && <Field label="Employé" required><EmployeePicker value={employee} onChange={setEmployee} /></Field>}
      <Grid>
        <Field label="Date de la dépense"><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="Catégorie">
          <Select value={category} onChange={(e) => setCategory(e.target.value)}>
            {Object.entries(EXPENSE_CATEGORIES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Montant" required><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <Field label="Remboursement">
          <Select value={settlement} onChange={(e) => setSettlement(e.target.value)}>
            <option value="payroll">Avec la prochaine paie</option>
            <option value="direct">Paiement direct</option>
          </Select>
        </Field>
      </Grid>
      <Field label="Description" required><TextInput value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
      <Field label="Justificatif"><input type="file" onChange={(e) => setFile(e.target.files?.[0] ?? null)} /></Field>
    </FormModal>
  );
}

export function AdjustmentDialog({ itemId, employeeName, runNumber, currency, onClose, onDone }: {
  itemId: string; employeeName: string; runNumber: string; currency: string; onClose: () => void; onDone: () => void;
}) {
  const [kind, setKind] = useState<"earning" | "deduction">("earning");
  const [amount, setAmount] = useState("");
  const [taxable, setTaxable] = useState(true);
  const [reason, setReason] = useState("");
  return (
    <FormModal title="Demander une régularisation" onClose={onClose} submitLabel="Envoyer pour approbation"
               disabled={!amount || reason.trim().length < 5}
               onSubmit={async () => { await requestAdjustment({ originalItemId: itemId, kind, amount: Number(amount), taxable, reason }); onDone(); }}>
      <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
        La paie {runNumber} de {employeeName} n'est jamais modifiée : la correction est reliée à cette ligne, approuvée par une autre personne,
        puis intégrée à la paie suivante (ou à une paie hors cycle) avec sa référence (§36).
      </p>
      <Grid>
        <Field label="Nature">
          <Select value={kind} onChange={(e) => setKind(e.target.value as "earning" | "deduction")}>
            <option value="earning">Rappel (en faveur de l'employé)</option>
            <option value="deduction">Trop-perçu (retenue)</option>
          </Select>
        </Field>
        <Field label={`Montant (${currency})`} required><TextInput type="number" min={0} value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
      </Grid>
      <label style={{ display: "flex", gap: 8 }}><input type="checkbox" checked={taxable} onChange={(e) => setTaxable(e.target.checked)} /> Soumis aux retenues légales</label>
      <Field label="Justification" required><textarea className="nx-textarea" rows={3} value={reason} onChange={(e) => setReason(e.target.value)} /></Field>
    </FormModal>
  );
}

/** Décision avec note (obligatoire pour un refus). */
export function DecisionDialog({ title, approveLabel = "Approuver", onClose, onDecide, children }: {
  title: string; approveLabel?: string; onClose: () => void; onDecide: (approve: boolean, note: string) => Promise<void>; children?: ReactNode;
}) {
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const toast = useToast();
  const decide = async (approve: boolean) => {
    setBusy(true);
    setError(null);
    try { await onDecide(approve, note.trim()); toast.success(approve ? "Décision enregistrée" : "Demande refusée"); } catch (e) { setError(errorText(e)); } finally { setBusy(false); }
  };
  return (
    <Modal title={title} onClose={onClose} footer={<>
      <Button variant="ghost" onClick={onClose}>Fermer</Button>
      <Button variant="danger" loading={busy} disabled={!note.trim()} onClick={() => decide(false)}>Refuser</Button>
      <Button loading={busy} onClick={() => decide(true)}>{approveLabel}</Button>
    </>}>
      <div style={{ display: "grid", gap: 12, minWidth: "min(480px, 90vw)" }}>
        {children}
        <Field label="Note" hint="Obligatoire pour un refus ; conservée comme preuve de la décision"><textarea className="nx-textarea" rows={2} value={note} onChange={(e) => setNote(e.target.value)} /></Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
      </div>
    </Modal>
  );
}

// ================================================================== Règles légales (§11, §31)

export function RuleDialog({ rule, mode, onClose, onDone }: { rule?: PayrollRule; mode: "create" | "edit" | "version"; onClose: () => void; onDone: () => void }) {
  const [r, setR] = useState<RuleInput>(() => rule ? { ...rule, validFrom: mode === "version" ? today() : rule.validFrom } : {
    code: "", name: "", jurisdiction: "HT", ruleType: "employee_contribution", baseType: "social", method: "percentage", rate: null,
    fixedAmount: null, brackets: [], annualize: false, baseFloor: null, baseCeiling: null, minAmount: null, maxAmount: null,
    validFrom: today(), validTo: null, legalReference: null, notes: null,
  });
  const set = (k: keyof RuleInput) => (e: { target: { value: string } }) => setR((x) => ({ ...x, [k]: e.target.value === "" ? null : e.target.value }));
  const setNum = (k: keyof RuleInput) => (e: { target: { value: string } }) => setR((x) => ({ ...x, [k]: num(e.target.value) }));
  const setBracket = (i: number, patch: Partial<RuleBracket>) => setR((x) => ({ ...x, brackets: x.brackets.map((b, j) => j === i ? { ...b, ...patch } : b) }));
  return (
    <FormModal wide title={mode === "version" ? `Nouvelle version — ${rule?.code}` : mode === "edit" ? `Modifier ${rule?.code}` : "Nouvelle règle légale"}
               onClose={onClose} submitLabel="Enregistrer (brouillon)" disabled={!r.code || !r.name}
               onSubmit={async () => {
                 const body = { ...r, brackets: r.method === "progressive" ? r.brackets : [] };
                 if (mode === "version" && rule) await newRuleVersion(rule.id, body);
                 else if (mode === "edit" && rule) await updateRule(rule.id, body);
                 else await createRule(body);
                 onDone();
               }}>
      <p style={{ margin: 0, color: "var(--nx-text-2)" }}>Une règle n'est appliquée qu'après validation par une autre personne ; elle est alors figée (§11, §31).</p>
      <Grid cols={3}>
        <Field label="Code" required><TextInput value={r.code} onChange={set("code")} disabled={mode !== "create"} /></Field>
        <Field label="Libellé" required><TextInput value={r.name} onChange={set("name")} /></Field>
        <Field label="Juridiction"><TextInput value={r.jurisdiction} onChange={set("jurisdiction")} /></Field>
        <Field label="Type">
          <Select value={r.ruleType} onChange={set("ruleType")}>{Object.entries(RULE_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</Select>
        </Field>
        <Field label="Assiette">
          <Select value={r.baseType} onChange={set("baseType")}>{Object.entries(RULE_BASES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</Select>
        </Field>
        <Field label="Méthode">
          <Select value={r.method} onChange={set("method")}>
            <option value="percentage">Pourcentage</option><option value="fixed">Montant fixe</option><option value="progressive">Barème progressif</option>
          </Select>
        </Field>
        {r.method === "percentage" && <Field label="Taux (%)"><TextInput type="number" value={r.rate ?? ""} onChange={setNum("rate")} /></Field>}
        {r.method === "fixed" && <Field label="Montant par paie"><TextInput type="number" value={r.fixedAmount ?? ""} onChange={setNum("fixedAmount")} /></Field>}
        <Field label="Seuil d'application" hint="Assiette ≤ seuil → pas de retenue"><TextInput type="number" value={r.baseFloor ?? ""} onChange={setNum("baseFloor")} /></Field>
        <Field label="Plafond d'assiette"><TextInput type="number" value={r.baseCeiling ?? ""} onChange={setNum("baseCeiling")} /></Field>
        <Field label="Effet au" required><TextInput type="date" value={r.validFrom} onChange={set("validFrom")} /></Field>
        <Field label="Fin de validité"><TextInput type="date" value={r.validTo ?? ""} onChange={set("validTo")} /></Field>
        <Field label="Référence légale"><TextInput value={r.legalReference ?? ""} onChange={set("legalReference")} /></Field>
      </Grid>
      {r.method === "progressive" && (
        <div style={{ display: "grid", gap: 8 }}>
          <label style={{ display: "flex", gap: 8 }}><input type="checkbox" checked={r.annualize} onChange={(e) => setR({ ...r, annualize: e.target.checked })} /> Barème annuel (assiette annualisée puis ramenée à la période)</label>
          {r.brackets.map((b, i) => (
            <Grid key={i} cols={3}>
              <Field label="De"><TextInput type="number" value={b.from} onChange={(e) => setBracket(i, { from: Number(e.target.value) })} /></Field>
              <Field label="À (vide = au-delà)"><TextInput type="number" value={b.to ?? ""} onChange={(e) => setBracket(i, { to: num(e.target.value) })} /></Field>
              <Field label="Taux (%)"><TextInput type="number" value={b.rate} onChange={(e) => setBracket(i, { rate: Number(e.target.value) })} /></Field>
            </Grid>
          ))}
          <div><Button size="sm" variant="ghost" onClick={() => setR({ ...r, brackets: [...r.brackets, { from: r.brackets.at(-1)?.to ?? 0, to: null, rate: 0 }] })}>Ajouter une tranche</Button></div>
        </div>
      )}
      <Field label="Notes"><TextInput value={r.notes ?? ""} onChange={set("notes")} /></Field>
      {mode === "version" && <Badge tone="info">La version actuelle prendra fin la veille de la date d'effet, une fois celle-ci validée.</Badge>}
    </FormModal>
  );
}

/** Saisie d'une valeur avant une action (note de validation, date…) — jamais de window.prompt. */
export function PromptDialog({ title, label, hint, type = "text", initial = "", minLength = 1, submitLabel, onClose, onSubmit }: {
  title: string; label: string; hint?: string; type?: "text" | "date" | "textarea"; initial?: string; minLength?: number; submitLabel: string;
  onClose: () => void; onSubmit: (value: string) => Promise<void>;
}) {
  const [value, setValue] = useState(initial);
  return (
    <FormModal title={title} onClose={onClose} submitLabel={submitLabel} disabled={value.trim().length < minLength}
               onSubmit={() => onSubmit(value.trim())}>
      <Field label={label} required hint={hint}>
        {type === "textarea"
          ? <textarea className="nx-textarea" rows={3} value={value} onChange={(e) => setValue(e.target.value)} />
          : <TextInput type={type} value={value} onChange={(e) => setValue(e.target.value)} />}
      </Field>
    </FormModal>
  );
}
