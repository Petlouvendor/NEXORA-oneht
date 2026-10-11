import { useRef, useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateTime } from "@/shared/format";
import { Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import {
  ACCEPTED_FILES, DOCUMENT_TYPES, MAX_FILE_BYTES, addVersion, createDocument, listSignatureCandidates, readFileAsBase64,
  requestSignature, saveRetentionPolicy, shareDocument, shareUrl, signAs, signerName, validateSigners,
  type DocumentShare, type GedDocument, type SignatureRequest, type SignerInput,
} from "@/ged/gedApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;

function checkFile(file: File | undefined): string | null {
  if (!file) return "Choisissez un fichier.";
  if (file.size === 0) return "Le fichier est vide.";
  if (file.size > MAX_FILE_BYTES) return "Fichier trop volumineux (25 Mo au plus).";
  return null;
}

/** Import d'un document : nom, type, catégorie, fichier ; doublon exact signalé (jamais bloquant). */
export function UploadDialog({ onClose, onDone }: { onClose: () => void; onDone: (doc: GedDocument) => void }) {
  const toast = useToast();
  const file = useRef<HTMLInputElement>(null);
  const [name, setName] = useState("");
  const [type, setType] = useState("internal");
  const [category, setCategory] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "document.name": "name", "document.type": "type", "document.content_type": "file", "document.size": "file", "document.content": "file",
  });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const f = file.current?.files?.[0];
    if (!name.trim()) return fail("name", "Nom obligatoire.");
    const bad = checkFile(f);
    if (bad) return fail("file", bad);
    const r = await submit(async () => {
      const { base64, contentType } = await readFileAsBase64(f!);
      return createDocument({ name: name.trim(), documentType: type, category: category.trim() || null, contentBase64: base64, contentType });
    });
    if (!r) return;
    if (r.potentialDuplicateOf) toast.push("Document importé — un document au contenu identique existe déjà dans la bibliothèque.", "warning");
    else toast.success(`« ${r.document.name} » importé (version 1).`);
    onDone(r.document);
  }

  return (
    <Modal title="Importer un document" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Importer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={200} onChange={(e) => setName(e.target.value)} /></Field>
        <Field label="Type" required error={fieldErrors.type}>
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            {Object.entries(DOCUMENT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Catégorie"><TextInput value={category} maxLength={100} placeholder="Ex. baux, assurances" onChange={(e) => setCategory(e.target.value)} /></Field>
        <Field label="Fichier" required error={fieldErrors.file} hint="PDF, image, Word, Excel ou CSV — 25 Mo au plus.">
          <input ref={file} type="file" accept={ACCEPTED_FILES} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Nouvelle version : fichier + motif (l'historique garde toutes les versions). */
export function NewVersionDialog({ doc, onClose, onDone }: { doc: GedDocument; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const file = useRef<HTMLInputElement>(null);
  const [reason, setReason] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "document.content_type": "file", "document.size": "file", "document.content": "file" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const f = file.current?.files?.[0];
    const bad = checkFile(f);
    if (bad) return fail("file", bad);
    if (reason.trim().length < 3) return fail("reason", "Indiquez ce qui change.");
    const r = await submit(async () => {
      const { base64, contentType } = await readFileAsBase64(f!);
      return addVersion(doc.id, { contentBase64: base64, contentType, changeReason: reason.trim() });
    });
    if (r) { toast.success(`Version ${r.document.currentVersionNo} enregistrée.`); onDone(); }
  }

  return (
    <Modal title={`Nouvelle version — ${doc.name}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Fichier" required error={fieldErrors.file}><input ref={file} type="file" accept={ACCEPTED_FILES} /></Field>
        <Field label="Motif du changement" required error={fieldErrors.reason}>
          <TextInput value={reason} maxLength={300} placeholder="Ex. clause 4 corrigée" onChange={(e) => setReason(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

type SignerRow = SignerInput & { key: number };

/** Demande de signature : destinataires dans l'ordre (utilisateurs habilités ou externes), échéance, message. */
export function SignatureRequestDialog({ doc, onClose, onDone }: { doc: GedDocument; onClose: () => void; onDone: (r: SignatureRequest) => void }) {
  const toast = useToast();
  const candidates = useAsync(listSignatureCandidates, []);
  const [rows, setRows] = useState<SignerRow[]>([{ key: 1, signerType: "user", userId: "" }]);
  const [message, setMessage] = useState("");
  const { busy, error, submit, setError } = useSubmit();
  const setRow = (key: number, patch: Partial<SignerInput>) => setRows((p) => p.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const move = (i: number, d: -1 | 1) => setRows((p) => {
    const n = [...p]; const j = i + d;
    if (j < 0 || j >= n.length) return p;
    [n[i], n[j]] = [n[j], n[i]];
    return n;
  });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const signers: SignerInput[] = rows.map(({ key: _k, ...s }) => ({
      ...s,
      userId: s.signerType === "user" ? s.userId || null : null,
      externalName: s.signerType === "external" ? s.externalName?.trim() || null : null,
      externalEmail: s.signerType === "external" ? s.externalEmail?.trim() || null : null,
      role: s.role?.trim() || null,
      dueAt: s.dueAt ? new Date(`${s.dueAt}T23:59:00`).toISOString() : null,
    }));
    const problem = validateSigners(signers);
    if (problem) { setError(problem); return; }
    const r = await submit(() => requestSignature(doc.id, signers, message.trim() || null));
    if (r) { toast.success(`Demande envoyée : ${signerName(r.signers[0])} est invité(e) à signer en premier.`); onDone(r); }
  }

  return (
    <Modal title={`Demande de signature — ${doc.name}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Envoyer la demande</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(640px, 92vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={hint}>
          Version {doc.currentVersionNo} — c'est cette version qui sera signée ; le document est verrouillé jusqu'à l'issue.
          Les signataires répondent dans l'ordre de la liste.
        </div>
        {rows.map((r, i) => (
          <fieldset key={r.key} style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 10, display: "grid", gap: 8 }}>
            <legend style={{ fontSize: 13 }}>Signataire {i + 1}</legend>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 8 }}>
              <Field label="Type">
                <Select aria-label={`Type signataire ${i + 1}`} value={r.signerType}
                  onChange={(e) => setRow(r.key, { signerType: e.target.value as "user" | "external" })}>
                  <option value="user">Utilisateur NEXORA</option>
                  <option value="external">Personne externe</option>
                </Select>
              </Field>
              {r.signerType === "user" ? (
                <Field label="Utilisateur" required>
                  <Select aria-label={`Utilisateur signataire ${i + 1}`} value={r.userId ?? ""} onChange={(e) => setRow(r.key, { userId: e.target.value })}>
                    <option value="">— Choisir —</option>
                    {(candidates.data ?? []).map((c) => <option key={c.id} value={c.id}>{c.fullName}</option>)}
                  </Select>
                </Field>
              ) : (<>
                <Field label="Nom" required><TextInput aria-label={`Nom signataire ${i + 1}`} value={r.externalName ?? ""} maxLength={150}
                  onChange={(e) => setRow(r.key, { externalName: e.target.value })} /></Field>
                <Field label="E-mail"><TextInput type="email" value={r.externalEmail ?? ""} maxLength={200}
                  onChange={(e) => setRow(r.key, { externalEmail: e.target.value })} /></Field>
              </>)}
              <Field label="Qualité"><TextInput value={r.role ?? ""} maxLength={80} placeholder="Ex. Bailleur" onChange={(e) => setRow(r.key, { role: e.target.value })} /></Field>
              <Field label="Échéance"><TextInput type="date" value={r.dueAt ?? ""} onChange={(e) => setRow(r.key, { dueAt: e.target.value })} /></Field>
            </div>
            <div style={{ display: "flex", gap: 6 }}>
              <Button type="button" size="sm" variant="ghost" disabled={i === 0} onClick={() => move(i, -1)}>Monter</Button>
              <Button type="button" size="sm" variant="ghost" disabled={i === rows.length - 1} onClick={() => move(i, 1)}>Descendre</Button>
              {rows.length > 1 && <Button type="button" size="sm" variant="ghost" onClick={() => setRows((p) => p.filter((x) => x.key !== r.key))}>Retirer</Button>}
            </div>
            {r.signerType === "external" && <div style={hint}>NEXORA n'envoie pas de lien de signature : la signature de l'externe est recueillie par vous (ou un gestionnaire GED), en sa présence ou sur papier, et le dossier l'indique.</div>}
          </fieldset>
        ))}
        {rows.length < 10 && <Button type="button" size="sm" variant="subtle"
          onClick={() => setRows((p) => [...p, { key: Math.max(...p.map((x) => x.key)) + 1, signerType: "user", userId: "" }])}>+ Signataire</Button>}
        <Field label="Message aux signataires"><TextInput value={message} maxLength={500} placeholder="Ex. merci de signer avant vendredi" onChange={(e) => setMessage(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Signature : rappel de ce qui est signé (document, version), note facultative, engagement explicite. */
export function SignDialog({ request, onClose, onDone }: { request: SignatureRequest; onClose: () => void; onDone: (r: SignatureRequest) => void }) {
  const toast = useToast();
  const signer = request.signers.find((s) => s.id === request.currentSignerId)!;
  const external = signer.signerType === "external";
  const [note, setNote] = useState("");
  const [agree, setAgree] = useState(false);
  const { busy, error, fieldErrors, submit, fail } = useSubmit();

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!agree) return fail("agree", "Cochez la confirmation.");
    const r = await submit(() => signAs(request.id, signer.id, note.trim() || null));
    if (r) { toast.success(r.status === "completed" ? "Document signé par tous : il est désormais figé." : "Signature enregistrée."); onDone(r); }
  }

  return (
    <Modal title={external ? `Recueillir la signature de ${signer.externalName}` : "Signer le document"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Signer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <p style={{ margin: 0 }}>« {request.documentName} », version {request.versionNo}. Demandé par {request.requestedByName ?? "—"} le {dateTime(request.createdAt)}.</p>
        {request.message && <p style={{ margin: 0, ...hint }}>Message : « {request.message} »</p>}
        <Field label="Note"><TextInput value={note} maxLength={500} placeholder={external ? "Ex. signé en agence, pièce d'identité vérifiée" : "Ex. lu et approuvé"} onChange={(e) => setNote(e.target.value)} /></Field>
        <Field label="Engagement" error={fieldErrors.agree}>
          <label style={{ display: "flex", gap: 8, alignItems: "center", fontSize: 13 }}>
            <input type="checkbox" checked={agree} onChange={(e) => setAgree(e.target.checked)} />
            {external ? `${signer.externalName} a signé ce document en ma présence.` : "J'ai lu ce document et je le signe."}
          </label>
        </Field>
        <div style={hint}>Signature électronique simple : identité de session, date et empreinte du document sont conservées comme preuve.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Partage temporaire : durée, téléchargement, mot de passe ; affiche le lien à copier. */
export function ShareDialog({ doc, onClose }: { doc: GedDocument; onClose: () => void }) {
  const toast = useToast();
  const [days, setDays] = useState("1");
  const [password, setPassword] = useState("");
  const [share, setShare] = useState<DocumentShare | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "document_share.expires_at": "days" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const d = num(days);
    if (d == null || d <= 0 || d > 30) return fail("days", "Entre 1 et 30 jours.");
    const r = await submit(() => shareDocument(doc.id, {
      expiresAt: new Date(Date.now() + d * 86400000).toISOString(), allowDownload: true, password: password.trim() || null,
    }));
    if (r) setShare(r);
  }
  async function copy() {
    try { await navigator.clipboard.writeText(shareUrl(share!.token)); toast.success("Lien copié."); }
    catch { toast.push("Copie impossible : sélectionnez le lien et copiez-le.", "warning"); }
  }

  return (
    <Modal title={`Partager — ${doc.name}`} onClose={onClose}
      footer={share ? <Button onClick={onClose}>Fermer</Button>
        : <><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer le lien</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(460px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {share ? (<>
          <Field label="Lien de partage"><TextInput readOnly value={shareUrl(share.token)} onFocus={(e) => e.currentTarget.select()} /></Field>
          <div style={hint}>Valable jusqu'au {dateTime(share.expiresAt)}{password ? ", protégé par mot de passe" : ""}. Révocable à tout moment.</div>
          <Button type="button" size="sm" variant="subtle" onClick={copy}>Copier le lien</Button>
        </>) : (<>
          <Field label="Durée (jours)" required error={fieldErrors.days}><TextInput type="number" min={1} max={30} value={days} onChange={(e) => setDays(e.target.value)} /></Field>
          <Field label="Mot de passe" hint="Facultatif — à transmettre séparément."><TextInput type="password" value={password} maxLength={100} onChange={(e) => setPassword(e.target.value)} /></Field>
        </>)}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Politique de rétention propre à l'entreprise (surcharge le gabarit système). */
export function RetentionPolicyDialog({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [type, setType] = useState("");
  const [years, setYears] = useState("10");
  const [basis, setBasis] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "retention.years": "years", "retention.legal_basis": "basis" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const y = num(years);
    if (y == null || !Number.isInteger(y) || y < 1 || y > 100) return fail("years", "Nombre d'années entier entre 1 et 100.");
    if (basis.trim().length < 3) return fail("basis", "Indiquez la base légale ou le motif.");
    const ok = await submit(() => saveRetentionPolicy({ documentType: type || null, retentionYears: y, legalBasis: basis.trim() }));
    if (ok) { toast.success("Politique de rétention enregistrée."); onDone(); }
  }

  return (
    <Modal title="Politique de rétention" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Type de document">
          <Select value={type} onChange={(e) => setType(e.target.value)}>
            <option value="">Tous les types</option>
            {Object.entries(DOCUMENT_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select>
        </Field>
        <Field label="Durée de conservation (années)" required error={fieldErrors.years}><TextInput type="number" min={1} max={100} value={years} onChange={(e) => setYears(e.target.value)} /></Field>
        <Field label="Base légale / motif" required error={fieldErrors.basis}>
          <TextInput value={basis} maxLength={300} placeholder="Ex. obligation de conservation des pièces comptables" onChange={(e) => setBasis(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
