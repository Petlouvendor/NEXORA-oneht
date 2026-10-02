import { useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateShort, money, todayIso } from "@/shared/format";
import { Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import {
  BLOCK_TYPES, CHANNELS, blockCustomer, createPromise, openDispute, resolveDispute, writeOffReceivable,
  type CreditReceivable, type Dispute,
} from "@/credit/creditApi";
import { promisable, selectionTotal, validatePromise } from "@/credit/promiseDraft";
import { ACTION_OUTCOMES, ACTION_TYPES, logCollectionAction } from "@/customers/customersApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;

/** Promesse de paiement : créances visées, montant (≤ reste dû), date, canal. */
export function PromiseDialog({ customerId, customerName, receivables, preselect, onClose, onDone }: {
  customerId: string; customerName: string; receivables: CreditReceivable[]; preselect?: string;
  onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const eligible = receivables.filter(promisable);
  const [selected, setSelected] = useState<string[]>(preselect ? [preselect] : eligible.map((r) => r.id));
  const sel = selectionTotal(eligible, selected);
  const [amount, setAmount] = useState(sel.total ? String(sel.total) : "");
  const [date, setDate] = useState("");
  const [channel, setChannel] = useState("phone");
  const [note, setNote] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "credit.promise_amount": "amount", "credit.promise_date": "date", "credit.promise_receivables": "receivables",
    "credit.promise_overlap": "receivables", "credit.promise_disputed": "receivables", "credit.promise_currency": "receivables",
  });
  const toggle = (id: string) => {
    const next = selected.includes(id) ? selected.filter((x) => x !== id) : [...selected, id];
    setSelected(next);
    setAmount(String(selectionTotal(eligible, next).total || ""));
  };

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    const problem = validatePromise({ selected, amount: a, date, today: todayIso(), total: sel.total, mixed: sel.mixed });
    if (problem) return fail(problem.field, problem.message);
    const ok = await submit(() => createPromise({ customerId, amount: a!, promisedDate: date, channel, receivableIds: selected, note: note.trim() || null }));
    if (ok) { toast.success(`Promesse de ${money(a!, sel.currency ?? undefined)} enregistrée pour le ${dateShort(date)}.`); onDone(); }
  }

  return (
    <Modal title={`Promesse de paiement — ${customerName}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy || eligible.length === 0} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {eligible.length === 0
          ? <div className="nx-error-text">Aucune créance ne peut recevoir de promesse (soldées, en litige ou déjà promises).</div>
          : (
            <Field label="Créances concernées" required error={fieldErrors.receivables}>
              <div role="group" aria-label="Créances concernées" style={{ display: "grid", gap: 4 }}>
                {eligible.map((r) => (
                  <label key={r.id} style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 13 }}>
                    <input type="checkbox" checked={selected.includes(r.id)} onChange={() => toggle(r.id)} />
                    {r.creditNumber} — reste {money(r.remainingAmount, r.currency)}
                    {r.dueDate && <span style={hint}>· échéance {dateShort(r.dueDate)}{r.daysOverdue > 0 ? ` (${r.daysOverdue} j de retard)` : ""}</span>}
                  </label>
                ))}
              </div>
            </Field>
          )}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 8 }}>
          <Field label="Montant promis" required error={fieldErrors.amount} hint={sel.total ? `Reste dû : ${money(sel.total, sel.currency ?? undefined)}` : undefined}>
            <TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </Field>
          <Field label="Date promise" required error={fieldErrors.date}>
            <TextInput type="date" min={todayIso()} value={date} onChange={(e) => setDate(e.target.value)} />
          </Field>
          <Field label="Canal">
            <Select value={channel} onChange={(e) => setChannel(e.target.value)}>
              {Object.entries(CHANNELS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
        </div>
        <Field label="Note"><TextInput value={note} maxLength={500} placeholder="Ex. paiera après la paie du 15" onChange={(e) => setNote(e.target.value)} /></Field>
        <div style={hint}>À l'échéance, la promesse est classée automatiquement (tenue, partiellement tenue ou non tenue) d'après les versements reçus.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Litige sur une créance : montant contesté (≤ reste dû) et motif. */
export function DisputeDialog({ customerId, receivable, onClose, onDone }: {
  customerId: string; receivable: CreditReceivable; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [amount, setAmount] = useState(String(receivable.remainingAmount));
  const [reason, setReason] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "credit.dispute_amount": "amount", "credit.dispute_reason": "reason" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const a = num(amount);
    if (a == null || a <= 0) return fail("amount", "Montant contesté positif requis.");
    if (a > receivable.remainingAmount) return fail("amount", `Le montant dépasse le reste dû (${money(receivable.remainingAmount, receivable.currency)}).`);
    if (reason.trim().length < 3) return fail("reason", "Motif obligatoire.");
    const ok = await submit(() => openDispute(customerId, receivable.id, a, reason.trim()));
    if (ok) { toast.success(`Litige ouvert sur ${receivable.creditNumber} : la créance est suspendue du recouvrement.`); onDone(); }
  }

  return (
    <Modal title={`Litige — ${receivable.creditNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Ouvrir le litige</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Montant contesté" required error={fieldErrors.amount} hint={`Reste dû : ${money(receivable.remainingAmount, receivable.currency)}`}>
          <TextInput type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} />
        </Field>
        <Field label="Motif" required error={fieldErrors.reason}>
          <TextInput value={reason} maxLength={500} placeholder="Ex. marchandise livrée incomplète" onChange={(e) => setReason(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Clôture d'un litige : issue + résolution écrite (gardée au dossier). */
export function ResolveDisputeDialog({ dispute, onClose, onDone }: { dispute: Dispute; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [outcome, setOutcome] = useState("resolved_company");
  const [resolution, setResolution] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "credit.dispute_resolution": "resolution" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (resolution.trim().length < 3) return fail("resolution", "Décrivez la résolution.");
    const ok = await submit(() => resolveDispute(dispute.id, outcome, resolution.trim()).then(() => true));
    if (ok) {
      toast.success(outcome === "resolved_customer"
        ? "Litige tranché pour le client : émettez l'avoir ou annulez le crédit depuis la fiche client."
        : "Litige clos : la créance reprend son recouvrement.");
      onDone();
    }
  }

  return (
    <Modal title={`Trancher le litige — ${dispute.customerName}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Clore le litige</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={hint}>{dispute.creditNumber ? `Créance ${dispute.creditNumber} · ` : ""}contesté {money(dispute.disputedAmount, dispute.currency)} — « {dispute.reason} »</div>
        <Field label="Issue" required>
          <Select value={outcome} onChange={(e) => setOutcome(e.target.value)}>
            <option value="resolved_company">En faveur de l'entreprise (la somme reste due)</option>
            <option value="resolved_customer">En faveur du client (avoir ou annulation à émettre)</option>
            <option value="cancelled">Litige retiré</option>
          </Select>
        </Field>
        <Field label="Résolution" required error={fieldErrors.resolution}>
          <TextInput value={resolution} maxLength={500} placeholder="Ex. bon de livraison signé par le client" onChange={(e) => setResolution(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Passage en perte : toujours sur le reste dû exact ; écriture Dr créances irrécouvrables / Cr clients. */
export function WriteOffDialog({ receivable, customerName, onClose, onDone }: {
  receivable: CreditReceivable; customerName: string; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [operationUuid] = useState(newOperationId);
  const [reason, setReason] = useState("");
  const [confirm, setConfirm] = useState(false);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "credit.writeoff_reason": "reason" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (reason.trim().length < 3) return fail("reason", "Motif obligatoire.");
    if (!confirm) return fail("confirm", "Cochez la confirmation.");
    const r = await submit(() => writeOffReceivable(receivable.id, receivable.remainingAmount, reason.trim(), operationUuid));
    if (r) { toast.success(`${receivable.creditNumber} passée en perte : écriture comptable passée.`); onDone(); }
  }

  return (
    <Modal title={`Passer en perte — ${receivable.creditNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button variant="danger" disabled={busy} onClick={() => save()}>Passer en perte</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <p style={{ margin: 0 }}>
          Le reste dû de <strong>{money(receivable.remainingAmount, receivable.currency)}</strong> par {customerName} est constaté comme perte
          (charge « créances irrécouvrables »). La créance est close ; son montant d'origine reste visible au dossier. Opération définitive.
        </p>
        <Field label="Motif" required error={fieldErrors.reason}>
          <TextInput value={reason} maxLength={500} placeholder="Ex. client injoignable depuis 6 mois, procédure sans suite" onChange={(e) => setReason(e.target.value)} />
        </Field>
        <Field label="Confirmation" error={fieldErrors.confirm}>
          <label style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 13 }}>
            <input type="checkbox" checked={confirm} onChange={(e) => setConfirm(e.target.checked)} />
            Je confirme que cette somme ne sera plus réclamée.
          </label>
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Blocage d'un client : type et motif obligatoire. */
export function BlockDialog({ customerId, customerName, activeTypes, onClose, onDone }: {
  customerId: string; customerName: string; activeTypes: string[]; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const available = Object.keys(BLOCK_TYPES).filter((t) => !activeTypes.includes(t));
  const [type, setType] = useState(available[0] ?? "credit");
  const [reason, setReason] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "credit.block_reason": "reason", "credit.already_blocked": "type" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (reason.trim().length < 3) return fail("reason", "Motif obligatoire.");
    const ok = await submit(() => blockCustomer(customerId, type, reason.trim()));
    if (ok) { toast.success(`${customerName} : blocage « ${BLOCK_TYPES[type]} » posé.`); onDone(); }
  }

  return (
    <Modal title={`Bloquer ${customerName}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button variant="danger" disabled={busy || available.length === 0} onClick={() => save()}>Bloquer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(420px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Type de blocage" required error={fieldErrors.type}
          hint="Crédit : plus de vente à crédit. Ventes / compte : plus aucune vente. Livraisons : plus d'expédition.">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            {available.map((t) => <option key={t} value={t}>{BLOCK_TYPES[t]}</option>)}
          </Select>
        </Field>
        <Field label="Motif" required error={fieldErrors.reason}>
          <TextInput value={reason} maxLength={500} placeholder="Ex. trois échéances impayées" onChange={(e) => setReason(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Action de recouvrement (appel, visite…) avec résultat et prochaine relance — alimente l'agenda. */
export function ActionDialog({ customerId, customerName, receivables, onClose, onDone }: {
  customerId: string; customerName: string; receivables: CreditReceivable[]; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [type, setType] = useState("call");
  const [outcome, setOutcome] = useState("");
  const [credit, setCredit] = useState("");
  const [next, setNext] = useState("");
  const [note, setNote] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "collection.note": "note", "collection.next_date": "next" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!note.trim()) return fail("note", "Note obligatoire.");
    if (next && next < todayIso()) return fail("next", "La prochaine relance ne peut pas être dans le passé.");
    const ok = await submit(() => logCollectionAction(customerId, {
      actionType: type, note: note.trim(), outcome: outcome || null, receivableId: credit || null, nextActionDate: next || null,
    }));
    if (ok) { toast.success(next ? `Action consignée ; relance prévue le ${dateShort(next)}.` : "Action consignée."); onDone(); }
  }

  return (
    <Modal title={`Action de recouvrement — ${customerName}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Consigner</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(480px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 8 }}>
          <Field label="Action" required>
            <Select value={type} onChange={(e) => setType(e.target.value)}>
              {Object.entries(ACTION_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
          <Field label="Résultat">
            <Select value={outcome} onChange={(e) => setOutcome(e.target.value)}>
              <option value="">—</option>
              {Object.entries(ACTION_OUTCOMES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
          <Field label="Crédit concerné">
            <Select value={credit} onChange={(e) => setCredit(e.target.value)}>
              <option value="">Tout le compte</option>
              {receivables.map((r) => <option key={r.id} value={r.id}>{r.creditNumber}</option>)}
            </Select>
          </Field>
          <Field label="Prochaine relance" error={fieldErrors.next}>
            <TextInput type="date" min={todayIso()} value={next} onChange={(e) => setNext(e.target.value)} />
          </Field>
        </div>
        <Field label="Note" required error={fieldErrors.note}>
          <TextInput value={note} maxLength={500} placeholder="Ex. joint au téléphone, attend sa paie" onChange={(e) => setNote(e.target.value)} />
        </Field>
        {outcome === "promise" && <div style={hint}>Pensez à enregistrer la promesse elle-même (montant, date) pour qu'elle soit suivie à l'échéance.</div>}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
