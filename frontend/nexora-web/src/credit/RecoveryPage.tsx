import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, money } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Field, KpiCard, Select, Tabs } from "@/shared/ui";
import {
  BLOCK_TYPES, CHANNELS, DISPUTE_STATUS, FOLLOW_UP_KIND, PROMISE_STATUS, cancelPromise, getAging, getCollections,
  listBlocks, listDisputes, listFollowUps, listPromises, listWriteOffs, releaseBlock, settlePromise,
  type CustomerBlock, type Dispute, type Promise_,
} from "@/credit/creditApi";
import { ResolveDisputeDialog } from "@/credit/CreditDialogs";
import { CustomerCreditDrawer } from "@/credit/CustomerCreditDrawer";
import { CustomerApprovalsPanel, CustomerReportsPanel } from "@/customers/CustomerWorkQueues";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit", textAlign: "left" } as const;

/** Crédit & recouvrement : agenda, portefeuille âgé, promesses, litiges, blocages, pertes ; dossier client par client. */
export function RecoveryPage() {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [tab, setTab] = useState("todo");
  const [version, setVersion] = useState(0);
  const changed = () => setVersion((v) => v + 1);
  const [onlyOverdue, setOnlyOverdue] = useState(true);
  const [promiseStatus, setPromiseStatus] = useState("open");
  const [disputeStatus, setDisputeStatus] = useState("open");
  const [onlyActiveBlocks, setOnlyActiveBlocks] = useState(true);
  const aging = useAsync(getAging, [version]);
  const followUps = useAsync(listFollowUps, [version]);
  const rows = useAsync(() => getCollections(onlyOverdue), [onlyOverdue, version]);
  const promises = useAsync(() => listPromises(promiseStatus || undefined), [promiseStatus, version]);
  const disputes = useAsync(() => listDisputes(disputeStatus || undefined), [disputeStatus, version]);
  const openDisputes = useAsync(() => listDisputes("open"), [version]);
  const blocks = useAsync(() => listBlocks(onlyActiveBlocks), [onlyActiveBlocks, version]);
  const writeOffs = useAsync(() => listWriteOffs(), [version]);
  const [dossier, setDossier] = useState<{ id: string; name: string } | null>(null);
  const [dialog, setDialog] = useState<{ kind: "cancel"; promise: Promise_ } | { kind: "resolve"; dispute: Dispute }
    | { kind: "release"; block: CustomerBlock } | null>(null);
  const { busy, error, submit } = useSubmit();

  if (!hasPermission("credit.view_portfolio")) {
    return <Card>Le portefeuille crédit n'est pas accessible avec vos permissions.</Card>;
  }

  const a = aging.data;
  const cur = a?.currency ?? "HTG";
  const todo = followUps.data ?? [];
  const open = (id: string, name: string) => setDossier({ id, name });
  const customerLink = (id: string, name: string) => <button type="button" style={linkStyle} onClick={() => open(id, name)}>{name}</button>;
  const canCollect = hasPermission("credit.collect");

  async function evaluate(p: Promise_) {
    const status = await submit(() => settlePromise(p.id));
    if (status) { toast.success(`Promesse de ${p.customerName} : ${PROMISE_STATUS[status]?.label.toLowerCase() ?? status}.`); changed(); }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Crédit & recouvrement</h1>
      {(error || aging.error) && <div role="alert" className="nx-error-text">{error ?? aging.error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Créances" value={a ? money(a.total, cur) : "—"} />
        <KpiCard label="Échu" value={a ? money(a.totalOverdue, cur) : "—"} tone={(a?.totalOverdue ?? 0) > 0 ? "warning" : undefined} />
        <KpiCard label="Plus de 90 jours" value={a ? money(a.bucket90Plus, cur) : "—"} tone={(a?.bucket90Plus ?? 0) > 0 ? "warning" : undefined} />
        <KpiCard label="Délai moyen d'encaissement" value={a?.dso != null ? `${a.dso} j` : "—"} />
        <KpiCard label="Litiges ouverts" value={String(openDisputes.data?.length ?? 0)} />
        <KpiCard label="À traiter" value={String(todo.length)} tone={todo.length ? "warning" : undefined} />
      </div>
      {a?.otherCurrencies && a.otherCurrencies.length > 0 && (
        <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>
          Créances en autre devise (non additionnées) : {a.otherCurrencies.map((o) => money(o.amount, o.currency)).join(" · ")}
        </div>
      )}

      <Tabs active={tab} onChange={setTab} tabs={[
        { id: "todo", label: `À traiter (${todo.length})` },
        { id: "portfolio", label: "Portefeuille" },
        { id: "promises", label: "Promesses" },
        { id: "disputes", label: "Litiges" },
        { id: "blocks", label: "Blocages" },
        { id: "writeoffs", label: "Pertes" },
        { id: "approvals", label: "Validations" },
        { id: "reports", label: "Rapports clients" },
      ]} />

      {tab === "todo" && (
        <Card>
          <div style={{ fontSize: 13, color: "var(--nx-text-2)", marginBottom: 8 }}>
            Relances prévues arrivées à date, promesses à échéance, promesses non tenues sans action depuis. Une relance disparaît dès qu'une nouvelle action est consignée.
          </div>
          {followUps.error && <div role="alert" className="nx-error-text">{followUps.error}</div>}
          <DataTable exportTitle="Recouvrement — à traiter"
            columns={[
              { key: "d", header: "Date", render: (f) => dateShort(f.dueDate) },
              { key: "k", header: "Type", render: (f) => <Badge tone={f.kind === "promise_broken" ? "error" : "warning"}>{FOLLOW_UP_KIND[f.kind]}</Badge> },
              { key: "c", header: "Client", render: (f) => customerLink(f.customerId, f.customerName) },
              { key: "t", header: "Détail", render: (f) => f.detail },
              { key: "a", header: "Montant", numeric: true, render: (f) => (f.amount != null ? money(f.amount, f.currency ?? cur) : "") },
              { key: "x", header: "", render: (f) => <Button size="sm" variant="subtle" onClick={() => open(f.customerId, f.customerName)}>Traiter</Button> },
            ]}
            rows={todo} rowKey={(f) => `${f.kind}-${f.referenceId}`} empty={followUps.loading ? "Chargement…" : "Rien à traiter aujourd'hui."} />
        </Card>
      )}

      {tab === "portfolio" && (
        <Card>
          <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, marginBottom: 8 }}>
            <input type="checkbox" checked={onlyOverdue} onChange={(e) => setOnlyOverdue(e.target.checked)} /> Clients en retard seulement
          </label>
          {rows.error && <div role="alert" className="nx-error-text">{rows.error}</div>}
          <DataTable exportTitle="Portefeuille clients — balance âgée"
            columns={[
              { key: "c", header: "Client", render: (r) => <>{customerLink(r.customerId, r.customerName)} {r.blocked && <Badge tone="error">bloqué</Badge>}</> },
              { key: "t", header: "Reste dû", numeric: true, render: (r) => money(r.totalOutstanding, r.currency) },
              { key: "cur", header: "À échoir", numeric: true, render: (r) => money(r.current, r.currency) },
              { key: "b1", header: "1–30 j", numeric: true, render: (r) => money(r.bucket1To30, r.currency) },
              { key: "b2", header: "31–60 j", numeric: true, render: (r) => money(r.bucket31To60, r.currency) },
              { key: "b3", header: "61–90 j", numeric: true, render: (r) => money(r.bucket61To90, r.currency) },
              { key: "b4", header: "> 90 j", numeric: true, render: (r) => money(r.bucket90Plus, r.currency) },
              { key: "od", header: "Retard max", numeric: true, render: (r) => (r.oldestOverdueDays > 0 ? <Badge tone="warning">{r.oldestOverdueDays} j</Badge> : "—") },
              { key: "pr", header: "Promesses", numeric: true, render: (r) => r.openPromises || "—" },
              { key: "li", header: "Litiges", numeric: true, render: (r) => r.openDisputes || "—" },
            ]}
            rows={rows.data ?? []} rowKey={(r) => `${r.customerId}-${r.currency}`}
            empty={rows.loading ? "Chargement…" : onlyOverdue ? "Aucun client en retard." : "Aucune créance ouverte."} />
        </Card>
      )}

      {tab === "promises" && (
        <Card>
          <Field label="Statut">
            <Select value={promiseStatus} onChange={(e) => setPromiseStatus(e.target.value)}>
              <option value="">Toutes</option>
              {Object.entries(PROMISE_STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
            </Select>
          </Field>
          {promises.error && <div role="alert" className="nx-error-text">{promises.error}</div>}
          <DataTable exportTitle="Promesses de paiement"
            columns={[
              { key: "c", header: "Client", render: (p) => customerLink(p.customerId, p.customerName) },
              { key: "d", header: "Pour le", render: (p) => dateShort(p.promisedDate) },
              { key: "a", header: "Promis", numeric: true, render: (p) => money(p.amount, p.currency) },
              { key: "k", header: "Reçu", numeric: true, render: (p) => (p.status === "open" ? "—" : money(p.keptAmount, p.currency)) },
              { key: "ch", header: "Canal", render: (p) => CHANNELS[p.channel] ?? p.channel },
              { key: "s", header: "Statut", render: (p) => <Badge tone={PROMISE_STATUS[p.status]?.tone}>{PROMISE_STATUS[p.status]?.label ?? p.status}</Badge> },
              { key: "by", header: "Par", render: (p) => p.createdByName ?? "—" },
              { key: "x", header: "", render: (p) => canCollect && p.status === "open" ? (
                <div style={{ display: "flex", gap: 4, justifyContent: "flex-end" }}>
                  <Button size="sm" variant="subtle" disabled={busy} onClick={() => evaluate(p)}>Évaluer</Button>
                  <Button size="sm" variant="ghost" disabled={busy} onClick={() => setDialog({ kind: "cancel", promise: p })}>Annuler</Button>
                </div>) : p.cancelReason ?? "" },
            ]}
            rows={promises.data ?? []} rowKey={(p) => p.id} empty={promises.loading ? "Chargement…" : "Aucune promesse."} />
          <div style={{ fontSize: 12, color: "var(--nx-text-2)", marginTop: 6 }}>
            « Évaluer » compare les versements reçus depuis la promesse : avant l'échéance, seule une promesse entièrement payée peut être close.
          </div>
        </Card>
      )}

      {tab === "disputes" && (
        <Card>
          <Field label="Statut">
            <Select value={disputeStatus} onChange={(e) => setDisputeStatus(e.target.value)}>
              <option value="">Tous</option>
              {Object.entries(DISPUTE_STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
            </Select>
          </Field>
          {disputes.error && <div role="alert" className="nx-error-text">{disputes.error}</div>}
          <DataTable exportTitle="Litiges clients"
            columns={[
              { key: "c", header: "Client", render: (d) => customerLink(d.customerId, d.customerName) },
              { key: "o", header: "Ouvert le", render: (d) => dateShort(d.createdAt) },
              { key: "n", header: "Crédit", render: (d) => d.creditNumber ?? "Compte" },
              { key: "a", header: "Contesté", numeric: true, render: (d) => money(d.disputedAmount, d.currency) },
              { key: "r", header: "Motif", render: (d) => d.reason },
              { key: "s", header: "Statut", render: (d) => <Badge tone={DISPUTE_STATUS[d.status]?.tone}>{DISPUTE_STATUS[d.status]?.label ?? d.status}</Badge> },
              { key: "x", header: "", render: (d) => hasPermission("credit.dispute") && ["open", "under_review"].includes(d.status)
                ? <Button size="sm" variant="subtle" onClick={() => setDialog({ kind: "resolve", dispute: d })}>Trancher</Button> : d.resolution ?? "" },
            ]}
            rows={disputes.data ?? []} rowKey={(d) => d.id} empty={disputes.loading ? "Chargement…" : "Aucun litige."} />
        </Card>
      )}

      {tab === "blocks" && (
        <Card>
          <label style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, marginBottom: 8 }}>
            <input type="checkbox" checked={onlyActiveBlocks} onChange={(e) => setOnlyActiveBlocks(e.target.checked)} /> Blocages actifs seulement
          </label>
          {blocks.error && <div role="alert" className="nx-error-text">{blocks.error}</div>}
          <DataTable exportTitle="Blocages clients"
            columns={[
              { key: "c", header: "Client", render: (b) => customerLink(b.customerId, b.customerName) },
              { key: "t", header: "Type", render: (b) => BLOCK_TYPES[b.blockType] ?? b.blockType },
              { key: "r", header: "Motif", render: (b) => b.reason },
              { key: "d", header: "Depuis", render: (b) => dateShort(b.blockedAt) },
              { key: "e", header: "Levé", render: (b) => (b.releasedAt ? `${dateShort(b.releasedAt)} — ${b.releaseReason ?? ""}` : "—") },
              { key: "x", header: "", render: (b) => hasPermission("credit.block") && b.isActive
                ? <Button size="sm" variant="subtle" onClick={() => setDialog({ kind: "release", block: b })}>Débloquer</Button> : null },
            ]}
            rows={blocks.data ?? []} rowKey={(b) => b.id} empty={blocks.loading ? "Chargement…" : "Aucun blocage."} />
        </Card>
      )}

      {tab === "writeoffs" && (
        <Card>
          {writeOffs.error && <div role="alert" className="nx-error-text">{writeOffs.error}</div>}
          <DataTable exportTitle="Créances passées en perte"
            columns={[
              { key: "d", header: "Date", render: (w) => dateShort(w.writtenOffAt) },
              { key: "c", header: "Client", render: (w) => customerLink(w.customerId, w.customerName) },
              { key: "n", header: "Crédit", render: (w) => w.creditNumber ?? "—" },
              { key: "a", header: "Montant", numeric: true, render: (w) => money(w.amount, w.currency) },
              { key: "r", header: "Motif", render: (w) => w.reason },
              { key: "e", header: "Écriture", render: (w) => w.journalEntryNumber ?? "—" },
              { key: "u", header: "Par", render: (w) => w.createdByName ?? "—" },
            ]}
            rows={writeOffs.data ?? []} rowKey={(w) => w.id} empty={writeOffs.loading ? "Chargement…" : "Aucune créance passée en perte."} />
        </Card>
      )}

      {tab === "approvals" && <CustomerApprovalsPanel />}
      {tab === "reports" && <CustomerReportsPanel />}

      {dossier && <CustomerCreditDrawer key={dossier.id} customerId={dossier.id} customerName={dossier.name}
                                        onClose={() => setDossier(null)} onChanged={changed} />}
      {dialog?.kind === "cancel" && (
        <ConfirmDialog title={`Annuler la promesse de ${dialog.promise.customerName}`} requireReason confirmLabel="Annuler la promesse"
          message="La promesse sort du suivi ; le motif reste au dossier."
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            const p = dialog.promise; setDialog(null);
            if (await submit(() => cancelPromise(p.id, reason).then(() => true))) { toast.success("Promesse annulée."); changed(); }
          }} />
      )}
      {dialog?.kind === "resolve" && <ResolveDisputeDialog dispute={dialog.dispute} onClose={() => setDialog(null)}
                                                           onDone={() => { setDialog(null); changed(); }} />}
      {dialog?.kind === "release" && (
        <ConfirmDialog title={`Débloquer ${dialog.block.customerName}`} requireReason confirmLabel="Débloquer"
          message={`Blocage « ${BLOCK_TYPES[dialog.block.blockType] ?? dialog.block.blockType} » : le motif du déblocage est conservé au dossier.`}
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            const b = dialog.block; setDialog(null);
            if (await submit(() => releaseBlock(b.id, reason).then(() => true))) { toast.success("Blocage levé."); changed(); }
          }} />
      )}
    </div>
  );
}
