import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { Badge, Button, Card, DataTable, SectionLabel, TextInput, type Column } from "@/shared/ui";
import {
  approveDocument, archiveDocument, confirmDeletion, createDocument, declineAs, lockDocument,
  listEligibleForDeletion, listGedAudit, listRetentionPolicies, listSignatureRequests, listWorkflowQueue,
  readFileAsBase64, rejectDocument, requestDeletion, restoreDocument, runOcr,
  saveRetentionPolicy, searchDocuments, setLegalHold, shareDocument, signAs, submitForReview, unlockDocument,
  type GedAuditEntry, type GedDocument, type RetentionPolicy, type SignatureRequest,
} from "@/ged/gedApi";

// GED, documents, archivage & signature (Phase 45 §3-6, §13-31, §39, §45).

function useAsync<T>(loader: () => Promise<T>) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const load = useCallback(() => {
    setError(null);
    loader().then(setData).catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);
  useEffect(load, [load]);
  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setError(null);
    try { await fn(); load(); } catch (e) { setError(e instanceof ApiError ? e.message : "Erreur"); }
    finally { setBusy(false); }
  }
  return { data, error, busy, load, run };
}

const statusTone = (s: string) => s === "signed" || s === "approved" ? "success" : s === "archived" ? undefined
  : s === "pending_review" ? "warning" : s === "frozen" ? "error" : undefined;

const DOCUMENT_TYPES = [
  "invoice_sale", "invoice_purchase", "receipt", "credit_note", "quote", "purchase_order", "delivery_note",
  "contract", "accounting_voucher", "expense_proof", "statement", "tax_document", "customer_document",
  "supplier_document", "internal", "photo", "scan", "other",
];

// --- §7, §13-14, §16 Bibliothèque documentaire ---------------------------------

export function DocumentsSection({ canUpload, canManage }: { canUpload: boolean; canManage: boolean }) {
  const [text, setText] = useState("");
  const [type, setType] = useState("");
  const docs = useAsync(() => searchDocuments({ text: text || undefined, documentType: type || undefined }));
  const fileInput = useRef<HTMLInputElement>(null);
  const [name, setName] = useState("");
  const [newType, setNewType] = useState("internal");

  async function upload() {
    const file = fileInput.current?.files?.[0];
    if (!file || !name.trim()) { alert("Nom et fichier requis."); return; }
    const { base64, contentType } = await readFileAsBase64(file);
    await docs.run(async () => {
      const result = await createDocument({ name: name.trim(), documentType: newType, contentBase64: base64, contentType });
      if (result.potentialDuplicateOf) alert(`Attention : un document identique existe déjà (§39) — id ${result.potentialDuplicateOf}`);
    });
    setName(""); if (fileInput.current) fileInput.current.value = "";
  }

  const cols: Array<Column<GedDocument>> = [
    { key: "n", header: "Nom", render: (d) => <><strong>{d.name}</strong><div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>{d.documentType}{d.category ? ` · ${d.category}` : ""}</div></> },
    { key: "s", header: "Statut", render: (d) => <Badge tone={statusTone(d.status)}>{d.status}{d.locked ? " 🔒" : ""}{d.legalHold ? " ⚖" : ""}</Badge> },
    { key: "v", header: "Version", numeric: true, render: (d) => d.currentVersionNo ?? "—" },
    { key: "r", header: "Rétention", render: (d) => d.retentionUntil ?? "—" },
    { key: "a", header: "", render: (d) => canManage && (
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        {d.status === "draft" && <Button size="sm" variant="ghost" onClick={() => docs.run(() => submitForReview(d.id))}>Soumettre</Button>}
        {d.status === "pending_review" && <Button size="sm" variant="ghost" onClick={() => docs.run(() => approveDocument(d.id))}>Approuver</Button>}
        {d.status !== "archived" && <Button size="sm" variant="ghost" onClick={() => docs.run(() => archiveDocument(d.id))}>Archiver</Button>}
        {d.status === "archived" && <Button size="sm" variant="ghost" onClick={() => docs.run(() => restoreDocument(d.id))}>Restaurer</Button>}
        {!d.locked
          ? <Button size="sm" variant="ghost" onClick={() => { const r = prompt("Motif de verrouillage :"); if (r) void docs.run(() => lockDocument(d.id, r)); }}>Verrouiller</Button>
          : <Button size="sm" variant="ghost" onClick={() => docs.run(() => unlockDocument(d.id))}>Déverrouiller</Button>}
        <Button size="sm" variant="ghost" onClick={() => docs.run(() => runOcr(d.id))}>OCR</Button>
        <Button size="sm" variant="ghost" onClick={() => {
          const reason = d.legalHold ? undefined : (prompt("Motif du legal hold (§29) :") ?? undefined);
          if (!d.legalHold && !reason) return;
          void docs.run(() => setLegalHold(d.id, !d.legalHold, reason));
        }}>{d.legalHold ? "Lever le legal hold" : "Legal hold"}</Button>
        <Button size="sm" variant="ghost" onClick={async () => {
          const share = await shareDocument(d.id, { expiresAt: new Date(Date.now() + 24 * 3600 * 1000).toISOString(), allowDownload: true });
          prompt("Lien de partage (24h) :", `${window.location.origin}/api/v1/shared/${share.token}`);
        }}>Partager</Button>
      </div>
    ) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {canUpload && (
        <Card>
          <SectionLabel>Importer un document (§7-9)</SectionLabel>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <TextInput placeholder="Nom du document" value={name} onChange={(e) => setName(e.target.value)} />
            <select value={newType} onChange={(e) => setNewType(e.target.value)} className="nx-select">
              {DOCUMENT_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
            </select>
            <input ref={fileInput} type="file" accept=".pdf,.jpg,.jpeg,.png,.webp,.tiff,.docx,.xlsx,.csv" />
            <Button size="sm" onClick={upload} disabled={docs.busy}>Importer</Button>
          </div>
        </Card>
      )}
      <Card>
        <SectionLabel>Bibliothèque documentaire (§13-14)</SectionLabel>
        <div style={{ display: "flex", gap: 8, marginBottom: 10 }}>
          <TextInput placeholder="Recherche (nom, catégorie, texte OCR)" value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && docs.load()} />
          <select value={type} onChange={(e) => setType(e.target.value)} className="nx-select">
            <option value="">Tous types</option>
            {DOCUMENT_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
          </select>
          <Button size="sm" variant="ghost" onClick={docs.load}>Rechercher</Button>
        </div>
        {docs.error && <div className="nx-error-text">{docs.error}</div>}
        <DataTable columns={cols} rows={docs.data ?? []} rowKey={(d) => d.id} empty="Aucun document." />
      </Card>
    </div>
  );
}

// --- §23-24 Workflow --------------------------------------------------------------

export function WorkflowSection({ canManage }: { canManage: boolean }) {
  const queue = useAsync(listWorkflowQueue);
  const cols: Array<Column<GedDocument>> = [
    { key: "n", header: "Document", render: (d) => d.name },
    { key: "t", header: "Type", render: (d) => d.documentType },
    { key: "a", header: "", render: (d) => canManage && (
      <div style={{ display: "flex", gap: 6 }}>
        <Button size="sm" onClick={() => queue.run(() => approveDocument(d.id))}>Approuver</Button>
        <Button size="sm" variant="ghost" onClick={() => { const r = prompt("Motif de rejet :"); if (r) void queue.run(() => rejectDocument(d.id, r)); }}>Rejeter</Button>
      </div>
    ) },
  ];
  return (
    <Card>
      <SectionLabel>Documents en revue (§23-24)</SectionLabel>
      {queue.error && <div className="nx-error-text">{queue.error}</div>}
      <DataTable columns={cols} rows={queue.data ?? []} rowKey={(d) => d.id} empty="Rien en attente de revue." />
    </Card>
  );
}

// --- §19-22 Signature --------------------------------------------------------------

export function SignaturesSection({ canSign }: { canSign: boolean }) {
  const requests = useAsync(() => listSignatureRequests());
  return (
    <div style={{ display: "grid", gap: 16 }}>
      {(requests.data ?? []).map((r: SignatureRequest) => (
        <Card key={r.id}>
          <SectionLabel>Demande {r.id.slice(0, 8)} · <Badge tone={statusTone(r.status)}>{r.status}</Badge></SectionLabel>
          <ul style={{ margin: 0, paddingLeft: 18 }}>
            {r.signers.map((s) => (
              <li key={s.id}>
                #{s.sequenceNo} {s.externalName ?? s.userId ?? "?"} — <Badge tone={s.status === "signed" ? "success" : s.status === "declined" ? "error" : undefined}>{s.status}</Badge>
                {canSign && s.status === "pending" && (
                  <span style={{ marginLeft: 8 }}>
                    <Button size="sm" onClick={() => requests.run(() => signAs(r.id, s.id))}>Signer</Button>{" "}
                    <Button size="sm" variant="ghost" onClick={() => { const reason = prompt("Motif du refus :"); if (reason) void requests.run(() => declineAs(r.id, s.id, reason)); }}>Refuser</Button>
                  </span>
                )}
              </li>
            ))}
          </ul>
        </Card>
      ))}
      {requests.error && <Card><div className="nx-error-text">{requests.error}</div></Card>}
      {(requests.data ?? []).length === 0 && !requests.error && <Card>Aucune demande de signature.</Card>}
    </div>
  );
}

// --- §27-29 Rétention & suppression contrôlée --------------------------------------

export function RetentionSection({ canManage }: { canManage: boolean }) {
  const policies = useAsync(listRetentionPolicies);
  const eligible = useAsync(listEligibleForDeletion);

  const policyCols: Array<Column<RetentionPolicy>> = [
    { key: "t", header: "Type", render: (p) => p.documentType ?? "(tous types)" },
    { key: "s", header: "Portée", render: (p) => <Badge tone={p.isSystemDefault ? undefined : "info"}>{p.isSystemDefault ? "système" : "entreprise"}</Badge> },
    { key: "y", header: "Durée", numeric: true, render: (p) => `${p.retentionYears} an(s)` },
    { key: "l", header: "Base légale", render: (p) => <span style={{ color: "var(--nx-text-2)" }}>{p.legalBasis}</span> },
  ];
  const eligibleCols: Array<Column<GedDocument>> = [
    { key: "n", header: "Document", render: (d) => d.name },
    { key: "r", header: "Rétention échue le", render: (d) => d.retentionUntil },
    { key: "a", header: "", render: (d) => canManage && (
      d.deletionRequestedAt
        ? <Button size="sm" onClick={() => eligible.run(() => confirmDeletion(d.id))}>Confirmer suppression</Button>
        : <Button size="sm" variant="ghost" onClick={() => { const r = prompt("Motif :"); if (r) void eligible.run(() => requestDeletion(d.id, r)); }}>Demander suppression</Button>
    ) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={canManage && (
          <Button size="sm" onClick={() => {
            const type = prompt("Type de document (vide = tous types) :") || null;
            const years = Number(prompt("Durée de conservation (années) :", "5") ?? "5");
            const legalBasis = prompt("Base légale / motif :", "Politique interne") ?? "";
            void policies.run(() => saveRetentionPolicy({ documentType: type, retentionYears: years, legalBasis }));
          }}>Nouvelle politique</Button>
        )}>Politiques de rétention (§28)</SectionLabel>
        {policies.error && <div className="nx-error-text">{policies.error}</div>}
        <DataTable columns={policyCols} rows={policies.data ?? []} rowKey={(p) => p.id} empty="—" />
      </Card>
      <Card>
        <SectionLabel>Documents éligibles à la suppression (§27, §30)</SectionLabel>
        {eligible.error && <div className="nx-error-text">{eligible.error}</div>}
        <DataTable columns={eligibleCols} rows={eligible.data ?? []} rowKey={(d) => d.id} empty="Aucun document éligible." />
      </Card>
    </div>
  );
}

// --- §45 Audit ----------------------------------------------------------------------

export function GedAuditSection() {
  const audit = useAsync(() => listGedAudit());
  const cols: Array<Column<GedAuditEntry>> = [
    { key: "t", header: "Table", render: (a) => <code>{a.tableName}</code> },
    { key: "a", header: "Action", render: (a) => a.action },
    { key: "d", header: "Date", render: (a) => new Date(a.occurredAt).toLocaleString() },
  ];
  return (
    <Card>
      <SectionLabel>Journal d'audit GED (§45)</SectionLabel>
      {audit.error && <div className="nx-error-text">{audit.error}</div>}
      <DataTable columns={cols} rows={audit.data ?? []} rowKey={(a) => a.id} empty="—" />
    </Card>
  );
}
