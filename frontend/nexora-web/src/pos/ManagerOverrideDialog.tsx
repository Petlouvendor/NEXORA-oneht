import { useState, type ReactNode } from "react";
import { ApiError } from "@/core/apiClient";
import { Button, Field, Modal, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { useToast } from "@/shared/Toast";
import { managerOverride, type ManagerOverrideAction, type ManagerOverrideResult } from "@/pos/posApi";

/**
 * Validation sur place (guide §10, §12, §13) : le responsable, présent au poste du caissier, saisit
 * SES identifiants. Le serveur vérifie son identité (verrouillage après échecs, double authentification),
 * sa permission et qu'il n'est pas le demandeur, puis valide en son nom. Rien n'est conservé dans le
 * navigateur : les champs sont vidés à la fermeture et le navigateur n'est pas invité à mémoriser le
 * mot de passe du responsable.
 */
export function ManagerOverrideDialog({
  title, summary, approveAction, rejectAction, targetId, noteRequiredToApprove, onDone, onClose,
}: {
  title: string;
  summary: ReactNode;
  approveAction: ManagerOverrideAction;
  rejectAction?: ManagerOverrideAction;
  targetId: string;
  noteRequiredToApprove?: boolean;
  onDone: (result: ManagerOverrideResult) => void;
  onClose: () => void;
}) {
  const toast = useToast();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [mfaCode, setMfaCode] = useState("");
  const [needMfa, setNeedMfa] = useState(false);
  const [note, setNote] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(action: ManagerOverrideAction) {
    setBusy(true);
    setError(null);
    try {
      const result = await managerOverride({
        action, targetId, email: email.trim(), password, mfaCode: needMfa ? mfaCode.trim() : null, note: note.trim() || null,
      });
      setPassword("");
      setMfaCode("");
      toast.success(`${action.startsWith("reject") ? "Refusé" : "Validé"} par ${result.approvedByName ?? "le responsable"}.`);
      onDone(result);
    } catch (e) {
      setPassword("");
      if (e instanceof ApiError && e.problem.title === "pos.override_mfa_required") setNeedMfa(true);
      setError(e instanceof ApiError ? e.message : "Validation impossible.");
    } finally {
      setBusy(false);
    }
  }

  const credentialsReady = email.trim() !== "" && password !== "" && (!needMfa || mfaCode.trim() !== "");
  return (
    <Modal title={title} onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             {rejectAction && (
               <Button variant="danger" loading={busy} disabled={busy || !credentialsReady || !note.trim()}
                       onClick={() => void submit(rejectAction)}>Refuser</Button>
             )}
             <Button loading={busy} disabled={busy || !credentialsReady || (noteRequiredToApprove && !note.trim())}
                     onClick={() => void submit(approveAction)}>Valider</Button>
           </>}>
      <form style={{ display: "grid", gap: 10 }} autoComplete="off" onSubmit={(e) => e.preventDefault()}>
        <div>{summary}</div>
        <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
          <Icon.Shield size={14} /> Le responsable saisit ses propres identifiants. La validation est enregistrée
          et auditée à son nom ; la session du caissier ne reçoit aucun droit supplémentaire.
        </div>
        <Field label="E-mail du responsable" required>
          <TextInput type="email" autoFocus autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} />
        </Field>
        <Field label="Mot de passe du responsable" required>
          <TextInput type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        {needMfa && (
          <Field label="Code de double authentification" required>
            <TextInput inputMode="numeric" autoComplete="one-time-code" value={mfaCode} onChange={(e) => setMfaCode(e.target.value)} />
          </Field>
        )}
        <Field label={noteRequiredToApprove ? "Motif" : rejectAction ? "Motif (obligatoire pour refuser)" : "Commentaire"}
               required={noteRequiredToApprove}>
          <TextInput value={note} onChange={(e) => setNote(e.target.value)} />
        </Field>
        {error && <div className="nx-error-text" role="alert">{error}</div>}
      </form>
    </Modal>
  );
}
