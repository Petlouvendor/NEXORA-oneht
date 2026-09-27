import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, KpiCard, Money, SectionLabel, StatePill, Tabs,
  type Column,
} from "@/shared/ui";
import {
  blockCustomer, getAging, getCollections, listBlocks, listDisputes, listPromises, releaseBlock,
  resolveDispute, settlePromise,
  type AgingSummary, type CollectionsRow, type CustomerBlock, type Dispute, type Promise_,
} from "@/credit/creditApi";
import { CustomerApprovalsPanel, CustomerReportsPanel } from "@/customers/CustomerWorkQueues";

/** Crédit & recouvrement (Phase 27 §15, §21-27, §37) : portefeuille, aging, promesses, litiges, blocages ;
 * module clients : validations (dépenses imputées, remboursements d'avoir) et rapports clients (§26). */
export function RecoveryPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("portfolio");
  const [aging, setAging] = useState<AgingSummary | null>(null);
  const [rows, setRows] = useState<CollectionsRow[]>([]);
  const [promises, setPromises] = useState<Promise_[]>([]);
  const [disputes, setDisputes] = useState<Dispute[]>([]);
  const [blocks, setBlocks] = useState<CustomerBlock[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canCollect = hasPermission("credit.collect");
  const canDispute = hasPermission("credit.dispute");
  const canBlock = hasPermission("credit.block");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      getAging().catch(() => null),
      getCollections(true).catch(() => []),
      listPromises().catch(() => []),
      listDisputes().catch(() => []),
      listBlocks(true).catch(() => []),
    ])
      .then(([a, c, p, d, b]) => { setAging(a); setRows(c); setPromises(p); setDisputes(d); setBlocks(b); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, []);

  useEffect(load, [load]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("credit.view_portfolio")) {
    return <Card>Le portefeuille crédit n'est pas accessible avec vos permissions.</Card>;
  }

  const cur = aging?.currency ?? "HTG";
  const portfolioCols: Array<Column<CollectionsRow>> = [
    { key: "c", header: "Client", render: (r) => <><Link to={`/customers/${r.customerId}`}>{r.customerName}</Link> {r.blocked && <Badge tone="error">bloqué</Badge>}</> },
    { key: "t", header: "Encours", numeric: true, render: (r) => <Money value={r.totalOutstanding} currency={r.currency} /> },
    { key: "cur", header: "Courant", numeric: true, render: (r) => <Money value={r.current} currency={r.currency} /> },
    { key: "b1", header: "1–30", numeric: true, render: (r) => <Money value={r.bucket1To30} currency={r.currency} /> },
    { key: "b2", header: "31–60", numeric: true, render: (r) => <Money value={r.bucket31To60} currency={r.currency} /> },
    { key: "b3", header: "61–90", numeric: true, render: (r) => <Money value={r.bucket61To90} currency={r.currency} /> },
    { key: "b4", header: "90+", numeric: true, render: (r) => <Money value={r.bucket90Plus} currency={r.currency} /> },
    { key: "od", header: "Retard", numeric: true, render: (r) => (r.oldestOverdueDays > 0 ? <Badge tone="warning">{r.oldestOverdueDays} j</Badge> : "—") },
    { key: "pr", header: "Prom.", numeric: true, render: (r) => r.openPromises || "—" },
    {
      key: "a", header: "", render: (r) => canBlock && !r.blocked ? (
        <Button size="sm" variant="danger" disabled={busy}
          onClick={() => { const why = window.prompt("Motif du blocage crédit ?"); if (why) run(() => blockCustomer(r.customerId, "credit", why)); }}>
          Bloquer
        </Button>
      ) : null,
    },
  ];

  const promiseCols: Array<Column<Promise_>> = [
    { key: "c", header: "Client", render: (p) => p.customerName },
    { key: "a", header: "Montant", numeric: true, render: (p) => <Money value={p.amount} currency={cur} /> },
    { key: "d", header: "Date promise", render: (p) => p.promisedDate },
    { key: "ch", header: "Canal", render: (p) => p.channel },
    { key: "s", header: "Statut", render: (p) => <StatePill status={p.status} /> },
    { key: "act", header: "", render: (p) => canCollect && p.status === "open"
      ? <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(() => settlePromise(p.id))}>Solder</Button> : null },
  ];

  const disputeCols: Array<Column<Dispute>> = [
    { key: "c", header: "Client", render: (d) => d.customerName },
    { key: "a", header: "Contesté", numeric: true, render: (d) => <Money value={d.disputedAmount} currency={cur} /> },
    { key: "r", header: "Motif", render: (d) => d.reason },
    { key: "s", header: "Statut", render: (d) => <StatePill status={d.status} /> },
    { key: "act", header: "", render: (d) => canDispute && ["open", "under_review"].includes(d.status)
      ? (
        <div style={{ display: "flex", gap: 6 }}>
          <Button size="sm" variant="subtle" disabled={busy}
            onClick={() => run(() => resolveDispute(d.id, "resolved_company", window.prompt("Résolution ?") || "en faveur de l'entreprise"))}>
            Trancher (entreprise)
          </Button>
          <Button size="sm" variant="subtle" disabled={busy}
            onClick={() => run(() => resolveDispute(d.id, "resolved_customer", window.prompt("Résolution ?") || "en faveur du client"))}>
            Client
          </Button>
        </div>
      ) : null },
  ];

  const blockCols: Array<Column<CustomerBlock>> = [
    { key: "c", header: "Client", render: (b) => b.customerName },
    { key: "t", header: "Type", render: (b) => <Badge>{b.blockType}</Badge> },
    { key: "r", header: "Motif", render: (b) => b.reason },
    { key: "d", header: "Depuis", render: (b) => new Date(b.blockedAt).toLocaleDateString() },
    { key: "act", header: "", render: (b) => canBlock && b.isActive
      ? <Button size="sm" disabled={busy} onClick={() => { const why = window.prompt("Motif du déblocage ?"); if (why) run(() => releaseBlock(b.id, why)); }}>Débloquer</Button> : null },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Crédit & recouvrement</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Total créances" value={<Money value={aging?.total ?? 0} currency={cur} />} />
        <KpiCard label="Échu" value={<Money value={aging?.totalOverdue ?? 0} currency={cur} />} tone={(aging?.totalOverdue ?? 0) > 0 ? "warning" : undefined} tag={(aging?.totalOverdue ?? 0) > 0 ? "!" : undefined} />
        <KpiCard label="90+ jours" value={<Money value={aging?.bucket90Plus ?? 0} currency={cur} />} />
        <KpiCard label="DSO (jours)" value={aging?.dso != null ? String(aging.dso) : "—"} />
        <KpiCard label="Litiges ouverts" value={String(disputes.filter((d) => ["open", "under_review"].includes(d.status)).length)} />
      </div>

      <Card>
        <Tabs
          tabs={[
            { id: "portfolio", label: `Portefeuille (${rows.length})` },
            { id: "promises", label: `Promesses (${promises.length})` },
            { id: "disputes", label: `Litiges (${disputes.length})` },
            { id: "blocks", label: `Blocages (${blocks.length})` },
            { id: "approvals", label: "Validations" },
            { id: "reports", label: "Rapports clients" },
          ]}
          active={tab}
          onChange={setTab}
        />
        <div style={{ marginTop: 12 }}>
          {tab === "portfolio" && <DataTable columns={portfolioCols} rows={rows} rowKey={(r) => r.customerId} empty="Aucune créance échue." />}
          {tab === "promises" && <DataTable columns={promiseCols} rows={promises} rowKey={(p) => p.id} empty="Aucune promesse." />}
          {tab === "disputes" && <DataTable columns={disputeCols} rows={disputes} rowKey={(d) => d.id} empty="Aucun litige." />}
          {tab === "blocks" && <DataTable columns={blockCols} rows={blocks} rowKey={(b) => b.id} empty="Aucun blocage actif." />}
          {tab === "approvals" && <CustomerApprovalsPanel />}
          {tab === "reports" && <CustomerReportsPanel />}
        </div>
      </Card>

      <Card>
        <SectionLabel>Aging du portefeuille (§15)</SectionLabel>
        {aging && (
          <div style={{ display: "flex", gap: 16, flexWrap: "wrap", marginTop: 8 }}>
            {[
              ["Courant", aging.current],
              ["1–30 j", aging.bucket1To30],
              ["31–60 j", aging.bucket31To60],
              ["61–90 j", aging.bucket61To90],
              ["90+ j", aging.bucket90Plus],
            ].map(([label, v]) => (
              <div key={label as string}>
                <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{label}</div>
                <div style={{ fontSize: 16 }}><Money value={v as number} currency={cur} /></div>
              </div>
            ))}
          </div>
        )}
      </Card>
    </div>
  );
}
