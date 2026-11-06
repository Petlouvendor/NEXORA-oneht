import { useMemo, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  createDraft, deleteDraft, getEntry, listAccountingCurrencies, postEntry, reverseEntry, searchEntries, updateDraft,
  type ChartAccount, type Journal, type JournalEntry,
} from "@/accounting/accountingApi";
import { draftTotals, emptyRow, rowsFromEntry, validateDraft, type DraftRow } from "@/accounting/entryDraft";

export const ENTRY_STATUS: Record<string, { label: string; tone: "success" | "warning" | "info" | "error" }> = {
  draft: { label: "Brouillon", tone: "warning" },
  posted: { label: "Validée", tone: "success" },
  reversed: { label: "Contrepassée", tone: "info" },
};
const ORIGIN: Record<string, string> = { manual: "Manuelle", automatic: "Automatique", system: "Système", import: "Import" };

/** Écritures contrepassables depuis cet écran (audit 2026-10, A-006) : les autres se corrigent par leur opération. */
const REVERSIBLE = new Set(["manual", "adjustment"]);

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit" } as const;

/** Écritures : recherche, détail, saisie / modification de brouillon, validation, suppression, contrepassation. */
export function EntriesPanel({ journals, accounts, onChanged }: {
  journals: Journal[]; accounts: ChartAccount[]; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const [status, setStatus] = useState("");
  const [journal, setJournal] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [q, setQ] = useState("");
  const [term, setTerm] = useState("");
  const entries = useAsync(() => searchEntries({ status, journal, from, to, q: term, limit: 300 }), [status, journal, from, to, term]);
  const [openId, setOpenId] = useState<string | null>(null);
  const [editing, setEditing] = useState<JournalEntry | "new" | null>(null);
  const reload = () => { entries.reload(); onChanged(); };
  const list = entries.data ?? [];

  return (
    <Card>
      <SectionLabel action={hasPermission("accounting.create") &&
        <Button size="sm" onClick={() => setEditing("new")}>Nouvelle écriture</Button>}>
        Écritures
      </SectionLabel>
      <form onSubmit={(e) => { e.preventDefault(); setTerm(q.trim()); }}
        style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end", marginBottom: 10 }}>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(ENTRY_STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </Select>
        </Field>
        <Field label="Journal">
          <Select value={journal} onChange={(e) => setJournal(e.target.value)}>
            <option value="">Tous</option>
            {journals.map((j) => <option key={j.code} value={j.code}>{j.code} — {j.name}</option>)}
          </Select>
        </Field>
        <Field label="Du"><TextInput type="date" value={from} onChange={(e) => setFrom(e.target.value)} /></Field>
        <Field label="Au"><TextInput type="date" value={to} onChange={(e) => setTo(e.target.value)} /></Field>
        <Field label="Recherche"><TextInput value={q} placeholder="N°, libellé ou compte" onChange={(e) => setQ(e.target.value)} /></Field>
        <Button type="submit" size="sm" variant="subtle">Rechercher</Button>
      </form>
      {entries.error && <div role="alert" className="nx-error-text">{entries.error}</div>}
      <DataTable exportTitle="Écritures comptables"
        columns={[
          { key: "d", header: "Date", render: (e) => dateShort(e.entryDate) },
          { key: "j", header: "Journal", render: (e) => <Badge>{e.journalCode}</Badge> },
          { key: "n", header: "N°", render: (e) => <button type="button" style={linkStyle} onClick={() => setOpenId(e.id)}>{e.entryNumber}</button> },
          { key: "l", header: "Libellé", render: (e) => e.description ?? "—" },
          { key: "o", header: "Origine", render: (e) => ORIGIN[e.origin] ?? e.origin },
          { key: "m", header: "Montant", numeric: true, render: (e) => money(e.totalDebit, e.currency) },
          { key: "s", header: "Statut", render: (e) => <Badge tone={ENTRY_STATUS[e.status]?.tone}>{ENTRY_STATUS[e.status]?.label ?? e.status}</Badge> },
        ]}
        rows={list} rowKey={(e) => e.id}
        empty={entries.loading ? "Chargement…" : "Aucune écriture pour ces critères."} />
      {list.length >= 300 && <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginTop: 6 }}>300 écritures affichées : affinez la recherche.</div>}
      {openId && <EntryDrawer key={openId} id={openId} onOpen={setOpenId} onClose={() => setOpenId(null)}
                              onEdit={(e) => { setOpenId(null); setEditing(e); }} onChanged={reload} />}
      {editing && <EntryEditor entry={editing === "new" ? null : editing} journals={journals} accounts={accounts}
                               onClose={() => setEditing(null)}
                               onSaved={(id) => { setEditing(null); reload(); setOpenId(id); }} />}
    </Card>
  );
}

function EntryDrawer({ id, onOpen, onClose, onEdit, onChanged }: {
  id: string; onOpen: (id: string) => void; onClose: () => void; onEdit: (e: JournalEntry) => void; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const entry = useAsync(() => getEntry(id), [id]);
  const [dialog, setDialog] = useState<"delete" | "reverse" | null>(null);
  const { busy, error, submit } = useSubmit();
  const e = entry.data;
  const foreign = !!e && !!e.functionalCurrency && e.currency !== e.functionalCurrency;

  async function post() {
    const r = await submit(() => postEntry(id));
    if (!r) return;
    const warn = r.controls.filter((c) => c.status !== "pass");
    toast.success(`Écriture ${r.entryNumber} validée.`);
    if (warn.length) toast.push(warn.map((c) => c.detail ?? c.label).join(" · "), "warning");
    entry.reload(); onChanged();
  }

  return (
    <Drawer title={e ? `Écriture ${e.entryNumber}` : "Écriture"} onClose={onClose}>
      {entry.error && <div role="alert" className="nx-error-text">{entry.error}</div>}
      {e && (
        <div style={{ display: "grid", gap: 12 }}>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <Badge>{e.journalCode}</Badge>
            <Badge tone={ENTRY_STATUS[e.status]?.tone}>{ENTRY_STATUS[e.status]?.label ?? e.status}</Badge>
            <span>{dateShort(e.entryDate)} · {ORIGIN[e.origin] ?? e.origin}{e.sourceType && e.sourceType !== e.origin ? ` (${e.sourceType})` : ""}</span>
          </div>
          {e.description && <div>{e.description}</div>}
          <div style={{ fontSize: 24, fontWeight: 700 }} className="nx-numeric">
            {money(e.totalDebit, e.currency)}
            {foreign && <span style={{ fontSize: 13, fontWeight: 400, color: "var(--nx-text-2)" }}> = {money(e.baseTotal, e.functionalCurrency!)}</span>}
          </div>
          <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
            Saisie {dateTime(e.createdAt)}{e.createdByName ? ` par ${e.createdByName}` : ""}
            {e.postedAt && <> · validée {dateTime(e.postedAt)}{e.postedByName ? ` par ${e.postedByName}` : ""}</>}
          </div>
          {e.reversalOfEntryId && <div>Contrepassation de <button type="button" style={linkStyle} onClick={() => onOpen(e.reversalOfEntryId!)}>l'écriture d'origine</button></div>}
          {e.reversedByEntryId && <div>Annulée par <button type="button" style={linkStyle} onClick={() => onOpen(e.reversedByEntryId!)}>son écriture de contrepassation</button></div>}
          <DataTable exportTitle={`Écriture ${e.entryNumber}`}
            columns={[
              { key: "a", header: "Compte", render: (l) => `${l.accountCode} ${l.accountName}` },
              { key: "l", header: "Libellé", render: (l) => l.description ?? "" },
              { key: "d", header: "Débit", numeric: true, render: (l) => (l.debit ? money(l.debit, e.currency) : "") },
              { key: "c", header: "Crédit", numeric: true, render: (l) => (l.credit ? money(l.credit, e.currency) : "") },
              ...(foreign ? [{ key: "b", header: `Contre-valeur ${e.functionalCurrency}`, numeric: true,
                render: (l: JournalEntry["lines"][number]) => money(l.baseDebit - l.baseCredit, e.functionalCurrency!) }] : []),
            ]}
            rows={e.lines} rowKey={(l) => String(l.lineNumber)}
            footer={<><td colSpan={2}><strong>Total</strong></td>
              <td className="nx-num"><strong>{money(e.totalDebit, e.currency)}</strong></td>
              <td className="nx-num"><strong>{money(e.totalCredit, e.currency)}</strong></td>{foreign && <td />}</>} />
          {!e.isBalanced && <div className="nx-error-text">Écriture déséquilibrée : elle ne peut pas être validée.</div>}
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            {e.status === "draft" && hasPermission("accounting.post") &&
              <Button size="sm" disabled={busy || !e.isBalanced} onClick={post}>Valider</Button>}
            {e.status === "draft" && hasPermission("accounting.create") && <>
              <Button size="sm" variant="subtle" disabled={busy} onClick={() => onEdit(e)}>Modifier</Button>
              <Button size="sm" variant="danger" disabled={busy} onClick={() => setDialog("delete")}>Supprimer</Button>
            </>}
            {e.status === "posted" && !e.reversedByEntryId && hasPermission("accounting.reverse") && REVERSIBLE.has(e.origin) &&
              <Button size="sm" variant="subtle" disabled={busy} onClick={() => setDialog("reverse")}>Contrepasser</Button>}
          </div>
          {e.status === "posted" && <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>
            {REVERSIBLE.has(e.origin)
              ? "Une écriture validée ne se modifie pas : on la corrige par une contrepassation puis une nouvelle écriture."
              : "Écriture née d'une opération (vente, paiement, dépense…) : pour la corriger, annulez l'opération elle-même, qui remet aussi d'aplomb créances, trésorerie et stock."}
          </div>}
        </div>
      )}
      {dialog === "delete" && e && (
        <ConfirmDialog title="Supprimer le brouillon" danger confirmLabel="Supprimer"
          message={`Le brouillon ${e.entryNumber} sera supprimé. Il n'a encore aucun effet sur les comptes.`}
          onCancel={() => setDialog(null)}
          onConfirm={async () => {
            setDialog(null);
            if ((await submit(() => deleteDraft(e.id).then(() => true)))) { toast.success("Brouillon supprimé."); onChanged(); onClose(); }
          }} />
      )}
      {dialog === "reverse" && e && (
        <ConfirmDialog title={`Contrepasser l'écriture ${e.entryNumber}`} requireReason confirmLabel="Contrepasser"
          message="Une écriture inverse est validée à la date du jour : les soldes reviennent à leur état antérieur et les deux écritures restent visibles."
          impact={<>Montant : <strong>{money(e.totalDebit, e.currency)}</strong></>}
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            setDialog(null);
            const rid = await submit(() => reverseEntry(e.id, reason));
            if (rid) { toast.success("Écriture contrepassée."); onChanged(); onOpen(rid); }
          }} />
      )}
    </Drawer>
  );
}

function EntryEditor({ entry, journals, accounts, onClose, onSaved }: {
  entry: JournalEntry | null; journals: Journal[]; accounts: ChartAccount[]; onClose: () => void; onSaved: (id: string) => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const currencies = useAsync(listAccountingCurrencies, []);
  const [journalCode, setJournalCode] = useState(entry?.journalCode ?? "OD");
  const [entryDate, setEntryDate] = useState(entry?.entryDate ?? todayIso());
  const [currency, setCurrency] = useState(entry?.currency ?? "");
  const [description, setDescription] = useState(entry?.description ?? "");
  const [rows, setRows] = useState<DraftRow[]>(entry ? rowsFromEntry(entry) : [emptyRow(), emptyRow()]);
  const { busy, error, fieldErrors, submit, fail, setError } = useSubmit({
    "accounting.future_date": "date", "accounting.period_closed": "date", "accounting.no_open_period": "date",
    "accounting.currency_unknown": "currency",
  });
  const postable = useMemo(() => accounts.filter((a) => a.isPostable && a.status === "active"), [accounts]);
  const effectiveCurrency = currency || currencies.data?.[0]?.code || "HTG";
  const totals = draftTotals(rows);
  const manualJournals = journals.filter((j) => j.status === "active" && !["AN", "CL"].includes(j.code));
  const setRow = (i: number, patch: Partial<DraftRow>) => setRows((p) => p.map((r, j) => (j === i ? { ...r, ...patch } : r)));

  async function save(thenPost: boolean, e?: FormEvent) {
    e?.preventDefault();
    if (!entryDate) return fail("date", "Date obligatoire.");
    if (entryDate > todayIso()) return fail("date", "Une écriture ne peut pas être datée dans le futur.");
    const v = validateDraft(rows);
    if ("error" in v) { setError(v.error); return; }
    const body = { journalCode, entryDate, currency: effectiveCurrency, description: description.trim() || null, lines: v.lines };
    const id = await submit(async () => {
      const draftId = entry ? (await updateDraft(entry.id, body), entry.id) : await createDraft(body);
      if (thenPost) {
        const r = await postEntry(draftId);
        toast.success(`Écriture ${r.entryNumber} validée.`);
      } else toast.success(entry ? "Brouillon mis à jour." : "Brouillon enregistré : il n'a pas encore d'effet sur les comptes.");
      return draftId;
    });
    if (id) onSaved(id);
  }

  return (
    <Modal title={entry ? `Modifier le brouillon ${entry.entryNumber}` : "Nouvelle écriture"} onClose={onClose}
      footer={<>
        <Button variant="ghost" onClick={onClose}>Annuler</Button>
        <Button variant="subtle" disabled={busy} onClick={() => save(false)}>Enregistrer le brouillon</Button>
        {hasPermission("accounting.post") && <Button disabled={busy || !totals.balanced} onClick={() => save(true)}>Enregistrer et valider</Button>}
      </>}>
      <form onSubmit={(e) => save(false, e)} style={{ display: "grid", gap: 10, minWidth: "min(760px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 8 }}>
          <Field label="Journal" required>
            <Select value={journalCode} onChange={(e) => setJournalCode(e.target.value)}>
              {manualJournals.map((j) => <option key={j.code} value={j.code}>{j.code} — {j.name}</option>)}
            </Select>
          </Field>
          <Field label="Date" required error={fieldErrors.date}>
            <TextInput type="date" value={entryDate} max={todayIso()} onChange={(e) => setEntryDate(e.target.value)} />
          </Field>
          <Field label="Devise" required error={fieldErrors.currency}>
            <Select value={effectiveCurrency} onChange={(e) => setCurrency(e.target.value)}>
              {(currencies.data ?? [{ code: effectiveCurrency, name: effectiveCurrency, isCompanyDefault: true }]).map((c) =>
                <option key={c.code} value={c.code}>{c.code}{c.isCompanyDefault ? " (entreprise)" : ""}</option>)}
            </Select>
          </Field>
        </div>
        <Field label="Libellé"><TextInput value={description} maxLength={250} placeholder="Ex. régularisation loyer septembre" onChange={(e) => setDescription(e.target.value)} /></Field>
        <SectionLabel>Lignes</SectionLabel>
        <div className="nx-scroll-x">
          <table className="nx-table">
            <thead><tr><th>Compte</th><th>Libellé de ligne</th><th className="nx-num">Débit</th><th className="nx-num">Crédit</th><th /></tr></thead>
            <tbody>
              {rows.map((r, i) => (
                <tr key={i}>
                  <td style={{ minWidth: 220 }}>
                    <Select aria-label={`Compte ligne ${i + 1}`} value={r.accountCode} onChange={(e) => setRow(i, { accountCode: e.target.value })}>
                      <option value="">— Choisir —</option>
                      {postable.map((a) => <option key={a.id} value={a.code}>{a.code} {a.name}</option>)}
                    </Select>
                  </td>
                  <td><TextInput aria-label={`Libellé ligne ${i + 1}`} value={r.description} maxLength={250} onChange={(e) => setRow(i, { description: e.target.value })} /></td>
                  <td style={{ width: 120 }}><TextInput aria-label={`Débit ligne ${i + 1}`} inputMode="decimal" value={r.debit}
                    onChange={(e) => setRow(i, { debit: e.target.value, credit: e.target.value ? "" : r.credit })} /></td>
                  <td style={{ width: 120 }}><TextInput aria-label={`Crédit ligne ${i + 1}`} inputMode="decimal" value={r.credit}
                    onChange={(e) => setRow(i, { credit: e.target.value, debit: e.target.value ? "" : r.debit })} /></td>
                  <td>{rows.length > 2 && <Button type="button" size="sm" variant="ghost" aria-label={`Retirer la ligne ${i + 1}`}
                    onClick={() => setRows((p) => p.filter((_, j) => j !== i))}>✕</Button>}</td>
                </tr>
              ))}
            </tbody>
            <tfoot><tr>
              <td colSpan={2}><Button type="button" size="sm" variant="subtle" onClick={() => setRows((p) => [...p, emptyRow()])}>+ Ligne</Button></td>
              <td className="nx-num"><strong>{money(totals.debit, effectiveCurrency)}</strong></td>
              <td className="nx-num"><strong>{money(totals.credit, effectiveCurrency)}</strong></td>
              <td />
            </tr></tfoot>
          </table>
        </div>
        <div role="status" style={{ fontSize: 13, color: totals.balanced ? "var(--nx-pos, inherit)" : "var(--nx-warning, #b45309)" }}>
          {totals.balanced ? "Écriture équilibrée." : totals.debit || totals.credit
            ? `Déséquilibre : ${money(Math.abs(totals.gap), effectiveCurrency)} manquant au ${totals.gap > 0 ? "crédit" : "débit"}.`
            : "Saisissez au moins deux lignes."}
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
