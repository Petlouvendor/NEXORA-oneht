import { useCallback, useEffect, useState, type ReactNode } from "react";
import { ApiError } from "@/core/apiClient";
import { Button, Card, EmptyState, Modal, Spinner, TextInput } from "@/shared/ui";
import { dateTime, money, percent } from "@/shared/format";
import { useToast } from "@/shared/Toast";
import {
  approveDiscount, approveReturn, closeSession, listDiscountRequests, listOpenSessions, listPendingClosings,
  listReturnRequests, rejectDiscount, rejectReturn, validateClosing,
  type DiscountRequest, type OpenCashSession, type PendingClosing, type PosLimits, type ReturnRequest,
} from "@/pos/posApi";

type Decision =
  | { kind: "return"; item: ReturnRequest; approve: boolean }
  | { kind: "discount"; item: DiscountRequest; approve: boolean }
  | { kind: "closing"; item: PendingClosing; approve: true };

/**
 * Validations Manager (guide §10, §12, §13, §20). Chaque liste n'apparaît que si l'utilisateur détient
 * la permission correspondante ; le serveur refuse de toute façon l'auto-validation et une remise
 * au-delà de la limite d'approbation. Un refus exige un motif, qui reste visible du demandeur.
 */
export function ApprovalsPanel({ limits, focusId }: { limits: PosLimits; focusId?: string | null }) {
  const toast = useToast();
  const [returns, setReturns] = useState<ReturnRequest[]>([]);
  const [discounts, setDiscounts] = useState<DiscountRequest[]>([]);
  const [closings, setClosings] = useState<PendingClosing[]>([]);
  const [openSessions, setOpenSessions] = useState<OpenCashSession[]>([]);
  const [closingOther, setClosingOther] = useState<OpenCashSession | null>(null);
  const [counted, setCounted] = useState("");
  const [decision, setDecision] = useState<Decision | null>(null);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  const refresh = useCallback(() => {
    const calls: Promise<unknown>[] = [];
    if (limits.canRefund) calls.push(listReturnRequests("pending_approval").then(setReturns));
    if (limits.canApproveDiscount) calls.push(listDiscountRequests("pending_approval").then(setDiscounts));
    if (limits.canManageSessions) {
      calls.push(listPendingClosings().then(setClosings));
      calls.push(listOpenSessions().then(setOpenSessions));
    }
    // « Aucune validation » ne s'affiche qu'une fois les listes chargées (pas pendant le chargement).
    void Promise.allSettled(calls).then(() => setLoaded(true));
  }, [limits]);
  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 15000);
    return () => clearInterval(t);
  }, [refresh]);

  async function decide() {
    if (!decision) return;
    setBusy(true);
    setError(null);
    try {
      const n = note.trim();
      if (decision.kind === "return")
        await (decision.approve ? approveReturn(decision.item.id, n || null) : rejectReturn(decision.item.id, n));
      else if (decision.kind === "discount")
        await (decision.approve ? approveDiscount(decision.item.id, n || null) : rejectDiscount(decision.item.id, n));
      else await validateClosing(decision.item.sessionId, n);
      toast.success(decision.approve ? "Validation enregistrée." : "Refus enregistré.");
      setDecision(null);
      setNote("");
      refresh();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Décision impossible.");
    } finally {
      setBusy(false);
    }
  }

  /** Clôture, par un responsable, d'une session laissée ouverte par un autre caissier (§13). */
  async function closeOther() {
    if (!closingOther) return;
    setBusy(true);
    setError(null);
    try {
      const r = await closeSession(closingOther.sessionId, Number(counted), note.trim() || null);
      toast.success(r.status === "closed"
        ? `Caisse ${closingOther.registerName} clôturée (écart ${money(r.difference ?? 0, closingOther.currency, { sign: true })}).`
        : `Écart au-delà du seuil : la clôture de ${closingOther.registerName} attend une validation.`);
      setClosingOther(null);
      setCounted("");
      setNote("");
      refresh();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Clôture impossible.");
    } finally {
      setBusy(false);
    }
  }

  const noteRequired = decision ? (!decision.approve || decision.kind === "closing") : false;
  const nothing = returns.length + discounts.length + closings.length + openSessions.length === 0;

  return (
    <div style={{ display: "grid", gap: 12 }}>
      {!loaded && <Card><Spinner /></Card>}
      {loaded && nothing && <Card><EmptyState title="Aucune validation en attente" hint="Les demandes des caissiers apparaîtront ici." /></Card>}

      {limits.canRefund && returns.length > 0 && (
        <Section title={`Retours à valider (${returns.length})`}>
          {returns.map((r) => (
            <Item key={r.id} focused={focusId === r.id}
                  title={`${r.requestNumber} · vente ${r.saleNumber} · ${money(r.estimatedRefund, r.currency)}`}
                  detail={`${r.requestedByName ?? "?"} · ${dateTime(r.requestedAt)} · motif : ${r.reason} · ${r.lines.map((l) => `${l.quantity} × ${l.description}`).join(", ")}`}
                  onApprove={() => setDecision({ kind: "return", item: r, approve: true })}
                  onReject={() => setDecision({ kind: "return", item: r, approve: false })} />
          ))}
        </Section>
      )}

      {limits.canApproveDiscount && discounts.length > 0 && (
        <Section title={`Remises exceptionnelles (${discounts.length}) — votre limite : ${limits.discountApprovalLimitPercent ?? "∞"} %`}>
          {discounts.map((d) => (
            <Item key={d.id} focused={focusId === d.id}
                  title={`${d.requestNumber} · ${percent(d.requestedPercent, 2)} sur ${d.productName} × ${d.quantity}`}
                  detail={`${money(d.requestedAmount, d.currency)} sur ${money(d.lineGross, d.currency)} · ${d.requestedByName ?? "?"} · motif : ${d.reason}`}
                  onApprove={() => setDecision({ kind: "discount", item: d, approve: true })}
                  onReject={() => setDecision({ kind: "discount", item: d, approve: false })} />
          ))}
        </Section>
      )}

      {limits.canManageSessions && closings.length > 0 && (
        <Section title={`Écarts de caisse à valider (${closings.length})`}>
          {closings.map((c) => (
            <Item key={c.sessionId} focused={focusId === c.sessionId}
                  title={`${c.registerName} · écart ${money(c.difference, c.currency, { sign: true })}`}
                  detail={`${c.cashierName ?? "?"} · attendu ${money(c.expectedBalance, c.currency)}, compté ${money(c.countedBalance, c.currency)}${c.notes ? ` · ${c.notes}` : ""}`}
                  onApprove={() => setDecision({ kind: "closing", item: c, approve: true })} />
          ))}
        </Section>
      )}

      {limits.canManageSessions && openSessions.length > 0 && (
        <Section title={`Sessions ouvertes par d'autres caissiers (${openSessions.length})`}>
          {openSessions.map((s) => (
            <div key={s.sessionId} style={{ display: "flex", justifyContent: "space-between", gap: 12, alignItems: "center", padding: 8 }}>
              <div style={{ display: "grid" }}>
                <b>{s.registerName} · {s.cashierName ?? "?"}</b>
                <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
                  Ouverte {dateTime(s.openedAt)} · {s.saleCount} vente(s) — la caisse est bloquée pour les autres caissiers
                </span>
              </div>
              <Button size="sm" variant="ghost" onClick={() => { setError(null); setClosingOther(s); }}>Clôturer</Button>
            </div>
          ))}
        </Section>
      )}

      {closingOther && (
        <Modal title={`Clôturer ${closingOther.registerName}`} onClose={() => setClosingOther(null)}
               footer={<>
                 <Button variant="ghost" onClick={() => setClosingOther(null)}>Annuler</Button>
                 <Button loading={busy} disabled={busy || counted === "" || Number(counted) < 0 || !note.trim()}
                         onClick={() => void closeOther()}>Valider le comptage</Button>
               </>}>
          <div style={{ display: "grid", gap: 8 }}>
            <p style={{ margin: 0 }}>
              Session de {closingOther.cashierName ?? "?"} ouverte {dateTime(closingOther.openedAt)}. Comptez le tiroir et
              saisissez le total : l'attendu et l'écart sont calculés par le serveur, la clôture est auditée.
            </p>
            <TextInput type="number" min={0} inputMode="decimal" autoFocus aria-label={`Montant compté (${closingOther.currency})`}
                       placeholder={`Montant compté (${closingOther.currency})`} value={counted} onChange={(e) => setCounted(e.target.value)} />
            <TextInput aria-label="Motif" placeholder="Motif (obligatoire) — ex. caisse oubliée ouverte" value={note}
                       onChange={(e) => setNote(e.target.value)} />
            {error && <div className="nx-error-text" role="alert">{error}</div>}
          </div>
        </Modal>
      )}

      {decision && (
        <Modal title={decision.approve ? "Confirmer la validation" : "Motif du refus"} onClose={() => setDecision(null)}
               footer={<>
                 <Button variant="ghost" onClick={() => setDecision(null)}>Annuler</Button>
                 <Button variant={decision.approve ? "primary" : "danger"} loading={busy}
                         disabled={busy || (noteRequired && !note.trim())} onClick={() => void decide()}>
                   {decision.approve ? "Valider" : "Refuser"}
                 </Button>
               </>}>
          <div style={{ display: "grid", gap: 8 }}>
            {decision.kind === "return" && decision.approve && (
              <p style={{ margin: 0 }}>Le retour sera exécuté immédiatement : réintégration du stock, remboursement et écriture comptable.</p>
            )}
            <TextInput autoFocus aria-label="Commentaire" placeholder={noteRequired ? "Motif (obligatoire)" : "Commentaire (facultatif)"}
                       value={note} onChange={(e) => setNote(e.target.value)} />
            {error && <div className="nx-error-text" role="alert">{error}</div>}
          </div>
        </Modal>
      )}
    </div>
  );
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card style={{ display: "grid", gap: 8 }}>
      <div style={{ fontWeight: 700 }}>{title}</div>
      {children}
    </Card>
  );
}

function Item({ title, detail, focused, onApprove, onReject }: {
  title: string; detail: string; focused?: boolean; onApprove: () => void; onReject?: () => void;
}) {
  return (
    <div style={{
      display: "flex", justifyContent: "space-between", gap: 12, alignItems: "center", padding: 8, borderRadius: 8,
      outline: focused ? "2px solid var(--nx-brand)" : undefined,
    }}>
      <div style={{ display: "grid" }}>
        <b>{title}</b>
        <span style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>{detail}</span>
      </div>
      <div style={{ display: "flex", gap: 6 }}>
        {onReject && <Button size="sm" variant="ghost" onClick={onReject}>Refuser</Button>}
        <Button size="sm" onClick={onApprove}>Valider</Button>
      </div>
    </div>
  );
}
