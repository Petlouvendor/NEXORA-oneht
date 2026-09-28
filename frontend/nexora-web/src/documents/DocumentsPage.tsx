import { useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, dateTime, money } from "@/shared/format";
import { Icon } from "@/shared/icons";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, IconButton, KpiCard, Modal, SectionLabel, Select, Tabs, TextInput,
  type Column,
} from "@/shared/ui";
import {
  cancelDocument, documentSends, DOCUMENT_TYPE_LABEL, openDocumentPdf, searchDocuments, sendDocument, verifyDocument,
  type DocumentVerification, type IssuedDocument,
} from "@/documents/documentsApi";

const STATUS_LABEL: Record<string, string> = { issued: "Émis", sent: "Envoyé", cancelled: "Annulé", replaced: "Remplacé" };
const STATUS_TONE: Record<string, "success" | "error" | "info" | undefined> = { issued: undefined, sent: "success", cancelled: "error", replaced: "error" };
const CHANNEL_LABEL: Record<string, string> = { email: "E-mail", sms: "SMS", link: "Lien", print: "Impression", download: "Téléchargement", portal: "Portail" };

/** Documents commerciaux émis (factures, avoirs, reçus, bons de livraison, proformas) : consulter, imprimer, envoyer, annuler, vérifier. */
export function DocumentsPage() {
  const { hasPermission } = useAuth();
  const [tab, setTab] = useState("documents");
  const [typeFilter, setTypeFilter] = useState("");
  const [statusFilter, setStatusFilter] = useState("");
  // ?q= : numéro de document venu de la recherche Ctrl+K (NEXORA ONE §5).
  const [params, setParams] = useSearchParams();
  const numberFilter = params.get("q") ?? "";
  const docs = useAsync(() => searchDocuments({ type: typeFilter || undefined, status: statusFilter || undefined, number: numberFilter || undefined }),
    [typeFilter, statusFilter, numberFilter]);
  const [openDoc, setOpenDoc] = useState<IssuedDocument | null>(null);

  if (!hasPermission("documents.view")) {
    return <Card>Les documents commerciaux ne sont pas accessibles avec vos permissions.</Card>;
  }

  const rows = docs.data ?? [];
  const invoices = rows.filter((d) => ["invoice", "deposit_invoice", "final_invoice"].includes(d.documentType) && d.status !== "cancelled" && d.status !== "replaced");
  const cancelled = rows.filter((d) => d.status === "cancelled").length;

  const cols: Array<Column<IssuedDocument>> = [
    { key: "n", header: "Numéro", render: (d) => (
      <button type="button" onClick={() => setOpenDoc(d)} style={{ appearance: "none", background: "transparent", border: "none", padding: 0,
        cursor: "pointer", color: "var(--nx-brand)", font: "inherit" }}><code>{d.documentNumber}</code></button>) },
    { key: "t", header: "Type", render: (d) => <Badge>{DOCUMENT_TYPE_LABEL[d.documentType] ?? d.documentType}</Badge> },
    { key: "p", header: "Tiers", render: (d) => d.partyName ?? "—" },
    { key: "dt", header: "Date", render: (d) => dateShort(d.issueDate) },
    { key: "tot", header: "Total", numeric: true, render: (d) => money(d.total, d.currency) },
    { key: "s", header: "Statut", render: (d) => <Badge tone={STATUS_TONE[d.status]}>{STATUS_LABEL[d.status] ?? d.status}</Badge> },
    { key: "a", header: "", render: (d) => <IconButton label={`Ouvrir ${d.documentNumber} (PDF)`} onClick={() => void openDocumentPdf(d.id)}><Icon.Printer size={14} /></IconButton> },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Documents commerciaux</h1>
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(150px, 1fr))", gap: 12 }}>
        <KpiCard label="Documents listés" value={String(rows.length)} />
        <KpiCard label="Factures valides" value={String(invoices.length)} />
        <KpiCard label="Annulés" value={String(cancelled)} tone={cancelled > 0 ? "warning" : undefined} />
      </div>
      <Card>
        <Tabs tabs={[{ id: "documents", label: "Documents émis" }, { id: "verify", label: "Vérifier un document" }]} active={tab} onChange={setTab} />
        <div style={{ marginTop: 12 }}>
          {tab === "documents" && (
            <>
              <div style={{ display: "flex", gap: 12, flexWrap: "wrap", alignItems: "flex-end", marginBottom: 10 }}>
                <div style={{ minWidth: 200 }}>
                  <Field label="Type">
                    <Select value={typeFilter} onChange={(e) => setTypeFilter(e.target.value)}>
                      <option value="">Tous les types</option>
                      {Object.entries(DOCUMENT_TYPE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                    </Select>
                  </Field>
                </div>
                <div style={{ minWidth: 160 }}>
                  <Field label="Statut">
                    <Select value={statusFilter} onChange={(e) => setStatusFilter(e.target.value)}>
                      <option value="">Tous</option>
                      {Object.entries(STATUS_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
                    </Select>
                  </Field>
                </div>
                {numberFilter && (
                  <button type="button" className="nx-chip" aria-pressed="true" onClick={() => setParams({})} title="Retirer le filtre">
                    N° « {numberFilter} » ✕
                  </button>
                )}
                <span style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
                  Les factures s'émettent depuis une vente, les devis et commandes depuis <Link to="/orders">Ventes</Link>.
                </span>
              </div>
              {docs.error && <div role="alert" className="nx-error-text">{docs.error}</div>}
              <DataTable exportTitle="Documents commerciaux" columns={cols} rows={rows} rowKey={(d) => d.id}
                         empty={docs.loading ? "Chargement…" : "Aucun document émis."} />
            </>
          )}
          {tab === "verify" && <VerifyPanel />}
        </div>
      </Card>
      {openDoc && <DocumentDrawer key={openDoc.id} doc={openDoc} onClose={() => setOpenDoc(null)} onChanged={() => { setOpenDoc(null); docs.reload(); }} />}
    </div>
  );
}

function DocumentDrawer({ doc, onClose, onChanged }: { doc: IssuedDocument; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const sends = useAsync(() => documentSends(doc.id), [doc.id]);
  const [dialog, setDialog] = useState<"send" | "cancel" | null>(null);
  const { error, submit } = useSubmit();
  const closed = doc.status === "cancelled" || doc.status === "replaced";
  const sourceLink = doc.referenceType === "sale" && doc.referenceId ? `/sales/${doc.referenceId}`
    : doc.referenceType === "sales_order" && doc.referenceId ? `/orders?tab=orders&order=${doc.referenceId}` : null;

  return (
    <Drawer title={`${DOCUMENT_TYPE_LABEL[doc.documentType] ?? "Document"} ${doc.documentNumber}`} onClose={onClose}>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
          <Badge tone={STATUS_TONE[doc.status]}>{STATUS_LABEL[doc.status] ?? doc.status}</Badge>
          <span>{doc.partyName ?? "—"}</span>
          <span style={{ color: "var(--nx-text-2)" }}>· {dateShort(doc.issueDate)}</span>
        </div>
        <div>Total : <b>{money(doc.total, doc.currency)}</b> (HT {money(doc.totalNet, doc.currency)}, taxes {money(doc.totalTax, doc.currency)})</div>
        {doc.verificationCode && <div>Code de vérification : <code>{doc.verificationCode}</code></div>}
        {sourceLink && <div>Pièce d'origine : <Link to={sourceLink}>ouvrir</Link></div>}
        {doc.cancelReason && <div className="nx-error-text">Motif d'annulation : {doc.cancelReason}</div>}
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          <Button size="sm" onClick={() => void openDocumentPdf(doc.id)}><Icon.Printer size={14} /> PDF A4</Button>
          <Button size="sm" variant="ghost" onClick={() => void openDocumentPdf(doc.id, "ticket")}>Ticket 80 mm</Button>
          {hasPermission("documents.send") && !closed && <Button size="sm" variant="subtle" onClick={() => setDialog("send")}>Envoyer</Button>}
          {hasPermission("documents.cancel") && !closed && <Button size="sm" variant="ghost" onClick={() => setDialog("cancel")}>Annuler le document</Button>}
        </div>
        <SectionLabel>Envois</SectionLabel>
        <DataTable
          columns={[
            { key: "c", header: "Canal", render: (s) => CHANNEL_LABEL[s.channel] ?? s.channel },
            { key: "r", header: "Destinataire", render: (s) => s.recipient ?? "—" },
            { key: "s", header: "Statut", render: (s) => (s.status === "queued" ? <Badge tone="warning">en file (pas de connecteur)</Badge> : <Badge>{s.status}</Badge>) },
            { key: "d", header: "Date", render: (s) => dateTime(s.sentAt ?? s.createdAt) },
          ]}
          rows={sends.data ?? []} rowKey={(s) => s.id} empty={sends.loading ? "Chargement…" : "Aucun envoi."} />
      </div>
      {dialog === "send" && <SendDialog doc={doc} onClose={() => setDialog(null)} onDone={() => { setDialog(null); sends.reload(); }} />}
      {dialog === "cancel" && (
        <ConfirmDialog title={`Annuler ${doc.documentNumber}`} danger requireReason confirmLabel="Annuler le document"
          message="Le document reste consultable avec la mention ANNULÉ ; son numéro n'est jamais réutilisé."
          onCancel={() => setDialog(null)}
          onConfirm={async (reason) => {
            setDialog(null);
            const ok = await submit(async () => { await cancelDocument(doc.id, reason); return true; });
            if (ok) { toast.success("Document annulé."); onChanged(); }
          }} />
      )}
    </Drawer>
  );
}

function SendDialog({ doc, onClose, onDone }: { doc: IssuedDocument; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [channel, setChannel] = useState("email");
  const [recipient, setRecipient] = useState("");
  const [subject, setSubject] = useState(`${DOCUMENT_TYPE_LABEL[doc.documentType] ?? "Document"} ${doc.documentNumber}`);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "document.send_channel": "channel" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const to = recipient.trim();
    if ((channel === "email" || channel === "sms") && !to) return fail("recipient", "Destinataire obligatoire.");
    if (channel === "email" && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(to)) return fail("recipient", "Adresse e-mail invalide.");
    const ok = await submit(async () => { await sendDocument(doc.id, channel, to || null, subject.trim() || null); return true; });
    if (ok) {
      toast.success(channel === "email" || channel === "sms" ? "Envoi enregistré (mis en file tant qu'aucun connecteur n'est configuré)." : "Envoi enregistré.");
      onDone();
    }
  }

  return (
    <Modal title={`Envoyer ${doc.documentNumber}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Envoyer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Canal" error={fieldErrors.channel}>
          <Select value={channel} onChange={(e) => setChannel(e.target.value)}>
            {Object.entries(CHANNEL_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Destinataire" required={channel === "email" || channel === "sms"} error={fieldErrors.recipient}>
          <TextInput value={recipient} placeholder={channel === "sms" ? "+509…" : "client@exemple.com"} onChange={(e) => setRecipient(e.target.value)} />
        </Field>
        <Field label="Objet"><TextInput value={subject} maxLength={200} onChange={(e) => setSubject(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function VerifyPanel() {
  const [code, setCode] = useState("");
  const [result, setResult] = useState<DocumentVerification | null>(null);
  const { busy, error, submit } = useSubmit();
  async function verify(e?: FormEvent) {
    e?.preventDefault();
    const r = await submit(() => verifyDocument(code.trim()));
    if (r) setResult(r);
  }
  return (
    <form onSubmit={verify} style={{ display: "grid", gap: 12, maxWidth: 480 }}>
      <SectionLabel>Vérification d'un document (code imprimé / QR)</SectionLabel>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <div style={{ display: "flex", gap: 8, alignItems: "flex-end" }}>
        <div style={{ flex: 1 }}>
          <Field label="Code de vérification"><TextInput value={code} onChange={(e) => setCode(e.target.value)} /></Field>
        </div>
        <Button type="submit" disabled={busy || !code.trim()}>Vérifier</Button>
      </div>
      {result && (
        <Card>
          {result.found ? (
            <div style={{ display: "grid", gap: 4 }}>
              <div><strong>{result.documentNumber}</strong> · {DOCUMENT_TYPE_LABEL[result.documentType ?? ""] ?? result.documentType}</div>
              <div><Badge tone={STATUS_TONE[result.status ?? ""]}>{STATUS_LABEL[result.status ?? ""] ?? result.status}</Badge> — {result.message}</div>
              <div style={{ color: "var(--nx-text-2)", fontSize: 12 }}>{result.companyName} · {result.issueDate ? dateShort(result.issueDate) : ""}</div>
              {result.total != null && <div>{money(result.total, result.currency ?? "")}</div>}
            </div>
          ) : <div>{result.message}</div>}
        </Card>
      )}
    </form>
  );
}
