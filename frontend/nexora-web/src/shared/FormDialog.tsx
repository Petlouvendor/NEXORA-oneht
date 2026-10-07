import { useCallback, useRef, useState, type FormEvent, type ReactNode } from "react";
import { Button, Field, Modal, Select, TextInput } from "@/shared/ui";
import { TopLayer } from "@/shared/TopLayer";

export interface FormField {
  name: string;
  label: string;
  type?: "text" | "number" | "date" | "select" | "textarea";
  required?: boolean;
  defaultValue?: string;
  hint?: string;
  /** Liste de choix (type « select »). */
  options?: Array<{ value: string; label: string }>;
  /** Bornes d'un nombre. */
  min?: number;
  max?: number;
  /** Nombre entier exigé. */
  integer?: boolean;
  /** Longueur minimale d'un texte obligatoire (2 par défaut). */
  minLength?: number;
  /** Contrôle propre au champ : rend le message d'erreur, ou null. */
  validate?: (value: string, all: Record<string, string>) => string | null;
}

export interface FormDialogOptions {
  title: string;
  fields: FormField[];
  intro?: ReactNode;
  confirmLabel?: string;
  danger?: boolean;
}

interface Pending extends FormDialogOptions { resolve: (values: Record<string, string> | null) => void }

function initial(fields: FormField[]): Record<string, string> {
  return Object.fromEntries(fields.map((f) => [f.name, f.defaultValue ?? (f.type === "select" ? f.options?.[0]?.value ?? "" : "")]));
}

/**
 * Formulaire en fenêtre (finalisation phase 20, §45) : remplace les suites de `window.prompt` par une vraie saisie —
 * libellés, valeurs par défaut, listes de choix, validation et erreur par champ, annulation, protection contre la
 * double validation. Rend les valeurs saisies (texte) ou `null` si l'utilisateur annule.
 * Usage : `const form = useFormDialog();` puis `const v = await form.ask({ title, fields }); if (!v) return;` et
 * `{form.dialog}` dans le rendu.
 */
export function useFormDialog(): { ask: (o: FormDialogOptions) => Promise<Record<string, string> | null>; dialog: ReactNode } {
  const [pending, setPending] = useState<Pending | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});
  const [errors, setErrors] = useState<Record<string, string>>({});
  const pendingRef = useRef<Pending | null>(null);
  pendingRef.current = pending;

  const ask = useCallback((o: FormDialogOptions) => new Promise<Record<string, string> | null>((resolve) => {
    pendingRef.current?.resolve(null);
    setValues(initial(o.fields));
    setErrors({});
    setPending({ ...o, resolve });
  }), []);

  const close = (v: Record<string, string> | null) => { pending?.resolve(v); setPending(null); };

  function check(f: FormField, raw: string, all: Record<string, string>): string | null {
    const v = raw.trim();
    if (!v) return f.required ? "Obligatoire." : null;
    if (f.type === "number") {
      const n = Number(v.replace(",", "."));
      if (!Number.isFinite(n)) return "Nombre attendu.";
      if (f.integer && !Number.isInteger(n)) return "Nombre entier attendu.";
      if (f.min != null && n < f.min) return `Au moins ${f.min}.`;
      if (f.max != null && n > f.max) return `Au plus ${f.max}.`;
    } else if (f.type !== "select" && f.type !== "date" && f.required && v.length < (f.minLength ?? 2)) {
      return `${f.minLength ?? 2} caractères au moins.`;
    }
    return f.validate?.(v, all) ?? null;
  }

  function submit(e?: FormEvent) {
    e?.preventDefault();
    if (!pending) return;
    const next: Record<string, string> = {};
    for (const f of pending.fields) {
      const err = check(f, values[f.name] ?? "", values);
      if (err) next[f.name] = err;
    }
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    const out: Record<string, string> = {};
    for (const f of pending.fields) {
      const v = (values[f.name] ?? "").trim();
      out[f.name] = f.type === "number" ? v.replace(",", ".") : v;
    }
    close(out);
  }

  const set = (name: string, v: string) => setValues((cur) => ({ ...cur, [name]: v }));

  const dialog = pending ? (
    <TopLayer>
      <Modal title={pending.title} onClose={() => close(null)}
        footer={<><Button variant="ghost" onClick={() => close(null)}>Annuler</Button>
          <Button variant={pending.danger ? "danger" : "primary"} onClick={() => submit()}>{pending.confirmLabel ?? "Valider"}</Button></>}>
        <form noValidate onSubmit={submit} style={{ display: "grid", gap: 10 }}>
          {pending.intro && <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>{pending.intro}</div>}
          {pending.fields.map((f, i) => (
            <Field key={f.name} label={f.label} required={f.required} error={errors[f.name]} hint={f.hint}>
              {f.type === "select"
                ? <Select autoFocus={i === 0} value={values[f.name] ?? ""} onChange={(e) => set(f.name, e.target.value)}>
                    {f.options?.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
                  </Select>
                : f.type === "textarea"
                  ? <textarea className="nx-textarea" rows={3} autoFocus={i === 0} value={values[f.name] ?? ""} onChange={(e) => set(f.name, e.target.value)} />
                  : <TextInput autoFocus={i === 0} type={f.type === "date" ? "date" : "text"} inputMode={f.type === "number" ? "decimal" : undefined}
                      value={values[f.name] ?? ""} onChange={(e) => set(f.name, e.target.value)} />}
            </Field>
          ))}
          <button type="submit" hidden />
        </form>
      </Modal>
    </TopLayer>
  ) : null;

  return { ask, dialog };
}
