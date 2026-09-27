import { useCallback, useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  Badge, Button, Card, DataTable, KpiCard, Money, SectionLabel, StatePill, Tabs,
  type Column,
} from "@/shared/ui";
import {
  acceptQuote, cancelDocument, convertQuote, documentPdfUrl, listQuotes, searchDocuments,
  sendDocument, sendQuote, verifyDocument,
  type DocumentVerification, type IssuedDocument, type Quote,
} from "@/documents/documentsApi";

/** Facturation & documents commerciaux (Phase 30 §3-5, §19, §27-28, §34, §36). */
export function DocumentsPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("documents");
  const [docs, setDocs] = useState<IssuedDocument[]>([]);
  const [quotes, setQuotes] = useState<Quote[]>([]);
  const [typeFilter, setTypeFilter] = useState("");
  // ?q= : numéro de document venu de la recherche Ctrl+K (NEXORA ONE §5).
  const [params, setParams] = useSearchParams();
  const numberFilter = params.get("q") ?? "";
  const [verifyCode, setVerifyCode] = useState("");
  const [verifyResult, setVerifyResult] = useState<DocumentVerification | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const canQuote = hasPermission("documents.create_quote");
  const canCancel = hasPermission("documents.cancel");
  const canSend = hasPermission("documents.send");
  const canConvert = hasPermission("pos.manage_order");

  const load = useCallback(() => {
    setError(null);
    Promise.all([
      searchDocuments({ ...(typeFilter ? { type: typeFilter } : {}), ...(numberFilter ? { number: numberFilter } : {}) })
        .catch(() => [] as IssuedDocument[]),
      listQuotes().catch(() => [] as Quote[]),
    ])
      .then(([d, q]) => { setDocs(d); setQuotes(q); })
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur de chargement"));
  }, [typeFilter, numberFilter]);

  useEffect(load, [load]);

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); load(); }
    catch (e) { setError(e instanceof ApiError ? e.message : "Action refusée"); }
    finally { setBusy(false); }
  }

  if (!hasPermission("documents.view")) {
    return <Card>Les documents commerciaux ne sont pas accessibles avec vos permissions.</Card>;
  }

  const issued = docs.filter((d) => d.status !== "cancelled" && d.status !== "replaced").length;
  const cancelled = docs.filter((d) => d.status === "cancelled").length;

  const docCols: Array<Column<IssuedDocument>> = [
    { key: "n", header: "Numéro", render: (d) => <>{d.documentNumber}</> },
    { key: "t", header: "Type", render: (d) => <Badge>{d.documentType}</Badge> },
    { key: "p", header: "Tiers", render: (d) => d.partyName ?? "—" },
    { key: "dt", header: "Date", render: (d) => d.issueDate },
    { key: "tot", header: "Total", numeric: true, render: (d) => <Money value={d.total} currency={d.currency} /> },
    { key: "s", header: "Statut", render: (d) => <StatePill status={d.status} /> },
    {
      key: "a", header: "", render: (d) => (
        <div style={{ display: "flex", gap: 6 }}>
          <a href={documentPdfUrl(d.id)} target="_blank" rel="noreferrer"
            style={{ color: "var(--nx-brand)", fontSize: 13, alignSelf: "center" }}>PDF</a>
          {canSend && d.status !== "cancelled" && (
            <Button size="sm" variant="subtle" disabled={busy}
              onClick={() => { const to = window.prompt("Destinataire (lien) ?", ""); run(() => sendDocument(d.id, "link", to, "Votre document")); }}>
              Envoyer
            </Button>
          )}
          {canCancel && d.status !== "cancelled" && d.status !== "replaced" && (
            <Button size="sm" variant="danger" disabled={busy}
              onClick={() => { const why = window.prompt("Motif d'annulation ?"); if (why) run(() => cancelDocument(d.id, why)); }}>
              Annuler
            </Button>
          )}
        </div>
      ),
    },
  ];

  const quoteCols: Array<Column<Quote>> = [
    { key: "n", header: "Numéro", render: (q) => q.documentNumber },
    { key: "c", header: "Client", render: (q) => q.customerName ?? "—" },
    { key: "d", header: "Date", render: (q) => q.quoteDate },
    { key: "v", header: "Validité", render: (q) => q.validUntil ?? "—" },
    { key: "tot", header: "Total", numeric: true, render: (q) => <Money value={q.total} currency={q.currency} /> },
    { key: "s", header: "Statut", render: (q) => <StatePill status={q.status} /> },
    {
      key: "a", header: "", render: (q) => (
        <div style={{ display: "flex", gap: 6 }}>
          {canQuote && q.status === "draft" && (
            <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(() => sendQuote(q.id))}>Envoyer</Button>
          )}
          {canQuote && q.status === "sent" && (
            <Button size="sm" variant="subtle" disabled={busy} onClick={() => run(() => acceptQuote(q.id))}>Accepter</Button>
          )}
          {canConvert && (q.status === "accepted" || q.status === "sent") && (
            <Button size="sm" disabled={busy} onClick={() => run(() => convertQuote(q.id))}>→ Commande</Button>
          )}
          {q.status === "converted" && <Badge tone="success">commande créée</Badge>}
        </div>
      ),
    },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Documents commerciaux</h1>
      {error && <div className="nx-error-text">{error}</div>}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Documents émis" value={String(issued)} />
        <KpiCard label="Annulés" value={String(cancelled)} tone={cancelled > 0 ? "warning" : undefined} />
        <KpiCard label="Devis" value={String(quotes.length)} />
        <KpiCard label="Devis en attente" value={String(quotes.filter((q) => q.status === "sent").length)} />
      </div>

      <Card>
        <Tabs
          tabs={[
            { id: "documents", label: `Documents (${docs.length})` },
            { id: "quotes", label: `Devis (${quotes.length})` },
            { id: "verify", label: "Vérifier" },
          ]}
          active={tab}
          onChange={setTab}
        />
        <div style={{ marginTop: 12 }}>
          {tab === "documents" && (
            <>
              <div style={{ marginBottom: 10 }}>
                <select value={typeFilter} onChange={(e) => setTypeFilter(e.target.value)}>
                  <option value="">Tous les types</option>
                  <option value="invoice">Factures</option>
                  <option value="credit_note">Avoirs</option>
                  <option value="receipt">Reçus</option>
                  <option value="delivery_note">Bons de livraison</option>
                  <option value="proforma">Proforma</option>
                </select>
                {numberFilter && (
                  <button type="button" className="nx-chip" aria-pressed="true" style={{ marginLeft: 8 }}
                          onClick={() => setParams({})} title="Retirer le filtre">
                    N° « {numberFilter} » ✕
                  </button>
                )}
              </div>
              <DataTable columns={docCols} rows={docs} rowKey={(d) => d.id} empty="Aucun document émis." />
            </>
          )}

          {tab === "quotes" && (
            <DataTable columns={quoteCols} rows={quotes} rowKey={(q) => q.id} empty="Aucun devis." />
          )}

          {tab === "verify" && (
            <div style={{ display: "grid", gap: 12, maxWidth: 480 }}>
              <SectionLabel>Vérification d'un document (§27-28)</SectionLabel>
              <div style={{ display: "flex", gap: 8 }}>
                <input
                  value={verifyCode}
                  onChange={(e) => setVerifyCode(e.target.value)}
                  placeholder="Code de vérification (QR)"
                  style={{ flex: 1 }}
                />
                <Button
                  disabled={busy || !verifyCode}
                  onClick={() => run(async () => setVerifyResult(await verifyDocument(verifyCode.trim())))}
                >
                  Vérifier
                </Button>
              </div>
              {verifyResult && (
                <Card>
                  {verifyResult.found ? (
                    <div style={{ display: "grid", gap: 4 }}>
                      <div><strong>{verifyResult.documentNumber}</strong> · {verifyResult.documentType}</div>
                      <div><StatePill status={verifyResult.status ?? ""} /> — {verifyResult.message}</div>
                      <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{verifyResult.companyName} · {verifyResult.issueDate}</div>
                      {verifyResult.total != null && (
                        <Money value={verifyResult.total} currency={verifyResult.currency ?? "HTG"} />
                      )}
                    </div>
                  ) : (
                    <div>{verifyResult.message}</div>
                  )}
                </Card>
              )}
            </div>
          )}
        </div>
      </Card>
    </div>
  );
}
