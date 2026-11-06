import { useCallback, useEffect, useState } from "react";
import { Link, useNavigate, useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, DataTable, EmptyState, Field, KpiCard, Money, SectionLabel, Select, Spinner, Tabs, TextInput, type Column } from "@/shared/ui";
import { ExportMenu } from "@/shared/ExportMenu";
import { dateShort, money, todayIso } from "@/shared/format";
import {
  EMPLOYEE_STATUS, FREQUENCIES, PERIOD_STATUS, RULE_BASES, RULE_STATUS, RULE_TYPES, adoptTemplate, calculatePeriod, exportReport,
  listComponents, listEmployees, listPeriods, listRules, payrollDashboard, reportCatalog, retireRule, runReport, validateRule,
  type EmployeeListItem, type PayComponent, type PayrollDashboard, type PayrollPeriod, type PayrollReport, type PayrollRule, type ReportInfo,
} from "@/hr/hrApi";
import { EmployeeDialog, PeriodDialog, PromptDialog, RuleDialog, errorText } from "@/hr/HrDialogs";
import { AdjustmentsPanel, AdvancesPanel, ApprovalsPanel, ExpensesPanel, TimePanel, VariablesPanel } from "@/hr/HrPanels";

type Tone = "success" | "warning" | "error" | "info" | undefined;
export const statusTone = (s: string): Tone =>
  ["active", "approved", "paid", "closed", "settled", "validated", "applied"].includes(s) ? "success"
  : ["requested", "submitted", "calculated", "reviewed", "pending", "in_validation", "in_repayment", "in_payroll", "draft"].includes(s) ? "info"
  : ["rejected", "cancelled", "terminated"].includes(s) ? "error" : "warning";
const amounts = (xs: Array<{ currency: string; amount: number }>) =>
  xs.length === 0 ? "—" : xs.map((x) => money(x.amount, x.currency)).join(" · ");

/**
 * RH & Paie (docs/module-paie-rh-avances.md) : chaque onglet n'apparaît qu'avec sa permission, mais c'est le serveur
 * qui décide (§5 « cacher un menu ne constitue pas une sécurité ») — un salaire n'arrive jamais à un poste qui n'y a pas droit.
 */
export function HrPage() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const tabs = [
    hasPermission("payroll.reports") && { id: "dashboard", label: "Tableau de bord" },
    hasPermission("hr.view_employees") && { id: "employees", label: "Employés" },
    hasPermission("payroll.view_salaries") && { id: "payroll", label: "Paie" },
    hasPermission("hr.view_employees") && { id: "advances", label: "Avances & prêts" },
    hasPermission("hr.view_employees") && { id: "time", label: "Temps & absences" },
    hasPermission("hr.view_employees") && { id: "variables", label: "Variables" },
    hasPermission("hr.view_employees") && { id: "expenses", label: "Notes de frais" },
    hasPermission("payroll.view_salaries") && { id: "adjustments", label: "Régularisations" },
    hasPermission("hr.view_employees") && { id: "approvals", label: "À valider" },
    hasPermission("payroll.reports") && { id: "reports", label: "Rapports" },
    hasPermission("payroll.configure") && { id: "settings", label: "Règles & composants" },
  ].filter(Boolean) as Array<{ id: string; label: string }>;
  const tab = params.get("tab") ?? tabs[0]?.id ?? "employees";
  const setTab = (t: string) => setParams((p) => { p.set("tab", t); return p; }, { replace: true });

  if (tabs.length === 0) return <Card><EmptyState title="RH & Paie" hint="Aucun droit RH ou paie sur cette entreprise." /></Card>;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div>
        <h1 style={{ margin: 0 }}>RH & Paie</h1>
        <div style={{ color: "var(--nx-text-2)" }}>Employés, contrats, temps, paie, avances et bulletins — montants calculés et contrôlés par le serveur.</div>
      </div>
      <Tabs tabs={tabs} active={tab} onChange={setTab} />
      {tab === "dashboard" && <DashboardPanel onOpen={setTab} />}
      {tab === "employees" && <EmployeesPanel />}
      {tab === "payroll" && <PeriodsPanel />}
      {tab === "advances" && <AdvancesPanel />}
      {tab === "time" && <TimePanel />}
      {tab === "variables" && <VariablesPanel />}
      {tab === "expenses" && <ExpensesPanel />}
      {tab === "adjustments" && <AdjustmentsPanel />}
      {tab === "approvals" && <ApprovalsPanel />}
      {tab === "reports" && <ReportsPanel />}
      {tab === "settings" && <SettingsPanel />}
    </div>
  );
}

// ================================================================== Tableau de bord (§28, §33)

function DashboardPanel({ onOpen }: { onOpen: (tab: string) => void }) {
  const [d, setD] = useState<PayrollDashboard | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => { payrollDashboard().then(setD).catch((e) => setError(errorText(e))); }, []);
  if (error) return <Card><EmptyState title="Tableau de bord" hint={error} /></Card>;
  if (!d) return <Card><Spinner /></Card>;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "grid", gap: 12, gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))" }}>
        <KpiCard label="Employés actifs" value={d.activeEmployees} />
        <KpiCard label={`Masse salariale brute${d.lastPeriodLabel ? ` — ${d.lastPeriodLabel}` : ""}`}
                 value={d.lastPeriodTotals.length ? d.lastPeriodTotals.map((t) => money(t.gross, t.currency)).join(" · ") : "—"} />
        <KpiCard label="Net de la dernière paie" value={d.lastPeriodTotals.length ? d.lastPeriodTotals.map((t) => money(t.net, t.currency)).join(" · ") : "—"} />
        <KpiCard label="Paie à payer" value={amounts(d.payrollToPay)} tone={d.payrollToPay.length ? "warning" : undefined} tag={d.payrollToPay.length ? "à régler" : undefined} />
        <KpiCard label={`Avances en cours (${d.openAdvances})`} value={amounts(d.advanceBalance)} />
        <KpiCard label="Prêts au personnel" value={amounts(d.loanBalance)} />
        <KpiCard label="Retenues de la période" value={amounts(d.deductionsOfPeriod)} />
        <KpiCard label="Paiements du mois" value={`${amounts(d.paymentsThisMonth)} (${d.paymentsThisMonthCount})`} />
        <KpiCard label="Paies en validation" value={d.runsInValidation} />
        <KpiCard label="Anomalies à contrôler" value={d.anomalies} tone={d.anomalies ? "warning" : undefined} />
        <KpiCard label="Demandes à valider" value={<button className="nx-linkbtn" onClick={() => onOpen("approvals")}>{d.pendingRequests}</button>} />
      </div>
      <Card>
        <SectionLabel>Intelligence de paie — constats déterministes (§33)</SectionLabel>
        {d.insights.length === 0 ? <EmptyState title="Aucun constat" hint="Pas d'écart inhabituel de masse salariale, d'heures supplémentaires ou d'avances." /> : (
          <ul style={{ margin: 0, paddingLeft: 18, display: "grid", gap: 8 }}>
            {d.insights.map((i, k) => (
              <li key={k}><Badge tone={i.severity === "warning" ? "warning" : "info"}>{i.title}</Badge> {i.detail}</li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

// ================================================================== Employés (§7)

function EmployeesPanel() {
  const { hasPermission } = useAuth();
  const navigate = useNavigate();
  const [rows, setRows] = useState<EmployeeListItem[] | null>(null);
  const [search, setSearch] = useState("");
  const [status, setStatus] = useState("active");
  const [creating, setCreating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    listEmployees({ search: search || undefined, status: status || undefined }).then(setRows).catch((e) => setError(errorText(e)));
  }, [search, status]);
  useEffect(() => { const t = setTimeout(load, 250); return () => clearTimeout(t); }, [load]);
  const columns: Array<Column<EmployeeListItem>> = [
    { key: "number", header: "Matricule", render: (r) => <Link to={`/hr/employees/${r.id}`}>{r.employeeNumber}</Link> },
    { key: "name", header: "Nom", render: (r) => `${r.firstName} ${r.lastName}` },
    { key: "job", header: "Poste", render: (r) => r.jobTitle ?? "—" },
    { key: "dept", header: "Département", render: (r) => r.departmentName ?? "—" },
    { key: "hire", header: "Entrée", render: (r) => dateShort(r.hireDate) },
    { key: "contract", header: "Contrat", render: (r) => r.contractType ?? "—" },
    { key: "salary", header: "Salaire", numeric: true, render: (r) => (r.currentSalary == null ? "—" : <Money value={r.currentSalary} currency={r.currency ?? "HTG"} />) },
    { key: "status", header: "Statut", render: (r) => <Badge tone={statusTone(r.status)}>{EMPLOYEE_STATUS[r.status] ?? r.status}</Badge> },
  ];
  return (
    <Card>
      <SectionLabel action={hasPermission("hr.manage_employees") && <Button size="sm" onClick={() => setCreating(true)}>Nouvel employé</Button>}>
        Dossiers employés
      </SectionLabel>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 10 }}>
        <TextInput placeholder="Rechercher (nom, matricule)" value={search} onChange={(e) => setSearch(e.target.value)} style={{ maxWidth: 320 }} />
        <Select value={status} onChange={(e) => setStatus(e.target.value)} style={{ maxWidth: 200 }}>
          <option value="">Tous les statuts</option>
          {Object.entries(EMPLOYEE_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </Select>
      </div>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {rows ? <DataTable columns={columns} rows={rows} rowKey={(r) => r.id} empty="Aucun employé." exportTitle="Employés" /> : <Spinner />}
      {creating && (
        <EmployeeDialog canSetSalary={hasPermission("payroll.manage_compensation")} onClose={() => setCreating(false)}
                        onDone={(e) => { setCreating(false); void navigate(`/hr/employees/${e.id}`); }} />
      )}
    </Card>
  );
}

// ================================================================== Périodes de paie (§9)

function PeriodsPanel() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const navigate = useNavigate();
  const [year, setYear] = useState(new Date().getFullYear());
  const [rows, setRows] = useState<PayrollPeriod[] | null>(null);
  const [creating, setCreating] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const load = useCallback(() => { listPeriods(year).then(setRows).catch(() => setRows([])); }, [year]);
  useEffect(load, [load]);
  const calculate = async (p: PayrollPeriod) => {
    setBusy(p.id);
    try {
      const run = await calculatePeriod(p.id);
      toast.success(`Paie ${run.runNumber} calculée : ${run.itemCount} employé(s), ${run.blockingCount} anomalie(s) bloquante(s).`);
      void navigate(`/hr/payroll/${run.id}`);
    } catch (e) { toast.error(errorText(e)); } finally { setBusy(null); }
  };
  const columns: Array<Column<PayrollPeriod>> = [
    { key: "label", header: "Période", render: (p) => p.runId ? <Link to={`/hr/payroll/${p.runId}`}>{p.label}</Link> : p.label },
    { key: "dates", header: "Dates", render: (p) => `${dateShort(p.startDate)} → ${dateShort(p.endDate)}` },
    { key: "freq", header: "Fréquence", render: (p) => (p.kind === "off_cycle" ? "Hors cycle" : FREQUENCIES[p.frequency]) },
    { key: "pay", header: "Paiement prévu", render: (p) => dateShort(p.paymentDate) },
    { key: "emp", header: "Employés", numeric: true, render: (p) => p.employeeCount },
    { key: "net", header: "Net", numeric: true, render: (p) => p.totals.map((t) => money(t.net, t.currency)).join(" · ") || "—" },
    { key: "status", header: "Statut", render: (p) => <Badge tone={statusTone(p.status)}>{PERIOD_STATUS[p.status] ?? p.status}</Badge> },
    { key: "act", header: "", render: (p) => (
      <div style={{ display: "flex", gap: 6 }}>
        {p.runId && <Button size="sm" variant="ghost" onClick={() => navigate(`/hr/payroll/${p.runId}`)}>Ouvrir</Button>}
        {hasPermission("payroll.calculate") && ["draft", "calculated", "in_validation"].includes(p.status) && p.runStatus !== "validated" && (
          <Button size="sm" loading={busy === p.id} onClick={() => calculate(p)}>{p.status === "draft" ? "Calculer" : "Recalculer"}</Button>
        )}
      </div>
    ) },
  ];
  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 8 }}>
          <Select value={year} onChange={(e) => setYear(Number(e.target.value))}>
            {[0, 1, 2].map((d) => new Date().getFullYear() - d).map((y) => <option key={y} value={y}>{y}</option>)}
          </Select>
          {hasPermission("payroll.calculate") && <Button size="sm" onClick={() => setCreating(true)}>Nouvelle période</Button>}
        </div>
      }>Périodes de paie</SectionLabel>
      {rows ? <DataTable columns={columns} rows={rows} rowKey={(r) => r.id} empty="Aucune période cette année." exportTitle="Périodes de paie" /> : <Spinner />}
      {creating && <PeriodDialog onClose={() => setCreating(false)} onDone={(p) => { setCreating(false); toast.success(`Période ${p.label} créée`); load(); }} />}
    </Card>
  );
}

// ================================================================== Rapports (§27)

function ReportsPanel() {
  const [catalog, setCatalog] = useState<ReportInfo[]>([]);
  const [key, setKey] = useState("payroll_mass");
  const [from, setFrom] = useState(`${new Date().getFullYear()}-01-01`);
  const [to, setTo] = useState(todayIso());
  const [periods, setPeriods] = useState<PayrollPeriod[]>([]);
  const [periodId, setPeriodId] = useState("");
  const [report, setReport] = useState<PayrollReport | null>(null);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    reportCatalog().then(setCatalog).catch(() => {});
    listPeriods().then((p) => { setPeriods(p); if (p[0]) setPeriodId(p[0].id); }).catch(() => {});
  }, []);
  const info = catalog.find((c) => c.key === key);
  const filter = { from, to, periodId: info?.needsPeriod ? periodId : undefined };
  const run = () => { setError(null); runReport(key, filter).then(setReport).catch((e) => { setReport(null); setError(errorText(e)); }); };
  useEffect(run, [key, from, to, periodId, info?.needsPeriod]); // eslint-disable-line react-hooks/exhaustive-deps
  const cell = (v: string | number | null, kind: string, currency: string | null) =>
    v == null ? "—" : kind === "money" && typeof v === "number" ? money(v, currency ?? undefined) : kind === "date" && typeof v === "string" ? dateShort(v)
      : kind === "percent" && typeof v === "number" ? `${v.toLocaleString("fr-FR")} %` : String(v);
  return (
    <Card>
      <SectionLabel action={<ExportMenu run={(format) => exportReport(key, format, filter)} />}>Rapports de paie</SectionLabel>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 10 }}>
        <Select value={key} onChange={(e) => setKey(e.target.value)} style={{ maxWidth: 320 }}>
          {catalog.map((c) => <option key={c.key} value={c.key}>{c.title}</option>)}
        </Select>
        {info?.needsPeriod ? (
          <Select value={periodId} onChange={(e) => setPeriodId(e.target.value)}>
            {periods.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
          </Select>
        ) : (
          <>
            <Field label="Du"><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
            <Field label="Au"><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
          </>
        )}
      </div>
      {info && <p style={{ marginTop: 0, color: "var(--nx-text-2)" }}>{info.description}{report?.subtitle ? ` — ${report.subtitle}` : ""}</p>}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {report && (
        <div className="nx-scroll-x">
          <table className="nx-table">
            <thead><tr>{report.columns.map((c) => <th key={c.key} className={c.kind === "money" ? "nx-num" : undefined}>{c.label}</th>)}</tr></thead>
            <tbody>
              {report.rows.length === 0 && <tr><td colSpan={report.columns.length} style={{ textAlign: "center", color: "var(--nx-text-2)" }}>Aucune donnée.</td></tr>}
              {report.rows.map((row, i) => {
                const cur = String(row[report.columns.findIndex((c) => c.key === "currency")] ?? "") || null;
                return <tr key={i}>{row.map((v, j) => <td key={j} className={report.columns[j].kind === "money" ? "nx-num" : undefined}>{cell(v, report.columns[j].kind, cur)}</td>)}</tr>;
              })}
            </tbody>
            {report.totals && (
              <tfoot><tr>{report.totals.map((v, j) => {
                const cur = String(report.totals![report.columns.findIndex((c) => c.key === "currency")] ?? "") || null;
                return <td key={j} className={report.columns[j].kind === "money" ? "nx-num" : undefined}><b>{cell(v, report.columns[j].kind, cur)}</b></td>;
              })}</tr></tfoot>
            )}
          </table>
        </div>
      )}
    </Card>
  );
}

// ================================================================== Règles légales et composants (§11, §20)

function SettingsPanel() {
  const toast = useToast();
  const [rules, setRules] = useState<PayrollRule[] | null>(null);
  const [components, setComponents] = useState<PayComponent[]>([]);
  const [editing, setEditing] = useState<{ rule?: PayrollRule; mode: "create" | "edit" | "version" } | null>(null);
  const [prompt, setPrompt] = useState<{ rule: PayrollRule; action: "validate" | "retire" } | null>(null);
  const load = useCallback(() => {
    listRules().then(setRules).catch(() => setRules([]));
    listComponents().then(setComponents).catch(() => {});
  }, []);
  useEffect(load, [load]);
  const act = async (f: () => Promise<unknown>, ok: string) => {
    try { await f(); toast.success(ok); load(); } catch (e) { toast.error(errorText(e)); }
  };
  const columns: Array<Column<PayrollRule>> = [
    { key: "code", header: "Code", render: (r) => <b>{r.code}</b> },
    { key: "name", header: "Règle", render: (r) => r.name },
    { key: "type", header: "Type", render: (r) => RULE_TYPES[r.ruleType] },
    { key: "base", header: "Assiette", render: (r) => RULE_BASES[r.baseType] },
    { key: "value", header: "Calcul", render: (r) => r.method === "percentage" ? `${r.rate} %` : r.method === "fixed" ? money(r.fixedAmount ?? 0)
        : `Barème ${r.annualize ? "annuel" : ""} (${r.brackets.length} tranches)` },
    { key: "valid", header: "Validité", render: (r) => `${dateShort(r.validFrom)}${r.validTo ? ` → ${dateShort(r.validTo)}` : ""}` },
    { key: "v", header: "Version", numeric: true, render: (r) => r.version },
    { key: "status", header: "Statut", render: (r) => <Badge tone={r.status === "validated" ? "success" : r.status === "template" ? "info" : r.status === "draft" ? "warning" : undefined}>{RULE_STATUS[r.status]}</Badge> },
    { key: "by", header: "Validée par", render: (r) => r.validatedByName ?? "—" },
    { key: "act", header: "", render: (r) => (
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        {r.isTemplate && <Button size="sm" variant="ghost" onClick={() => act(() => adoptTemplate(r.id), "Modèle adopté en brouillon : à vérifier puis valider")}>Adopter</Button>}
        {r.status === "draft" && <Button size="sm" variant="ghost" onClick={() => setEditing({ rule: r, mode: "edit" })}>Modifier</Button>}
        {r.status === "draft" && <Button size="sm" onClick={() => setPrompt({ rule: r, action: "validate" })}>Valider</Button>}
        {r.status === "validated" && <Button size="sm" variant="ghost" onClick={() => setEditing({ rule: r, mode: "version" })}>Nouvelle version</Button>}
        {r.status === "validated" && <Button size="sm" variant="ghost" onClick={() => setPrompt({ rule: r, action: "retire" })}>Retirer</Button>}
      </div>
    ) },
  ];
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={<Button size="sm" onClick={() => setEditing({ mode: "create" })}>Nouvelle règle</Button>}>Règles fiscales et sociales (versionnées)</SectionLabel>
        <p style={{ marginTop: 0, color: "var(--nx-text-2)" }}>
          Les modèles fournis (Haïti) ne sont jamais appliqués d'office : adoptez-les, vérifiez taux, assiettes et barèmes au regard des textes en vigueur,
          puis faites-les valider par une autre personne. Une règle validée est figée ; toute évolution crée une nouvelle version datée.
        </p>
        {rules ? <DataTable columns={columns} rows={rules} rowKey={(r) => r.id} exportTitle="Règles de paie" /> : <Spinner />}
      </Card>
      <Card>
        <SectionLabel>Composants de paie</SectionLabel>
        <DataTable<PayComponent> rows={components} rowKey={(c) => c.id} exportTitle="Composants de paie" columns={[
          { key: "code", header: "Code", render: (c) => c.code },
          { key: "name", header: "Libellé", render: (c) => c.name },
          { key: "kind", header: "Nature", render: (c) => (c.kind === "earning" ? "Gain" : "Retenue") },
          { key: "cat", header: "Catégorie", render: (c) => c.category },
          { key: "flags", header: "Imposable / social / brut / espèces", render: (c) => [c.taxable, c.subjectToSocial, c.inGross, c.cash].map((b) => (b ? "oui" : "non")).join(" / ") },
          { key: "sys", header: "", render: (c) => (c.isSystem ? <Badge>Système</Badge> : <Badge tone="info">Entreprise</Badge>) },
        ]} />
      </Card>
      {prompt?.action === "validate" && (
        <PromptDialog title={`Valider ${prompt.rule.code} v${prompt.rule.version}`} label="Vérification effectuée" type="textarea" minLength={5}
                      hint="Texte de référence, date, taux et assiettes contrôlés — conservé comme preuve de validation"
                      submitLabel="Valider la règle" onClose={() => setPrompt(null)}
                      onSubmit={async (note) => { await validateRule(prompt.rule.id, note); setPrompt(null); toast.success("Règle validée : elle s'applique aux paies de sa période"); load(); }} />
      )}
      {prompt?.action === "retire" && (
        <PromptDialog title={`Retirer ${prompt.rule.code} v${prompt.rule.version}`} label="Dernier jour d'application" type="date"
                      initial={todayIso()} submitLabel="Retirer la règle" onClose={() => setPrompt(null)}
                      onSubmit={async (date) => { await retireRule(prompt.rule.id, date); setPrompt(null); toast.success("Règle retirée"); load(); }} />
      )}
      {editing && <RuleDialog rule={editing.rule} mode={editing.mode} onClose={() => setEditing(null)} onDone={() => { setEditing(null); toast.success("Règle enregistrée en brouillon"); load(); }} />}
    </div>
  );
}
