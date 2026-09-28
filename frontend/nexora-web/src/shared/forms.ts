import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { errorMessage } from "@/shared/useAsync";

/**
 * Soumission de formulaire : un seul envoi à la fois (protection double clic), erreur globale et
 * erreur par champ déduite du code renvoyé par l'API (`fields` : code → nom du champ).
 */
export function useSubmit(fields: Record<string, string> = {}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const inFlight = useRef(false);
  const fieldsRef = useRef(fields);
  fieldsRef.current = fields;

  const submit = useCallback(async <T,>(action: () => Promise<T>): Promise<T | undefined> => {
    if (inFlight.current) return undefined;
    inFlight.current = true;
    setBusy(true);
    setError(null);
    setFieldErrors({});
    try {
      return await action();
    } catch (e) {
      const msg = errorMessage(e);
      const field = e instanceof ApiError ? fieldsRef.current[e.problem.title] : undefined;
      if (field) setFieldErrors({ [field]: msg });
      else setError(msg);
      return undefined;
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }, []);

  /** Erreur de validation détectée avant l'envoi (même présentation que celles du serveur). */
  const fail = useCallback((field: string, message: string) => setFieldErrors({ [field]: message }), []);

  // Le premier champ en erreur peut être hors de vue (long formulaire) : on l'amène à l'écran et on y place le focus.
  useEffect(() => {
    if (Object.keys(fieldErrors).length === 0) return;
    requestAnimationFrame(() => {
      const err = document.querySelector<HTMLElement>(".nx-err");
      err?.scrollIntoView({ block: "center", behavior: "smooth" });
      const input = err?.id ? document.querySelector<HTMLElement>(`[aria-describedby~="${err.id}"]`) : null;
      input?.focus({ preventScroll: true });
    });
  }, [fieldErrors]);

  return { busy, error, fieldErrors, submit, fail, setError };
}

/** Lit un fichier en base64 (sans l'en-tête data:). */
export function fileToBase64(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(",")[1] ?? "");
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

/** Nombre saisi → nombre, ou null si vide / invalide. */
export const num = (v: string): number | null => {
  if (v.trim() === "") return null;
  const n = Number(v.replace(",", "."));
  return Number.isFinite(n) ? n : null;
};

/** Identifiant d'opération (idempotence serveur) — stable pour une tentative, nouveau pour la suivante. */
export const newOperationId = () =>
  typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : "10000000-1000-4000-8000-100000000000".replace(/[018]/g, (c) =>
        (Number(c) ^ (Math.random() * 16) >> (Number(c) / 4)).toString(16));
