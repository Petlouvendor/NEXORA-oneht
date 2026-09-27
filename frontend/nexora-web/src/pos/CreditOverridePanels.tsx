import { useCallback, useEffect, useState } from "react";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, EmptyState, Field, Modal, TextInput } from "@/shared/ui";
import { dateTime, money } from "@/shared/format";
import {
  approveCreditOverride, getCreditOverride, listCreditOverrides, rejectCreditOverride, requestCreditOverride,
  type CreditOverride,
} from "@/customers/customersApi";
import { errorText } from "@/customers/CustomerDialogs";

/**
 * Demande de dépassement de crédit au POS (module clients §12) : le caissier motive, la demande suit le
 * workflow d'approbation configuré (niveaux, approbateurs distincts), puis la vente de CE panier passe
 * une seule fois. Le caissier peut attendre la décision à distance ou faire valider sur place.
 */
export function CreditOverrideRequestDialog({ cartId, customerId, customerName, amount, currency, limitMessage, onClose, onRequested }: {
  cartId: string; customerId: string; customerName: string; amount: number; currency: string; limitMessage: string;
  onClose: () => void; onRequested: (r: CreditOverride, validateNow: boolean) => void;
}) {
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function send(validateNow: boolean) {
    setBusy(true);
    setError(null);
    try {
      const r = await requestCreditOverride({ cartOperationUuid: cartId, customerId, amount, currency, reason: reason.trim() });
      onRequested(r, validateNow);
    } catch (e) {
      setError(errorText(e, "Demande impossible."));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title="Dépassement de crédit" onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button variant="ghost" loading={busy} disabled={busy || reason.trim().length < 5} onClick={() => void send(false)}>Envoyer au responsable</Button>
             <Button loading={busy} disabled={busy || reason.trim().length < 5} onClick={() => void send(true)}>Responsable présent</Button>
           </>}>
      <div style={{ display: "grid", gap: 10 }}>
        <p style={{ margin: 0 }}>
          Crédit de <b>{money(amount, currency)}</b> pour <b>{customerName}</b> refusé : {limitMessage}
        </p>
        <p style={{ margin: 0, color: "var(--nx-text-2)" }}>
          Un responsable doit approuver ce dépassement selon le circuit d'approbation de l'entreprise. L'approbation vaut pour ce
          panier uniquement.
        </p>
        <Field label="Motif" required>
          <TextInput autoFocus value={reason} onChange={(e) => setReason(e.target.value)} maxLength={300} placeholder="Ex. client fidèle, commande urgente" />
        </Field>
        {error && <div className="nx-error-text" role="alert">{error}</div>}
      </div>
    </Modal>
  );
}

/** Suivi d'une demande de dépassement du panier en cours (rafraîchi toutes les 5 s tant qu'elle est en attente). */
export function useCreditOverride(initial: CreditOverride | null) {
  const [request, setRequest] = useState<CreditOverride | null>(initial);
  useEffect(() => setRequest(initial), [initial]);
  useEffect(() => {
    if (!request || request.status !== "pending") return;
    const t = setInterval(() => { getCreditOverride(request.id).then(setRequest).catch(() => {}); }, 5000);
    return () => clearInterval(t);
  }, [request]);
  const refresh = useCallback(() => { if (request) getCreditOverride(request.id).then(setRequest).catch(() => {}); }, [request]);
  return { request, setRequest, refresh };
}

/** Validations des dépassements de crédit en attente (détenteurs de pos.credit_override). */
export function CreditOverridesPanel({ focusId }: { focusId?: string | null }) {
  const toast = useToast();
  const [rows, setRows] = useState<CreditOverride[] | null>(null);
  const [rejecting, setRejecting] = useState<CreditOverride | null>(null);
  const [note, setNote] = useState("");

  const load = useCallback(() => { listCreditOverrides("pending").then(setRows).catch(() => setRows([])); }, []);
  useEffect(() => {
    load();
    const t = setInterval(load, 15000);
    return () => clearInterval(t);
  }, [load]);

  const act = (p: Promise<CreditOverride>, ok: string) =>
    p.then((r) => { toast.success(r.status === "pending" ? `${ok} — niveau suivant requis.` : ok); load(); })
      .catch((e) => toast.error(errorText(e)));

  if (!rows) return null;
  return (
    <Card>
      <h3 style={{ margin: "0 0 8px" }}>Dépassements de crédit <Badge tone={rows.length ? "warning" : undefined}>{rows.length}</Badge></h3>
      {rows.length === 0 ? <EmptyState title="Aucun dépassement en attente" /> : (
        <div style={{ display: "grid", gap: 10 }}>
          {rows.map((r) => (
            <div key={r.id} style={{ display: "grid", gap: 4, padding: 8, borderRadius: 8,
                                     outline: r.id === focusId ? "2px solid var(--nx-brand)" : undefined }}>
              <div><b>{r.customerName}</b> — {money(r.amount, r.currency)} à crédit · niveau {r.currentLevel}/{r.requiredLevels}</div>
              <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                {r.limitMessage} — motif : {r.reason} · demandé par {r.requestedByName ?? "—"}, {dateTime(r.requestedAt)}
              </div>
              <div style={{ display: "flex", gap: 6 }}>
                <Button size="sm" onClick={() => void act(approveCreditOverride(r.id), "Dépassement approuvé")}>Approuver</Button>
                <Button size="sm" variant="ghost" onClick={() => { setRejecting(r); setNote(""); }}>Refuser</Button>
              </div>
            </div>
          ))}
        </div>
      )}
      {rejecting && (
        <Modal title="Refuser le dépassement" onClose={() => setRejecting(null)}
               footer={<>
                 <Button variant="ghost" onClick={() => setRejecting(null)}>Annuler</Button>
                 <Button variant="danger" disabled={!note.trim()} onClick={() => { void act(rejectCreditOverride(rejecting.id, note.trim()), "Dépassement refusé"); setRejecting(null); }}>Refuser</Button>
               </>}>
          <Field label="Motif du refus" required><TextInput autoFocus value={note} onChange={(e) => setNote(e.target.value)} /></Field>
        </Modal>
      )}
    </Card>
  );
}
