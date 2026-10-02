import { useMemo, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { money } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  ACCOUNT_TYPES, createChartAccount, listAccountingCurrencies, setChartAccountStatus, updateChartAccount, type ChartAccount,
} from "@/accounting/accountingApi";

/** Profondeur de chaque compte dans l'arborescence (indentation de l'affichage). */
function depths(accounts: ChartAccount[]) {
  const byId = new Map(accounts.map((a) => [a.id, a]));
  const memo = new Map<string, number>();
  const depth = (a: ChartAccount, guard = 0): number => {
    if (memo.has(a.id)) return memo.get(a.id)!;
    const p = a.parentAccountId ? byId.get(a.parentAccountId) : undefined;
    const d = p && guard < 20 ? depth(p, guard + 1) + 1 : 0;
    memo.set(a.id, d);
    return d;
  };
  accounts.forEach((a) => depth(a));
  return memo;
}

/** Plan comptable : consultation (solde, usage) et gestion (création, intitulé, regroupement, désactivation). */
export function ChartPanel({ accounts, loading, error, functionalCurrency, onChanged }: {
  accounts: ChartAccount[]; loading: boolean; error: string | null; functionalCurrency: string; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const canManage = hasPermission("accounting.manage_chart");
  const [type, setType] = useState("");
  const [q, setQ] = useState("");
  const [showInactive, setShowInactive] = useState(false);
  const [editing, setEditing] = useState<ChartAccount | "new" | null>(null);
  const depth = useMemo(() => depths(accounts), [accounts]);
  const term = q.trim().toLowerCase();
  const rows = accounts.filter((a) => (!type || a.accountType === type) && (showInactive || a.status === "active")
    && (!term || a.code.toLowerCase().startsWith(term) || a.name.toLowerCase().includes(term)));

  return (
    <Card>
      <SectionLabel action={canManage && <Button size="sm" onClick={() => setEditing("new")}>Nouveau compte</Button>}>
        Plan comptable
      </SectionLabel>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end", marginBottom: 10 }}>
        <Field label="Type">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(ACCOUNT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Recherche"><TextInput value={q} placeholder="Code ou intitulé" onChange={(e) => setQ(e.target.value)} /></Field>
        <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, paddingBottom: 8 }}>
          <input type="checkbox" checked={showInactive} onChange={(e) => setShowInactive(e.target.checked)} /> Afficher les comptes désactivés
        </label>
      </div>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <DataTable exportTitle="Plan comptable"
        columns={[
          { key: "c", header: "Code", render: (a) => <span style={{ paddingLeft: (depth.get(a.id) ?? 0) * 14, fontWeight: a.isPostable ? 400 : 600 }}>{a.code}</span> },
          { key: "n", header: "Intitulé", render: (a) => canManage
            ? <button type="button" className="nx-link" style={{ appearance: "none", background: "none", border: 0, padding: 0, cursor: "pointer", color: "var(--nx-brand)", font: "inherit" }}
                onClick={() => setEditing(a)}>{a.name}</button>
            : a.name },
          { key: "t", header: "Type", render: (a) => ACCOUNT_TYPES[a.accountType] ?? a.accountType },
          { key: "p", header: "Nature", render: (a) => (a.isPostable ? "Mouvementable" : "Regroupement") },
          { key: "b", header: `Solde (${functionalCurrency})`, numeric: true, render: (a) => (a.isPostable ? money(a.balance, functionalCurrency) : "") },
          { key: "l", header: "Lignes", numeric: true, render: (a) => (a.isPostable ? String(a.lineCount) : "") },
          { key: "s", header: "Statut", render: (a) => <Badge tone={a.status === "active" ? "success" : "warning"}>{a.status === "active" ? "Actif" : "Désactivé"}</Badge> },
        ]}
        rows={rows} rowKey={(a) => a.id} empty={loading ? "Chargement…" : "Aucun compte."} />
      <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginTop: 6 }}>
        Solde positif = débiteur, négatif = créditeur (écritures validées).
      </div>
      {editing && <AccountDialog account={editing === "new" ? null : editing} accounts={accounts}
                                 onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onChanged(); }} />}
    </Card>
  );
}

function AccountDialog({ account, accounts, onClose, onSaved }: {
  account: ChartAccount | null; accounts: ChartAccount[]; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const currencies = useAsync(listAccountingCurrencies, []);
  const [code, setCode] = useState(account?.code ?? "");
  const [name, setName] = useState(account?.name ?? "");
  const [accountType, setAccountType] = useState(account?.accountType ?? "expense");
  const [isPostable, setIsPostable] = useState(account?.isPostable ?? true);
  const [parent, setParent] = useState(account?.parentAccountId ?? "");
  const [currency, setCurrency] = useState(account?.currency ?? "");
  const [statusDialog, setStatusDialog] = useState(false);
  const locked = (account?.lineCount ?? 0) > 0;
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "accounting.account_code": "code", "accounting.account_exists": "code", "accounting.account_name": "name",
    "accounting.account_type": "type", "accounting.account_parent": "parent", "accounting.currency_unknown": "currency",
  });
  const parents = accounts.filter((a) => !a.isPostable && a.accountType === accountType && a.id !== account?.id && a.status === "active");

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const c = code.trim();
    if (!c) return fail("code", "Code obligatoire.");
    if (!/^[A-Za-z0-9.-]{1,20}$/.test(c)) return fail("code", "20 caractères au plus : lettres, chiffres, point ou tiret.");
    if (!name.trim()) return fail("name", "Intitulé obligatoire.");
    const body = { code: c, name: name.trim(), accountType, isPostable, parentAccountId: parent || null, currency: currency || null };
    const ok = await submit(async () => {
      if (account) await updateChartAccount(account.id, body); else await createChartAccount(body);
      return true;
    });
    if (ok) { toast.success(account ? "Compte modifié." : `Compte ${c} créé.`); onSaved(); }
  }

  return (
    <Modal title={account ? `Compte ${account.code}` : "Nouveau compte"} onClose={onClose}
      footer={<>
        {account && (account.status === "active"
          ? <Button variant="ghost" disabled={busy} onClick={() => setStatusDialog(true)}>Désactiver</Button>
          : <Button variant="ghost" disabled={busy} onClick={() => setStatusDialog(true)}>Réactiver</Button>)}
        <Button variant="ghost" onClick={onClose}>Annuler</Button>
        <Button disabled={busy} onClick={() => save()}>Enregistrer</Button>
      </>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(480px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {locked && <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
          Ce compte a déjà {account!.lineCount} ligne(s) d'écriture : code, type, nature et devise sont figés pour que la balance et les états restent justes.</div>}
        <Field label="Code" required error={fieldErrors.code}><TextInput value={code} maxLength={20} disabled={locked} onChange={(e) => setCode(e.target.value)} /></Field>
        <Field label="Intitulé" required error={fieldErrors.name}><TextInput value={name} maxLength={150} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Type" required error={fieldErrors.type}>
          <Select value={accountType} disabled={locked} onChange={(e) => { setAccountType(e.target.value); setParent(""); }}>
            {Object.entries(ACCOUNT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Nature" hint="Un compte de regroupement totalise d'autres comptes et ne reçoit pas d'écriture.">
          <Select value={isPostable ? "postable" : "group"} disabled={locked} onChange={(e) => setIsPostable(e.target.value === "postable")}>
            <option value="postable">Mouvementable</option>
            <option value="group">Regroupement</option>
          </Select>
        </Field>
        <Field label="Regroupé sous" error={fieldErrors.parent}>
          <Select value={parent} onChange={(e) => setParent(e.target.value)}>
            <option value="">— Aucun —</option>
            {parents.map((a) => <option key={a.id} value={a.id}>{a.code} {a.name}</option>)}
          </Select>
        </Field>
        <Field label="Devise imposée" error={fieldErrors.currency} hint="Vide : toutes devises.">
          <Select value={currency} disabled={locked} onChange={(e) => setCurrency(e.target.value)}>
            <option value="">Toutes</option>
            {(currencies.data ?? []).map((c) => <option key={c.code} value={c.code}>{c.code}</option>)}
          </Select>
        </Field>
        {account && account.usedBy.length > 0 && (
          <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>Utilisé par : {account.usedBy.join(", ")}.</div>
        )}
        <button type="submit" hidden />
      </form>
      {statusDialog && account && (
        <ConfirmDialog requireReason
          title={account.status === "active" ? `Désactiver le compte ${account.code}` : `Réactiver le compte ${account.code}`}
          confirmLabel={account.status === "active" ? "Désactiver" : "Réactiver"}
          message={account.status === "active"
            ? "Un compte désactivé n'est plus proposé à la saisie. Il doit être soldé et ne servir à aucun paramétrage."
            : "Le compte sera de nouveau proposé à la saisie."}
          onCancel={() => setStatusDialog(false)}
          onConfirm={async (reason) => {
            setStatusDialog(false);
            const ok = await submit(() => setChartAccountStatus(account.id, account.status === "active" ? "inactive" : "active", reason).then(() => true));
            if (ok) { toast.success(account.status === "active" ? "Compte désactivé." : "Compte réactivé."); onSaved(); }
          }} />
      )}
    </Modal>
  );
}
