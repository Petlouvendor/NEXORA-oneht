import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  approveExpense, cancelExpense, createExpense, createExpenseCategory, EXPENSE_STATUS, EXPENSE_TONE, financeLookups, getExpense, ledgerOptions,
  listExpenseCategories, listExpenses, listManagedAccounts, payExpense, rejectExpense, submitExpense, updateExpense,
  updateExpenseCategory, type Expense, type ExpenseCategory,
} from "@/finance/treasuryApi";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Dépenses : saisie → approbation par un autre que le demandeur → paiement comptabilisé sur un compte. */
export function ExpensesPanel({ onChanged }: { onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const expenses = useAsync(() => listExpenses(status || undefined), [status]);
  const [editing, setEditing] = useState<Expense | "new" | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [categories, setCategories] = useState(false);
  const reload = () => { expenses.reload(); onChanged(); };
  const list = expenses.data ?? [];
  const toApprove = list.filter((e) => e.status === "submitted").length;

  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {hasPermission("finance.update") && <Button size="sm" variant="ghost" onClick={() => setCategories(true)}>Catégories</Button>}
          {hasPermission("finance.create") && <Button size="sm" onClick={() => setEditing("new")}>Nouvelle dépense</Button>}
        </div>}>
        Dépenses {toApprove > 0 && <Badge tone="warning">{toApprove} à approuver</Badge>}
      </SectionLabel>
      <div style={{ maxWidth: 240, marginBottom: 10 }}>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(EXPENSE_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
      </div>
      {expenses.error && <div role="alert" className="nx-error-text">{expenses.error}</div>}
      <DataTable exportTitle="Dépenses"
        columns={[
          { key: "n", header: "N°", render: (e) => <button type="button" style={linkStyle} onClick={() => setOpenId(e.id)}><code>{e.documentNumber}</code></button> },
          { key: "d", header: "Date", render: (e) => dateShort(e.expenseDate) },
          { key: "l", header: "Description", render: (e) => e.description },
          { key: "c", header: "Catégorie", render: (e) => e.categoryName ?? "—" },
          { key: "f", header: "Fournisseur", render: (e) => e.supplierName ?? "—" },
          { key: "t", header: "Total", numeric: true, render: (e) => money(e.total, e.currency) },
          { key: "s", header: "Statut", render: (e) => <Badge tone={EXPENSE_TONE[e.status]}>{EXPENSE_STATUS[e.status] ?? e.status}</Badge> },
        ]}
        rows={list} rowKey={(e) => e.id} empty={expenses.loading ? "Chargement…" : "Aucune dépense."} />
      {editing && <ExpenseDialog expense={editing === "new" ? null : editing} onClose={() => setEditing(null)}
                                 onSaved={(id) => { setEditing(null); reload(); setOpenId(id); }} />}
      {openId && <ExpenseDrawer key={openId} id={openId} onClose={() => setOpenId(null)} onChanged={reload}
                                onEdit={(e) => { setOpenId(null); setEditing(e); }} />}
      {categories && <CategoriesDialog onClose={() => setCategories(false)} />}
    </Card>
  );
}

const FIELDS: Record<string, string> = {
  "expense.description": "description", "expense.amount": "amount", "expense.tax": "tax", "expense.date": "date",
  "expense.category": "category", "expense.category_inactive": "category", "expense.supplier": "supplier", "expense.currency": "currency",
};

function ExpenseDialog({ expense, onClose, onSaved }: { expense: Expense | null; onClose: () => void; onSaved: (id: string) => void }) {
  const toast = useToast();
  const categories = useAsync(listExpenseCategories, []);
  const lookups = useAsync(financeLookups, []);
  const [description, setDescription] = useState(expense?.description ?? "");
  const [categoryId, setCategoryId] = useState(expense?.categoryId ?? "");
  const [supplierId, setSupplierId] = useState(expense?.supplierId ?? "");
  const [amount, setAmount] = useState(expense ? String(expense.amount) : "");
  const [tax, setTax] = useState(expense?.taxAmount ? String(expense.taxAmount) : "");
  const [currency, setCurrency] = useState(expense?.currency ?? "HTG");
  const [date, setDate] = useState(expense?.expenseDate ?? todayIso());
  const [reference, setReference] = useState(expense?.reference ?? "");
  const { busy, error, fieldErrors, submit, fail } = useSubmit(FIELDS);
  const total = (num(amount) ?? 0) + (num(tax) ?? 0);

  async function save(submitNow: boolean) {
    const a = num(amount), t = num(tax) ?? 0;
    if (!description.trim()) return fail("description", "Description obligatoire.");
    if (!categoryId) return fail("category", "Choisissez la catégorie.");
    if (a == null || a <= 0) return fail("amount", "Montant HT positif requis.");
    if (t < 0 || t > a) return fail("tax", "Taxe entre 0 et le montant HT.");
    if (date > todayIso()) return fail("date", "La date ne peut être dans le futur.");
    const body = { description: description.trim(), categoryId, supplierId: supplierId || null, amount: a, taxAmount: t, currency,
      expenseDate: date, reference: reference.trim() || null, submit: submitNow };
    const id = await submit(async () => { if (expense) { await updateExpense(expense.id, body); return expense.id; } return createExpense(body); });
    if (id) { toast.success(submitNow ? "Dépense soumise à approbation." : "Brouillon enregistré."); onSaved(id); }
  }

  return (
    <Modal title={expense ? `Modifier la dépense ${expense.documentNumber}` : "Nouvelle dépense"} onClose={onClose}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Annuler</Button>
        {(!expense || expense.status === "draft") && <Button variant="subtle" disabled={busy} onClick={() => save(false)}>Enregistrer le brouillon</Button>}
        <Button disabled={busy} onClick={() => save(true)}>{expense?.status === "submitted" ? "Enregistrer" : "Soumettre à approbation"}</Button>
      </>}>
      <form onSubmit={(e: FormEvent) => { e.preventDefault(); void save(true); }} style={{ display: "grid", gap: 10, minWidth: "min(560px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Description" required error={fieldErrors.description}>
          <TextInput value={description} maxLength={500} placeholder="Ex. réparation du climatiseur" onChange={(e) => setDescription(e.target.value)} />
        </Field>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 10 }}>
          <Field label="Catégorie" required error={fieldErrors.category}>
            <Select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">—</option>
              {(categories.data ?? []).filter((c) => c.status === "active" || c.id === categoryId).map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
            </Select>
          </Field>
          <Field label="Fournisseur" error={fieldErrors.supplier}>
            <Select value={supplierId} onChange={(e) => setSupplierId(e.target.value)}>
              <option value="">Aucun</option>
              {(lookups.data?.suppliers ?? []).map((s) => <option key={s.id} value={s.id}>{s.name}</option>)}
            </Select>
          </Field>
          <Field label="Date" required error={fieldErrors.date}><TextInput type="date" max={todayIso()} value={date} onChange={(e) => setDate(e.target.value)} /></Field>
          <Field label="Montant HT" required error={fieldErrors.amount}><TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
          <Field label="Taxe récupérable" error={fieldErrors.tax}><TextInput type="number" min={0} step="0.01" value={tax} onChange={(e) => setTax(e.target.value)} /></Field>
          <Field label="Devise" error={fieldErrors.currency}>
            <Select value={currency} onChange={(e) => setCurrency(e.target.value)}>
              {(lookups.data?.currencies ?? [{ code: currency }]).map((c) => <option key={c.code} value={c.code}>{c.code}</option>)}
            </Select>
          </Field>
        </div>
        <Field label="Référence (n° de facture, de reçu…)"><TextInput value={reference} maxLength={100} onChange={(e) => setReference(e.target.value)} /></Field>
        <div style={{ textAlign: "right" }}>Total : <b>{money(total, currency)}</b></div>
        {(categories.data ?? []).length === 0 && !categories.loading && (
          <div className="nx-err">Aucune catégorie de dépense : créez-en une (bouton « Catégories »).</div>
        )}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function ExpenseDrawer({ id, onClose, onChanged, onEdit }: { id: string; onClose: () => void; onChanged: () => void; onEdit: (e: Expense) => void }) {
  const { hasPermission, user } = useAuth();
  const toast = useToast();
  const detail = useAsync(() => getExpense(id), [id]);
  const [dialog, setDialog] = useState<"reject" | "cancel" | "pay" | null>(null);
  const { busy, error, submit } = useSubmit();
  const e = detail.data;
  const refresh = () => { detail.reload(); onChanged(); };
  const mine = e?.createdBy === user?.id;

  async function act(fn: () => Promise<void>, done: string) {
    const ok = await submit(async () => { await fn(); return true; });
    if (ok) { toast.success(done); refresh(); }
  }

  return (
    <Drawer title={e ? `Dépense ${e.documentNumber}` : "Dépense"} onClose={onClose}>
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {!e ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", gap: 8, alignItems: "center" }}>
            <Badge tone={EXPENSE_TONE[e.status]}>{EXPENSE_STATUS[e.status] ?? e.status}</Badge>
            <span>{dateShort(e.expenseDate)}</span>
          </div>
          <div style={{ fontWeight: 600 }}>{e.description}</div>
          <div style={{ display: "grid", gap: 2, color: "var(--nx-text-2)" }}>
            <div>Catégorie : {e.categoryName ?? "—"} · Fournisseur : {e.supplierName ?? "—"}{e.reference ? ` · réf. ${e.reference}` : ""}</div>
            <div>HT {money(e.amount, e.currency)} · taxe {money(e.taxAmount, e.currency)} · <b>total {money(e.total, e.currency)}</b></div>
            <div>Saisie par {e.createdByName ?? "—"} le {dateTime(e.createdAt)}</div>
            {e.approvedByName && <div>Approuvée par {e.approvedByName}{e.approvedAt ? ` le ${dateTime(e.approvedAt)}` : ""}</div>}
            {e.paidAt && <div>Payée le {dateTime(e.paidAt)} par {e.paidByName ?? "—"} sur {e.financialAccountName} · écriture {e.journalEntryNumber}</div>}
            {e.decisionReason && <div className="nx-error-text">Motif : {e.decisionReason}</div>}
          </div>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {hasPermission("finance.create") && (e.status === "draft" || e.status === "submitted") && <Button size="sm" variant="subtle" onClick={() => onEdit(e)}>Modifier</Button>}
            {hasPermission("finance.create") && e.status === "draft" && <Button size="sm" disabled={busy} onClick={() => act(() => submitExpense(e.id), "Dépense soumise.")}>Soumettre</Button>}
            {hasPermission("finance.pay") && e.status === "submitted" && (mine
              ? <Badge>Approbation par un autre responsable</Badge>
              : <>
                  <Button size="sm" disabled={busy} onClick={() => act(() => approveExpense(e.id), "Dépense approuvée.")}>Approuver</Button>
                  <Button size="sm" variant="ghost" onClick={() => setDialog("reject")}>Refuser</Button>
                </>)}
            {hasPermission("finance.pay") && e.status === "approved" && <Button size="sm" onClick={() => setDialog("pay")}>Payer</Button>}
            {hasPermission("finance.create") && ["draft", "submitted", "approved"].includes(e.status) && <Button size="sm" variant="ghost" onClick={() => setDialog("cancel")}>Annuler</Button>}
          </div>
        </div>
      )}
      {dialog === "reject" && e && (
        <ConfirmDialog title="Refuser la dépense" requireReason danger confirmLabel="Refuser" message="Le demandeur verra le motif du refus."
          onCancel={() => setDialog(null)} onConfirm={(reason) => { setDialog(null); void act(() => rejectExpense(e.id, reason), "Dépense refusée."); }} />
      )}
      {dialog === "cancel" && e && (
        <ConfirmDialog title="Annuler la dépense" requireReason danger confirmLabel="Annuler la dépense" message="La dépense ne pourra plus être payée."
          onCancel={() => setDialog(null)} onConfirm={(reason) => { setDialog(null); void act(() => cancelExpense(e.id, reason), "Dépense annulée."); }} />
      )}
      {dialog === "pay" && e && <PayDialog expense={e} onClose={() => setDialog(null)} onDone={() => { setDialog(null); refresh(); }} />}
    </Drawer>
  );
}

function PayDialog({ expense, onClose, onDone }: { expense: Expense; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [operationUuid] = useState(newOperationId);
  const accounts = useAsync(listManagedAccounts, []);
  const [account, setAccount] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "expense.insufficient_balance": "account", "expense.currency_mismatch": "account" });
  const eligible = (accounts.data ?? []).filter((a) => a.status === "active" && a.currency === expense.currency);

  async function save() {
    if (!account) return fail("account", "Choisissez le compte de paiement.");
    const r = await submit(() => payExpense(expense.id, operationUuid, account));
    if (r) { toast.success(`Dépense payée (écriture ${r.journalEntryNumber}).`); onDone(); }
  }

  return (
    <Modal title={`Payer ${expense.documentNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => void save()}>Payer {money(expense.total, expense.currency)}</Button></>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(420px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Payé depuis" required error={fieldErrors.account}>
          <Select value={account} onChange={(e) => setAccount(e.target.value)}>
            <option value="">—</option>
            {eligible.map((a) => <option key={a.id} value={a.id}>{a.name} — solde {money(a.balance, a.currency)}</option>)}
          </Select>
        </Field>
        <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>Écriture : charge + taxe récupérable au débit, compte de paiement au crédit.</div>
      </div>
    </Modal>
  );
}

function CategoriesDialog({ onClose }: { onClose: () => void }) {
  const toast = useToast();
  const categories = useAsync(listExpenseCategories, []);
  const ledgers = useAsync(() => ledgerOptions("expense"), []);
  const [edit, setEdit] = useState<ExpenseCategory | "new" | null>(null);
  const [name, setName] = useState("");
  const [code, setCode] = useState("");
  const [ledger, setLedger] = useState("");
  const [status, setStatus] = useState("active");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "expense.category_name": "name", "expense.category_exists": "name", "expense.ledger_account": "ledger",
  });

  function open(c: ExpenseCategory | "new") {
    setEdit(c);
    setName(c === "new" ? "" : c.name);
    setCode(c === "new" ? "" : c.code ?? "");
    setLedger(c === "new" ? "" : c.ledgerAccountId ?? "");
    setStatus(c === "new" ? "active" : c.status);
  }

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!name.trim()) return fail("name", "Nom obligatoire.");
    const body = { name: name.trim(), code: code.trim() || null, ledgerAccountId: ledger || null, status };
    const ok = await submit(async () => { if (edit && edit !== "new") await updateExpenseCategory(edit.id, body); else await createExpenseCategory(body); return true; });
    if (ok) { toast.success("Catégorie enregistrée."); setEdit(null); categories.reload(); }
  }

  return (
    <Modal title="Catégories de dépense" onClose={onClose} footer={<Button variant="ghost" onClick={onClose}>Fermer</Button>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(620px, 92vw)" }}>
        <DataTable
          columns={[
            { key: "n", header: "Catégorie", render: (c) => <button type="button" style={linkStyle} onClick={() => open(c)}>{c.name}</button> },
            { key: "g", header: "Compte de charge", render: (c) => (c.ledgerAccountCode ? `${c.ledgerAccountCode} ${c.ledgerAccountName}` : "charges diverses (défaut)") },
            { key: "s", header: "Statut", render: (c) => (c.status === "active" ? "Active" : "Inactive") },
          ]}
          rows={categories.data ?? []} rowKey={(c) => c.id} empty={categories.loading ? "Chargement…" : "Aucune catégorie."} />
        {!edit && <div><Button size="sm" onClick={() => open("new")}>Nouvelle catégorie</Button></div>}
        {edit && (
          <form onSubmit={save} style={{ display: "grid", gap: 8, gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", alignItems: "end" }}>
            {error && <div role="alert" className="nx-error-text" style={{ gridColumn: "1 / -1" }}>{error}</div>}
            <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={120} onChange={(e) => setName(e.target.value)} /></Field>
            <Field label="Code"><TextInput value={code} maxLength={30} onChange={(e) => setCode(e.target.value)} /></Field>
            <Field label="Compte de charge" error={fieldErrors.ledger}>
              <Select value={ledger} onChange={(e) => setLedger(e.target.value)}>
                <option value="">Charges diverses (défaut)</option>
                {(ledgers.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.code} {l.name}</option>)}
              </Select>
            </Field>
            <Field label="Statut">
              <Select value={status} onChange={(e) => setStatus(e.target.value)}>
                <option value="active">Active</option><option value="inactive">Inactive</option>
              </Select>
            </Field>
            <div style={{ display: "flex", gap: 6 }}>
              <Button size="sm" variant="ghost" onClick={() => setEdit(null)}>Annuler</Button>
              <Button size="sm" disabled={busy} onClick={() => save()}>Enregistrer</Button>
            </div>
          </form>
        )}
      </div>
    </Modal>
  );
}
