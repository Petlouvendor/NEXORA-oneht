import { useCallback, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import { TopLayer } from "@/shared/TopLayer";

export interface PromptOptions {
  title: string;
  label: string;
  /** Texte obligatoire (au moins `minLength` caractères, 3 par défaut). */
  required?: boolean;
  minLength?: number;
  defaultValue?: string;
  hint?: string;
  confirmLabel?: string;
  danger?: boolean;
  /** Liste de choix (remplace la saisie libre). */
  choices?: Array<{ value: string; label: string }>;
  multiline?: boolean;
}

interface Pending extends PromptOptions { resolve: (value: string | null) => void }

/**
 * Remplace `window.prompt` (interdit par le plan maître) : une fenêtre de l'application, avec validation,
 * qui rend la valeur saisie ou `null` si l'utilisateur annule. Usage : `const { ask, dialog } = usePrompt();`
 * puis `const note = await ask({ title, label, required: true }); if (note === null) return;` et `{dialog}` dans le rendu.
 */
export function usePrompt(): { ask: (o: PromptOptions) => Promise<string | null>; dialog: ReactNode } {
  const [pending, setPending] = useState<Pending | null>(null);
  const [value, setValue] = useState("");
  const [error, setError] = useState<string | null>(null);
  const pendingRef = useRef<Pending | null>(null);
  pendingRef.current = pending;

  const ask = useCallback((o: PromptOptions) => new Promise<string | null>((resolve) => {
    pendingRef.current?.resolve(null);
    setValue(o.defaultValue ?? o.choices?.[0]?.value ?? "");
    setError(null);
    setPending({ ...o, resolve });
  }), []);

  const close = (v: string | null) => { pending?.resolve(v); setPending(null); };

  function confirm(e?: FormEvent) {
    e?.preventDefault();
    if (!pending) return;
    const v = value.trim();
    const min = pending.minLength ?? 3;
    if (pending.required && v.length < min) { setError(min > 1 ? `${min} caractères au moins.` : "Obligatoire."); return; }
    close(v);
  }

  const dialog = pending ? (
    <TopLayer>
    <Modal title={pending.title} onClose={() => close(null)}
      footer={<><Button variant="ghost" onClick={() => close(null)}>Annuler</Button>
        <Button variant={pending.danger ? "danger" : "primary"} onClick={() => confirm()}>{pending.confirmLabel ?? "Valider"}</Button></>}>
      <form noValidate onSubmit={confirm} style={{ display: "grid", gap: 10 }}>
        <Field label={pending.label} required={pending.required} error={error} hint={pending.hint}>
          {pending.choices
            ? <Select value={value} onChange={(e) => setValue(e.target.value)}>
                {pending.choices.map((c) => <option key={c.value} value={c.value}>{c.label}</option>)}
              </Select>
            : pending.multiline
              ? <textarea className="nx-textarea" rows={3} autoFocus value={value} onChange={(e) => setValue(e.target.value)} />
              : <TextInput autoFocus value={value} onChange={(e) => setValue(e.target.value)} />}
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
    </TopLayer>
  ) : null;

  return { ask, dialog };
}
