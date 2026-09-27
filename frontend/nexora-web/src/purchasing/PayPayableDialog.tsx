import { useEffect, useMemo, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import { dateShort, money } from "@/shared/format";
import { listPaymentAccounts, payPayable, type Payable, type PaymentAccount } from "@/purchasing/purchasingApi";

const ACCOUNT_TYPE: Record<string, string> = { cash: "Caisse", bank: "Banque", mobile_money: "Mobile money", card: "Carte" };

/**
 * Paiement d'une dette fournisseur : montant (au plus le solde), compte de paiement choisi parmi les
 * comptes actifs dans la devise de la facture, référence (chèque, virement). L'identifiant d'opération est fixé à
 * l'ouverture : un double clic ne paie jamais deux fois. Le serveur refuse un montant supérieur au solde ou
 * une dette bloquée par le rapprochement.
 */
export function PayPayableDialog({ payable, onClose, onPaid }: {
  payable: Payable; onClose: () => void; onPaid: (paymentId: string | null, amount: number) => void;
}) {
  const [operationUuid] = useState(() => crypto.randomUUID());
  const [accounts, setAccounts] = useState<PaymentAccount[] | null>(null);
  const [accountId, setAccountId] = useState("");
  const [amount, setAmount] = useState(payable.remainingAmount);
  const [reference, setReference] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    listPaymentAccounts()
      .then((list) => {
        setAccounts(list);
        const same = list.filter((a) => a.currency === payable.currency);
        if (same.length === 1) setAccountId(same[0].id);
      })
      .catch((e) => { setAccounts([]); setError(e instanceof ApiError ? e.message : "Comptes indisponibles."); });
  }, [payable.currency]);

  // Seuls les comptes dans la devise de la facture : jamais deux devises mélangées dans un paiement.
  const usable = useMemo(() => (accounts ?? []).filter((a) => a.currency === payable.currency)
    .sort((a, b) => a.name.localeCompare(b.name)), [accounts, payable.currency]);
  const tooMuch = amount > payable.remainingAmount + 0.001;

  async function save() {
    setBusy(true);
    setError(null);
    try {
      const r = await payPayable(payable.id, amount, accountId, reference.trim() || undefined, operationUuid);
      onPaid(r.paymentId, amount);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Paiement refusé.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title={`Payer — ${payable.supplierName}`} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button loading={busy} disabled={busy || !accountId || amount <= 0 || tooMuch} onClick={() => void save()}>
               Payer {amount > 0 ? money(amount, payable.currency) : ""}
             </Button>
           </>}>
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ color: "var(--nx-text-2)" }}>
          Facture <b>{payable.invoiceNumber ?? "—"}</b>{payable.supplierReference ? ` (réf. ${payable.supplierReference})` : ""} ·
          solde <b className="nx-numeric">{money(payable.remainingAmount, payable.currency)}</b>
          {payable.dueDate ? ` · échéance ${dateShort(payable.dueDate)}` : ""}
        </div>
        <Field label="Montant" required error={tooMuch ? "Le montant dépasse le solde de la facture." : null}>
          <TextInput type="number" min={0} max={payable.remainingAmount} step="0.01" className="nx-input nx-numeric"
                     value={amount || ""} onChange={(e) => setAmount(Math.max(0, Number(e.target.value) || 0))} />
        </Field>
        <Field label="Payer depuis" required hint="Caisses et comptes bancaires actifs de l'entreprise.">
          <Select value={accountId} onChange={(e) => setAccountId(e.target.value)} disabled={!accounts}>
            <option value="">{accounts ? "Choisir un compte…" : "Chargement…"}</option>
            {usable.map((a) => (
              <option key={a.id} value={a.id}>{a.name} — {ACCOUNT_TYPE[a.accountType] ?? a.accountType} ({a.currency})</option>
            ))}
          </Select>
        </Field>
        {accounts && usable.length === 0 && (
          <div className="nx-error-text" role="status">Aucun compte actif en {payable.currency} : créez-en un dans Finance avant de payer.</div>
        )}
        <Field label="Référence" hint="N° de chèque, de virement… (imprimé sur le reçu)">
          <TextInput value={reference} maxLength={100} onChange={(e) => setReference(e.target.value)} placeholder="Ex. CHQ-001245" />
        </Field>
        {error && <div className="nx-error-text" role="alert">{error}</div>}
      </div>
    </Modal>
  );
}
