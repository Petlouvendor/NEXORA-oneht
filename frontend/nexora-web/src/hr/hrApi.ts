import { apiDownload, apiFetch } from "@/core/apiClient";
import { openPrintable, saveFile } from "@/shared/printApi";

// Module « Paie, RH & avances sur salaire » (docs/module-paie-rh-avances.md).
// Le serveur est l'unique autorité sur les montants (§39) : aucun brut, net ou solde n'est calculé ici.
// Tout paiement (salaires, avances, remboursements, frais) porte un identifiant d'opération fixé à
// l'ouverture du formulaire ET envoyé comme clé d'idempotence : un double clic ou un renvoi ne paie jamais deux fois.

const post = <T>(path: string, body: unknown = {}, idempotencyKey?: string) =>
  apiFetch<T>(path, { method: "POST", body: JSON.stringify(body), idempotencyKey });
const put = <T>(path: string, body: unknown) => apiFetch<T>(path, { method: "PUT", body: JSON.stringify(body) });
const qs = (params: Record<string, string | number | boolean | null | undefined>) => {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) if (v !== null && v !== undefined && v !== "") q.set(k, String(v));
  const s = q.toString();
  return s ? `?${s}` : "";
};

// --- Libellés ----------------------------------------------------------------------------------------

export const EMPLOYEE_STATUS: Record<string, string> = { active: "Actif", suspended: "Suspendu", on_leave: "En congé", terminated: "Sorti" };
export const CONTRACT_TYPES: Record<string, string> = {
  permanent: "Durée indéterminée", fixed_term: "Durée déterminée", temporary: "Temporaire", internship: "Stage",
  apprenticeship: "Apprentissage", consultant: "Consultant", daily: "Journalier",
};
export const FREQUENCIES: Record<string, string> = { monthly: "Mensuelle", biweekly: "Bihebdomadaire", weekly: "Hebdomadaire" };
export const PAY_BASES: Record<string, string> = { monthly: "Salaire mensuel", daily: "Taux journalier", hourly: "Taux horaire" };
export const PAYMENT_METHODS: Record<string, string> = {
  bank_transfer: "Virement bancaire", cash: "Espèces", mobile_money: "Mobile money", check: "Chèque", other: "Autre",
};
export const RUN_STATUS: Record<string, string> = {
  draft: "Brouillon", calculated: "Calculée", reviewed: "Contrôlée", validated: "Validée", approved: "Approuvée",
  paid: "Payée", closed: "Clôturée", cancelled: "Annulée",
};
export const PERIOD_STATUS: Record<string, string> = {
  draft: "Brouillon", calculated: "Calculée", in_validation: "En validation", approved: "Approuvée", paid: "Payée",
  closed: "Clôturée", cancelled: "Annulée",
};
export const ADVANCE_KINDS: Record<string, string> = { salary_advance: "Avance sur salaire", acompte: "Acompte", loan: "Prêt au personnel" };
export const ADVANCE_STATUS: Record<string, string> = {
  requested: "Demandée", approved: "Approuvée", paid: "Payée", in_repayment: "En remboursement", settled: "Soldée",
  rejected: "Rejetée", cancelled: "Annulée",
};
export const RECOVERY_METHODS: Record<string, string> = {
  next_payroll: "Retenue sur la prochaine paie", installments: "Échéancier", percent_of_net: "Pourcentage du net", manual: "Remboursement direct",
};
export const LEAVE_TYPES: Record<string, string> = {
  annual: "Congé annuel", sick: "Maladie", maternity: "Maternité", paternity: "Paternité", unpaid: "Congé sans solde",
  justified_absence: "Absence justifiée", unjustified_absence: "Absence injustifiée", other: "Autre",
};
export const REQUEST_STATUS: Record<string, string> = {
  requested: "Demandée", submitted: "Soumise", approved: "Approuvée", rejected: "Refusée", cancelled: "Annulée",
  pending: "À valider", validated: "Validée", applied: "Intégrée", in_payroll: "Dans la paie", paid: "Payée",
};
export const OVERTIME_TYPES: Record<string, string> = { standard: "Standard", night: "Nuit", weekend: "Fin de semaine", holiday: "Jour férié" };
export const ATTENDANCE_STATUS: Record<string, string> = {
  present: "Présent", absent: "Absent", late: "Retard", half_day: "Demi-journée", holiday: "Férié", rest: "Repos", leave: "Congé",
};
export const EXPENSE_CATEGORIES: Record<string, string> = {
  transport: "Transport", meal: "Repas", lodging: "Hébergement", supplies: "Fournitures", communication: "Communication",
  fuel: "Carburant", other: "Autre",
};
export const DOCUMENT_TYPES: Record<string, string> = {
  contract: "Contrat", id_document: "Pièce d'identité", diploma: "Diplôme", certificate: "Attestation", medical: "Médical",
  expense_receipt: "Justificatif de frais", disciplinary: "Disciplinaire", other: "Autre",
};
export const RULE_TYPES: Record<string, string> = {
  employee_contribution: "Cotisation salariale", employer_contribution: "Cotisation patronale", income_tax: "Impôt / contribution",
};
export const RULE_BASES: Record<string, string> = {
  taxable: "Assiette imposable", social: "Assiette sociale", gross: "Brut", taxable_after_social: "Imposable après cotisations",
};
export const RULE_STATUS: Record<string, string> = { template: "Modèle", draft: "Brouillon", validated: "Validée", retired: "Retirée" };

// --- Types -------------------------------------------------------------------------------------------

export interface CurrencyTotals { currency: string; gross: number; deductions: number; net: number; employerCost: number; paid: number; unpaid: number }
export interface CurrencyAmount { currency: string; amount: number }
export interface Department { id: string; code: string; name: string; managerEmployeeId: string | null; managerName: string | null; status: string; employeeCount: number }

export interface EmployeeListItem {
  id: string; employeeNumber: string; firstName: string; lastName: string; jobTitle: string | null; departmentId: string | null;
  departmentName: string | null; status: string; hireDate: string; establishmentName: string | null; hasUser: boolean;
  contractType: string | null; currentSalary: number | null; salaryBasis: string | null; currency: string | null;
}

export interface EmployeeCompensation {
  contractId: string | null; contractNumber: string | null; contractType: string | null; payFrequency: string | null; payBasis: string | null;
  currency: string | null; baseSalary: number | null; hourlyRate: number | null; effectiveDate: string | null; advanceBalance: number;
  loanBalance: number; openAdvances: number;
}

export interface Employee {
  id: string; employeeNumber: string; firstName: string; lastName: string; birthDate: string | null; phone: string | null; email: string | null;
  address: string | null; city: string | null; emergencyContactName: string | null; emergencyContactPhone: string | null; jobTitle: string | null;
  departmentId: string | null; departmentName: string | null; managerEmployeeId: string | null; managerName: string | null;
  establishmentId: string | null; establishmentName: string | null; hireDate: string; terminationDate: string | null;
  terminationReason: string | null; status: string; userId: string | null; userEmail: string | null; paymentMethod: string;
  bankName: string | null; bankAccountMasked: string | null; mobileMoneyMasked: string | null; taxIdMasked: string | null;
  socialIdMasked: string | null; notes: string | null; createdAt: string; createdByName: string | null; updatedAt: string;
  compensation: EmployeeCompensation | null; canViewSalaries: boolean; canViewSensitive: boolean; canManage: boolean;
}

export interface ContractInput {
  contractType: string; startDate: string; endDate?: string | null; trialEndDate?: string | null; payFrequency: string; payBasis: string;
  currency: string; weeklyHours?: number | null; jobTitle?: string | null; benefits?: string | null; conditions?: string | null;
  baseSalary?: number | null; hourlyRate?: number | null; salaryReason?: string | null;
}

export interface EmployeeInput {
  employeeNumber?: string | null; firstName: string; lastName: string; birthDate?: string | null; phone?: string | null; email?: string | null;
  address?: string | null; city?: string | null; emergencyContactName?: string | null; emergencyContactPhone?: string | null;
  jobTitle?: string | null; departmentId?: string | null; managerEmployeeId?: string | null; establishmentId?: string | null; hireDate: string;
  paymentMethod?: string | null; bankName?: string | null; bankAccount?: string | null; mobileMoney?: string | null; taxId?: string | null;
  socialId?: string | null; notes?: string | null; contract?: ContractInput | null;
}

export interface Contract {
  id: string; contractNumber: string; contractType: string; startDate: string; endDate: string | null; trialEndDate: string | null;
  payFrequency: string; payBasis: string; currency: string; weeklyHours: number; jobTitle: string | null; benefits: string | null;
  conditions: string | null; status: string; endReason: string | null; createdAt: string; createdByName: string | null;
}

export interface SalaryHistoryEntry {
  id: string; contractId: string; contractNumber: string; effectiveDate: string; payBasis: string; amount: number; hourlyRate: number | null;
  currency: string; previousAmount: number | null; previousEffectiveDate: string | null; reason: string; createdByName: string | null; createdAt: string;
}

export interface PayElement {
  id: string; componentId: string; componentCode: string; componentName: string; kind: string; category: string; calcMethod: string;
  amount: number | null; rate: number | null; currency: string; startDate: string; endDate: string | null; status: string; note: string | null;
}

export interface EmployeeDocument {
  id: string; employeeId: string; documentType: string; title: string; fileName: string; contentType: string; sizeBytes: number; sha256: string;
  visibleToEmployee: boolean; supersedesId: string | null; archivedAt: string | null; uploadedByName: string | null; createdAt: string;
}

export interface Anomaly { code: string; severity: "blocking" | "warning"; message: string }

export interface PayrollPeriod {
  id: string; code: string; label: string; kind: string; frequency: string; startDate: string; endDate: string; cutoffDate: string;
  paymentDate: string; status: string; runId: string | null; runNumber: string | null; runStatus: string | null; employeeCount: number;
  totals: CurrencyTotals[]; createdAt: string;
}

export interface PayrollItemSummary {
  id: string; employeeId: string; employeeNumber: string; employeeName: string; departmentName: string | null; currency: string; gross: number;
  totalDeductions: number; net: number; employerCost: number; advanceRecovery: number; loanRecovery: number; status: string;
  excludedReason: string | null; blockingCount: number; warningCount: number; anomalies: Anomaly[]; paymentId: string | null;
  paymentNumber: string | null; payslipId: string | null; payslipNumber: string | null; paymentMethod: string;
}

export interface PayrollRun {
  id: string; runNumber: string; periodId: string; periodCode: string; periodLabel: string; periodKind: string; frequency: string;
  startDate: string; endDate: string; cutoffDate: string; paymentDate: string; status: string; stale: boolean; calculationCount: number;
  calculatedByName: string | null; calculatedAt: string | null; reviewedByName: string | null; reviewedAt: string | null; reviewNote: string | null;
  warningsAcknowledged: boolean; validatedByName: string | null; validatedAt: string | null; validationNote: string | null;
  approvedByName: string | null; approvedAt: string | null; approvalNote: string | null; journalEntryId: string | null;
  journalEntryNumber: string | null; paidAt: string | null; closedByName: string | null; closedAt: string | null; returnedNote: string | null;
  cancelReason: string | null; itemCount: number; excludedCount: number; blockingCount: number; warningCount: number; paidCount: number;
  totals: CurrencyTotals[]; items: PayrollItemSummary[]; allowedActions: string[];
}

export interface PayrollLine {
  id: string; code: string; category: string; label: string; quantity: number | null; rate: number | null; baseAmount: number | null;
  plannedAmount: number | null; amount: number; borneBy: string | null; sourceType: string; sourceId: string | null; ruleId: string | null;
  ruleVersion: number | null; taxable: boolean | null; inGross: boolean | null;
}

export interface SalaryPayment {
  id: string; paymentNumber: string; payrollItemId: string; runId: string; runNumber: string; employeeId: string; employeeName: string;
  amount: number; currency: string; paymentMethod: string; financialAccountId: string; financialAccountName: string; reference: string | null;
  beneficiaryAccount: string | null; paidAt: string; status: string; journalEntryId: string; cancelReason: string | null;
  cancelledAt: string | null; payslipId: string | null; payslipNumber: string | null;
}

export interface PayrollItemDetail {
  summary: PayrollItemSummary; runId: string; runNumber: string; runStatus: string; periodLabel: string; periodStart: string; periodEnd: string;
  jobTitle: string | null; contractType: string | null; payBasis: string | null; payFrequency: string | null; baseSalary: number;
  periodDays: number; daysEmployed: number; absenceDays: number; prorata: number; dailyRate: number; hourlyRate: number; baseAmount: number;
  reimbursements: number; taxableBase: number; socialBase: number; legalDeductions: number; absenceDeduction: number; benefitOffset: number;
  otherDeductions: number; employerContributions: number; earnings: PayrollLine[]; deductions: PayrollLine[];
  trace: Array<{ step: string; detail: string; amount: number | null }>; payments: SalaryPayment[];
}

export interface PayRunResult { paid: number; alreadyPaid: number; skipped: number; payments: SalaryPayment[]; errors: string[]; runStatus: string }
export interface PaymentAccount { id: string; name: string; accountType: string; currency: string }

export interface Payslip {
  id: string; payslipNumber: string; payrollItemId: string; runId: string; employeeId: string; employeeName: string; periodLabel: string;
  periodStart: string; periodEnd: string; currency: string; gross: number; net: number; status: string; issuedAt: string; sha256: string;
  salaryPaymentId: string | null; paymentReference: string | null;
}

export interface Reconciliation {
  runId: string; runNumber: string; reconciled: boolean;
  lines: Array<{ key: string; label: string; currency: string; hr: number; ledger: number; difference: number }>;
  employees: Array<{ employeeId: string; employeeName: string; currency: string; netApproved: number; paid: number; ledgerPayable: number; ok: boolean }>;
}

export interface AdvanceCheck { code: string; passed: boolean; blocking: boolean; message: string }
export interface Installment { id: string; seqNo: number; scheduleVersion: number; dueDate: string; amount: number; recoveredAmount: number; status: string }

export interface Advance {
  id: string; advanceNumber: string; employeeId: string; employeeName: string; employeeNumber: string; kind: string; currency: string;
  requestDate: string; amountRequested: number; amountApproved: number | null; amountPaid: number; recoveredAmount: number; balance: number;
  reason: string | null; conditions: string | null; recoveryMethod: string; installmentsCount: number; recoveryPercent: number | null;
  firstRecoveryDate: string | null; status: string; requestedByName: string | null; requestedAt: string; approvedByName: string | null;
  approvedAt: string | null; decisionNote: string | null; paidByName: string | null; paidAt: string | null; financialAccountName: string | null;
  paymentMethod: string | null; paymentReference: string | null; journalEntryId: string | null; settledAt: string | null;
  cancelReason: string | null; approvalLevel: number; requiredLevels: number; checks: AdvanceCheck[]; schedule: Installment[];
}

export interface AdvanceRequestInput {
  kind: string; amount: number; reason?: string | null; recoveryMethod?: string | null; installments?: number | null;
  recoveryPercent?: number | null; firstRecoveryDate?: string | null; conditions?: string | null;
}

export interface AdvanceHistory {
  advance: Advance;
  events: Array<{ id: string; eventType: string; amount: number | null; balanceAfter: number | null; reference: string | null; note: string | null; actorName: string | null; occurredAt: string }>;
  repayments: Array<{ id: string; source: string; amount: number; currency: string; repaidAt: string; runNumber: string | null; periodLabel: string | null; reference: string | null; journalEntryId: string | null; createdByName: string | null }>;
}

export interface Attendance {
  id: string; employeeId: string; employeeName: string; workDate: string; status: string; hoursWorked: number | null; justified: boolean;
  source: string; note: string | null; validationStatus: string; validatedByName: string | null; validatedAt: string | null;
}

export interface Leave {
  id: string; employeeId: string; employeeName: string; leaveType: string; startDate: string; endDate: string; days: number; paid: boolean;
  reason: string | null; status: string; requestedByName: string | null; requestedAt: string; decidedByName: string | null;
  decidedAt: string | null; decisionNote: string | null;
}

export interface Overtime {
  id: string; employeeId: string; employeeName: string; workDate: string; hours: number; overtimeType: string; coefficient: number;
  reason: string | null; status: string; requestedByName: string | null; requestedAt: string; decidedByName: string | null;
  decidedAt: string | null; decisionNote: string | null; payrollItemId: string | null;
}

export interface PayrollVariable {
  id: string; employeeId: string; employeeName: string; periodId: string; periodLabel: string; componentCode: string; componentName: string;
  kind: string; amount: number; description: string; legalBasis: string | null; status: string; submittedByName: string | null;
  submittedAt: string; decidedByName: string | null; decidedAt: string | null; decisionNote: string | null; payrollItemId: string | null;
}

export interface ApprovalItem {
  kind: string; id: string; employeeId: string; employeeName: string; summary: string; amount: number | null; currency: string | null;
  requestedAt: string; requestedByName: string | null; canDecide: boolean; cannotDecideReason: string | null;
}

export interface ExpenseClaim {
  id: string; claimNumber: string; employeeId: string; employeeName: string; expenseDate: string; category: string; description: string;
  amount: number; currency: string; receiptDocumentId: string | null; settlement: string; status: string; submittedByName: string | null;
  submittedAt: string; decidedByName: string | null; decidedAt: string | null; decisionNote: string | null;
  approvalJournalEntryId: string | null; payrollItemId: string | null; paidAt: string | null; paymentReference: string | null;
}

export interface PayrollAdjustment {
  id: string; adjustmentNumber: string; employeeId: string; employeeName: string; originalRunId: string; originalRunNumber: string;
  originalPeriodLabel: string; originalItemId: string; kind: string; componentCode: string | null; amount: number; currency: string;
  taxable: boolean; reason: string; status: string; requestedByName: string | null; requestedAt: string; decidedByName: string | null;
  decidedAt: string | null; decisionNote: string | null; appliedItemId: string | null; appliedRunNumber: string | null; appliedAt: string | null;
}

export interface PayComponent {
  id: string; isSystem: boolean; code: string; name: string; kind: string; category: string; taxable: boolean; subjectToSocial: boolean;
  inGross: boolean; cash: boolean; requiresLegalBasis: boolean; defaultAmount: number | null; status: string;
}

export interface RuleBracket { from: number; to: number | null; rate: number }
export interface PayrollRule {
  id: string; isTemplate: boolean; code: string; name: string; jurisdiction: string; ruleType: string; baseType: string; method: string;
  rate: number | null; fixedAmount: number | null; brackets: RuleBracket[]; annualize: boolean; baseFloor: number | null; baseCeiling: number | null;
  minAmount: number | null; maxAmount: number | null; validFrom: string; validTo: string | null; version: number; status: string;
  legalReference: string | null; notes: string | null; supersedesId: string | null; validatedByName: string | null; validatedAt: string | null;
  validationNote: string | null;
}
export type RuleInput = Omit<PayrollRule, "id" | "isTemplate" | "version" | "status" | "supersedesId" | "validatedByName" | "validatedAt" | "validationNote">;

export interface Insight { code: string; severity: string; title: string; detail: string; employeeId: string | null; employeeName: string | null; amount: number | null; currency: string | null }

export interface PayrollDashboard {
  activeEmployees: number; lastPeriodLabel: string | null; lastPeriodTotals: CurrencyTotals[]; payrollToPay: CurrencyAmount[];
  openAdvances: number; advanceBalance: CurrencyAmount[]; loanBalance: CurrencyAmount[]; deductionsOfPeriod: CurrencyAmount[];
  anomalies: number; runsInValidation: number; paymentsThisMonth: CurrencyAmount[]; paymentsThisMonthCount: number;
  pendingRequests: number; insights: Insight[];
}

export interface ReportInfo { key: string; title: string; description: string; needsPeriod: boolean }
export interface ReportColumn { key: string; label: string; kind: string; currency: string | null }
export interface PayrollReport { key: string; title: string; subtitle: string | null; columns: ReportColumn[]; rows: Array<Array<string | number | null>>; totals: Array<string | number | null> | null }

export interface HistoryEntry {
  occurredAt: string; type: string; title: string; detail: string | null; amount: number | null; currency: string | null;
  reference: string | null; sourceId: string | null; actorName: string | null;
}

export interface MyProfile {
  employeeId: string; employeeNumber: string; fullName: string; jobTitle: string | null; departmentName: string | null; hireDate: string;
  status: string; contractType: string | null; payFrequency: string | null; currency: string | null; baseSalary: number | null;
  paymentMethod: string; bankAccountMasked: string | null; advanceBalance: number; loanBalance: number; annualLeaveEntitlement: number;
  annualLeaveTaken: number;
}

// --- Organisation & employés (§7, §8) --------------------------------------------------------------------

export const listDepartments = () => apiFetch<Department[]>("/departments");
export const saveDepartment = (d: { code: string; name: string; managerEmployeeId?: string | null; status?: string }, id?: string) =>
  id ? put<Department>(`/departments/${id}`, d) : post<Department>("/departments", d);

export const listEmployees = (f: { search?: string; status?: string; departmentId?: string } = {}) =>
  apiFetch<EmployeeListItem[]>(`/employees${qs(f)}`);
export const getEmployee = (id: string) => apiFetch<Employee>(`/employees/${id}`);
export const createEmployee = (e: EmployeeInput) => post<Employee>("/employees", e);
export const updateEmployee = (id: string, e: EmployeeInput) => put<Employee>(`/employees/${id}`, e);
export const setEmployeeStatus = (id: string, status: string, date?: string | null, reason?: string | null) =>
  post<Employee>(`/employees/${id}/status`, { status, date, reason });
export const linkEmployeeUser = (id: string, userId: string | null) => post<Employee>(`/employees/${id}/user`, { userId });
export const getSensitive = (id: string) => apiFetch<{ bankName: string | null; bankAccount: string | null; mobileMoney: string | null; taxId: string | null; socialId: string | null }>(`/employees/${id}/sensitive`);

export const listContracts = (id: string) => apiFetch<Contract[]>(`/employees/${id}/contracts`);
export const createContract = (id: string, c: ContractInput) => post<Contract>(`/employees/${id}/contracts`, c);
export const endContract = (contractId: string, endDate: string, reason: string, terminated = false) =>
  post<Contract>(`/employee-contracts/${contractId}/end`, { endDate, reason, terminated });
export const salaryHistory = (id: string) => apiFetch<SalaryHistoryEntry[]>(`/employees/${id}/salary-history`);
export const changeSalary = (id: string, c: { effectiveDate: string; amount: number; hourlyRate?: number | null; reason: string; contractId?: string | null }) =>
  post<SalaryHistoryEntry>(`/employees/${id}/salary-changes`, c);
export const applicableSalary = (id: string, date: string) =>
  apiFetch<{ date: string; contractNumber: string | null; amount: number | null; payBasis: string | null; currency: string | null; effectiveDate: string | null }>(
    `/employees/${id}/compensation${qs({ date })}`);
export const listPayElements = (id: string) => apiFetch<PayElement[]>(`/employees/${id}/pay-elements`);
export const addPayElement = (id: string, e: { componentCode: string; calcMethod: string; amount?: number | null; rate?: number | null; startDate: string; endDate?: string | null; note?: string | null }) =>
  post<PayElement>(`/employees/${id}/pay-elements`, e);
export const endPayElement = (elementId: string, endDate: string) => post<PayElement>(`/employee-pay-elements/${elementId}/end`, { endDate });

export const listDocuments = (id: string, self = false) => apiFetch<EmployeeDocument[]>(self ? "/me/hr/documents" : `/employees/${id}/documents`);
export const uploadDocument = (id: string, d: { documentType: string; title: string; fileName: string; contentType: string; contentBase64: string; visibleToEmployee?: boolean }) =>
  post<EmployeeDocument>(`/employees/${id}/documents`, d);
export async function downloadEmployeeDocument(documentId: string, self = false) {
  const { blob, filename } = await apiDownload(self ? `/me/hr/documents/${documentId}/content` : `/employee-documents/${documentId}/content`);
  saveFile(blob, filename ?? "document");
}
export const employeeHistory = (id: string, self = false) => apiFetch<HistoryEntry[]>(self ? "/me/hr/history" : `/employees/${id}/history`);

/** Lit un fichier choisi par l'utilisateur en base64 (sans l'en-tête data:). */
export function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(",", 2)[1] ?? "");
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

// --- Paie (§9, §22-§25, §32) --------------------------------------------------------------------------------

export const listPeriods = (year?: number) => apiFetch<PayrollPeriod[]>(`/payroll/periods${qs({ year })}`);
export const createPeriod = (p: { frequency: string; startDate: string; endDate: string; cutoffDate?: string | null; paymentDate?: string | null; kind?: string; label?: string | null; employeeIds?: string[] }) =>
  post<PayrollPeriod>("/payroll/periods", p);
export const calculatePeriod = (periodId: string) => post<PayrollRun>(`/payroll/periods/${periodId}/calculate`);
export const getRun = (runId: string) => apiFetch<PayrollRun>(`/payroll/${runId}`);
export const getItem = (itemId: string, self = false) => apiFetch<PayrollItemDetail>(self ? `/me/hr/payroll-items/${itemId}` : `/payroll/items/${itemId}`);
export const excludeItem = (itemId: string, reason: string) => post<PayrollRun>(`/payroll/items/${itemId}/exclude`, { reason });
export const includeItem = (itemId: string) => post<PayrollRun>(`/payroll/items/${itemId}/include`);

export type WorkflowStep = "review" | "validate" | "approve" | "return" | "close";
export const runStep = (runId: string, step: WorkflowStep, note?: string | null, acknowledgeWarnings = false) =>
  post<PayrollRun>(`/payroll/${runId}/${step}`, { note, acknowledgeWarnings });
export const cancelRun = (runId: string, reason: string) => post<PayrollRun>(`/payroll/${runId}/cancel`, { reason });

export const paymentAccounts = () => apiFetch<PaymentAccount[]>("/payroll/payment-accounts");
/** Paiement individuel ou groupé : l'identifiant d'opération sert aussi de clé d'idempotence (§39). */
export const payRun = (runId: string, p: { financialAccountId: string; paymentMethod?: string | null; itemIds?: string[] | null; reference?: string | null; paidOn?: string | null }, operationUuid: string) =>
  post<PayRunResult>(`/payroll/${runId}/pay`, { ...p, operationUuid }, operationUuid);
export const cancelSalaryPayment = (paymentId: string, reason: string) =>
  post<SalaryPayment>(`/salary-payments/${paymentId}/cancel`, { reason }, crypto.randomUUID());
export const runPayments = (runId: string) => apiFetch<SalaryPayment[]>(`/payroll/${runId}/payments`);
export const runPayslips = (runId: string) => apiFetch<Payslip[]>(`/payroll/${runId}/payslips`);
export const generatePayslips = (runId: string) => post<Payslip[]>(`/payroll/${runId}/payslips`);
export const employeePayslips = (id: string, self = false) => apiFetch<Payslip[]>(self ? "/me/hr/payslips" : `/employees/${id}/payslips`);
export const reconcileRun = (runId: string) => apiFetch<Reconciliation>(`/payroll/${runId}/reconciliation`);
export const verifyPayslip = (id: string) => apiFetch<{ payslipNumber: string; integrityOk: boolean }>(`/payslips/${id}/verify`);

const payslipPath = (id: string, format: string, self: boolean) =>
  self ? `/me/hr/payslips/${id}/file?format=${format}` : `/payslips/${id}/file?format=${format}`;
/** Ouvre le bulletin (vrai PDF) pour impression dans un onglet réservé pendant le clic. */
export const printPayslip = (id: string, self = false) => openPrintable(payslipPath(id, "pdf", self), "bulletin.pdf");
export async function downloadPayslip(id: string, format: "pdf" | "xlsx" = "pdf", self = false) {
  const { blob, filename } = await apiDownload(payslipPath(id, format, self));
  saveFile(blob, filename ?? `bulletin.${format}`);
}

// --- Avances, acomptes, prêts (§12-§17) ---------------------------------------------------------------------

export const listAdvances = (f: { employeeId?: string; status?: string; kind?: string } = {}, self = false) =>
  apiFetch<Advance[]>(self ? "/me/hr/advances" : `/salary-advances${qs(f)}`);
export const getAdvance = (id: string) => apiFetch<Advance>(`/salary-advances/${id}`);
export const previewAdvance = (employeeId: string, r: AdvanceRequestInput, self = false) =>
  post<AdvanceCheck[]>(self ? "/me/hr/advances/preview" : `/employees/${employeeId}/salary-advances/preview`, r);
export const requestAdvance = (employeeId: string, r: AdvanceRequestInput, self = false) =>
  post<Advance>(self ? "/me/hr/advances" : `/employees/${employeeId}/salary-advances`, r);
export const approveAdvance = (id: string, r: { amountApproved?: number | null; note?: string | null; recoveryMethod?: string | null; installments?: number | null; recoveryPercent?: number | null; firstRecoveryDate?: string | null }) =>
  post<Advance>(`/salary-advances/${id}/approve`, r);
export const rejectAdvance = (id: string, note: string) => post<Advance>(`/salary-advances/${id}/reject`, { note });
export const cancelAdvance = (id: string, reason: string, self = false) =>
  post<Advance>(self ? `/me/hr/advances/${id}/cancel` : `/salary-advances/${id}/cancel`, { reason });
export const payAdvance = (id: string, p: { financialAccountId: string; paymentMethod: string; reference?: string | null; paidOn?: string | null }, operationUuid: string) =>
  post<Advance>(`/salary-advances/${id}/pay`, { ...p, operationUuid }, operationUuid);
export const repayAdvance = (id: string, p: { amount: number; financialAccountId: string; paymentMethod: string; reference?: string | null; note?: string | null }, operationUuid: string) =>
  post<Advance>(`/salary-advances/${id}/repay`, { ...p, operationUuid }, operationUuid);
export const rescheduleAdvance = (id: string, installments: Array<{ dueDate: string; amount: number }>, reason: string) =>
  put<Advance>(`/salary-advances/${id}/schedule`, { installments, reason });
export const advanceHistory = (id: string, self = false) =>
  apiFetch<AdvanceHistory>(self ? `/me/hr/advances/${id}/history` : `/salary-advances/${id}/history`);

// --- Temps, variables, frais, validations (§17-§20) -----------------------------------------------------------

export const listAttendance = (f: { employeeId?: string; from?: string; to?: string; validationStatus?: string } = {}) =>
  apiFetch<Attendance[]>(`/hr/attendance${qs(f)}`);
export const recordAttendance = (records: Array<{ employeeId: string; workDate: string; status: string; hoursWorked?: number | null; justified: boolean; note?: string | null }>, source = "manual") =>
  post<{ created: number; updated: number; errors: string[] }>("/hr/attendance", { records, source });
export const decideAttendance = (id: string, validate: boolean, note?: string | null) =>
  post<Attendance>(`/hr/attendance/${id}/${validate ? "validate" : "reject"}`, { note });

export const listLeaves = (f: { employeeId?: string; status?: string } = {}, self = false) =>
  apiFetch<Leave[]>(self ? "/me/hr/leaves" : `/hr/leaves${qs(f)}`);
export const requestLeave = (r: { employeeId: string; leaveType: string; startDate: string; endDate: string; days?: number | null; paid?: boolean | null; reason?: string | null }, self = false) =>
  post<Leave>(self ? "/me/hr/leaves" : "/hr/leaves", r);
export const decideLeave = (id: string, approve: boolean, note?: string | null) => post<Leave>(`/hr/leaves/${id}/${approve ? "approve" : "reject"}`, { note });
export const cancelLeave = (id: string, reason: string, self = false) => post<Leave>(self ? `/me/hr/leaves/${id}/cancel` : `/hr/leaves/${id}/cancel`, { reason });

export const listOvertime = (f: { employeeId?: string; status?: string } = {}, self = false) =>
  apiFetch<Overtime[]>(self ? "/me/hr/overtime" : `/hr/overtime${qs(f)}`);
export const recordOvertime = (r: { employeeId: string; workDate: string; hours: number; overtimeType: string; reason?: string | null }, self = false) =>
  post<Overtime>(self ? "/me/hr/overtime" : "/hr/overtime", r);
export const decideOvertime = (id: string, approve: boolean, note?: string | null) => post<Overtime>(`/hr/overtime/${id}/${approve ? "approve" : "reject"}`, { note });

export const listVariables = (f: { periodId?: string; employeeId?: string; status?: string } = {}) => apiFetch<PayrollVariable[]>(`/hr/variables${qs(f)}`);
export const submitVariable = (v: { employeeId: string; periodId: string; componentCode: string; amount: number; description: string; legalBasis?: string | null }) =>
  post<PayrollVariable>("/hr/variables", v);
export const decideVariable = (id: string, approve: boolean, note?: string | null) => post<PayrollVariable>(`/hr/variables/${id}/${approve ? "approve" : "reject"}`, { note });
export const cancelVariable = (id: string, reason: string) => post<PayrollVariable>(`/hr/variables/${id}/cancel`, { reason });

export const pendingApprovals = () => apiFetch<ApprovalItem[]>("/hr/approvals");

export const listExpenseClaims = (f: { employeeId?: string; status?: string } = {}, self = false) =>
  apiFetch<ExpenseClaim[]>(self ? "/me/hr/expense-claims" : `/hr/expense-claims${qs(f)}`);
export const submitExpenseClaim = (c: { employeeId: string; expenseDate: string; category: string; description: string; amount: number; currency?: string | null; settlement?: string; receipt?: { documentType: string; title: string; fileName: string; contentType: string; contentBase64: string } | null }, self = false) =>
  post<ExpenseClaim>(self ? "/me/hr/expense-claims" : "/hr/expense-claims", c);
export const approveExpenseClaim = (id: string, note?: string | null) => post<ExpenseClaim>(`/hr/expense-claims/${id}/approve`, { note });
export const rejectExpenseClaim = (id: string, note: string) => post<ExpenseClaim>(`/hr/expense-claims/${id}/reject`, { note });
export const payExpenseClaim = (id: string, financialAccountId: string, reference: string | null, operationUuid: string) =>
  post<ExpenseClaim>(`/hr/expense-claims/${id}/pay`, { financialAccountId, reference, operationUuid }, operationUuid);

// --- Régularisations (§36), paramétrage (§11, §20) -----------------------------------------------------------

export const listAdjustments = (f: { employeeId?: string; status?: string } = {}) => apiFetch<PayrollAdjustment[]>(`/payroll/adjustments${qs(f)}`);
export const requestAdjustment = (r: { originalItemId: string; kind: "earning" | "deduction"; amount: number; taxable?: boolean; reason: string; componentCode?: string | null }) =>
  post<PayrollAdjustment>("/payroll/adjustments", r);
export const decideAdjustment = (id: string, approve: boolean, note?: string | null) =>
  post<PayrollAdjustment>(`/payroll/adjustments/${id}/${approve ? "approve" : "reject"}`, { note });

export const listComponents = () => apiFetch<PayComponent[]>("/payroll/components");
export const saveComponent = (c: Omit<PayComponent, "id" | "isSystem">, id?: string) =>
  id ? put<PayComponent>(`/payroll/components/${id}`, c) : post<PayComponent>("/payroll/components", c);
export const listRules = () => apiFetch<PayrollRule[]>("/payroll/rules?includeTemplates=true");
export const createRule = (r: RuleInput) => post<PayrollRule>("/payroll/rules", r);
export const updateRule = (id: string, r: RuleInput) => put<PayrollRule>(`/payroll/rules/${id}`, r);
export const newRuleVersion = (id: string, r: RuleInput) => post<PayrollRule>(`/payroll/rules/${id}/versions`, r);
export const validateRule = (id: string, note: string) => post<PayrollRule>(`/payroll/rules/${id}/validate`, { note });
export const retireRule = (id: string, validTo: string) => post<PayrollRule>(`/payroll/rules/${id}/retire`, { validTo });
export const adoptTemplate = (id: string) => post<PayrollRule>(`/payroll/rule-templates/${id}/adopt`);

// --- Tableau de bord, rapports (§27, §28), espace employé ---------------------------------------------------

export const payrollDashboard = () => apiFetch<PayrollDashboard>("/hr/dashboard");
export const reportCatalog = () => apiFetch<ReportInfo[]>("/hr/reports");
export const runReport = (key: string, f: { from?: string; to?: string; periodId?: string } = {}) => apiFetch<PayrollReport>(`/hr/reports/${key}${qs(f)}`);
export async function exportReport(key: string, format: "xlsx" | "pdf" | "csv", f: { from?: string; to?: string; periodId?: string } = {}) {
  const { blob, filename } = await apiDownload(`/hr/reports/${key}/export${qs({ ...f, format })}`);
  saveFile(blob, filename ?? `paie-${key}.${format}`);
}
export const myProfile = () => apiFetch<MyProfile>("/me/hr/profile");
