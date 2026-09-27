import { webcrypto } from "node:crypto";
import { beforeEach, describe, expect, it } from "vitest";
import { configureSecureStore, memoryBackend, secureGet, secureRemove, secureSet, secureStoreAvailable } from "@/shared/secureStore";

const subtle = webcrypto.subtle as unknown as SubtleCrypto;

/** Résumé fonctionnel §15, recette BUG-012 — coffre chiffré du navigateur. */
describe("secureStore", () => {
  let vault: ReturnType<typeof memoryBackend>;

  beforeEach(() => {
    vault = memoryBackend();
    configureSecureStore({ backend: vault, subtle });
  });

  it("chiffre et relit une valeur ; la donnée rangée n'est pas lisible", async () => {
    await secureSet("auth.token", "eyJhbGciOi.jeton.secret");
    const sealed = vault.raw.get("auth.token") as { iv: Uint8Array; data: ArrayBuffer };
    expect(new TextDecoder().decode(sealed.data)).not.toContain("jeton");
    expect(sealed.iv).toHaveLength(12);
    await expect(secureGet<string>("auth.token")).resolves.toBe("eyJhbGciOi.jeton.secret");
  });

  it("clé AES-256-GCM non exportable", async () => {
    await secureSet("x", 1);
    const key = vault.raw.get("__key") as CryptoKey;
    expect(key.extractable).toBe(false);
    expect(key.algorithm).toMatchObject({ name: "AES-GCM", length: 256 });
    await expect(subtle.exportKey("raw", key)).rejects.toThrow();
  });

  it("IV aléatoire : deux écritures identiques donnent deux chiffrés différents", async () => {
    await secureSet("a", "même valeur");
    const first = new Uint8Array((vault.raw.get("a") as { data: ArrayBuffer }).data);
    await secureSet("a", "même valeur");
    const second = new Uint8Array((vault.raw.get("a") as { data: ArrayBuffer }).data);
    expect(first.join(",")).not.toBe(second.join(","));
  });

  it("donnée altérée ou clé perdue : rien n'est renvoyé", async () => {
    await secureSet("a", { total: 100 });
    const sealed = vault.raw.get("a") as { iv: Uint8Array; data: ArrayBuffer };
    const tampered = new Uint8Array(sealed.data.slice(0));
    tampered[0] ^= 0xff;
    vault.raw.set("a", { ...sealed, data: tampered.buffer });
    await expect(secureGet("a")).resolves.toBeNull();

    await secureSet("b", "valeur");
    vault.raw.delete("__key");
    configureSecureStore({ backend: vault, subtle });   // nouvelle clé générée à la prochaine utilisation
    await expect(secureGet("b")).resolves.toBeNull();
  });

  it("suppression et indisponibilité", async () => {
    await secureSet("a", 1);
    await secureRemove("a");
    await expect(secureGet("a")).resolves.toBeNull();
    configureSecureStore({ disabled: true });
    expect(secureStoreAvailable()).toBe(false);
    await expect(secureGet("a")).resolves.toBeNull();
  });
});
