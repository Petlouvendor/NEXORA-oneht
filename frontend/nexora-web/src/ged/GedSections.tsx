import { useState } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, dateTime, number } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  DOCUMENT_STATUS, DOCUMENT_TYPES, REQUEST_STATUS, SIGNER_STATUS, approveDocument, archiveDocument, cancelDeletion, cancelSignature,
  confirmDeletion, declineAs, downloadVersion, getDocument, listEligibleForDeletion, listGedAudit, listRetentionPolicies,
  listSignatureRequests, listVersions, listWorkflowQueue, lockDocument, rejectDocument, requestDeletion, restoreDocument, runOcr,
  searchDocuments, setLegalHold, signerName, submitForReview, unlockDocument,
  type GedDocument, type SignatureRequest,
} from "@/ged/gedApi";
import { NewVersionDialog, RetentionPolicyDialog, ShareDialog, SignDialog, SignatureRequestDialog, UploadDialog } from "@/ged/GedDialogs";

const linkStyle = { appearance: "none", background: "transparent", border: "none", padding: 0, cursor: "pointer",
  color: "var(--nx-brand)", font: "inherit", textAlign: "left" } as const;
const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const kb = (n: number) => (n < 1024 * 1024 ? `${number(n / 1024, 0)} Ko` : `${number(n / 1048576, 1)} Mo`);

function StatusBadge({ d }: { d: GedDocument }) {
  const s = DOCUMENT_STATUS[d.status];
  return <>
    <Badge tone={s?.tone}>{s?.label ?? d.status}</Badge>
    {d.locked && <> <Badge>Verrouillé</Badge></>}
    {d.legalHold && <> <Badge tone="error">Conservation légale</Badge></>}
  </>;
}

// ------------------------------------------------------------------ Bibliothèque

export function LibrarySection({ onChanged }: { onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [text, setText] = useState("");
  const [term, setTerm] = useState("");
  const [type, setType] = useState("");
  const [status, setStatus] = useState("");
  const [version, setVersion] = useState(0);
  const docs = useAsync(() => searchDocuments({ text: term || undefined, documentType: type || undefined, status: status || undefined }),
    [term, type, status, version]);
  const [uploading, setUploading] = useState(false);
  const [openId, setOpenId] = useState<string | null>(null);
  const changed = () => { setVersion((v) => v + 1); onChanged(); };

  return (
    <Card>
      <SectionLabel action={hasPermission("ged.upload") && <Button size="sm" onClick={() => setUploading(true)}>Importer un document</Button>}>
        Bibliothèque
      </SectionLabel>
      <form onSubmit={(e) => { e.preventDefault(); setTerm(text.trim()); }}
        style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end", marginBottom: 10 }}>
        <Field label="Recherche"><TextInput value={text} placeholder="Nom, catégorie, texte" onChange={(e) => setText(e.target.value)} /></Field>
        <Field label="Type">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(DOCUMENT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(DOCUMENT_STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </Select>
        </Field>
        <Button type="submit" size="sm" variant="subtle">Rechercher</Button>
      </form>
      {docs.error && <div role="alert" className="nx-error-text">{docs.error}</div>}
      <DataTable exportTitle="Bibliothèque documentaire"
        columns={[
          { key: "n", header: "Document", render: (d) => <button type="button" style={linkStyle} onClick={() => setOpenId(d.id)}>{d.name}</button> },
          { key: "t", header: "Type", render: (d) => DOCUMENT_TYPES[d.documentType] ?? d.documentType },
          { key: "c", header: "Catégorie", render: (d) => d.category ?? "—" },
          { key: "s", header: "Statut", render: (d) => <StatusBadge d={d} /> },
          { key: "v", header: "Version", numeric: true, render: (d) => d.currentVersionNo ?? "—" },
          { key: "u", header: "Modifié", render: (d) => dateShort(d.updatedAt) },
        ]}
        rows={docs.data ?? []} rowKey={(d) => d.id} empty={docs.loading ? "Chargement…" : "Aucun document."} />
      {uploading && <UploadDialog onClose={() => setUploading(false)} onDone={(d) => { setUploading(false); changed(); setOpenId(d.id); }} />}
      {openId && <DocumentDrawer key={openId} id={openId} onClose={() => setOpenId(null)} onChanged={changed} />}
    </Card>
  );
}

type DocDialog = "version" | "signature" | "share" | "lock" | "unlock" | "reject" | "hold" | "unhold" | "deletion"
  | { kind: "sign"; request: SignatureRequest } | { kind: "decline"; request: SignatureRequest } | { kind: "cancel"; request: SignatureRequest };

/** Fiche document : versions (téléchargeables), cycle de vie, signatures, audit. */
export function DocumentDrawer({ id, onClose, onChanged }: { id: string; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const doc = useAsync(() => getDocument(id), [id, version]);
  const versions = useAsync(() => listVersions(id), [id, version]);
  const requests = useAsync(() => listSignatureRequests({ documentId: id }), [id, version]);
  const audit = useAsync(() => listGedAudit(id), [id, version]);
  const [dialog, setDialog] = useState<DocDialog | null>(null);
  const { busy, error, submit } = useSubmit();
  const d = doc.data;
  const refresh = () => { setDialog(null); setVersion((v) => v + 1); onChanged(); };
  const act = async (fn: () => Promise<unknown>, done: string) => {
    if (await submit(() => fn().then(() => true))) { toast.success(done); refresh(); }
  };
  const can = { manage: hasPermission("ged.manage"), upload: hasPermission("ged.upload"), sign: hasPermission("ged.sign"),
    retention: hasPermission("ged.manage_retention") };
  const pending = (requests.data ?? []).find((r) => r.status === "pending");

  return (
    <Drawer title={d ? d.name : "Document"} onClose={onClose}>
      {doc.error && <div role="alert" className="nx-error-text">{doc.error}</div>}
      {d && (
        <div style={{ display: "grid", gap: 14 }}>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <StatusBadge d={d} /><span>{DOCUMENT_TYPES[d.documentType] ?? d.documentType}{d.category ? ` · ${d.category}` : ""}</span>
          </div>
          {d.locked && d.lockedReason && <div style={hint}>Verrou : {d.lockedReason}</div>}
          {d.legalHold && <div style={hint}>Conservation légale : {d.legalHoldReason}</div>}
          {d.retentionUntil && <div style={hint}>Conservation obligatoire jusqu'au {dateShort(d.retentionUntil)}.</div>}
          {d.deletionRequestedAt && <div className="nx-error-text">Suppression demandée le {dateShort(d.deletionRequestedAt)} — en attente de confirmation par une autre personne.</div>}

          <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
            <Button size="sm" variant="subtle" disabled={busy} onClick={() => submit(() => downloadVersion(d))}>Télécharger</Button>
            {can.upload && !d.locked && d.status !== "archived" && <Button size="sm" variant="subtle" onClick={() => setDialog("version")}>Nouvelle version</Button>}
            {can.sign && !pending && ["draft", "approved"].includes(d.status) && !d.deletionRequestedAt &&
              <Button size="sm" onClick={() => setDialog("signature")}>Demander une signature</Button>}
            {can.manage && d.status === "draft" && <Button size="sm" variant="ghost" disabled={busy}
              onClick={() => act(() => submitForReview(d.id), "Document soumis à la revue.")}>Soumettre à la revue</Button>}
            {can.manage && d.status === "pending_review" && <>
              <Button size="sm" variant="ghost" disabled={busy} onClick={() => act(() => approveDocument(d.id), "Document approuvé.")}>Approuver</Button>
              <Button size="sm" variant="ghost" onClick={() => setDialog("reject")}>Rejeter</Button>
            </>}
            {can.manage && <Button size="sm" variant="ghost" onClick={() => setDialog("share")}>Partager</Button>}
            {can.manage && !d.locked && d.status !== "signed" && <Button size="sm" variant="ghost" onClick={() => setDialog("lock")}>Verrouiller</Button>}
            {can.manage && d.locked && d.status !== "signed" && !pending && <Button size="sm" variant="ghost" onClick={() => setDialog("unlock")}>Déverrouiller</Button>}
            {can.manage && d.status !== "archived" && !pending && <Button size="sm" variant="ghost" disabled={busy}
              onClick={() => act(() => archiveDocument(d.id), "Document archivé.")}>Archiver</Button>}
            {can.manage && d.status === "archived" && <Button size="sm" variant="ghost" disabled={busy}
              onClick={() => act(() => restoreDocument(d.id), "Document restauré.")}>Restaurer</Button>}
            {can.upload && <Button size="sm" variant="ghost" disabled={busy}
              onClick={async () => {
                const r = await submit(() => runOcr(d.id));
                if (r) toast.push(r.status === "completed" ? "Texte extrait : le document est trouvable par son contenu."
                  : "Extraction de texte non disponible pour ce format (CSV uniquement).", r.status === "completed" ? "success" : "warning");
              }}>Extraire le texte</Button>}
            {can.retention && (d.legalHold
              ? <Button size="sm" variant="ghost" onClick={() => setDialog("unhold")}>Lever la conservation légale</Button>
              : <Button size="sm" variant="ghost" onClick={() => setDialog("hold")}>Conservation légale</Button>)}
            {can.retention && !d.deletionRequestedAt && !d.legalHold && <Button size="sm" variant="danger" onClick={() => setDialog("deletion")}>Demander la suppression</Button>}
          </div>

          <SectionLabel>Versions</SectionLabel>
          <DataTable exportTitle={`Versions — ${d.name}`}
            columns={[
              { key: "v", header: "Version", render: (v) => <>v{v.versionNo}{v.versionNo === d.currentVersionNo && <> <Badge tone="info">courante</Badge></>}</> },
              { key: "d", header: "Date", render: (v) => dateTime(v.createdAt) },
              { key: "r", header: "Motif", render: (v) => v.changeReason },
              { key: "s", header: "Taille", numeric: true, render: (v) => kb(v.sizeBytes) },
              { key: "h", header: "Empreinte", render: (v) => <code title={v.sha256Hash}>{v.sha256Hash.slice(0, 12)}…</code> },
              { key: "x", header: "", render: (v) => <Button size="sm" variant="ghost" onClick={() => submit(() => downloadVersion(d, v.versionNo))}>Télécharger</Button> },
            ]}
            rows={versions.data ?? []} rowKey={(v) => v.id} empty={versions.loading ? "Chargement…" : "—"} />

          <SectionLabel>Signatures</SectionLabel>
          {(requests.data ?? []).length === 0 && <div style={hint}>Aucune demande de signature.</div>}
          {(requests.data ?? []).map((r) => (
            <RequestCard key={r.id} request={r}
              onSign={() => setDialog({ kind: "sign", request: r })}
              onDecline={() => setDialog({ kind: "decline", request: r })}
              onCancel={() => setDialog({ kind: "cancel", request: r })} />
          ))}

          <SectionLabel>Historique</SectionLabel>
          <DataTable exportTitle={`Historique — ${d.name}`}
            columns={[
              { key: "d", header: "Date", render: (a) => dateTime(a.occurredAt) },
              { key: "a", header: "Action", render: (a) => AUDIT_ACTION[a.action] ?? a.action },
              { key: "t", header: "Élément", render: (a) => AUDIT_TABLE[a.tableName] ?? a.tableName },
            ]}
            rows={audit.data ?? []} rowKey={(a) => a.id} empty="—" />
        </div>
      )}

      {d && dialog === "version" && <NewVersionDialog doc={d} onClose={() => setDialog(null)} onDone={refresh} />}
      {d && dialog === "signature" && <SignatureRequestDialog doc={d} onClose={() => setDialog(null)} onDone={refresh} />}
      {d && dialog === "share" && <ShareDialog doc={d} onClose={() => setDialog(null)} />}
      {d && dialog === "lock" && <ConfirmDialog title="Verrouiller le document" requireReason confirmLabel="Verrouiller"
        message="Un document verrouillé ne reçoit plus de nouvelle version ni de modification." onCancel={() => setDialog(null)}
        onConfirm={(r) => act(() => lockDocument(d.id, r), "Document verrouillé.")} />}
      {d && dialog === "unlock" && <ConfirmDialog title="Déverrouiller le document" confirmLabel="Déverrouiller"
        message={`Verrou actuel : ${d.lockedReason ?? "—"}.`} onCancel={() => setDialog(null)}
        onConfirm={() => act(() => unlockDocument(d.id), "Document déverrouillé.")} />}
      {d && dialog === "reject" && <ConfirmDialog title="Rejeter le document" requireReason confirmLabel="Rejeter" danger
        message="Le document retourne en brouillon ; le motif est conservé." onCancel={() => setDialog(null)}
        onConfirm={(r) => act(() => rejectDocument(d.id, r), "Document rejeté.")} />}
      {d && dialog === "hold" && <ConfirmDialog title="Mettre sous conservation légale" requireReason confirmLabel="Confirmer"
        message="Le document ne pourra plus être supprimé, même après sa durée de rétention, tant que la conservation n'est pas levée."
        onCancel={() => setDialog(null)} onConfirm={(r) => act(() => setLegalHold(d.id, true, r), "Conservation légale posée.")} />}
      {d && dialog === "unhold" && <ConfirmDialog title="Lever la conservation légale" confirmLabel="Lever"
        message={`Motif de la conservation : ${d.legalHoldReason ?? "—"}.`} onCancel={() => setDialog(null)}
        onConfirm={() => act(() => setLegalHold(d.id, false, null), "Conservation légale levée.")} />}
      {d && dialog === "deletion" && <ConfirmDialog title="Demander la suppression" requireReason confirmLabel="Demander" danger
        message="Une autre personne habilitée devra confirmer. Refusé tant que la durée de rétention court." onCancel={() => setDialog(null)}
        onConfirm={(r) => act(() => requestDeletion(d.id, r), "Suppression demandée.")} />}
      <SignatureActions dialog={typeof dialog === "object" ? dialog : null} onClose={() => setDialog(null)} onDone={refresh} />
    </Drawer>
  );
}

const AUDIT_ACTION: Record<string, string> = { INSERT: "Création", UPDATE: "Modification", DELETE: "Suppression", insert: "Création", update: "Modification", delete: "Suppression" };
const AUDIT_TABLE: Record<string, string> = {
  document: "Document", document_version: "Version", document_signature_request: "Demande de signature", document_signer: "Signataire",
  document_share: "Partage", document_link: "Lien",
};

/** Une demande de signature : signataires dans l'ordre, tour en cours, actions possibles. */
function RequestCard({ request: r, onSign, onDecline, onCancel }: {
  request: SignatureRequest; onSign: () => void; onDecline: () => void; onCancel: () => void;
}) {
  const { user, hasPermission } = useAuth();
  const canCancel = r.status === "pending" && (r.requestedById === user?.id || hasPermission("ged.manage"));
  return (
    <div style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 6 }}>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
        <Badge tone={REQUEST_STATUS[r.status]?.tone}>{REQUEST_STATUS[r.status]?.label ?? r.status}</Badge>
        <span style={hint}>v{r.versionNo} · demandé par {r.requestedByName ?? "—"} le {dateTime(r.createdAt)}</span>
      </div>
      {r.message && <div style={hint}>« {r.message} »</div>}
      <ol style={{ margin: 0, paddingLeft: 20 }}>
        {r.signers.map((s) => (
          <li key={s.id} style={{ fontWeight: s.id === r.currentSignerId ? 600 : 400 }}>
            {signerName(s)}{s.role ? ` — ${s.role}` : ""} : <Badge tone={SIGNER_STATUS[s.status]?.tone}>{SIGNER_STATUS[s.status]?.label ?? s.status}</Badge>
            {s.signedAt && <span style={hint}> le {dateTime(s.signedAt)}{s.recordedByName ? ` (recueillie par ${s.recordedByName})` : ""}</span>}
            {s.declinedReason && <span style={hint}> — « {s.declinedReason} »</span>}
            {s.status === "pending" && s.dueAt && <span style={hint}> · avant le {dateShort(s.dueAt)}</span>}
            {s.id === r.currentSignerId && <span style={hint}> ← à son tour</span>}
          </li>
        ))}
      </ol>
      {r.cancelledReason && <div style={hint}>{r.cancelledReason}{r.cancelledByName ? ` (${r.cancelledByName})` : ""}</div>}
      {(r.isMyTurn || canCancel) && (
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {r.isMyTurn && <Button size="sm" onClick={onSign}>Signer</Button>}
          {r.isMyTurn && <Button size="sm" variant="ghost" onClick={onDecline}>Refuser</Button>}
          {canCancel && <Button size="sm" variant="ghost" onClick={onCancel}>Annuler la demande</Button>}
        </div>
      )}
    </div>
  );
}

/** Dialogues de signature partagés par la fiche document et l'onglet Signatures. */
function SignatureActions({ dialog, onClose, onDone }: {
  dialog: { kind: "sign" | "decline" | "cancel"; request: SignatureRequest } | null; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const { submit } = useSubmit();
  if (!dialog) return null;
  const r = dialog.request;
  if (dialog.kind === "sign") return <SignDialog request={r} onClose={onClose} onDone={onDone} />;
  if (dialog.kind === "decline") {
    const s = r.signers.find((x) => x.id === r.currentSignerId)!;
    return <ConfirmDialog title={s.signerType === "external" ? `Refus de ${s.externalName}` : "Refuser de signer"} requireReason confirmLabel="Refuser" danger
      message="La demande est close et le document libéré pour être corrigé ; le demandeur est prévenu."
      onCancel={onClose}
      onConfirm={async (reason) => { if (await submit(() => declineAs(r.id, s.id, reason))) { toast.success("Refus enregistré."); onDone(); } }} />;
  }
  return <ConfirmDialog title="Annuler la demande de signature" requireReason confirmLabel="Annuler la demande" danger
    message="Les signatures déjà données restent au dossier ; le document est libéré." onCancel={onClose}
    onConfirm={async (reason) => { if (await submit(() => cancelSignature(r.id, reason).then(() => true))) { toast.success("Demande annulée."); onDone(); } }} />;
}

// ------------------------------------------------------------------ Signatures

export function SignaturesSection({ onChanged }: { onChanged: () => void }) {
  const [status, setStatus] = useState("pending");
  const [version, setVersion] = useState(0);
  const mine = useAsync(() => listSignatureRequests({ mine: true }), [version]);
  const all = useAsync(() => listSignatureRequests({ status: status || undefined }), [status, version]);
  const [dialog, setDialog] = useState<{ kind: "sign" | "decline" | "cancel"; request: SignatureRequest } | null>(null);
  const [openId, setOpenId] = useState<string | null>(null);
  const done = () => { setDialog(null); setVersion((v) => v + 1); onChanged(); };

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>À signer par moi</SectionLabel>
        {mine.error && <div role="alert" className="nx-error-text">{mine.error}</div>}
        {(mine.data ?? []).length === 0 && !mine.loading && <div style={hint}>Rien à signer pour l'instant.</div>}
        <div style={{ display: "grid", gap: 8 }}>
          {(mine.data ?? []).map((r) => (
            <div key={r.id} style={{ display: "grid", gap: 4 }}>
              <button type="button" style={linkStyle} onClick={() => setOpenId(r.documentId)}>{r.documentName}</button>
              <RequestCard request={r} onSign={() => setDialog({ kind: "sign", request: r })}
                onDecline={() => setDialog({ kind: "decline", request: r })} onCancel={() => setDialog({ kind: "cancel", request: r })} />
            </div>
          ))}
        </div>
      </Card>
      <Card>
        <SectionLabel>Toutes les demandes</SectionLabel>
        <Field label="Statut">
          <Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(REQUEST_STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </Select>
        </Field>
        {all.error && <div role="alert" className="nx-error-text">{all.error}</div>}
        <DataTable exportTitle="Demandes de signature"
          columns={[
            { key: "d", header: "Document", render: (r) => <button type="button" style={linkStyle} onClick={() => setOpenId(r.documentId)}>{r.documentName}</button> },
            { key: "v", header: "Version", numeric: true, render: (r) => r.versionNo ?? "—" },
            { key: "s", header: "Statut", render: (r) => <Badge tone={REQUEST_STATUS[r.status]?.tone}>{REQUEST_STATUS[r.status]?.label ?? r.status}</Badge> },
            { key: "p", header: "Avancement", render: (r) => `${r.signers.filter((x) => x.status === "signed").length} / ${r.signers.length}` },
            { key: "c", header: "Au tour de", render: (r) => { const s = r.signers.find((x) => x.id === r.currentSignerId); return s ? signerName(s) : "—"; } },
            { key: "r", header: "Demandé par", render: (r) => r.requestedByName ?? "—" },
            { key: "t", header: "Le", render: (r) => dateShort(r.createdAt) },
          ]}
          rows={all.data ?? []} rowKey={(r) => r.id} empty={all.loading ? "Chargement…" : "Aucune demande."} />
      </Card>
      <SignatureActions dialog={dialog} onClose={() => setDialog(null)} onDone={done} />
      {openId && <DocumentDrawer key={openId} id={openId} onClose={() => setOpenId(null)} onChanged={done} />}
    </div>
  );
}

// ------------------------------------------------------------------ Revue

export function ReviewSection({ onChanged }: { onChanged: () => void }) {
  const [version, setVersion] = useState(0);
  const queue = useAsync(listWorkflowQueue, [version]);
  const [openId, setOpenId] = useState<string | null>(null);
  const changed = () => { setVersion((v) => v + 1); onChanged(); };
  return (
    <Card>
      <SectionLabel>Documents en attente de revue</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>Le déposant d'un document ne peut pas l'approuver lui-même.</div>
      {queue.error && <div role="alert" className="nx-error-text">{queue.error}</div>}
      <DataTable exportTitle="Documents en revue"
        columns={[
          { key: "n", header: "Document", render: (d) => <button type="button" style={linkStyle} onClick={() => setOpenId(d.id)}>{d.name}</button> },
          { key: "t", header: "Type", render: (d) => DOCUMENT_TYPES[d.documentType] ?? d.documentType },
          { key: "u", header: "Soumis le", render: (d) => dateShort(d.updatedAt) },
        ]}
        rows={queue.data ?? []} rowKey={(d) => d.id} empty={queue.loading ? "Chargement…" : "Rien en attente de revue."} />
      {openId && <DocumentDrawer key={openId} id={openId} onClose={() => setOpenId(null)} onChanged={changed} />}
    </Card>
  );
}

// ------------------------------------------------------------------ Rétention

export function RetentionSection() {
  const { hasPermission, user } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const policies = useAsync(listRetentionPolicies, [version]);
  const eligible = useAsync(() => (hasPermission("ged.manage_retention") ? listEligibleForDeletion() : Promise.resolve([])), [version]);
  const [editing, setEditing] = useState(false);
  const [dialog, setDialog] = useState<{ kind: "request" | "confirm" | "cancel"; doc: GedDocument } | null>(null);
  const { error, submit } = useSubmit();
  const canManage = hasPermission("ged.manage_retention");
  const done = (msg: string) => { setDialog(null); toast.success(msg); setVersion((v) => v + 1); };

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <Card>
        <SectionLabel action={canManage && <Button size="sm" onClick={() => setEditing(true)}>Nouvelle politique</Button>}>Politiques de rétention</SectionLabel>
        {policies.error && <div role="alert" className="nx-error-text">{policies.error}</div>}
        <DataTable exportTitle="Politiques de rétention"
          columns={[
            { key: "t", header: "Type", render: (p) => (p.documentType ? DOCUMENT_TYPES[p.documentType] ?? p.documentType : "Tous les types") },
            { key: "s", header: "Origine", render: (p) => <Badge tone={p.isSystemDefault ? undefined : "info"}>{p.isSystemDefault ? "Par défaut" : "Entreprise"}</Badge> },
            { key: "y", header: "Durée", numeric: true, render: (p) => `${p.retentionYears} an${p.retentionYears > 1 ? "s" : ""}` },
            { key: "l", header: "Base légale", render: (p) => p.legalBasis },
          ]}
          rows={policies.data ?? []} rowKey={(p) => p.id} empty="—" />
      </Card>
      {canManage && (
        <Card>
          <SectionLabel>Documents dont la rétention est échue</SectionLabel>
          <div style={{ ...hint, marginBottom: 8 }}>La suppression demandée par une personne doit être confirmée par une autre.</div>
          {eligible.error && <div role="alert" className="nx-error-text">{eligible.error}</div>}
          <DataTable exportTitle="Documents supprimables"
            columns={[
              { key: "n", header: "Document", render: (d) => d.name },
              { key: "r", header: "Rétention échue le", render: (d) => (d.retentionUntil ? dateShort(d.retentionUntil) : "—") },
              { key: "q", header: "Suppression demandée", render: (d) => (d.deletionRequestedAt ? dateShort(d.deletionRequestedAt) : "—") },
              { key: "a", header: "", render: (d) => d.deletionRequestedAt ? (
                <div style={{ display: "flex", gap: 4 }}>
                  <Button size="sm" variant="danger" onClick={() => setDialog({ kind: "confirm", doc: d })}>Confirmer</Button>
                  <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "cancel", doc: d })}>Abandonner</Button>
                </div>)
                : <Button size="sm" variant="ghost" onClick={() => setDialog({ kind: "request", doc: d })}>Demander la suppression</Button> },
            ]}
            rows={eligible.data ?? []} rowKey={(d) => d.id} empty={eligible.loading ? "Chargement…" : "Aucun document éligible."} />
        </Card>
      )}
      {editing && <RetentionPolicyDialog onClose={() => setEditing(false)} onDone={() => { setEditing(false); setVersion((v) => v + 1); }} />}
      {dialog?.kind === "request" && <ConfirmDialog title={`Supprimer « ${dialog.doc.name} »`} requireReason danger confirmLabel="Demander"
        message="Une autre personne habilitée devra confirmer la suppression." onCancel={() => setDialog(null)}
        onConfirm={async (r) => { if (await submit(() => requestDeletion(dialog.doc.id, r).then(() => true))) done("Suppression demandée."); }} />}
      {dialog?.kind === "confirm" && <ConfirmDialog title={`Confirmer la suppression de « ${dialog.doc.name} »`} danger confirmLabel="Supprimer définitivement"
        message={`Suppression définitive du document et de toutes ses versions.${user ? " Vous ne pouvez pas confirmer une suppression que vous avez demandée." : ""}`}
        onCancel={() => setDialog(null)}
        onConfirm={async () => { if (await submit(() => confirmDeletion(dialog.doc.id).then(() => true))) done("Document supprimé."); }} />}
      {dialog?.kind === "cancel" && <ConfirmDialog title="Abandonner la demande de suppression" confirmLabel="Abandonner"
        message="Le document est conservé." onCancel={() => setDialog(null)}
        onConfirm={async () => { if (await submit(() => cancelDeletion(dialog.doc.id).then(() => true))) done("Demande de suppression abandonnée."); }} />}
    </div>
  );
}

// ------------------------------------------------------------------ Audit

export function GedAuditSection() {
  const audit = useAsync(() => listGedAudit(), []);
  return (
    <Card>
      <SectionLabel>Journal d'audit GED</SectionLabel>
      {audit.error && <div role="alert" className="nx-error-text">{audit.error}</div>}
      <DataTable exportTitle="Journal d'audit GED"
        columns={[
          { key: "d", header: "Date", render: (a) => dateTime(a.occurredAt) },
          { key: "a", header: "Action", render: (a) => AUDIT_ACTION[a.action] ?? a.action },
          { key: "t", header: "Élément", render: (a) => AUDIT_TABLE[a.tableName] ?? a.tableName },
        ]}
        rows={audit.data ?? []} rowKey={(a) => a.id} empty={audit.loading ? "Chargement…" : "—"} />
    </Card>
  );
}
