import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import {
  ACTIVITIES,
  MODULES,
  defineCustomField,
  getSectorConfig,
  listCustomFields,
  setActivities,
  setModule,
  type CustomField,
  type SectorConfig,
} from "@/sectors/sectorsApi";
import {
  applySectorProfile,
  listSectorProfiles,
  previewSectorProfile,
  validateConfig,
  type ApplyProfileResult,
  type GuardrailReport,
  type ProfilePreview,
  type SectorProfile,
} from "@/sectors/configApi";

const CF_ENTITIES = ["product", "customer", "supplier", "sale"];
const CF_TYPES = ["text", "number", "date", "boolean", "list", "reference"];

/** Configuration sectorielle (Phase 8 §3-4, §16). Activer des modules ne change pas le noyau. */
export function SectorSettingsPage() {
  const { activeCompanyId, hasPermission } = useAuth();
  const [config, setConfig] = useState<SectorConfig | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [cfEntity, setCfEntity] = useState("product");
  const [fields, setFields] = useState<CustomField[]>([]);
  const [draft, setDraft] = useState({ fieldKey: "", label: "", dataType: "text", isRequired: false });

  const [profiles, setProfiles] = useState<SectorProfile[]>([]);
  const [profileKey, setProfileKey] = useState("");
  const [preview, setPreview] = useState<ProfilePreview | null>(null);
  const [applied, setApplied] = useState<ApplyProfileResult | null>(null);
  const [guardrails, setGuardrails] = useState<GuardrailReport | null>(null);

  const load = useCallback(() => {
    setError(null);
    getSectorConfig()
      .then(setConfig)
      .catch((e) => setError(e instanceof ApiError ? e.message : "Erreur"));
  }, []);

  useEffect(load, [load, activeCompanyId]);
  useEffect(() => {
    listCustomFields(cfEntity).then(setFields).catch(() => setFields([]));
  }, [cfEntity, activeCompanyId]);
  useEffect(() => {
    setPreview(null);
    setApplied(null);
    setGuardrails(null);
    listSectorProfiles().then(setProfiles).catch(() => setProfiles([]));
  }, [activeCompanyId]);

  const canConfigure = hasPermission("sectors.configure");
  const canApplyProfile = hasPermission("config.apply_profile");
  const canValidate = hasPermission("config.validate");

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await fn();
      load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Erreur");
    } finally {
      setBusy(false);
    }
  }

  function toggleActivity(a: string) {
    if (!config) return;
    const next = config.activities.includes(a)
      ? config.activities.filter((x) => x !== a)
      : [...config.activities, a];
    if (next.length === 0) return;
    void run(() => setActivities(next));
  }

  if (!canConfigure) {
    return <div className="nx-card">Configuration sectorielle réservée aux administrateurs.</div>;
  }

  return (
    <div style={{ display: "grid", gap: 16, maxWidth: 900 }}>
      <h1 style={{ margin: 0 }}>Secteurs &amp; modules</h1>
      {error && <div className="nx-error-text">{error}</div>}
      {!config && <div style={{ color: "var(--nx-text-2)" }}>Chargement…</div>}

      {config && (
        <>
          <section className="nx-card" style={{ display: "grid", gap: 10 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Activités — la première est l'activité principale ({config.primaryActivity})
            </div>
            <div style={{ display: "flex", flexWrap: "wrap", gap: 8 }}>
              {ACTIVITIES.map((a) => {
                const on = config.activities.includes(a);
                return (
                  <button
                    key={a}
                    type="button"
                    disabled={busy}
                    onClick={() => toggleActivity(a)}
                    className={on ? "nx-btn" : "nx-btn nx-btn--ghost"}
                    style={{ padding: "6px 12px", fontSize: 13 }}
                  >
                    {a}
                  </button>
                );
              })}
            </div>
          </section>

          <section className="nx-card" style={{ display: "grid", gap: 10 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Modules — activer/désactiver ne supprime jamais l'historique (§29)
            </div>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(180px, 1fr))", gap: 8 }}>
              {MODULES.map((m) => {
                const state = config.modules.find((x) => x.key === m);
                const on = state?.enabled ?? false;
                return (
                  <label key={m} style={{ display: "flex", alignItems: "center", gap: 8, fontSize: 14 }}>
                    <input
                      type="checkbox"
                      checked={on}
                      disabled={busy}
                      onChange={() => void run(() => setModule(m, !on))}
                    />
                    {m}
                  </label>
                );
              })}
            </div>
          </section>

          <section className="nx-card" style={{ display: "grid", gap: 12 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Champs personnalisés (§16) — adaptation sans modifier le noyau
            </div>
            <select className="nx-input" value={cfEntity} onChange={(e) => setCfEntity(e.target.value)} style={{ maxWidth: 220 }}>
              {CF_ENTITIES.map((e) => <option key={e} value={e}>{e}</option>)}
            </select>

            <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
              <thead>
                <tr style={{ color: "var(--nx-text-2)", textAlign: "left" }}>
                  <th style={{ padding: "4px 8px" }}>Clé</th>
                  <th style={{ padding: "4px 8px" }}>Libellé</th>
                  <th style={{ padding: "4px 8px" }}>Type</th>
                  <th style={{ padding: "4px 8px" }}>Obligatoire</th>
                </tr>
              </thead>
              <tbody>
                {fields.length === 0 && (
                  <tr><td colSpan={4} style={{ padding: 8, color: "var(--nx-text-2)" }}>Aucun champ défini.</td></tr>
                )}
                {fields.map((f) => (
                  <tr key={f.id} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                    <td style={{ padding: "6px 8px" }} className="nx-numeric">{f.fieldKey}</td>
                    <td style={{ padding: "6px 8px" }}>{f.label}</td>
                    <td style={{ padding: "6px 8px" }}>{f.dataType}</td>
                    <td style={{ padding: "6px 8px" }}>{f.isRequired ? "oui" : "—"}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <form
              style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}
              onSubmit={(e) => {
                e.preventDefault();
                if (!draft.fieldKey || !draft.label) return;
                void run(async () => {
                  await defineCustomField({ entityType: cfEntity, ...draft });
                  setDraft({ fieldKey: "", label: "", dataType: "text", isRequired: false });
                  setFields(await listCustomFields(cfEntity));
                });
              }}
            >
              <input className="nx-input" style={{ maxWidth: 160 }} placeholder="clé"
                     value={draft.fieldKey} onChange={(e) => setDraft({ ...draft, fieldKey: e.target.value })} />
              <input className="nx-input" style={{ maxWidth: 200 }} placeholder="libellé"
                     value={draft.label} onChange={(e) => setDraft({ ...draft, label: e.target.value })} />
              <select className="nx-input" style={{ maxWidth: 140 }}
                      value={draft.dataType} onChange={(e) => setDraft({ ...draft, dataType: e.target.value })}>
                {CF_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
              </select>
              <label style={{ display: "flex", alignItems: "center", gap: 6, fontSize: 13 }}>
                <input type="checkbox" checked={draft.isRequired}
                       onChange={(e) => setDraft({ ...draft, isRequired: e.target.checked })} />
                obligatoire
              </label>
              <button className="nx-btn" disabled={busy}>Ajouter</button>
            </form>
          </section>

          <section className="nx-card" style={{ display: "grid", gap: 12 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Profils sectoriels (§6, §49) — bundle réutilisable, appliqué sans changer de version logicielle
            </div>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
              <select
                className="nx-input"
                style={{ maxWidth: 260 }}
                value={profileKey}
                onChange={(e) => { setProfileKey(e.target.value); setPreview(null); setApplied(null); }}
              >
                <option value="">— choisir un profil —</option>
                {profiles.map((p) => (
                  <option key={p.id} value={p.key}>
                    {p.name}{p.isSystem ? " (système)" : ""}
                  </option>
                ))}
              </select>
              <button
                type="button"
                className="nx-btn nx-btn--ghost"
                disabled={busy || !profileKey}
                onClick={() => void run(async () => {
                  setApplied(null);
                  setPreview(await previewSectorProfile(profileKey));
                })}
              >
                Prévisualiser
              </button>
              <button
                type="button"
                className="nx-btn"
                disabled={busy || !profileKey || !canApplyProfile}
                title={canApplyProfile ? undefined : "Permission config.apply_profile requise"}
                onClick={() => void run(async () => {
                  const r = await applySectorProfile(profileKey, false);
                  setApplied(r);
                  setPreview(null);
                })}
              >
                Appliquer
              </button>
            </div>

            {preview && (
              <div style={{ display: "grid", gap: 6 }}>
                {preview.guardrails.length > 0 && (
                  <div className="nx-error-text">
                    Garde-fous bloquants : {preview.guardrails.join(", ")}
                  </div>
                )}
                <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
                  <thead>
                    <tr style={{ color: "var(--nx-text-2)", textAlign: "left" }}>
                      <th style={{ padding: "4px 8px" }}>Type</th>
                      <th style={{ padding: "4px 8px" }}>Élément</th>
                      <th style={{ padding: "4px 8px" }}>État</th>
                    </tr>
                  </thead>
                  <tbody>
                    {preview.items.map((it) => (
                      <tr key={`${it.kind}:${it.key}`} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                        <td style={{ padding: "6px 8px" }}>{it.kind}</td>
                        <td style={{ padding: "6px 8px" }}>{it.label}</td>
                        <td style={{ padding: "6px 8px", color: it.alreadyPresent ? "var(--nx-text-2)" : "var(--nx-brand)" }}>
                          {it.alreadyPresent ? "déjà présent" : "nouveau"}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            {applied && (
              <div style={{ fontSize: 14, display: "grid", gap: 4 }}>
                <div>
                  Appliqué : {applied.modulesApplied} module(s), {applied.fieldsCreated} champ(s),{" "}
                  {applied.terminologySet} terme(s), {applied.settingsSet} réglage(s).
                </div>
                {applied.configVersionId && (
                  <div style={{ color: "var(--nx-text-2)" }}>Snapshot de configuration : {applied.configVersionId}</div>
                )}
                {applied.warnings.map((w, i) => (
                  <div key={i} className="nx-error-text">{w}</div>
                ))}
              </div>
            )}
          </section>

          <section className="nx-card" style={{ display: "grid", gap: 12 }}>
            <div style={{ color: "var(--nx-text-2)", fontSize: 12, textTransform: "uppercase" }}>
              Garde-fous (§52) — cohérence de la configuration avant publication
            </div>
            <div>
              <button
                type="button"
                className="nx-btn nx-btn--ghost"
                disabled={busy || !canValidate}
                title={canValidate ? undefined : "Permission config.validate requise"}
                onClick={() => void run(async () => { setGuardrails(await validateConfig()); })}
              >
                Vérifier
              </button>
            </div>
            {guardrails && (
              <div style={{ display: "grid", gap: 6 }}>
                <div style={{ fontSize: 14 }}>
                  {guardrails.canPublish
                    ? "Configuration cohérente — publication autorisée."
                    : "Publication bloquée — corriger les points en échec."}
                </div>
                <table style={{ width: "100%", borderCollapse: "collapse", fontSize: 14 }}>
                  <tbody>
                    {guardrails.checks.map((c) => (
                      <tr key={c.key} style={{ borderTop: "1px solid var(--nx-surface-2)" }}>
                        <td style={{ padding: "6px 8px", width: 90 }}>
                          {c.status === "pass" ? "✓ ok" : c.status === "warn" ? "⚠ alerte" : "✕ échec"}
                        </td>
                        <td style={{ padding: "6px 8px" }}>{c.label}</td>
                        <td style={{ padding: "6px 8px", color: "var(--nx-text-2)" }}>{c.detail ?? ""}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        </>
      )}
    </div>
  );
}
