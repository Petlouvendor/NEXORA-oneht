import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link, useParams, useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, EmptyState, Field, KpiCard, Modal, Money, SectionLabel, Select,
  Spinner, Tabs, TextInput, type Column,
} from "@/shared/ui";
import { dateShort, dateTime, money } from "@/shared/format";
import {
  ACTION_OUTCOMES, ACTION_TYPES, applyCreditMemo, approveExpense, approveRefund, cancelExpense, cancelRefund,
  changeCustomerStatus, CREDIT_STATUS, CUSTOMER_STATUS, EXPENSE_CATEGORIES, EXPENSE_STATUS, executeRefund, getAccountSummary,
  getCredit, getCustomer, getLedger, getPaymentOptions, getTimeline, listCollectionActions, listCreditMemos, listCredits,
  listExpenses, listPayments, listPurchases, listRefunds, logCollectionAction, MEMO_STATUS, patchCustomer, REFUND_STATUS,
  rejectExpense, rejectRefund, submitExpense, voidPayment,
  type CollectionAction, type CreditDetail, type CreditMemo, type CreditSettlement, type CustomerAccountSummary, type CustomerCredit,
  type CustomerExpense, type CustomerLedger, type CustomerPayment, type CustomerProfile, type CustomerPurchase,
  type CustomerRefund, type PaymentOptions, type TimelineEvent,
} from "@/customers/customersApi";
import { ExpenseDialog, OpenCreditDialog, PaymentDialog, RefundDialog, ScheduleForm, errorText } from "@/customers/CustomerDialogs";
import { ActionMenu, PrintMenu } from "@/shared/ExportMenu";
import { Icon } from "@/shared/icons";
import { customerStatement } from "@/shared/printApi";
import { listCustomerGroups, type CustomerGroup } from "@/catalog/catalogApi";

type Tone = "success" | "warning" | "error" | "info" | undefined;
const creditTone = (s: string): Tone =>
  s === "paid" ? "success" : s === "overdue" || s === "blocked" ? "error" : s === "partial" ? "warning" : s === "open" ? "info" : undefined;
const signalTone = (level: string): Tone => (level === "danger" ? "error" : level === "warning" ? "warning" : "info");

/**
 * Fiche client — compte commercial et financier (module clients §6-§16, §36). Un gestionnaire doit y
 * lire sans ambiguïté ce que le client a acheté, ce qu'il devait, chaque versement, ce qui reste dû,
 * les dépenses imputées, les retours/avoirs/remboursements et qui a fait chaque opération. Aucun
 * montant n'est calculé ici : tout vient du serveur, qui reconstruit le solde depuis le journal client.
 */
export function CustomerAccountPage() {
  const { id = "" } = useParams();
  const [params, setParams] = useSearchParams();
  const { hasPermission } = useAuth();
  const canFinance = hasPermission("credit.view_portfolio");
  const [profile, setProfile] = useState<CustomerProfile | null>(null);
  const [summary, setSummary] = useState<CustomerAccountSummary | null>(null);
  const [credits, setCredits] = useState<CustomerCredit[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [paying, setPaying] = useState<CustomerCredit | "account" | null>(null);
  const [addingExpense, setAddingExpense] = useState(false);
  const [openingCredit, setOpeningCredit] = useState(false);
  const [version, setVersion] = useState(0);
  const tab = params.get("tab") ?? "overview";

  const reload = useCallback(() => {
    setError(null);
    getCustomer(id).then(setProfile).catch((e) => setError(errorText(e, "Client introuvable")));
    if (canFinance) {
      getAccountSummary(id).then(setSummary).catch((e) => setError(errorText(e)));
      listCredits(id).then(setCredits).catch(() => setCredits([]));
    }
    setVersion((v) => v + 1);
  }, [id, canFinance]);
  useEffect(reload, [reload]);

  if (error && !profile) return <Card><EmptyState title="Fiche client" hint={error} /></Card>;
  if (!profile) return <Card><Spinner /></Card>;

  const cur = profile.currency;
  const tabs = [
    { id: "overview", label: "Vue générale" },
    ...(canFinance ? [
      { id: "purchases", label: "Achats" },
      { id: "credits", label: "Crédits" },
      { id: "payments", label: "Versements" },
      { id: "expenses", label: "Dépenses" },
      { id: "returns", label: "Retours & avoirs" },
      { id: "history", label: "Historique" },
      { id: "collection", label: "Recouvrement" },
    ] : []),
  ];
  const setTab = (t: string) => setParams((p) => { p.set("tab", t); return p; }, { replace: true });

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
            <Link to="/customers">Clients</Link> · {profile.customerNumber}
          </div>
          <h1 style={{ margin: "4px 0 0" }}>
            {[profile.firstName, profile.name].filter(Boolean).join(" ")}{" "}
            <Badge tone={summary?.blocked ? "error" : profile.status === "active" ? "success" : "warning"}>
              {summary?.blocked ? "Bloqué" : CUSTOMER_STATUS[profile.status] ?? profile.status}
            </Badge>
          </h1>
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {canFinance && hasPermission("credit.receive_payment") && (
            <Button onClick={() => setPaying("account")} disabled={profile.status === "archived"}>Enregistrer un versement</Button>
          )}
          {hasPermission("customers.create_expense") && (
            <Button variant="ghost" onClick={() => setAddingExpense(true)} disabled={profile.status === "archived"}>Imputer une dépense</Button>
          )}
          {hasPermission("credit.create") && (
            <Button variant="ghost" onClick={() => setOpeningCredit(true)} disabled={profile.status === "archived"}>Créer un crédit</Button>
          )}
          {canFinance && (
            <ActionMenu label="Relevé de compte" icon={<Icon.Printer size={14} />} items={[
              { label: "Imprimer le relevé (A4)", hint: "Total versé, reste à verser, mouvements", run: () => customerStatement(id, "pdf", {}, true) },
              { label: "Imprimer le relevé (ticket 80 mm)", hint: "Imprimante de caisse — 90 derniers jours, solde reporté", run: () => customerStatement(id, "ticket", {}, true) },
              { label: "Télécharger le PDF", run: () => customerStatement(id, "pdf") },
              { label: "Télécharger en Excel", run: () => customerStatement(id, "xlsx") },
            ]} />
          )}
        </div>
      </div>

      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <Tabs tabs={tabs} active={tab} onChange={setTab} />

      {tab === "overview" && <OverviewPanel profile={profile} summary={summary} onChanged={reload} />}
      {tab === "purchases" && <PurchasesPanel id={id} version={version} />}
      {tab === "credits" && <CreditsPanel credits={credits} onPay={(c) => setPaying(c)} onChanged={reload} />}
      {tab === "payments" && <PaymentsPanel id={id} version={version} onChanged={reload} />}
      {tab === "expenses" && <ExpensesPanel id={id} version={version} onChanged={reload} />}
      {tab === "returns" && <ReturnsPanel id={id} version={version} onChanged={reload} />}
      {tab === "history" && <HistoryPanel id={id} version={version} currency={cur}
                                          currencies={summary?.balances.map((b) => b.currency) ?? [cur]} />}
      {tab === "collection" && <CollectionPanel id={id} credits={credits} version={version} onChanged={reload} />}

      {paying && (
        <PaymentDialog customerId={id} customerName={profile.name} currency={cur} credits={credits}
                       preselected={paying === "account" ? null : paying}
                       onClose={() => setPaying(null)} onDone={() => { setPaying(null); reload(); }} />
      )}
      {openingCredit && (
        <OpenCreditDialog customerId={id} customerName={profile.name} currency={cur}
                          onClose={() => setOpeningCredit(false)} onDone={() => { setOpeningCredit(false); reload(); }} />
      )}
      {addingExpense && (
        <ExpenseDialog customerId={id} customerName={profile.name} currency={cur}
                       onClose={() => setAddingExpense(false)} onDone={() => { setAddingExpense(false); reload(); }} />
      )}
    </div>
  );
}

// ------------------------------------------------------------------ Vue générale (§6, §15, §27)

function OverviewPanel({ profile, summary, onChanged }: {
  profile: CustomerProfile; summary: CustomerAccountSummary | null; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const cur = profile.currency;
  return (
    <div style={{ display: "grid", gap: 16 }}>
      {summary && (
        <>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(190px, 1fr))", gap: 12 }}>
            <KpiCard label="Solde dû" value={<Money value={summary.balanceDue} currency={cur} />}
                     tag={summary.overdueAmount > 0 ? `${money(summary.overdueAmount, cur)} échus` : undefined}
                     tone={summary.overdueAmount > 0 ? "warning" : undefined} />
            <KpiCard label="Crédit disponible" value={<Money value={summary.availableCredit} currency={cur} />}
                     tag={`limite ${money(summary.creditLimit, cur)}`} tone={summary.exchangeRateMissing ? "warning" : undefined} />
            <KpiCard label="Avoir disponible" value={<Money value={summary.availableStoreCredit} currency={cur} />} />
            <KpiCard label="Solde net" value={<Money value={summary.netBalance} currency={cur} />}
                     tag={summary.ledgerConsistent ? "journal concordant" : "à vérifier"}
                     tone={summary.ledgerConsistent ? undefined : "warning"} />
            <KpiCard label="Total achats" value={<Money value={summary.totalPurchases} currency={cur} />} tag={`${summary.purchaseCount} vente(s)`} />
            <KpiCard label="Achats à crédit" value={<Money value={summary.creditPurchases} currency={cur} />} />
            <KpiCard label="Total payé" value={<Money value={summary.totalPaid} currency={cur} />} />
            <KpiCard label="Dépenses imputées" value={<Money value={summary.expensesBilled} currency={cur} />}
                     tag={summary.pendingExpenses > 0 ? `${summary.pendingExpenses} en attente` : undefined} />
            <KpiCard label="Retours" value={<Money value={summary.returnsAmount} currency={cur} />} tag={`${summary.returnsQuantity} article(s)`} />
            <KpiCard label="Dernier paiement"
                     value={summary.lastPaymentAt ? <Money value={summary.lastPaymentAmount ?? 0} currency={cur} /> : "—"}
                     tag={summary.lastPaymentAt ? dateShort(summary.lastPaymentAt) : undefined} />
          </div>

          {(summary.balances.length > 1 || summary.exchangeRateMissing) && (
            <Card>
              <SectionLabel>Soldes par devise</SectionLabel>
              {summary.exchangeRateMissing && (
                <div role="alert" style={{ color: "var(--nx-warning)", marginBottom: 8 }}>
                  Taux de change manquant : l'encours dans une autre devise ne peut pas être converti — la vente à crédit est refusée
                  tant que le taux du jour n'est pas saisi.
                </div>
              )}
              <DataTable
                columns={[
                  { key: "c", header: "Devise", render: (b) => b.currency },
                  { key: "d", header: "Solde dû", numeric: true, render: (b) => money(b.balanceDue, b.currency) },
                  { key: "o", header: "Dont échu", numeric: true, render: (b) => money(b.overdueAmount, b.currency) },
                  { key: "a", header: "Avoir", numeric: true, render: (b) => money(b.availableStoreCredit, b.currency) },
                  { key: "x", header: `Converti en ${cur}`, numeric: true,
                    render: (b) => (b.exposureInCustomerCurrency == null ? "taux manquant" : money(b.exposureInCustomerCurrency, cur)) },
                  { key: "j", header: "Journal", render: (b) => <Badge tone={b.ledgerConsistent ? "success" : "warning"}>{b.ledgerConsistent ? "concordant" : "à vérifier"}</Badge> },
                ]}
                rows={summary.balances} rowKey={(b) => b.currency}
                footer={<><td colSpan={4}><b>Encours total converti (limite)</b></td><td className="nx-num"><b>{money(summary.totalExposure, cur)}</b></td><td /></>}
              />
            </Card>
          )}

          {summary.signals.length > 0 && (
            <Card>
              <SectionLabel>Signaux à surveiller</SectionLabel>
              <div style={{ display: "grid", gap: 10 }}>
                {summary.signals.map((s) => (
                  <div key={s.code} style={{ display: "grid", gap: 4 }}>
                    <div><Badge tone={signalTone(s.level)}>{s.title}</Badge></div>
                    <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                      {s.explanation} — {Object.entries(s.data).map(([k, v]) => `${k} : ${v}`).join(" · ")}
                    </div>
                  </div>
                ))}
              </div>
              <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)", marginTop: 10 }}>
                Signaux calculés par des règles affichées ; ils ne modifient aucun solde et n'exécutent aucune opération.
              </div>
            </Card>
          )}
        </>
      )}
      <ProfileCard profile={profile} canEdit={hasPermission("customers.update")} canCredit={hasPermission("credit.set_limit")} onChanged={onChanged} />
    </div>
  );
}

function ProfileCard({ profile, canEdit, canCredit, onChanged }: {
  profile: CustomerProfile; canEdit: boolean; canCredit: boolean; onChanged: () => void;
}) {
  const toast = useToast();
  const [editing, setEditing] = useState(false);
  const [status, setStatus] = useState<"active" | "inactive" | "archived" | null>(null);
  const [form, setForm] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Groupes de clients (prix de groupe en caisse) — liste lue au catalogue ; sans accès, le champ n'apparaît pas.
  const [groups, setGroups] = useState<CustomerGroup[] | null>(null);

  const fields: Array<[string, string, string | null]> = [
    ["firstName", "Prénom", profile.firstName], ["name", "Nom / raison sociale", profile.name],
    ["phone", "Téléphone", profile.phone], ["email", "Email", profile.email], ["address", "Adresse", profile.address],
    ["city", "Ville", profile.city], ["country", "Pays", profile.country],
    ["secondaryContactName", "Contact secondaire", profile.secondaryContactName],
    ["secondaryContactPhone", "Tél. contact secondaire", profile.secondaryContactPhone],
    ["segment", "Segment", profile.segment], ["taxIdentifier", "Identifiant fiscal", profile.taxIdentifier],
    ["notes", "Notes", profile.notes],
  ];
  const creditFields: Array<[string, string, string]> = [
    ["creditLimit", "Limite de crédit", String(profile.creditLimit ?? "")],
    ["creditDays", "Durée de crédit (jours)", String(profile.creditDays ?? "")],
    ["creditTerms", "Conditions de crédit", profile.creditTerms ?? ""],
  ];

  function start() {
    setForm({
      ...Object.fromEntries([...fields.map(([k, , v]) => [k, v ?? ""]), ...creditFields.map(([k, , v]) => [k, v])]),
      customerGroupId: profile.customerGroupId ?? "",
    });
    listCustomerGroups().then(setGroups).catch(() => setGroups(null));
    setError(null);
    setEditing(true);
  }

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const patch: Record<string, string | number> = {};
      fields.forEach(([k, , v]) => { if ((form[k] ?? "") !== (v ?? "")) patch[k] = form[k]; });
      if (form.customerGroupId && form.customerGroupId !== (profile.customerGroupId ?? "")) patch.customerGroupId = form.customerGroupId;
      if (canCredit) {
        if (form.creditLimit !== String(profile.creditLimit ?? "")) patch.creditLimit = Number(form.creditLimit) || 0;
        if (form.creditDays !== String(profile.creditDays ?? "") && form.creditDays !== "") patch.creditDays = Number(form.creditDays);
        if (form.creditTerms !== (profile.creditTerms ?? "")) patch.creditTerms = form.creditTerms;
      }
      await patchCustomer(profile.id, patch);
      toast.success("Fiche client enregistrée.");
      setEditing(false);
      onChanged();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  const row = (label: string, value: ReactNode) => (
    <div style={{ display: "grid", gridTemplateColumns: "180px 1fr", gap: 8 }}>
      <span style={{ color: "var(--nx-text-2)" }}>{label}</span><span>{value || "—"}</span>
    </div>
  );

  return (
    <Card>
      <SectionLabel action={canEdit && !editing ? (
        <div style={{ display: "flex", gap: 6 }}>
          <Button size="sm" variant="ghost" onClick={start}>Modifier</Button>
          {profile.status !== "active" && profile.status !== "blocked" && <Button size="sm" variant="ghost" onClick={() => setStatus("active")}>Réactiver</Button>}
          {profile.status === "active" && <Button size="sm" variant="ghost" onClick={() => setStatus("inactive")}>Désactiver</Button>}
          {profile.status !== "archived" && profile.status !== "blocked" && <Button size="sm" variant="ghost" onClick={() => setStatus("archived")}>Archiver</Button>}
        </div>
      ) : undefined}>Fiche client</SectionLabel>

      {!editing ? (
        <div style={{ display: "grid", gap: 6 }}>
          {row("Code client", profile.customerNumber)}
          {row("Type", profile.type === "business" ? "Entreprise" : "Particulier")}
          {fields.map(([k, label, v]) => <div key={k}>{row(label, v)}</div>)}
          {row("Groupe", profile.customerGroupName)}
          {row("Responsable", profile.accountManagerName)}
          {row("Succursale", profile.establishmentName)}
          {profile.creditLimit != null && row("Limite de crédit", money(profile.creditLimit, profile.currency))}
          {profile.creditDays != null && row("Durée de crédit", `${profile.creditDays} jours`)}
          {profile.creditTerms && row("Conditions", profile.creditTerms)}
          {row("Créé", `${dateShort(profile.createdAt)}${profile.createdByName ? ` par ${profile.createdByName}` : ""}`)}
          {profile.updatedAt && row("Modifié", dateTime(profile.updatedAt))}
        </div>
      ) : (
        <div style={{ display: "grid", gap: 10, maxWidth: 560 }}>
          {fields.map(([k, label]) => (
            <Field key={k} label={label}>
              <TextInput value={form[k] ?? ""} onChange={(e) => setForm((f) => ({ ...f, [k]: e.target.value }))} />
            </Field>
          ))}
          {groups && groups.length > 0 && (
            <Field label="Groupe de clients" hint="Les prix du groupe s'appliquent automatiquement en caisse.">
              <Select value={form.customerGroupId ?? ""} onChange={(e) => setForm((f) => ({ ...f, customerGroupId: e.target.value }))}>
                <option value="">{profile.customerGroupId ? "(inchangé)" : "(aucun)"}</option>
                {groups.filter((g) => g.status === "active").map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
              </Select>
            </Field>
          )}
          {creditFields.map(([k, label]) => (
            <Field key={k} label={label} hint={canCredit ? undefined : "Réservé à la permission credit.set_limit."}>
              <TextInput value={form[k] ?? ""} disabled={!canCredit} type={k === "creditTerms" ? "text" : "number"}
                         onChange={(e) => setForm((f) => ({ ...f, [k]: e.target.value }))} />
            </Field>
          ))}
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <Button loading={busy} onClick={() => void save()}>Enregistrer</Button>
            <Button variant="ghost" onClick={() => setEditing(false)}>Annuler</Button>
          </div>
        </div>
      )}

      {status && (
        <ConfirmDialog
          title={`Statut : ${CUSTOMER_STATUS[status]}`}
          message={status === "archived"
            ? "Un client archivé n'apparaît plus dans la recherche ; ses mouvements financiers restent conservés. L'archivage est refusé tant que le compte n'est pas soldé."
            : status === "inactive" ? "Un client inactif ne peut plus acheter à crédit." : "Le client redevient utilisable."}
          requireReason confirmLabel="Confirmer"
          onCancel={() => setStatus(null)}
          onConfirm={(reason) => {
            changeCustomerStatus(profile.id, status, reason)
              .then(() => { toast.success("Statut modifié."); onChanged(); })
              .catch((e) => toast.error(errorText(e)))
              .finally(() => setStatus(null));
          }}
        />
      )}
    </Card>
  );
}

// ------------------------------------------------------------------ Achats

function PurchasesPanel({ id, version }: { id: string; version: number }) {
  const { hasPermission } = useAuth();
  const [rows, setRows] = useState<CustomerPurchase[] | null>(null);
  useEffect(() => { listPurchases(id).then(setRows).catch(() => setRows([])); }, [id, version]);
  if (!rows) return <Card><Spinner /></Card>;
  const columns: Array<Column<CustomerPurchase>> = [
    { key: "d", header: "Date", render: (r) => dateShort(r.soldAt) },
    { key: "n", header: "Ticket / facture", render: (r) => r.documentNumber },
    { key: "t", header: "Type", render: (r) => ({ cash: "Comptant", credit: "Crédit", mixed: "Mixte" } as Record<string, string>)[r.saleType] ?? r.saleType },
    { key: "tot", header: "Total", numeric: true, render: (r) => <Money value={r.total} currency={r.currency} /> },
    { key: "p", header: "Payé à la vente", numeric: true, render: (r) => <Money value={r.paidAtSale} currency={r.currency} /> },
    { key: "c", header: "À crédit", numeric: true, render: (r) => (r.creditAmount > 0 ? <>{money(r.creditAmount, r.currency)} <small>{r.creditNumber}</small></> : "—") },
    { key: "r", header: "Retourné", numeric: true, render: (r) => (r.returnedAmount > 0 ? money(r.returnedAmount, r.currency) : "—") },
    { key: "s", header: "Statut", render: (r) => <Badge tone={r.status === "void" ? "error" : r.status === "completed" ? "success" : "warning"}>{r.status}</Badge> },
    { key: "u", header: "Caissier", render: (r) => r.cashierName ?? "—" },
    { key: "x", header: "", render: (r) => (hasPermission("pos.view") ? <PrintMenu kind="sale" id={r.saleId} ticket iconOnly label="Ticket de caisse" /> : null) },
  ];
  return <Card style={{ padding: 0 }}><DataTable columns={columns} rows={rows} rowKey={(r) => r.saleId} empty="Aucun achat." /></Card>;
}

// ------------------------------------------------------------------ Crédits (§8)

function CreditsPanel({ credits, onPay, onChanged }: { credits: CustomerCredit[]; onPay: (c: CustomerCredit) => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [detail, setDetail] = useState<CreditDetail | null>(null);
  const [scheduling, setScheduling] = useState(false);
  const columns: Array<Column<CustomerCredit>> = [
    { key: "n", header: "Crédit", render: (c) => <Button variant="ghost" size="sm" onClick={() => getCredit(c.id).then(setDetail)}>{c.creditNumber}</Button> },
    { key: "o", header: "Origine", render: (c) => `${c.sourceType === "customer_expense" ? "Dépense" : "Vente"} ${c.sourceReference ?? ""}` },
    { key: "i", header: "Émis le", render: (c) => dateShort(c.issueDate) },
    { key: "d", header: "Échéance", render: (c) => (c.dueDate ? <>{dateShort(c.dueDate)}{c.hasSchedule && <small> · échéancier</small>}</> : "—") },
    { key: "a", header: "Montant", numeric: true, render: (c) => <Money value={c.originalAmount} currency={c.currency} /> },
    { key: "p", header: "Réglé", numeric: true, render: (c) => <Money value={c.paidAmount} currency={c.currency} /> },
    { key: "r", header: "Reste dû", numeric: true, render: (c) => <b><Money value={c.remainingAmount} currency={c.currency} /></b> },
    { key: "s", header: "Statut", render: (c) => <Badge tone={creditTone(c.displayStatus)}>{CREDIT_STATUS[c.displayStatus]}{c.daysOverdue > 0 ? ` · ${c.daysOverdue} j` : ""}</Badge> },
    { key: "x", header: "", render: (c) => (
      <div style={{ display: "flex", gap: 4, alignItems: "center" }}>
        {hasPermission("credit.receive_payment") && c.remainingAmount > 0 && ["open", "partial", "overdue", "blocked"].includes(c.displayStatus)
          && <Button size="sm" onClick={() => onPay(c)}>Verser</Button>}
        {hasPermission("credit.view_portfolio") && <PrintMenu kind="customer-credit" id={c.id} iconOnly label="Imprimer le crédit" />}
      </div>
    ) },
  ];
  return (
    <>
      <Card style={{ padding: 0 }}><DataTable columns={columns} rows={credits} rowKey={(c) => c.id} empty="Aucun crédit." /></Card>
      {detail && (
        <Modal title={`Crédit ${detail.credit.creditNumber}`} onClose={() => { setDetail(null); setScheduling(false); }}>
          <div style={{ display: "grid", gap: 8 }}>
            {hasPermission("credit.view_portfolio") && (
              <div><PrintMenu kind="customer-credit" id={detail.credit.id} label="Imprimer (reconnaissance de dette)" /></div>
            )}
            <div>Montant initial <b><Money value={detail.credit.originalAmount} currency={detail.credit.currency} /></b> ·
              réglé <Money value={detail.credit.paidAmount} currency={detail.credit.currency} /> ·
              reste <b><Money value={detail.credit.remainingAmount} currency={detail.credit.currency} /></b></div>
            <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
              Créé le {dateShort(detail.credit.issueDate)}{detail.credit.createdByName ? ` par ${detail.credit.createdByName}` : ""}
              {detail.credit.establishmentName ? ` — ${detail.credit.establishmentName}` : ""}
            </div>
            <DataTable
              columns={[
                { key: "d", header: "Date", render: (s: CreditSettlement) => dateTime(s.at) },
                { key: "k", header: "Règlement", render: (s: CreditSettlement) => `${{ payment: "Versement", credit_memo: "Avoir", write_off: "Radiation" }[s.kind]} ${s.reference}` },
                { key: "m", header: "Montant", numeric: true, render: (s: CreditSettlement) => <span style={{ textDecoration: s.voided ? "line-through" : undefined }}>{money(s.amount, detail.credit.currency)}</span> },
                { key: "w", header: "Moyen", render: (s: CreditSettlement) => s.method ?? "—" },
                { key: "u", header: "Par", render: (s: CreditSettlement) => s.receivedByName ?? "—" },
                { key: "v", header: "", render: (s: CreditSettlement) => (s.voided ? <Badge tone="error">annulé</Badge> : null) },
              ]}
              rows={detail.settlements} rowKey={(s) => `${s.kind}-${s.reference}-${s.at}`} empty="Aucun règlement."
            />
            {detail.schedule && (
              <div style={{ display: "grid", gap: 6 }}>
                <b>Échéancier v{detail.schedule.version} — {detail.schedule.reason}</b>
                <DataTable
                  columns={[
                    { key: "n", header: "N°", render: (i) => i.number },
                    { key: "d", header: "Échéance", render: (i) => dateShort(i.dueDate) },
                    { key: "a", header: "Montant", numeric: true, render: (i) => money(i.amount, detail.credit.currency) },
                    { key: "c", header: "Couvert", numeric: true, render: (i) => money(i.covered, detail.credit.currency) },
                    { key: "s", header: "État", render: (i) => (
                      <Badge tone={i.status === "paid" ? "success" : i.status === "overdue" ? "error" : i.status === "partial" ? "warning" : "info"}>
                        {{ paid: "Payée", partial: "Partielle", overdue: "En retard", upcoming: "À venir" }[i.status]}
                      </Badge>) },
                  ]}
                  rows={detail.schedule.installments} rowKey={(i) => String(i.number)}
                />
              </div>
            )}
            {hasPermission("credit.set_limit") && detail.credit.remainingAmount > 0
              && !["cancelled", "written_off", "paid"].includes(detail.credit.displayStatus) && (
              scheduling
                ? <ScheduleForm credit={detail.credit} onCancel={() => setScheduling(false)}
                                onDone={(d) => { setDetail(d); setScheduling(false); onChanged(); }} />
                : <div><Button size="sm" variant="ghost" onClick={() => setScheduling(true)}>
                    {detail.schedule ? "Modifier l'échéancier" : "Échelonner le reste dû"}
                  </Button></div>
            )}
          </div>
        </Modal>
      )}
    </>
  );
}

// ------------------------------------------------------------------ Versements (§9, §22)

function PaymentsPanel({ id, version, onChanged }: { id: string; version: number; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [rows, setRows] = useState<CustomerPayment[] | null>(null);
  const [voiding, setVoiding] = useState<CustomerPayment | null>(null);
  useEffect(() => { listPayments(id).then(setRows).catch(() => setRows([])); }, [id, version]);
  if (!rows) return <Card><Spinner /></Card>;

  const columns: Array<Column<CustomerPayment>> = [
    { key: "d", header: "Date", render: (p) => dateTime(p.receivedAt) },
    { key: "n", header: "Versement", render: (p) => p.paymentNumber },
    { key: "a", header: "Montant", numeric: true, render: (p) => <span style={{ textDecoration: p.status === "void" ? "line-through" : undefined }}><Money value={p.amount} currency={p.currency} /></span> },
    { key: "c", header: "Crédits réglés", render: (p) => p.allocations.map((a) => `${a.creditNumber} (${money(a.amount, p.currency)})`).join(", ") || (p.creditMemoNumber ? `Avoir ${p.creditMemoNumber}` : "—") },
    { key: "m", header: "Moyen", render: (p) => p.paymentMethod ?? p.financialAccountName ?? "—" },
    { key: "r", header: "Réf. / note", render: (p) => [p.reference, p.note].filter(Boolean).join(" — ") || "—" },
    { key: "u", header: "Reçu par", render: (p) => p.receivedByName ?? "—" },
    { key: "s", header: "Statut", render: (p) => p.status === "void"
      ? <span title={`${p.voidReason ?? ""} — ${p.voidedByName ?? ""}`}><Badge tone="error">Annulé</Badge></span>
      : <Badge tone="success">Validé</Badge> },
    { key: "x", header: "", render: (p) => (
      <div style={{ display: "flex", gap: 4, alignItems: "center" }}>
        {(hasPermission("credit.view_portfolio") || hasPermission("credit.receive_payment"))
          && <PrintMenu kind="customer-payment" id={p.id} ticket iconOnly label="Reçu de versement" />}
        {hasPermission("credit.void_payment") && p.status === "completed"
          && <Button size="sm" variant="ghost" onClick={() => setVoiding(p)}>Annuler</Button>}
      </div>
    ) },
  ];
  return (
    <>
      <Card style={{ padding: 0 }}><DataTable columns={columns} rows={rows} rowKey={(p) => p.id} empty="Aucun versement." /></Card>
      {voiding && (
        <ConfirmDialog
          title={`Annuler le versement ${voiding.paymentNumber}`}
          message="L'annulation passe une contre-écriture : la dette du client est rétablie, la trésorerie ressort le montant. Le versement reste visible, marqué annulé."
          impact={<>Montant : <b>{money(voiding.amount, voiding.currency)}</b></>}
          danger requireReason confirmLabel="Annuler le versement"
          onCancel={() => setVoiding(null)}
          onConfirm={(reason) => {
            voidPayment(voiding.id, reason)
              .then(() => { toast.success("Versement annulé."); onChanged(); })
              .catch((e) => toast.error(errorText(e)))
              .finally(() => setVoiding(null));
          }}
        />
      )}
    </>
  );
}

// ------------------------------------------------------------------ Dépenses (§10)

function ExpensesPanel({ id, version, onChanged }: { id: string; version: number; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [rows, setRows] = useState<CustomerExpense[] | null>(null);
  const [deciding, setDeciding] = useState<{ e: CustomerExpense; action: "reject" | "cancel" } | null>(null);
  useEffect(() => { listExpenses(id).then(setRows).catch(() => setRows([])); }, [id, version]);
  if (!rows) return <Card><Spinner /></Card>;

  const act = (p: Promise<unknown>, ok: string) =>
    p.then(() => { toast.success(ok); onChanged(); }).catch((e) => toast.error(errorText(e)));
  const columns: Array<Column<CustomerExpense>> = [
    { key: "n", header: "Référence", render: (e) => e.expenseNumber },
    { key: "d", header: "Date", render: (e) => dateShort(e.expenseDate) },
    { key: "c", header: "Catégorie", render: (e) => EXPENSE_CATEGORIES[e.category] ?? e.category },
    { key: "l", header: "Description", render: (e) => e.description },
    { key: "a", header: "Montant", numeric: true, render: (e) => <Money value={e.amount} currency={e.currency} /> },
    { key: "b", header: "Refacturée", render: (e) => (e.billable ? (e.creditNumber ? `oui — ${e.creditNumber}` : "oui") : "non") },
    { key: "s", header: "Statut", render: (e) => <Badge tone={e.status === "validated" ? "success" : e.status === "pending" || e.status === "draft" ? "warning" : "error"}>{EXPENSE_STATUS[e.status]}</Badge> },
    { key: "u", header: "Saisie / validée par", render: (e) => [e.createdByName, e.decidedByName].filter(Boolean).join(" → ") },
    {
      key: "x", header: "", render: (e) => (
        <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
          {e.status === "draft" && hasPermission("customers.create_expense") && <Button size="sm" variant="ghost" onClick={() => void act(submitExpense(e.id), "Dépense transmise.")}>Transmettre</Button>}
          {e.status === "pending" && hasPermission("customers.approve_expense") && <>
            <Button size="sm" onClick={() => void act(approveExpense(e.id), "Dépense validée.")}>Valider</Button>
            <Button size="sm" variant="ghost" onClick={() => setDeciding({ e, action: "reject" })}>Rejeter</Button>
          </>}
          {(e.status === "validated" ? hasPermission("customers.approve_expense") : ["draft", "pending"].includes(e.status))
            && <Button size="sm" variant="ghost" onClick={() => setDeciding({ e, action: "cancel" })}>Annuler</Button>}
          {(hasPermission("credit.view_portfolio") || hasPermission("customers.create_expense"))
            && <PrintMenu kind="customer-expense" id={e.id} iconOnly label="Imprimer la note de dépense" />}
        </div>
      ),
    },
  ];
  return (
    <>
      <Card style={{ padding: 0 }}><DataTable columns={columns} rows={rows} rowKey={(e) => e.id} empty="Aucune dépense imputée." /></Card>
      {deciding && (
        <ConfirmDialog
          title={deciding.action === "reject" ? `Rejeter ${deciding.e.expenseNumber}` : `Annuler ${deciding.e.expenseNumber}`}
          message={deciding.action === "reject" ? "La dépense ne touchera pas le compte du client."
            : deciding.e.status === "validated" ? "L'écriture est extournée et le crédit correspondant annulé (refusé s'il a déjà reçu un versement). Rien n'est supprimé."
            : "La dépense est annulée ; elle reste visible."}
          danger requireReason confirmLabel="Confirmer"
          onCancel={() => setDeciding(null)}
          onConfirm={(reason) => {
            void act(deciding.action === "reject" ? rejectExpense(deciding.e.id, reason) : cancelExpense(deciding.e.id, reason),
              deciding.action === "reject" ? "Dépense rejetée." : "Dépense annulée.");
            setDeciding(null);
          }}
        />
      )}
    </>
  );
}

// ------------------------------------------------------------------ Retours, avoirs, remboursements (§11)

function ReturnsPanel({ id, version, onChanged }: { id: string; version: number; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [memos, setMemos] = useState<CreditMemo[] | null>(null);
  const [refunds, setRefunds] = useState<CustomerRefund[]>([]);
  const [refunding, setRefunding] = useState<CreditMemo | null>(null);
  const [executing, setExecuting] = useState<CustomerRefund | null>(null);
  const [rejecting, setRejecting] = useState<CustomerRefund | null>(null);
  const load = useCallback(() => {
    listCreditMemos(id).then(setMemos).catch(() => setMemos([]));
    listRefunds(id).then(setRefunds).catch(() => setRefunds([]));
  }, [id]);
  useEffect(load, [load, version]);
  if (!memos) return <Card><Spinner /></Card>;

  const act = (p: Promise<unknown>, ok: string) =>
    p.then(() => { toast.success(ok); onChanged(); load(); }).catch((e) => toast.error(errorText(e)));
  const usable = (m: CreditMemo) => ["available", "partially_used"].includes(m.status) && m.remainingAmount > 0;

  const memoCols: Array<Column<CreditMemo>> = [
    { key: "n", header: "Avoir", render: (m) => m.memoNumber },
    { key: "o", header: "Origine", render: (m) => `${m.sourceType === "sale_return" ? "Retour" : "Excédent de versement"} ${m.sourceReference ?? ""}` },
    { key: "d", header: "Date", render: (m) => dateShort(m.createdAt) },
    { key: "a", header: "Montant", numeric: true, render: (m) => <Money value={m.amount} currency={m.currency} /> },
    { key: "u", header: "Utilisé", numeric: true, render: (m) => <Money value={m.usedAmount} currency={m.currency} /> },
    { key: "r", header: "Disponible", numeric: true, render: (m) => <b><Money value={m.remainingAmount} currency={m.currency} /></b> },
    { key: "s", header: "Statut", render: (m) => <Badge tone={m.status === "available" ? "success" : m.status === "cancelled" ? "error" : "info"}>{MEMO_STATUS[m.status]}</Badge> },
    { key: "h", header: "Utilisations", render: (m) => m.uses.map((u) => `${u.kind === "credit" ? "crédit" : "remb."} ${u.reference} ${money(u.amount, m.currency)}`).join(" ; ") || "—" },
    {
      key: "x", header: "", render: (m) => (
        <div style={{ display: "flex", gap: 4, alignItems: "center" }}>
          {usable(m) && hasPermission("credit.receive_payment") && <>
            <Button size="sm" onClick={() => void act(applyCreditMemo(m.id), "Avoir imputé sur les crédits ouverts.")}>Imputer</Button>
            <Button size="sm" variant="ghost" onClick={() => setRefunding(m)}>Rembourser</Button>
          </>}
          {(hasPermission("credit.view_portfolio") || hasPermission("credit.refund"))
            && <PrintMenu kind="credit-memo" id={m.id} iconOnly label="Imprimer l'avoir" />}
        </div>
      ),
    },
  ];
  const refundCols: Array<Column<CustomerRefund>> = [
    { key: "n", header: "Remboursement", render: (r) => r.refundNumber },
    { key: "m", header: "Avoir", render: (r) => r.memoNumber },
    { key: "a", header: "Montant", numeric: true, render: (r) => <Money value={r.amount} currency={r.currency} /> },
    { key: "w", header: "Mode", render: (r) => r.refundMethod },
    { key: "s", header: "Statut", render: (r) => <Badge tone={r.status === "executed" ? "success" : r.status === "requested" || r.status === "approved" ? "warning" : "error"}>{REFUND_STATUS[r.status]}</Badge> },
    { key: "p", header: "Demandé / décidé / exécuté", render: (r) => [r.requestedByName, r.decidedByName, r.executedByName].filter(Boolean).join(" → ") },
    {
      key: "x", header: "", render: (r) => (
        <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
          {r.status === "requested" && hasPermission("credit.refund") && <>
            <Button size="sm" onClick={() => void act(approveRefund(r.id), "Remboursement approuvé.")}>Approuver</Button>
            <Button size="sm" variant="ghost" onClick={() => setRejecting(r)}>Refuser</Button>
          </>}
          {r.status === "approved" && (hasPermission("credit.refund") || hasPermission("credit.receive_payment"))
            && <Button size="sm" onClick={() => setExecuting(r)}>Décaisser</Button>}
          {["requested", "approved"].includes(r.status) && <Button size="sm" variant="ghost"
            onClick={() => { const why = window.prompt("Motif d'annulation ?"); if (why) void act(cancelRefund(r.id, why), "Demande annulée."); }}>Annuler</Button>}
          {(hasPermission("credit.view_portfolio") || hasPermission("credit.refund"))
            && <PrintMenu kind="customer-refund" id={r.id} ticket iconOnly label="Reçu de remboursement" />}
        </div>
      ),
    },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card style={{ padding: 0 }}>
        <div style={{ padding: "12px 16px 0" }}><SectionLabel>Avoirs</SectionLabel></div>
        <DataTable columns={memoCols} rows={memos} rowKey={(m) => m.id}
                   empty="Aucun avoir. Un retour sur une vente à crédit réduit d'abord la dette ; le reliquat devient un avoir." />
      </Card>
      <Card style={{ padding: 0 }}>
        <div style={{ padding: "12px 16px 0" }}><SectionLabel>Remboursements</SectionLabel></div>
        <DataTable columns={refundCols} rows={refunds} rowKey={(r) => r.id} empty="Aucun remboursement." />
      </Card>
      <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>
        Les retours de marchandise se demandent depuis la caisse (onglet Retours du POS) et sont validés par un responsable.
      </div>
      {refunding && <RefundDialog memo={refunding} onClose={() => setRefunding(null)} onDone={() => { setRefunding(null); onChanged(); load(); }} />}
      {executing && <ExecuteRefundDialog refund={executing} onClose={() => setExecuting(null)} onDone={() => { setExecuting(null); onChanged(); load(); }} />}
      {rejecting && (
        <ConfirmDialog title={`Refuser ${rejecting.refundNumber}`} message="L'avoir reste disponible pour le client." requireReason
                       confirmLabel="Refuser" onCancel={() => setRejecting(null)}
                       onConfirm={(note) => { void act(rejectRefund(rejecting.id, note), "Remboursement refusé."); setRejecting(null); }} />
      )}
    </div>
  );
}

function ExecuteRefundDialog({ refund, onClose, onDone }: { refund: CustomerRefund; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [options, setOptions] = useState<PaymentOptions | null>(null);
  const [account, setAccount] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => {
    getPaymentOptions().then((o) => { setOptions(o); setAccount(o.accounts.find((a) => a.currency === refund.currency)?.id ?? ""); })
      .catch((e) => setError(errorText(e)));
  }, [refund.currency]);
  return (
    <Modal title={`Décaisser ${refund.refundNumber}`} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button loading={busy} disabled={busy || !account} onClick={() => {
               setBusy(true);
               executeRefund(refund.id, account)
                 .then(() => { toast.success(`${money(refund.amount, refund.currency)} remboursé.`); onDone(); })
                 .catch((e) => setError(errorText(e)))
                 .finally(() => setBusy(false));
             }}>Décaisser {money(refund.amount, refund.currency)}</Button>
           </>}>
      <Field label="Compte de décaissement" required>
        <Select value={account} onChange={(e) => setAccount(e.target.value)}>
          <option value="" disabled>—</option>
          {options?.accounts.filter((a) => a.currency === refund.currency).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
        </Select>
      </Field>
      {error && <div role="alert" className="nx-error-text" style={{ marginTop: 10 }}>{error}</div>}
    </Modal>
  );
}

// ------------------------------------------------------------------ Historique & journal (§7, §14)

function HistoryPanel({ id, version, currency, currencies }: { id: string; version: number; currency: string; currencies: string[] }) {
  const [view, setView] = useState<"timeline" | "ledger">("timeline");
  const [events, setEvents] = useState<TimelineEvent[] | null>(null);
  const [ledger, setLedger] = useState<CustomerLedger | null>(null);
  const [ledgerCurrency, setLedgerCurrency] = useState(currency);
  useEffect(() => { getTimeline(id).then(setEvents).catch(() => setEvents([])); }, [id, version]);
  // §21 : le journal se lit devise par devise, jamais deux devises dans une même colonne de solde.
  useEffect(() => { getLedger(id, ledgerCurrency).then(setLedger).catch(() => setLedger(null)); }, [id, version, ledgerCurrency]);

  return (
    <div style={{ display: "grid", gap: 12 }}>
      <Tabs tabs={[{ id: "timeline", label: "Chronologie" }, { id: "ledger", label: "Journal financier" }]} active={view}
            onChange={(v) => setView(v as "timeline" | "ledger")} />
      {view === "timeline" && (!events ? <Card><Spinner /></Card> : (
        <Card>
          {events.length === 0 ? <EmptyState title="Aucun événement" /> : (
            <ol style={{ listStyle: "none", margin: 0, padding: 0, display: "grid", gap: 10 }}>
              {events.map((e, i) => (
                <li key={i} style={{ display: "grid", gridTemplateColumns: "150px 1fr auto", gap: 12, alignItems: "baseline" }}>
                  <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>{dateTime(e.at)}</span>
                  <span>
                    <b>{e.title}</b>{e.reference ? ` — ${e.reference}` : ""}
                    {e.detail && <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>{e.detail}</div>}
                    {e.actorName && <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>par {e.actorName}</div>}
                  </span>
                  <span className="nx-numeric">{e.amount != null ? money(e.amount, e.currency ?? "HTG") : ""}</span>
                </li>
              ))}
            </ol>
          )}
        </Card>
      ))}
      {view === "ledger" && currencies.length > 1 && (
        <Field label="Devise du journal">
          <Select value={ledgerCurrency} onChange={(e) => setLedgerCurrency(e.target.value)}>
            {currencies.map((c) => <option key={c} value={c}>{c}</option>)}
          </Select>
        </Field>
      )}
      {view === "ledger" && (!ledger ? <Card><Spinner /></Card> : (
        <Card style={{ padding: 0 }}>
          <DataTable
            columns={[
              { key: "d", header: "Date", render: (l) => dateTime(l.occurredAt) },
              { key: "t", header: "Mouvement", render: (l) => l.label },
              { key: "r", header: "Référence", render: (l) => l.reference ?? "—" },
              { key: "x", header: "Libellé", render: (l) => l.description ?? "" },
              { key: "db", header: "Débit (dette +)", numeric: true, render: (l) => (l.debit ? money(l.debit, l.currency) : "") },
              { key: "cr", header: "Crédit (dette −)", numeric: true, render: (l) => (l.credit ? money(l.credit, l.currency) : "") },
              { key: "b", header: "Solde", numeric: true, render: (l) => <b>{money(l.balance, l.currency)}</b> },
              { key: "u", header: "Par", render: (l) => l.createdByName ?? "—" },
            ]}
            rows={ledger.entries} rowKey={(l) => l.id} empty="Aucun mouvement."
            footer={<>
              <td colSpan={4}><b>Totaux</b></td>
              <td className="nx-num">{money(ledger.totalDebit, ledger.currency)}</td>
              <td className="nx-num">{money(ledger.totalCredit, ledger.currency)}</td>
              <td className="nx-num"><b>{money(ledger.closingBalance, ledger.currency)}</b></td>
              <td />
            </>}
          />
        </Card>
      ))}
    </div>
  );
}

// ------------------------------------------------------------------ Recouvrement (§13)

function CollectionPanel({ id, credits, version, onChanged }: {
  id: string; credits: CustomerCredit[]; version: number; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [rows, setRows] = useState<CollectionAction[] | null>(null);
  const [type, setType] = useState("call");
  const [outcome, setOutcome] = useState("");
  const [note, setNote] = useState("");
  const [credit, setCredit] = useState("");
  const [next, setNext] = useState("");
  const load = useCallback(() => { listCollectionActions(id).then(setRows).catch(() => setRows([])); }, [id]);
  useEffect(load, [load, version]);
  const overdue = credits.filter((c) => c.displayStatus === "overdue" || c.displayStatus === "blocked");

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {hasPermission("credit.collect") && (
        <Card>
          <SectionLabel>Nouvelle action de recouvrement</SectionLabel>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))", gap: 10, alignItems: "end" }}>
            <Field label="Action"><Select value={type} onChange={(e) => setType(e.target.value)}>
              {Object.entries(ACTION_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select></Field>
            <Field label="Résultat"><Select value={outcome} onChange={(e) => setOutcome(e.target.value)}>
              <option value="">—</option>
              {Object.entries(ACTION_OUTCOMES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select></Field>
            <Field label="Crédit concerné"><Select value={credit} onChange={(e) => setCredit(e.target.value)}>
              <option value="">Tout le compte</option>
              {credits.filter((c) => c.remainingAmount > 0).map((c) => <option key={c.id} value={c.id}>{c.creditNumber}</option>)}
            </Select></Field>
            <Field label="Prochaine action"><TextInput type="date" value={next} onChange={(e) => setNext(e.target.value)} /></Field>
          </div>
          <Field label="Note" required><TextInput value={note} onChange={(e) => setNote(e.target.value)} maxLength={500} /></Field>
          <div style={{ marginTop: 10 }}>
            <Button disabled={!note.trim()} onClick={() => {
              logCollectionAction(id, { actionType: type, note: note.trim(), outcome: outcome || null, receivableId: credit || null, nextActionDate: next || null })
                .then(() => { toast.success("Action consignée."); setNote(""); setOutcome(""); setNext(""); load(); onChanged(); })
                .catch((e) => toast.error(errorText(e)));
            }}>Consigner</Button>
          </div>
          {overdue.length > 0 && (
            <div style={{ marginTop: 10, color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
              {overdue.length} crédit(s) échu(s) : {overdue.map((c) => `${c.creditNumber} (${c.daysOverdue} j)`).join(", ")}.
              Promesses, litiges et blocages : écran <Link to="/credit">Crédit & recouvrement</Link>.
            </div>
          )}
        </Card>
      )}
      {!rows ? <Card><Spinner /></Card> : (
        <Card style={{ padding: 0 }}>
          <DataTable
            columns={[
              { key: "d", header: "Date", render: (a) => dateTime(a.createdAt) },
              { key: "t", header: "Action", render: (a) => ACTION_TYPES[a.actionType] ?? a.actionType },
              { key: "o", header: "Résultat", render: (a) => (a.outcome ? ACTION_OUTCOMES[a.outcome] ?? a.outcome : "—") },
              { key: "c", header: "Crédit", render: (a) => a.creditNumber ?? "—" },
              { key: "n", header: "Note", render: (a) => a.note },
              { key: "x", header: "Prochaine action", render: (a) => (a.nextActionDate ? dateShort(a.nextActionDate) : "—") },
              { key: "u", header: "Par", render: (a) => a.createdByName ?? "—" },
            ]}
            rows={rows} rowKey={(a) => a.id} empty="Aucune action de recouvrement."
          />
        </Card>
      )}
    </div>
  );
}
