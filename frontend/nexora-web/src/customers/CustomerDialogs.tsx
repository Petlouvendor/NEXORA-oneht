import { useEffect, useMemo, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useToast } from "@/shared/Toast";
import { Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import { money, todayIso } from "@/shared/format";
import { printDocument } from "@/shared/printApi";
import {
  createExpense, EXPENSE_CATEGORIES, getPaymentOptions, openCredit, recordPaymentOrQueue, requestRefund, scheduleCredit,
  type CreditDetail, type CreditMemo, type CustomerCredit, type CustomerPayment, type PaymentOptions,
} from "@/customers/customersApi";

export const errorText = (e: unknown, fallback = "Opération refusée") =>
  e instanceof ApiError ? e.problem.detail ?? e.problem.title : fallback;

/**
 * Enregistrement d'un versement (§9). Le montant et le compte sont saisis ; la répartition sur les
 * crédits, le reste dû et le refus d'un excédent sont décidés par le serveur. L'identifiant
 * d'opération est fixé à l'ouverture : un double clic ou un retry réseau ne crée jamais deux versements.
 * Au POS, `cashAccountId` / `cashSessionId` imposent la caisse de la session ouverte.
 */
export function PaymentDialog({ customerId, customerName, currency, credits, preselected, cashAccountId, cashSessionId, onClose, onDone }: {
  customerId: string; customerName: string; currency: string; credits: CustomerCredit[]; preselected?: CustomerCredit | null;
  cashAccountId?: string | null; cashSessionId?: string | null;
  onClose: () => void; onDone: (payment: CustomerPayment | null) => void;
}) {
  const toast = useToast();
  const [operationUuid] = useState(() => crypto.randomUUID());
  const [options, setOptions] = useState<PaymentOptions | null>(null);
  const open = useMemo(() => credits.filter((c) => c.remainingAmount > 0 && !["cancelled", "written_off", "paid"].includes(c.displayStatus)), [credits]);
  const due = open.reduce((s, c) => s + c.remainingAmount, 0);
  const [amount, setAmount] = useState<number>(preselected?.remainingAmount ?? 0);
  const [target, setTarget] = useState<string>(preselected?.id ?? "");
  const [accountId, setAccountId] = useState<string>(cashAccountId ?? "");
  const [methodId, setMethodId] = useState<string>("");
  const [reference, setReference] = useState("");
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Versement confirmé par le serveur : on propose d'imprimer le reçu avant de fermer.
  const [done, setDone] = useState<CustomerPayment | null>(null);
  const [printing, setPrinting] = useState<"pdf" | "ticket" | null>(null);

  useEffect(() => {
    getPaymentOptions()
      .then((o) => {
        setOptions(o);
        if (!cashAccountId) setAccountId((cur) => cur || (o.accounts.find((a) => a.currency === currency)?.id ?? ""));
        setMethodId((cur) => cur || (o.methods[0]?.id ?? ""));
      })
      .catch((e) => setError(errorText(e, "Comptes d'encaissement indisponibles")));
  }, [cashAccountId, currency]);

  const targetCredit = open.find((c) => c.id === target) ?? null;
  const ceiling = targetCredit ? targetCredit.remainingAmount : due;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const outcome = await recordPaymentOrQueue(customerId, {
        amount, financialAccountId: accountId, paymentMethodId: methodId || null, cashSessionId: cashSessionId ?? null,
        reference: reference.trim() || null, note: note.trim() || null,
        allocations: targetCredit ? [{ receivableId: targetCredit.id, amount }] : undefined,
      }, operationUuid);
      if (outcome.queued) {
        // §24 : jamais déclaré confirmé tant que le serveur ne l'a pas enregistré.
        toast.push(`Hors ligne — versement de ${money(amount, currency)} mis en file, non encore confirmé. Il sera synchronisé à la reconnexion.`, "warning");
        onDone(null);
        return;
      }
      toast.success(`Versement ${outcome.payment.paymentNumber} enregistré (${money(outcome.payment.amount, outcome.payment.currency)}).`);
      setDone(outcome.payment);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  async function print(format: "pdf" | "ticket") {
    if (!done) return;
    setPrinting(format);
    try {
      await printDocument("customer-payment", done.id, format);
    } catch (e) {
      toast.error(errorText(e, "Impression impossible."));
    } finally {
      setPrinting(null);
    }
  }

  if (done) {
    // Reçu : montant versé, crédits réglés, total versé et reste à verser du client (situation du moment).
    const atPos = Boolean(cashSessionId);
    return (
      <Modal title={`Versement ${done.paymentNumber} enregistré`} onClose={() => onDone(done)}
             footer={<>
               <Button variant="ghost" onClick={() => onDone(done)}>Fermer</Button>
               <Button variant={atPos ? "ghost" : "primary"} loading={printing === "pdf"} onClick={() => void print("pdf")}>Imprimer le reçu (A4)</Button>
               <Button variant={atPos ? "primary" : "ghost"} loading={printing === "ticket"} onClick={() => void print("ticket")}>Imprimer le ticket</Button>
             </>}>
        <div style={{ display: "grid", gap: 8 }}>
          <div>Montant versé : <b className="nx-numeric">{money(done.amount, done.currency)}</b></div>
          {done.allocations.length > 0 && (
            <div style={{ color: "var(--nx-text-2)" }}>
              Crédits réglés : {done.allocations.map((a) => `${a.creditNumber} (${money(a.amount, done.currency)})`).join(", ")}
            </div>
          )}
          {done.creditMemoNumber && <div>Excédent conservé en avoir {done.creditMemoNumber}.</div>}
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
            Le reçu indique le total versé et le reste à verser du client à ce jour. Chaque réimpression porte la mention « DUPLICATA ».
          </div>
        </div>
      </Modal>
    );
  }

  return (
    <Modal title={`Versement — ${customerName}`} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button loading={busy} disabled={busy || amount <= 0 || !accountId} onClick={() => void save()}>
               Enregistrer {amount > 0 ? money(amount, currency) : ""}
             </Button>
           </>}>
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ color: "var(--nx-text-2)" }}>Reste dû sur le compte : <b className="nx-numeric">{money(due, currency)}</b></div>
        <Field label="Crédit à régler" hint="« Répartition automatique » solde d'abord les crédits les plus anciens.">
          <Select value={target} onChange={(e) => {
            setTarget(e.target.value);
            const c = open.find((x) => x.id === e.target.value);
            if (c) setAmount(c.remainingAmount);
          }}>
            <option value="">Répartition automatique</option>
            {open.map((c) => (
              <option key={c.id} value={c.id}>{c.creditNumber} — reste {money(c.remainingAmount, c.currency)}{c.daysOverdue > 0 ? ` (retard ${c.daysOverdue} j)` : ""}</option>
            ))}
          </Select>
        </Field>
        <Field label="Montant" required hint={amount > ceiling ? "Au-delà du reste dû : refusé, sauf si la règle « avoir » est configurée." : undefined}>
          <TextInput type="number" min={0} step="0.01" className="nx-input nx-numeric" value={amount || ""}
                     onChange={(e) => setAmount(Math.max(0, Number(e.target.value) || 0))} autoFocus />
        </Field>
        {!cashAccountId && (
          <Field label="Compte d'encaissement" required>
            <Select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
              <option value="" disabled>—</option>
              {options?.accounts.filter((a) => a.currency === currency).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </Select>
          </Field>
        )}
        <Field label="Moyen de paiement">
          <Select value={methodId} onChange={(e) => setMethodId(e.target.value)}>
            <option value="">Non précisé</option>
            {options?.methods.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
          </Select>
        </Field>
        <Field label="Référence" hint="N° de chèque, de transaction Mobile Money, de virement…">
          <TextInput value={reference} onChange={(e) => setReference(e.target.value)} maxLength={120} />
        </Field>
        <Field label="Note"><TextInput value={note} onChange={(e) => setNote(e.target.value)} maxLength={300} /></Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
      </div>
    </Modal>
  );
}

/** Nouvelle dépense imputée (§10) : transmise pour validation par une autre personne. */
export function ExpenseDialog({ customerId, customerName, currency, onClose, onDone }: {
  customerId: string; customerName: string; currency: string; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [category, setCategory] = useState("delivery");
  const [description, setDescription] = useState("");
  const [amount, setAmount] = useState(0);
  const [date, setDate] = useState(() => todayIso());
  const [billable, setBillable] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save(submit: boolean) {
    setBusy(true);
    setError(null);
    try {
      const e = await createExpense(customerId, { category, description: description.trim(), amount, expenseDate: date, billable, submit });
      toast.success(`Dépense ${e.expenseNumber} ${submit ? "transmise pour validation" : "enregistrée en brouillon"}.`);
      onDone();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const valid = description.trim().length > 0 && amount > 0;
  return (
    <Modal title={`Dépense imputée — ${customerName}`} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button variant="ghost" loading={busy} disabled={busy || !valid} onClick={() => void save(false)}>Brouillon</Button>
             <Button loading={busy} disabled={busy || !valid} onClick={() => void save(true)}>Transmettre pour validation</Button>
           </>}>
      <div style={{ display: "grid", gap: 12 }}>
        <Field label="Catégorie" required>
          <Select value={category} onChange={(e) => setCategory(e.target.value)}>
            {Object.entries(EXPENSE_CATEGORIES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Motif / description" required>
          <TextInput value={description} onChange={(e) => setDescription(e.target.value)} maxLength={300} placeholder="Ex. livraison spéciale à Jacmel" />
        </Field>
        <Field label={`Montant (${currency})`} required>
          <TextInput type="number" min={0} step="0.01" className="nx-input nx-numeric" value={amount || ""}
                     onChange={(e) => setAmount(Math.max(0, Number(e.target.value) || 0))} />
        </Field>
        <Field label="Date"><TextInput type="date" value={date} onChange={(e) => setDate(e.target.value)} /></Field>
        <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <input type="checkbox" checked={billable} onChange={(e) => setBillable(e.target.checked)} />
          Refacturer au client (la validation augmente sa dette)
        </label>
        {!billable && (
          <p role="note" style={{ margin: 0, fontSize: "var(--nx-fs-xs)", color: "var(--nx-text-3)" }}>
            Non refacturée : la validation ne crée ni dette ni écriture. Si de l'argent est sorti, enregistrez-le aussi
            dans Finance &amp; trésorerie → Dépenses.
          </p>
        )}
        {error &&<div role="alert" className="nx-error-text">{error}</div>}
      </div>
    </Modal>
  );
}

const REFUND_METHODS: Record<string, string> = {
  cash: "Espèces", mobile_money: "Mobile Money", bank_transfer: "Virement", check: "Chèque", card: "Carte", other: "Autre",
};

/** Demande de remboursement d'un avoir (§11) — approuvée par une autre personne avant décaissement. */
export function RefundDialog({ memo, onClose, onDone }: { memo: CreditMemo; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [amount, setAmount] = useState(memo.remainingAmount);
  const [method, setMethod] = useState("cash");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const r = await requestRefund(memo.id, amount, method, reason.trim());
      toast.push(`Demande ${r.refundNumber} envoyée pour approbation.`, "info");
      onDone();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title={`Rembourser l'avoir ${memo.memoNumber}`} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button loading={busy} disabled={busy || amount <= 0 || !reason.trim()} onClick={() => void save()}>Demander</Button>
           </>}>
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ color: "var(--nx-text-2)" }}>Disponible : <b className="nx-numeric">{money(memo.remainingAmount, memo.currency)}</b></div>
        <Field label="Montant" required>
          <TextInput type="number" min={0} max={memo.remainingAmount} step="0.01" className="nx-input nx-numeric" value={amount || ""}
                     onChange={(e) => setAmount(Math.max(0, Number(e.target.value) || 0))} />
        </Field>
        <Field label="Mode de remboursement">
          <Select value={method} onChange={(e) => setMethod(e.target.value)}>
            {Object.entries(REFUND_METHODS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Motif" required><TextInput value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} /></Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
      </div>
    </Modal>
  );
}

/**
 * Crédit hors vente — reprise de solde (§22) : dette antérieure du client, motif obligatoire,
 * contrepartie en compte d'attente jusqu'au rapprochement. L'identifiant d'opération est fixé à
 * l'ouverture : un double envoi ne crée jamais deux crédits.
 */
export function OpenCreditDialog({ customerId, customerName, currency, onClose, onDone }: {
  customerId: string; customerName: string; currency: string; onClose: () => void; onDone: (d: CreditDetail) => void;
}) {
  const toast = useToast();
  const [operationUuid] = useState(() => crypto.randomUUID());
  const [amount, setAmount] = useState(0);
  const [reason, setReason] = useState("");
  const [due, setDue] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const d = await openCredit(customerId, amount, reason.trim(), due || null, operationUuid);
      toast.success(`Crédit ${d.credit.creditNumber} créé (${money(d.credit.originalAmount, d.credit.currency)}).`);
      onDone(d);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title={`Créer un crédit — ${customerName}`} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button loading={busy} disabled={busy || amount <= 0 || reason.trim().length < 5} onClick={() => void save()}>Créer le crédit</Button>
           </>}>
      <div style={{ display: "grid", gap: 12 }}>
        <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
          Dette antérieure du client (reprise de solde d'un ancien système, dette hors caisse). Une vente à crédit se fait
          au POS ; ici, la contrepartie comptable reste en compte d'attente jusqu'au rapprochement.
        </p>
        <Field label={`Montant (${currency})`} required>
          <TextInput type="number" min={0} step="0.01" className="nx-input nx-numeric" value={amount || ""}
                     onChange={(e) => setAmount(Math.max(0, Number(e.target.value) || 0))} autoFocus />
        </Field>
        <Field label="Origine de la dette / motif" required>
          <TextInput value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} placeholder="Ex. solde repris de l'ancien logiciel au 31/08" />
        </Field>
        <Field label="Échéance" hint="Vide = durée de crédit de la fiche, sinon réglage de l'entreprise.">
          <TextInput type="date" value={due} onChange={(e) => setDue(e.target.value)} />
        </Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
      </div>
    </Modal>
  );
}

/** Échéancier du reste dû (§8) : N échéances égales ou lignes saisies ; remplace l'échéancier actif. */
export function ScheduleForm({ credit, onDone, onCancel }: { credit: CustomerCredit; onDone: (d: CreditDetail) => void; onCancel: () => void }) {
  const toast = useToast();
  const [mode, setMode] = useState<"equal" | "custom">("equal");
  const [count, setCount] = useState(3);
  const [first, setFirst] = useState(() => todayIso(new Date(Date.now() + 30 * 86400000)));
  const [monthly, setMonthly] = useState(true);
  const [interval, setIntervalDays] = useState(15);
  const [lines, setLines] = useState<Array<{ dueDate: string; amount: number }>>([{ dueDate: first, amount: credit.remainingAmount }]);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const total = lines.reduce((s, l) => s + (Number(l.amount) || 0), 0);

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const d = await scheduleCredit(credit.id, mode === "equal"
        ? { reason: reason.trim(), count, firstDueDate: first, monthly, intervalDays: interval }
        : { reason: reason.trim(), installments: lines });
      toast.success(`Échéancier posé sur ${credit.creditNumber}.`);
      onDone(d);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ display: "grid", gap: 10, borderTop: "1px solid var(--nx-border)", paddingTop: 12 }}>
      <b>Échelonner le reste dû : {money(credit.remainingAmount, credit.currency)}</b>
      <div style={{ display: "flex", gap: 12 }}>
        <label><input type="radio" checked={mode === "equal"} onChange={() => setMode("equal")} /> Échéances égales</label>
        <label><input type="radio" checked={mode === "custom"} onChange={() => setMode("custom")} /> Saisie libre</label>
      </div>
      {mode === "equal" ? (
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 10 }}>
          <Field label="Nombre"><TextInput type="number" min={2} max={120} value={count} onChange={(e) => setCount(Number(e.target.value) || 2)} /></Field>
          <Field label="Première échéance"><TextInput type="date" value={first} onChange={(e) => setFirst(e.target.value)} /></Field>
          <Field label="Fréquence">
            <Select value={monthly ? "m" : "d"} onChange={(e) => setMonthly(e.target.value === "m")}>
              <option value="m">Mensuelle</option>
              <option value="d">Tous les N jours</option>
            </Select>
          </Field>
          {!monthly && <Field label="Intervalle (jours)"><TextInput type="number" min={1} max={365} value={interval} onChange={(e) => setIntervalDays(Number(e.target.value) || 1)} /></Field>}
        </div>
      ) : (
        <div style={{ display: "grid", gap: 6 }}>
          {lines.map((l, i) => (
            <div key={i} style={{ display: "flex", gap: 6 }}>
              <TextInput type="date" value={l.dueDate} aria-label={`Échéance ${i + 1}`}
                         onChange={(e) => setLines((ls) => ls.map((x, j) => (j === i ? { ...x, dueDate: e.target.value } : x)))} />
              <TextInput type="number" min={0} step="0.01" value={l.amount || ""} aria-label={`Montant ${i + 1}`}
                         onChange={(e) => setLines((ls) => ls.map((x, j) => (j === i ? { ...x, amount: Number(e.target.value) || 0 } : x)))} />
              {lines.length > 1 && <Button size="sm" variant="ghost" onClick={() => setLines((ls) => ls.filter((_, j) => j !== i))}>Retirer</Button>}
            </div>
          ))}
          <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
            <Button size="sm" variant="ghost" onClick={() => setLines((ls) => [...ls, { dueDate: first, amount: 0 }])}>Ajouter une échéance</Button>
            <span style={{ color: Math.abs(total - credit.remainingAmount) > 0.001 ? "var(--nx-warning)" : undefined }}>
              Total {money(total, credit.currency)} / {money(credit.remainingAmount, credit.currency)}
            </span>
          </div>
        </div>
      )}
      <Field label="Motif" required><TextInput value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} /></Field>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <div style={{ display: "flex", gap: 8 }}>
        <Button loading={busy} disabled={busy || reason.trim().length < 5} onClick={() => void save()}>Poser l'échéancier</Button>
        <Button variant="ghost" onClick={onCancel}>Annuler</Button>
      </div>
    </div>
  );
}
