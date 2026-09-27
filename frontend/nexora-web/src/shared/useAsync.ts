import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "@/core/apiClient";

/** Message lisible d'une erreur d'API (détail métier, sinon code). */
export const errorMessage = (e: unknown, fallback = "Opération impossible") =>
  e instanceof ApiError ? e.problem.detail ?? e.problem.title : fallback;

/**
 * Chargement de données avec états explicites (chargement, erreur, rechargement). Hook partagé :
 * plusieurs écrans en gardent encore une copie locale (dette notée dans le rapport de phase 1).
 * `deps` relance le chargement ; une réponse arrivée après un changement de `deps` est ignorée.
 */
export function useAsync<T>(loader: () => Promise<T>, deps: unknown[] = []) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const seq = useRef(0);

  const reload = useCallback(() => {
    const mine = ++seq.current;
    setLoading(true);
    setError(null);
    loader()
      .then((d) => { if (mine === seq.current) setData(d); })
      .catch((e) => { if (mine === seq.current) setError(errorMessage(e, "Erreur de chargement")); })
      .finally(() => { if (mine === seq.current) setLoading(false); });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  useEffect(reload, [reload]);
  return { data, error, loading, reload };
}
