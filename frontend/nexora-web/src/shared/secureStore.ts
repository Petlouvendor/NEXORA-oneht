/**
 * Coffre du navigateur (résumé fonctionnel §15, recette Phase 55 BUG-012) : les données sensibles
 * conservées sur l'appareil — file des ventes hors ligne, jeton d'accès — ne sont plus écrites en
 * clair dans `localStorage`.
 *
 * - Chiffrement AES-256-GCM (WebCrypto), IV aléatoire de 96 bits à chaque écriture.
 * - Clé générée sur l'appareil, NON EXPORTABLE : le code de la page peut s'en servir, personne ne
 *   peut la lire ni la copier ; elle est rangée dans IndexedDB avec les données chiffrées.
 * - Une donnée illisible (clé perdue, altération) est ignorée, jamais renvoyée partiellement.
 *
 * Limite assumée : un script malveillant exécuté DANS la page (XSS) peut utiliser la clé — c'est le
 * rôle de la CSP (Phase 47). Le coffre protège le stockage au repos (profil copié, outils de
 * développement, extensions lisant localStorage).
 *
 * WebCrypto n'existe qu'en contexte sécurisé (HTTPS ou localhost) : `secureStoreAvailable()` le dit,
 * les appelants choisissent alors un repli explicite.
 */

export interface SecureBackend {
  load(name: string): Promise<unknown>;
  save(name: string, value: unknown): Promise<void>;
  remove(name: string): Promise<void>;
}

interface Sealed {
  v: 1;
  iv: Uint8Array<ArrayBuffer>;
  data: ArrayBuffer;
}

const DB_NAME = "nexora-secure";
const STORE = "vault";
const KEY_NAME = "__key";

/** Stockage IndexedDB (objets structurés : la CryptoKey y est rangée telle quelle, non exportable). */
function indexedDbBackend(): SecureBackend {
  let db: Promise<IDBDatabase> | null = null;
  const open = () => db ??= new Promise<IDBDatabase>((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
  const run = async <T>(mode: IDBTransactionMode, op: (s: IDBObjectStore) => IDBRequest<T>) => {
    const d = await open();
    return new Promise<T>((resolve, reject) => {
      const req = op(d.transaction(STORE, mode).objectStore(STORE));
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  };
  return {
    load: (name) => run("readonly", (s) => s.get(name)),
    save: async (name, value) => { await run("readwrite", (s) => s.put(value, name)); },
    remove: async (name) => { await run("readwrite", (s) => s.delete(name)); },
  };
}

let backend: SecureBackend | null = null;
let subtle: SubtleCrypto | null = null;
let keyPromise: Promise<CryptoKey> | null = null;
let disabled = false;

function environmentSubtle(): SubtleCrypto | null {
  const secure = typeof globalThis.isSecureContext === "boolean" ? globalThis.isSecureContext : true;
  return secure ? globalThis.crypto?.subtle ?? null : null;
}

/** Réservé aux tests : backend et implémentation WebCrypto explicites (null = environnement) ;
 *  `disabled` simule un contexte non sécurisé. */
export function configureSecureStore(opts: { backend?: SecureBackend | null; subtle?: SubtleCrypto | null; disabled?: boolean }): void {
  backend = opts.backend ?? null;
  subtle = opts.subtle ?? null;
  disabled = opts.disabled ?? false;
  keyPromise = null;
}

function currentSubtle(): SubtleCrypto | null {
  if (disabled) return null;
  return subtle ?? environmentSubtle();
}

function currentBackend(): SecureBackend | null {
  if (disabled) return null;
  if (backend) return backend;
  if (typeof indexedDB === "undefined") return null;
  backend = indexedDbBackend();
  return backend;
}

/** Vrai si le chiffrement au repos est possible sur cet appareil (WebCrypto + IndexedDB). */
export function secureStoreAvailable(): boolean {
  return currentSubtle() !== null && currentBackend() !== null;
}

async function key(): Promise<CryptoKey> {
  const s = currentSubtle();
  const b = currentBackend();
  if (!s || !b) throw new Error("Stockage chiffré indisponible (contexte non sécurisé).");
  return keyPromise ??= (async () => {
    const existing = await b.load(KEY_NAME);
    if (existing && typeof existing === "object" && (existing as CryptoKey).type === "secret") return existing as CryptoKey;
    const created = await s.generateKey({ name: "AES-GCM", length: 256 }, false, ["encrypt", "decrypt"]);
    await b.save(KEY_NAME, created);
    return created;
  })().catch((err) => { keyPromise = null; throw err; });
}

/** Chiffre et range une valeur (JSON). `null` / `undefined` l'efface. */
export async function secureSet(name: string, value: unknown): Promise<void> {
  const b = currentBackend();
  if (!b) throw new Error("Stockage chiffré indisponible.");
  if (value === null || value === undefined) {
    await b.remove(name);
    return;
  }
  const s = currentSubtle()!;
  const iv = globalThis.crypto.getRandomValues(new Uint8Array(12));
  const data = await s.encrypt({ name: "AES-GCM", iv }, await key(), new TextEncoder().encode(JSON.stringify(value)));
  await b.save(name, { v: 1, iv, data } satisfies Sealed);
}

/** Déchiffre une valeur ; `null` si absente ou illisible (clé différente, donnée altérée). */
export async function secureGet<T>(name: string): Promise<T | null> {
  const b = currentBackend();
  const s = currentSubtle();
  if (!b || !s) return null;
  const sealed = (await b.load(name)) as Sealed | undefined;
  if (!sealed || sealed.v !== 1) return null;
  try {
    const plain = await s.decrypt({ name: "AES-GCM", iv: sealed.iv }, await key(), sealed.data);
    return JSON.parse(new TextDecoder().decode(plain)) as T;
  } catch {
    return null;
  }
}

export async function secureRemove(name: string): Promise<void> {
  await currentBackend()?.remove(name);
}

/** Backend en mémoire (tests). */
export function memoryBackend(): SecureBackend & { raw: Map<string, unknown> } {
  const raw = new Map<string, unknown>();
  return {
    raw,
    load: async (name) => raw.get(name),
    save: async (name, value) => { raw.set(name, value); },
    remove: async (name) => { raw.delete(name); },
  };
}
