import { useState } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money } from "@/shared/format";
import { Badge, Button, ConfirmDialog, DataTable, Drawer, SectionLabel } from "@/shared/ui";
import {
  BLOCK_TYPES, CHANNELS, DISPUTE_STATUS, PROMISE_STATUS, RECEIVABLE_STATUS, listBlocks, listDisputes, listPromises,
  listReceivables, listWriteOffs, releaseBlock, type CreditReceivable, type CustomerBlock, type Dispute,
} from "@/credit/creditApi";
import { ActionDialog, BlockDialog, DisputeDialog, PromiseDialog, ResolveDisputeDialog, WriteOffDialog } from "@/credit/CreditDialogs";
import { ACTION_OUTCOMES, ACTION_TYPES, listCollectionActions } from "@/customers/customersApi";

type Dialog =
  | { kind: "action" } | { kind: "promise"; receivableId?: string } | { kind: "block" }
  | { kind: "dispute"; receivable: CreditReceivable } | { kind: "writeoff"; receivable: CreditReceivable }
  | { kind: "resolve"; dispute: Dispute } | { kind: "release"; block: CustomerBlock };

/** Dossier de recouvrement d'un client : créances, promesses, litiges, blocages, actions, pertes. */
export function CustomerCreditDrawer({ customerId, customerName, onClose, onChanged }: {
  customerId: string; customerName: string; onClose: () => void; onChanged: () => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const receivables = useAsync(() => listReceivables(customerId, true), [customerId, version]);
  const promises = useAsync(() => listPromises(undefined, customerId), [customerId, version]);
  const disputes = useAsync(() => listDisputes(undefined, customerId), [customerId, version]);
  const blocks = useAsync(() => listBlocks(false, customerId), [customerId, version]);
  const actions = useAsync(() => listCollectionActions(customerId), [customerId, version]);
  const writeOffs = useAsync(() => listWriteOffs(customerId), [customerId, version]);
  const [dialog, setDialog] = useState<Dialog | null>(null);
  const { error, submit } = useSubmit();
  const done = () => { setDialog(null); setVersion((v) => v + 1); onChanged(); };

  const recs = receivables.data ?? [];
  const activeBlocks = (blocks.data ?? []).filter((b) => b.isActive);
  const byCurrency = Object.entries(recs.reduce<Record<string, { due: number; overdue: number }>>((acc, r) => {
    const t = (acc[r.currency] ??= { due: 0, overdue: 0 });
    t.due += r.remainingAmount;
    if (r.daysOverdue > 0) t.overdue += r.remainingAmount;
    return acc;
  }, {}));
  const can = {
    collect: hasPermission("credit.collect"), dispute: hasPermission("credit.dispute"),
    block: hasPermission("credit.block"), writeOff: hasPermission("credit.write_off"),
  };

  return (
    <Drawer title={`Recouvrement — ${customerName}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 14 }}>
        {(error || receivables.error) && <div role="alert" className="nx-error-text">{error ?? receivables.error}</div>}
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
          {activeBlocks.length > 0
            ? activeBlocks.map((b) => <Badge key={b.id} tone="error">Bloqué : {BLOCK_TYPES[b.blockType] ?? b.blockType}</Badge>)
            : <Badge tone="success">Aucun blocage</Badge>}
          <Link to={`/customers/${customerId}`}>Fiche client et relevé</Link>
        </div>
        {byCurrency.map(([cur, t]) => (
          <div key={cur} style={{ display: "flex", gap: 16, flexWrap: "wrap" }}>
            <span>Reste dû : <strong className="nx-numeric">{money(t.due, cur)}</strong></span>
            <span>dont échu : <strong className="nx-numeric" style={{ color: t.overdue ? "var(--nx-neg)" : undefined }}>{money(t.overdue, cur)}</strong></span>
          </div>
        ))}
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {can.collect && <Button size="sm" disabled={!receivables.data} onClick={() => setDialog({ kind: "action" })}>Consigner une action</Button>}
          {can.collect && <Button size="sm" variant="subtle" disabled={!receivables.data} onClick={() => setDialog({ kind: "promise" })}>Promesse de paiement</Button>}
          {can.block && <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "block" })}>Bloquer</Button>}
        </div>

        <SectionLabel>Créances ouvertes</SectionLabel>
        <DataTable exportTitle={`Créances — ${customerName}`}
          columns={[
            { key: "n", header: "Crédit", render: (r) => r.creditNumber },
            { key: "d", header: "Échéance", render: (r) => (r.dueDate ? dateShort(r.dueDate) : "—") },
            { key: "l", header: "Retard", numeric: true, render: (r) => (r.daysOverdue > 0 ? <Badge tone="warning">{r.daysOverdue} j</Badge> : "—") },
            { key: "o", header: "Montant", numeric: true, render: (r) => money(r.originalAmount, r.currency) },
            { key: "r", header: "Reste dû", numeric: true, render: (r) => money(r.remainingAmount, r.currency) },
            { key: "s", header: "Statut", render: (r) => <>
              {RECEIVABLE_STATUS[r.status] ?? r.status}
              {r.openPromiseId && <> <Badge tone="info">promise</Badge></>}
            </> },
            { key: "a", header: "", render: (r) => (
              <div style={{ display: "flex", gap: 4, justifyContent: "flex-end", flexWrap: "wrap" }}>
                {can.collect && !r.openPromiseId && r.status !== "IN_DISPUTE" &&
                  <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "promise", receivableId: r.id })}>Promesse</Button>}
                {can.dispute && !r.openDisputeId &&
                  <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "dispute", receivable: r })}>Litige</Button>}
                {can.writeOff && r.status !== "IN_DISPUTE" &&
                  <Button size="sm" variant="danger" onClick={() => setDialog({ kind: "writeoff", receivable: r })}>Perte</Button>}
              </div>) },
          ]}
          rows={recs} rowKey={(r) => r.id} empty={receivables.loading ? "Chargement…" : "Aucune créance ouverte."} />

        <SectionLabel>Promesses</SectionLabel>
        <DataTable exportTitle={`Promesses — ${customerName}`}
          columns={[
            { key: "d", header: "Pour le", render: (p) => dateShort(p.promisedDate) },
            { key: "a", header: "Promis", numeric: true, render: (p) => money(p.amount, p.currency) },
            { key: "k", header: "Reçu", numeric: true, render: (p) => (p.status === "open" ? "—" : money(p.keptAmount, p.currency)) },
            { key: "c", header: "Canal", render: (p) => CHANNELS[p.channel] ?? p.channel },
            { key: "s", header: "Statut", render: (p) => <Badge tone={PROMISE_STATUS[p.status]?.tone}>{PROMISE_STATUS[p.status]?.label ?? p.status}</Badge> },
          ]}
          rows={promises.data ?? []} rowKey={(p) => p.id} empty="Aucune promesse." />

        <SectionLabel>Litiges</SectionLabel>
        <DataTable exportTitle={`Litiges — ${customerName}`}
          columns={[
            { key: "d", header: "Ouvert le", render: (d) => dateShort(d.createdAt) },
            { key: "n", header: "Crédit", render: (d) => d.creditNumber ?? "Compte" },
            { key: "a", header: "Contesté", numeric: true, render: (d) => money(d.disputedAmount, d.currency) },
            { key: "r", header: "Motif", render: (d) => d.reason },
            { key: "s", header: "Statut", render: (d) => <Badge tone={DISPUTE_STATUS[d.status]?.tone}>{DISPUTE_STATUS[d.status]?.label ?? d.status}</Badge> },
            { key: "x", header: "", render: (d) => can.dispute && ["open", "under_review"].includes(d.status)
              ? <Button size="sm" variant="subtle" onClick={() => setDialog({ kind: "resolve", dispute: d })}>Trancher</Button> : d.resolution ?? "" },
          ]}
          rows={disputes.data ?? []} rowKey={(d) => d.id} empty="Aucun litige." />

        <SectionLabel>Actions de recouvrement</SectionLabel>
        <DataTable exportTitle={`Actions de recouvrement — ${customerName}`}
          columns={[
            { key: "d", header: "Date", render: (a) => dateTime(a.createdAt) },
            { key: "t", header: "Action", render: (a) => ACTION_TYPES[a.actionType] ?? a.actionType },
            { key: "o", header: "Résultat", render: (a) => (a.outcome ? ACTION_OUTCOMES[a.outcome] ?? a.outcome : "—") },
            { key: "n", header: "Note", render: (a) => a.note },
            { key: "x", header: "Relance", render: (a) => (a.nextActionDate ? dateShort(a.nextActionDate) : "—") },
            { key: "u", header: "Par", render: (a) => a.createdByName ?? "—" },
          ]}
          rows={actions.data ?? []} rowKey={(a) => a.id} empty="Aucune action consignée." />

        {(blocks.data ?? []).length > 0 && <>
          <SectionLabel>Blocages</SectionLabel>
          <DataTable exportTitle={`Blocages — ${customerName}`}
            columns={[
              { key: "t", header: "Type", render: (b) => BLOCK_TYPES[b.blockType] ?? b.blockType },
              { key: "r", header: "Motif", render: (b) => b.reason },
              { key: "d", header: "Depuis", render: (b) => dateShort(b.blockedAt) },
              { key: "e", header: "Levé", render: (b) => (b.releasedAt ? `${dateShort(b.releasedAt)} — ${b.releaseReason ?? ""}` : "—") },
              { key: "x", header: "", render: (b) => can.block && b.isActive
                ? <Button size="sm" variant="subtle" onClick={() => setDialog({ kind: "release", block: b })}>Débloquer</Button> : null },
            ]}
            rows={blocks.data ?? []} rowKey={(b) => b.id} />
        </>}

        {(writeOffs.data ?? []).length > 0 && <>
          <SectionLabel>Pertes constatées</SectionLabel>
          <DataTable exportTitle={`Pertes — ${customerName}`}
            columns={[
              { key: "d", header: "Date", render: (w) => dateShort(w.writtenOffAt) },
              { key: "n", header: "Crédit", render: (w) => w.creditNumber ?? "—" },
              { key: "a", header: "Montant", numeric: true, render: (w) => money(w.amount, w.currency) },
              { key: "r", header: "Motif", render: (w) => w.reason },
              { key: "e", header: "Écriture", render: (w) => w.journalEntryNumber ?? "—" },
            ]}
            rows={writeOffs.data ?? []} rowKey={(w) => w.id} />
        </>}
      </div>

      {dialog?.kind === "action" && <ActionDialog customerId={customerId} customerName={customerName} receivables={recs} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === "promise" && <PromiseDialog customerId={customerId} customerName={customerName} receivables={recs}
                                                    preselect={dialog.receivableId} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === "block" && <BlockDialog customerId={customerId} customerName={customerName} activeTypes={activeBlocks.map((b) => b.blockType)}
                                                onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === "dispute" && <DisputeDialog customerId={customerId} receivable={dialog.receivable} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === "writeoff" && <WriteOffDialog receivable={dialog.receivable} customerName={customerName} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === "resolve" && <ResolveDisputeDialog dispute={dialog.dispute} onClose={() => setDialog(null)} onDone={done} />}
      {dialog?.kind === "release" && (
        <ConfirmDialog title={`Débloquer — ${BLOCK_TYPES[dialog.block.blockType] ?? dialog.block.blockType}`} requireReason confirmLabel="Débloquer"
          message="Le motif du déblocage est conservé au dossier."
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            const b = dialog.block; setDialog(null);
            if (await submit(() => releaseBlock(b.id, reason).then(() => true))) { toast.success("Blocage levé."); done(); }
          }} />
      )}
    </Drawer>
  );
}
