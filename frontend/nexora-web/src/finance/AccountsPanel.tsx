import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateTime, money, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  ACCOUNT_TYPE, accountMovements, createAccount, financeLookups, ledgerOptions, listManagedAccounts, recordCharge, recordOpeningBalance, setAccountStatus,
  transferFunds, updateAccount, type ManagedAccount,
} from "@/finance/treasuryApi";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Comptes de trésorerie : caisses, banques, mobile money — création, paramétrage, mouvements, transferts, frais. */
export function AccountsPanel({ onChanged }: { onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const accounts = useAsync(listManagedAccounts, []);
  const [editing, setEditing] = useState<ManagedAccount | "new" | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const [dialog, setDialog] = useState<"transfer" | "charge" | null>(null);
  const reload = () => { accounts.reload(); onChanged(); };
  const list = accounts.data ?? [];

  return (
    <Card>
      <SectionLabel action={
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {hasPermission("finance.transfer") && <Button size="sm" variant="subtle" onClick={() => setDialog("transfer")}>Transfert entre comptes</Button>}
          {hasPermission("finance.reconcile") && <Button size="sm" variant="ghost" onClick={() => setDialog("charge")}>Frais bancaires</Button>}
          {hasPermission("finance.update") && <Button size="sm" onClick={() => setEditing("new")}>Nouveau compte</Button>}
        </div>}>
        Comptes de trésorerie
      </SectionLabel>
      {accounts.error && <div role="alert" className="nx-error-text">{accounts.error}</div>}
      <DataTable exportTitle="Comptes de trésorerie"
        columns={[
          { key: "n", header: "Compte", render: (a) => <button type="button" style={linkStyle} onClick={() => setOpenId(a.id)}>{a.name}</button> },
          { key: "t", header: "Type", render: (a) => ACCOUNT_TYPE[a.accountType] ?? a.accountType },
          { key: "no", header: "N°", render: (a) => a.accountNumberMasked ?? "—" },
          { key: "gl", header: "Compte comptable", render: (a) => (a.ledgerAccountCode ? `${a.ledgerAccountCode} ${a.ledgerAccountName}` : "par défaut") },
          { key: "b", header: "Solde", numeric: true, render: (a) => money(a.balance, a.currency) },
          { key: "m", header: "Dernier mouvement", render: (a) => (a.lastMovementAt ? dateTime(a.lastMovementAt) : "—") },
          { key: "s", header: "Statut", render: (a) => <Badge tone={a.status === "active" ? "success" : a.status === "closed" ? "error" : "warning"}>
            {a.status === "active" ? "Actif" : a.status === "closed" ? "Clôturé" : "Inactif"}</Badge> },
        ]}
        rows={list} rowKey={(a) => a.id} empty={accounts.loading ? "Chargement…" : "Aucun compte."} />
      {editing && <AccountDialog account={editing === "new" ? null : editing} onClose={() => setEditing(null)}
                                 onSaved={() => { setEditing(null); reload(); }} />}
      {openId && <AccountDrawer key={openId} account={list.find((a) => a.id === openId) ?? null} onClose={() => setOpenId(null)}
                                onEdit={(a) => { setOpenId(null); setEditing(a); }} onChanged={reload} />}
      {dialog === "transfer" && <TransferDialog accounts={list} onClose={() => setDialog(null)} onDone={() => { setDialog(null); reload(); }} />}
      {dialog === "charge" && <ChargeDialog accounts={list} onClose={() => setDialog(null)} onDone={() => { setDialog(null); reload(); }} />}
    </Card>
  );
}

function AccountDialog({ account, onClose, onSaved }: { account: ManagedAccount | null; onClose: () => void; onSaved: () => void }) {
  const toast = useToast();
  const lookups = useAsync(financeLookups, []);
  const ledgers = useAsync(() => ledgerOptions("asset"), []);
  const [name, setName] = useState(account?.name ?? "");
  const [type, setType] = useState(account?.accountType ?? "bank");
  const [currency, setCurrency] = useState(account?.currency ?? "HTG");
  const [masked, setMasked] = useState(account?.accountNumberMasked ?? "");
  const [ledger, setLedger] = useState(account?.ledgerAccountId ?? "");
  const locked = (account?.transactionCount ?? 0) > 0;
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "treasury.account_name": "name", "treasury.account_exists": "name", "treasury.currency": "currency", "treasury.ledger_account": "ledger",
  });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!name.trim()) return fail("name", "Nom obligatoire.");
    const body = { name: name.trim(), accountType: type, currency, accountNumberMasked: masked.trim() || null, ledgerAccountId: ledger || null };
    const ok = await submit(async () => { if (account) await updateAccount(account.id, body); else await createAccount(body); return true; });
    if (ok) { toast.success(account ? "Compte modifié." : "Compte créé (solde 0 : alimentez-le par un transfert ou un encaissement)."); onSaved(); }
  }

  return (
    <Modal title={account ? `Modifier « ${account.name} »` : "Nouveau compte de trésorerie"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(480px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={120} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Type" hint={locked ? "Figé : le compte a déjà des mouvements." : undefined}>
          <Select value={type} disabled={locked} onChange={(e) => setType(e.target.value)}>
            {Object.entries(ACCOUNT_TYPE).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Devise" required error={fieldErrors.currency}>
          <Select value={currency} disabled={locked} onChange={(e) => setCurrency(e.target.value)}>
            {(lookups.data?.currencies ?? [{ code: currency }]).map((c) => <option key={c.code} value={c.code}>{c.code}</option>)}
          </Select>
        </Field>
        <Field label="N° de compte (masqué)" hint="Ex. ****1234 — jamais le numéro complet."><TextInput value={masked} maxLength={40} onChange={(e) => setMasked(e.target.value)} /></Field>
        <Field label="Compte comptable" error={fieldErrors.ledger} hint="Vide : compte Caisse ou Banque par défaut selon le type.">
          <Select value={ledger} disabled={locked} onChange={(e) => setLedger(e.target.value)}>
            <option value="">Par défaut</option>
            {(ledgers.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.code} {l.name}</option>)}
          </Select>
        </Field>
        {!account && <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>Un nouveau compte part à zéro : aucun solde ne s'écrit sans écriture comptable.</div>}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function AccountDrawer({ account, onClose, onEdit, onChanged }: {
  account: ManagedAccount | null; onClose: () => void; onEdit: (a: ManagedAccount) => void; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const movements = useAsync(() => (account ? accountMovements(account.id, from || undefined, to || undefined) : Promise.resolve([])), [account?.id, from, to]);
  const [statusDialog, setStatusDialog] = useState<"inactive" | "active" | "closed" | null>(null);
  const [opening, setOpening] = useState(false);
  const { error, submit } = useSubmit();
  if (!account) return null;

  return (
    <Drawer title={account.name} onClose={onClose}>
      <div style={{ display: "grid", gap: 12 }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div>{ACCOUNT_TYPE[account.accountType] ?? account.accountType} · {account.currency}{account.accountNumberMasked ? ` · ${account.accountNumberMasked}` : ""}</div>
        <div style={{ fontSize: 24, fontWeight: 700 }} className="nx-numeric">{money(account.balance, account.currency)}</div>
        {hasPermission("finance.update") && (
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <Button size="sm" variant="subtle" onClick={() => onEdit(account)}>Modifier</Button>
            {account.status === "active" && <Button size="sm" variant="ghost" onClick={() => setStatusDialog("inactive")}>Désactiver</Button>}
            {account.status === "inactive" && <Button size="sm" variant="ghost" onClick={() => setStatusDialog("active")}>Réactiver</Button>}
            {account.status !== "closed" && <Button size="sm" variant="ghost" onClick={() => setStatusDialog("closed")}>Clôturer</Button>}
            {account.status === "active" && account.transactionCount === 0 && (
              <Button size="sm" onClick={() => setOpening(true)}>Saisir le solde d'ouverture</Button>
            )}
          </div>
        )}
        {opening && <OpeningBalanceDialog account={account} onClose={() => setOpening(false)}
                                          onDone={() => { setOpening(false); toast.success("Solde d'ouverture enregistré et comptabilisé."); onChanged(); }} />}
        <SectionLabel>Mouvements</SectionLabel>
        <div style={{ display: "flex", gap: 8 }}>
          <Field label="Du"><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
          <Field label="Au"><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
        </div>
        {movements.error && <div role="alert" className="nx-error-text">{movements.error}</div>}
        <DataTable exportTitle={`Mouvements — ${account.name}`}
          columns={[
            { key: "d", header: "Date", render: (m) => dateTime(m.occurredAt) },
            { key: "l", header: "Opération", render: (m) => <>{m.label ?? m.transactionType}{m.counterpartyAccountName ? ` (${m.amount < 0 ? "vers" : "depuis"} ${m.counterpartyAccountName})` : ""}</> },
            { key: "e", header: "Écriture", render: (m) => m.journalEntryNumber ?? "—" },
            { key: "a", header: "Montant", numeric: true, render: (m) => <span style={{ color: m.amount < 0 ? "var(--nx-neg)" : undefined }}>{money(m.amount, m.currency)}</span> },
            { key: "u", header: "Par", render: (m) => m.createdByName ?? "—" },
          ]}
          rows={movements.data ?? []} rowKey={(m) => m.id} empty={movements.loading ? "Chargement…" : "Aucun mouvement."} />
      </div>
      {statusDialog && (
        <ConfirmDialog requireReason danger={statusDialog === "closed"}
          title={statusDialog === "closed" ? "Clôturer le compte" : statusDialog === "inactive" ? "Désactiver le compte" : "Réactiver le compte"}
          message={statusDialog === "closed" ? "Un compte clôturé ne se rouvre pas. Son solde doit être nul." : "Le compte ne pourra plus être utilisé pour encaisser ou payer tant qu'il est inactif."}
          confirmLabel="Confirmer" onCancel={() => setStatusDialog(null)}
          onConfirm={async (reason) => {
            const target = statusDialog;
            setStatusDialog(null);
            const ok = await submit(async () => { await setAccountStatus(account.id, target, reason); return true; });
            if (ok) { toast.success("Statut du compte mis à jour."); onChanged(); }
          }} />
      )}
    </Drawer>
  );
}

function TransferDialog({ accounts, onClose, onDone }: { accounts: ManagedAccount[]; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [operationUuid] = useState(newOperationId);
  const active = accounts.filter((a) => a.status === "active");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [amount, setAmount] = useState("");
  const [note, setNote] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "treasury.amount": "amount", "treasury.insufficient_balance": "amount", "treasury.same_account": "to", "treasury.currency_mismatch": "to",
  });
  const source = active.find((a) => a.id === from);

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    if (!from) return fail("from", "Choisissez le compte à débiter.");
    if (!to) return fail("to", "Choisissez le compte à créditer.");
    if (a == null || a <= 0) return fail("amount", "Montant positif requis.");
    const r = await submit(() => transferFunds({ operationUuid, fromAccountId: from, toAccountId: to, amount: a, note: note.trim() || null }));
    if (r) { toast.success(`Transfert enregistré (écriture ${r.entryNumber}).`); onDone(); }
  }

  return (
    <Modal title="Transfert entre comptes" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Transférer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Depuis" required error={fieldErrors.from}>
          <Select value={from} onChange={(e) => setFrom(e.target.value)}>
            <option value="">—</option>
            {active.map((a) => <option key={a.id} value={a.id}>{a.name} — {money(a.balance, a.currency)}</option>)}
          </Select>
        </Field>
        <Field label="Vers" required error={fieldErrors.to} hint={source ? `Comptes en ${source.currency} uniquement.` : undefined}>
          <Select value={to} onChange={(e) => setTo(e.target.value)}>
            <option value="">—</option>
            {active.filter((a) => a.id !== from && (!source || a.currency === source.currency)).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
          </Select>
        </Field>
        <Field label={`Montant${source ? ` (${source.currency})` : ""}`} required error={fieldErrors.amount}>
          <TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        <Field label="Note"><TextInput value={note} maxLength={200} placeholder="Ex. dépôt de la recette du jour" onChange={(e) => setNote(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function ChargeDialog({ accounts, onClose, onDone }: { accounts: ManagedAccount[]; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [operationUuid] = useState(newOperationId);
  const eligible = accounts.filter((a) => a.status === "active" && ["bank", "mobile_money", "card_processor"].includes(a.accountType));
  const [account, setAccount] = useState("");
  const [amount, setAmount] = useState("");
  const [description, setDescription] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "treasury.amount": "amount", "treasury.description": "description" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    if (!account) return fail("account", "Choisissez le compte.");
    if (a == null || a <= 0) return fail("amount", "Montant positif requis.");
    if (!description.trim()) return fail("description", "Libellé obligatoire.");
    const r = await submit(() => recordCharge({ operationUuid, accountId: account, amount: a, description: description.trim() }));
    if (r) { toast.success(`Frais enregistrés (écriture ${r.entryNumber}).`); onDone(); }
  }

  return (
    <Modal title="Frais bancaires" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Compte" required error={fieldErrors.account}>
          <Select value={account} onChange={(e) => setAccount(e.target.value)}>
            <option value="">—</option>
            {eligible.map((a) => <option key={a.id} value={a.id}>{a.name} ({a.currency})</option>)}
          </Select>
        </Field>
        <Field label="Montant" required error={fieldErrors.amount}><TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
        <Field label="Libellé" required error={fieldErrors.description}>
          <TextInput value={description} maxLength={200} placeholder="Ex. frais de tenue de compte — septembre" onChange={(e) => setDescription(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Solde d'ouverture : l'argent déjà en caisse ou en banque à la mise en service, contre le report à nouveau. */
function OpeningBalanceDialog({ account, onClose, onDone }: { account: ManagedAccount; onClose: () => void; onDone: () => void }) {
  const [amount, setAmount] = useState("");
  const [date, setDate] = useState(todayIso());
  const [note, setNote] = useState("");
  const [operationUuid] = useState(newOperationId);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "treasury.amount": "amount", "treasury.opening_date": "date" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const value = num(amount);
    if (value === null || value === 0) return fail("amount", "Montant différent de zéro.");
    const ok = await submit(async () => {
      await recordOpeningBalance(account.id, { operationUuid, amount: value, date: date === todayIso() ? null : date, note: note.trim() || null });
      return true;
    });
    if (ok) onDone();
  }

  return (
    <Modal title={`Solde d'ouverture — ${account.name}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(420px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <p style={{ margin: 0, fontSize: 13, color: "var(--nx-text-2)" }}>
          Montant présent sur ce compte à sa mise en service. Il entre en trésorerie et en comptabilité (contre le report à
          nouveau). Saisissable une seule fois, avant toute opération ; un découvert bancaire se saisit en négatif.
        </p>
        <Field label={`Montant (${account.currency})`} required error={fieldErrors.amount}>
          <TextInput inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        <Field label="Date" error={fieldErrors.date}><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <Field label="Note"><TextInput value={note} maxLength={200} onChange={(e) => setNote(e.target.value)} /></Field>
      </form>
    </Modal>
  );
}
