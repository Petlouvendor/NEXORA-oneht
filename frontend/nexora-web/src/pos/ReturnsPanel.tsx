import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Badge, Button, Card, EmptyState, Field, Select, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { dateTime, money } from "@/shared/format";
import { ManagerOverrideDialog } from "@/pos/ManagerOverrideDialog";
import { useToast } from "@/shared/Toast";
import {
  listReturnRequests, lookupSale, requestReturn, STATUS_LABEL,
  type CurrentSession, type ReturnableSale, type ReturnRequest,
} from "@/pos/posApi";

const REFUND_METHODS = [
  { value: "cash", label: "Espèces" },
  { value: "original", label: "Moyen de paiement d'origine" },
  { value: "store_credit", label: "Avoir magasin" },
];

export function statusTone(status: string): "success" | "warning" | "error" | undefined {
  if (status === "pending_approval") return "warning";
  if (status === "executed" || status === "approved" || status === "consumed") return "success";
  if (status === "rejected" || status === "expired") return "error";
  return undefined;
}

/**
 * Retours au POS (guide §10). Le caissier retrouve la vente par son ticket, choisit les articles et
 * les quantités (bornées par ce qui reste retournable), indique le motif : c'est une DEMANDE. Aucun
 * montant n'est saisi — le remboursement est calculé par le serveur — et rien ne bouge (stock,
 * caisse) avant la validation d'un Manager.
 */
export function ReturnsPanel({ session }: { session: CurrentSession | null }) {
  const toast = useToast();
  const [ticket, setTicket] = useState("");
  const [sale, setSale] = useState<ReturnableSale | null>(null);
  const [quantities, setQuantities] = useState<Record<string, number>>({});
  const [reason, setReason] = useState("");
  const [method, setMethod] = useState("cash");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [mine, setMine] = useState<ReturnRequest[]>([]);
  const [onSite, setOnSite] = useState<ReturnRequest | null>(null);

  const refresh = useCallback(() => {
    listReturnRequests().then(setMine).catch(() => { /* liste indicative */ });
  }, []);
  useEffect(() => { refresh(); }, [refresh]);

  async function find() {
    setError(null);
    setSale(null);
    setQuantities({});
    try {
      setSale(await lookupSale(ticket.trim()));
    } catch (e) {
      setError(e instanceof ApiError && e.problem.status === 404
        ? `Aucune vente « ${ticket.trim()} ».`
        : e instanceof ApiError ? e.message : "Recherche impossible.");
    }
  }

  const selected = sale?.lines.filter((l) => (quantities[l.saleLineId] ?? 0) > 0) ?? [];

  async function submit(validateNow: boolean) {
    if (!sale || !session) return;
    setBusy(true);
    setError(null);
    try {
      const r = await requestReturn({
        operationUuid: crypto.randomUUID(),
        saleId: sale.saleId,
        inventoryLocationId: session.inventoryLocationId ?? "",
        cashSessionId: session.status === "open" ? session.sessionId : null,
        reason: reason.trim(),
        refundMethod: method,
        refundFinancialAccountId: session.financialAccountId,
        lines: selected.map((l) => ({ saleLineId: l.saleLineId, quantity: quantities[l.saleLineId], restock: true })),
      });
      if (validateNow) setOnSite(r);
      else toast.success(`Demande ${r.requestNumber} envoyée au Manager (remboursement estimé ${money(r.estimatedRefund, r.currency)}).`);
      setSale(null);
      setTicket("");
      setReason("");
      setQuantities({});
      refresh();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Demande impossible.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ display: "grid", gap: 12 }}>
      <Card className="nx-field" style={{ gap: 10 }}>
        <div style={{ fontWeight: 700 }}>Demande de retour</div>
        <div style={{ display: "flex", gap: 8, alignItems: "flex-end" }}>
          <Field label="Numéro du ticket">
            <TextInput value={ticket} onChange={(e) => setTicket(e.target.value)}
                       onKeyDown={(e) => e.key === "Enter" && ticket.trim() && void find()} />
          </Field>
          <Button variant="ghost" disabled={!ticket.trim()} onClick={() => void find()}><Icon.Search size={14} /> Rechercher</Button>
        </div>

        {sale && (
          <>
            <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
              Vente {sale.documentNumber} du {dateTime(sale.soldAt)} · total {money(sale.total, sale.currency)}
            </div>
            <table className="nx-table">
              <thead>
                <tr><th>Article</th><th className="nx-num">Vendu</th><th className="nx-num">Retournable</th><th className="nx-num" style={{ width: 96 }}>À retourner</th></tr>
              </thead>
              <tbody>
                {sale.lines.map((l) => (
                  <tr key={l.saleLineId}>
                    <td>{l.description}</td>
                    <td className="nx-num">{l.quantitySold}</td>
                    <td className="nx-num">{l.quantityReturnable}{l.quantityPending > 0 && <span style={{ color: "var(--nx-text-2)" }}> ({l.quantityPending} en attente)</span>}</td>
                    <td>
                      <TextInput type="number" min={0} max={l.quantityReturnable} disabled={l.quantityReturnable <= 0}
                                 aria-label={`Quantité à retourner — ${l.description}`}
                                 value={quantities[l.saleLineId] ?? 0} style={{ textAlign: "right" }}
                                 onChange={(e) => setQuantities((q) => ({
                                   ...q, [l.saleLineId]: Math.max(0, Math.min(l.quantityReturnable, Number(e.target.value) || 0)),
                                 }))} />
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
            <Field label="Motif du retour" required>
              <TextInput value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Ex. article défectueux" />
            </Field>
            <Field label="Mode de remboursement souhaité">
              <Select value={method} onChange={(e) => setMethod(e.target.value)}>
                {REFUND_METHODS.map((m) => <option key={m.value} value={m.value}>{m.label}</option>)}
              </Select>
            </Field>
            {!session && <div className="nx-error-text">Ouvrez une session de caisse pour demander un retour.</div>}
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              <Button variant="ghost" disabled={busy || !session || selected.length === 0 || !reason.trim()} loading={busy}
                      onClick={() => void submit(false)}>
                Envoyer la demande au Manager
              </Button>
              <Button disabled={busy || !session || selected.length === 0 || !reason.trim()} loading={busy}
                      onClick={() => void submit(true)}>
                Responsable présent : valider maintenant
              </Button>
            </div>
          </>
        )}
        {error && <div className="nx-error-text" role="alert">{error}</div>}
      </Card>

      <Card style={{ display: "grid", gap: 8 }}>
        <div style={{ fontWeight: 700 }}>Mes demandes de retour</div>
        {mine.length === 0 && <EmptyState title="Aucune demande" />}
        {mine.slice(0, 10).map((r) => (
          <div key={r.id} style={{ display: "flex", justifyContent: "space-between", gap: 8, fontSize: "var(--nx-fs-sm)" }}>
            <span>
              <b>{r.requestNumber}</b> · vente {r.saleNumber} · {money(r.estimatedRefund, r.currency)}
              {r.decisionNote && <span style={{ color: "var(--nx-text-2)" }}> — {r.decisionNote}</span>}
            </span>
            <span style={{ display: "flex", gap: 6, alignItems: "center" }}>
              {r.status === "pending_approval" && (
                <Button size="sm" variant="ghost" onClick={() => setOnSite(r)}>Responsable présent</Button>
              )}
              <Badge tone={statusTone(r.status)}>{STATUS_LABEL[r.status] ?? r.status}</Badge>
            </span>
          </div>
        ))}
      </Card>

      {onSite && (
        <ManagerOverrideDialog
          title="Validation responsable — retour"
          summary={<span>Retour <b>{onSite.requestNumber}</b> sur la vente {onSite.saleNumber} :
            {" "}{onSite.lines.map((l) => `${l.quantity} × ${l.description}`).join(", ")} — remboursement
            {" "}<b>{money(onSite.estimatedRefund, onSite.currency)}</b>. Motif : {onSite.reason}.
            La validation exécute le retour (stock, remboursement, comptabilité).</span>}
          approveAction="approve_return" rejectAction="reject_return" targetId={onSite.id}
          onClose={() => { setOnSite(null); refresh(); }}
          onDone={() => { setOnSite(null); refresh(); }}
        />
      )}
    </div>
  );
}
