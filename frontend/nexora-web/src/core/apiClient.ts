import { config } from "@/core/config";

/** Erreur structurée renvoyée par l'API (RFC 7807 — Phase 3 §8, Phase 19 §48). */
export interface ApiProblem {
  status: number;   // 0 = serveur injoignable (réseau / hors ligne)
  title: string;
  detail?: string;
  correlationId?: string;
}

export class ApiError extends Error {
  constructor(readonly problem: ApiProblem) {
    super(problem.detail ?? problem.title);
    this.name = "ApiError";
  }

  /** Le serveur n'a pas répondu (réseau coupé, DNS, CORS bloqué…). */
  get isOffline(): boolean {
    return this.problem.status === 0;
  }

  get isForbidden(): boolean {
    return this.problem.status === 403;
  }
}

type TokenProvider = () => string | null;
type CompanyProvider = () => string | null;
type UnauthorizedHandler = () => void;
/** Renouvelle le jeton d'accès (cookie de rafraîchissement HttpOnly) ; renvoie le nouveau jeton ou null. */
type RefreshHandler = () => Promise<string | null>;
/** Demande une confirmation forte à l'utilisateur ; renvoie le jeton de confirmation ou null (annulé). */
type StepUpHandler = (purpose: string, detail: string) => Promise<string | null>;

let getToken: TokenProvider = () => null;
let getCompany: CompanyProvider = () => null;
let onUnauthorized: UnauthorizedHandler = () => {};
let refreshSession: RefreshHandler | null = null;
let stepUpHandler: StepUpHandler | null = null;

export function configureApiClient(opts: {
  token: TokenProvider;
  company: CompanyProvider;
  onUnauthorized: UnauthorizedHandler;
  refresh?: RefreshHandler;
}) {
  getToken = opts.token;
  getCompany = opts.company;
  onUnauthorized = opts.onUnauthorized;
  refreshSession = opts.refresh ?? null;
}

/** Fenêtre de confirmation forte (montée une fois dans l'application). */
export function setStepUpHandler(handler: StepUpHandler | null) {
  stepUpHandler = handler;
}

// Un seul rafraîchissement à la fois : les requêtes qui expirent ensemble attendent le même renouvellement.
let refreshing: Promise<string | null> | null = null;
function refreshOnce(): Promise<string | null> {
  if (!refreshSession) return Promise.resolve(null);
  if (!refreshing) refreshing = refreshSession().catch(() => null).finally(() => { refreshing = null; });
  return refreshing;
}

// Confirmations fortes obtenues, par catégorie (valables quelques minutes côté serveur).
const stepUpTokens = new Map<string, string>();
/** Oublie les confirmations fortes (déconnexion, changement d'utilisateur). */
export function clearStepUpTokens() {
  stepUpTokens.clear();
}

function newCorrelationId(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `cid-${Date.now()}-${Math.random().toString(16).slice(2)}`;
}

export interface ApiFetchInit extends RequestInit {
  /** En-tête `Idempotency-Key` (Phase 18 §12) pour les opérations rejouables. */
  idempotencyKey?: string;
}

/** Envoie la requête (en-têtes d'authentification, entreprise, corrélation) et lève une `ApiError`
 * normalisée pour tout statut non 2xx — partagé par `apiFetch` (JSON) et `apiDownload` (fichier). */
async function send(path: string, init: ApiFetchInit, accept: string, attempt = 0, stepUpToken?: string): Promise<Response> {
  const { idempotencyKey, ...rest } = init;
  const headers = new Headers(rest.headers);
  headers.set("Accept", accept);
  if (rest.body && !headers.has("Content-Type")) headers.set("Content-Type", "application/json");
  headers.set("X-Correlation-Id", newCorrelationId());
  if (idempotencyKey && !headers.has("Idempotency-Key")) headers.set("Idempotency-Key", idempotencyKey);
  if (stepUpToken) headers.set("X-Step-Up-Token", stepUpToken);

  const token = getToken();
  if (token) headers.set("Authorization", `Bearer ${token}`);

  const company = getCompany();
  if (company) headers.set("X-Company-Id", company);

  let response: Response;
  try {
    response = await fetch(`${config.apiBaseUrl}${path}`, { ...rest, headers });
  } catch (cause) {
    // fetch ne rejette que sur erreur réseau — pas sur un statut HTTP.
    throw new ApiError({
      status: 0,
      title: "network_unreachable",
      detail: cause instanceof Error && cause.name === "AbortError"
        ? "Requête annulée."
        : "Le serveur est injoignable. Vérifiez votre connexion.",
    });
  }

  if (response.status === 401) {
    // Jeton d'accès expiré : un renouvellement par le jeton de rafraîchissement, puis un seul nouvel essai.
    if (attempt === 0 && !path.startsWith("/auth/")) {
      const renewed = await refreshOnce();
      if (renewed) return send(path, init, accept, attempt + 1, stepUpToken);
    }
    onUnauthorized();
    throw new ApiError({ status: 401, title: "session_expired", detail: "Session expirée — reconnectez-vous." });
  }

  if (response.status === 403 && stepUpHandler && attempt < 3) {
    // Action critique : confirmation forte de l'identité, puis nouvel essai avec le jeton de confirmation.
    const body = await response.clone().json().catch(() => null) as { title?: string; purpose?: string; detail?: string } | null;
    if (body?.title === "security.step_up_required" && body.purpose) {
      const cached = stepUpTokens.get(body.purpose);
      if (cached && cached !== stepUpToken) return send(path, init, accept, attempt + 1, cached);
      stepUpTokens.delete(body.purpose);
      const granted = await stepUpHandler(body.purpose, body.detail ?? "Confirmez votre identité.");
      if (!granted) throw new ApiError({ status: 403, title: "security.step_up_cancelled", detail: "Action annulée : identité non confirmée." });
      stepUpTokens.set(body.purpose, granted);
      return send(path, init, accept, attempt + 1, granted);
    }
  }

  if (!response.ok) {
    let problem: ApiProblem = { status: response.status, title: response.statusText };
    try {
      const body = await response.json();
      problem = {
        status: response.status,
        title: body.title ?? response.statusText,
        detail: body.detail,
        correlationId: body.correlationId,
      };
    } catch {
      /* corps non JSON */
    }
    // Résumé fonctionnel §20 : application pas encore installée (ou redémarrage attendu après
    // l'installation) → l'assistant d'installation prend la main.
    if (response.status === 503 && isSetupProblem(problem.title)
        && typeof window !== "undefined" && !window.location.pathname.startsWith("/setup")) {
      window.location.assign("/setup");
    }
    throw new ApiError(problem);
  }
  return response;
}

export async function apiFetch<T>(path: string, init: ApiFetchInit = {}): Promise<T> {
  const response = await send(path, init, "application/json");
  // Un succès peut n'avoir aucun corps (204, ou 202 Accepted de « mot de passe oublié ») : ne pas
  // transformer une réussite en erreur de parsing JSON. Le corps est toujours lu, même vide : une
  // réponse 204 laissée non lue apparaissait comme requête annulée (net::ERR_ABORTED) dans le navigateur.
  const text = await response.text();
  if (response.status === 204) return undefined as T;
  return (text ? JSON.parse(text) : undefined) as T;
}

/** Enveloppe JSON d'un fichier (API : `Accept: application/vnd.nexora.file+json`). */
interface FileEnvelope { fileName: string; contentType: string; size: number; base64: string }

const FILE_ENVELOPE = "application/vnd.nexora.file+json";

/** Téléchargement d'un fichier (reçu PDF, classeur Excel, CSV…) avec les mêmes contrôles que `apiFetch`
 * — y compris en POST (export d'un tableau affiché). Le fichier est demandé dans une enveloppe JSON : un
 * navigateur réglé pour « télécharger les PDF » ou un gestionnaire de téléchargements intercepte sinon la
 * réponse PDF et la page n'en reçoit qu'un corps vide (fichier de 0 octet, impossible à ouvrir). Un fichier
 * vide n'est jamais remis à l'utilisateur. */
export async function apiDownload(path: string, init: ApiFetchInit = {}): Promise<{ blob: Blob; filename: string | null }> {
  const response = await send(path, init, `${FILE_ENVELOPE}, application/json;q=0.9, */*;q=0.1`);
  const type = response.headers.get("Content-Type") ?? "";
  let blob: Blob;
  let filename: string | null;
  if (type.includes("json")) {
    const env = (await response.json()) as FileEnvelope;
    const binary = atob(env.base64 ?? "");
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    blob = new Blob([bytes], { type: env.contentType });
    filename = env.fileName ?? null;
  } else {
    const disposition = response.headers.get("Content-Disposition") ?? "";
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    blob = await response.blob();
    filename = match ? decodeURIComponent(match[1]) : null;
  }
  if (blob.size === 0) {
    throw new ApiError({
      status: response.status,
      title: "empty_file",
      detail: "Le fichier reçu est vide : il n'a pas été enregistré. Réessayez ; si cela persiste, un module du navigateur (gestionnaire de téléchargements) bloque peut-être le fichier.",
    });
  }
  return { blob, filename };
}

export const isSetupProblem = (title: string) => title === "setup.required" || title === "setup.restart_required";
