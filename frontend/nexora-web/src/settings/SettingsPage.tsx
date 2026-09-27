import { useCallback, useEffect, useState } from "react";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { usePreferences } from "@/shared/PreferencesContext";
import { useToast } from "@/shared/Toast";
import {
  Badge, Button, Card, Field, HelpTip, SectionLabel, Select, Tabs, TextInput,
} from "@/shared/ui";
import type { CompanyBranding } from "@/shared/preferencesApi";
import type { Density, ThemeChoice } from "@/shared/preferencesApi";
import {
  deleteShortcut, getDesignSystemVersion, listShortcuts, saveShortcut,
  type DesignSystemVersion, type Shortcut,
} from "@/shared/personalizationApi";
import { changePassword } from "@/auth/accountApi";

const THEMES: Array<{ id: ThemeChoice; label: string }> = [
  { id: "system", label: "Système" },
  { id: "light", label: "Clair" },
  { id: "dark", label: "Sombre" },
];
const DENSITIES: Array<{ id: Density; label: string; hint: string }> = [
  { id: "comfortable", label: "Confort", hint: "Analytique, plus d'air" },
  { id: "compact", label: "Dense", hint: "Opérationnel, plus de lignes" },
];
const LOCALES = [
  { id: "fr-FR", label: "Français (France)" },
  { id: "fr-HT", label: "Français (Haïti)" },
  { id: "ht-HT", label: "Kreyòl ayisyen" },
  { id: "en-US", label: "English (US)" },
];
const TIMEZONES = [
  "America/Port-au-Prince", "America/New_York", "America/Toronto", "Europe/Paris", "UTC",
];
const TEXT_SCALES: Array<{ v: number; label: string }> = [
  { v: 0.9, label: "S" }, { v: 1, label: "M" }, { v: 1.15, label: "L" }, { v: 1.3, label: "XL" }, { v: 1.5, label: "XXL" },
];

/** Préférences utilisateur (§36) + accessibilité (§31-33) + raccourcis (§37-38) + personnalisation entreprise (§35). */
export function SettingsPage() {
  const { hasPermission } = useAuth();
  const { prefs, update } = usePreferences();
  const canBrand = hasPermission("admin.configure_company");
  const [tab, setTab] = useState<"appearance" | "a11y" | "shortcuts" | "password" | "branding">("appearance");

  const tabs = [
    { id: "appearance", label: "Apparence & langue" },
    { id: "a11y", label: "Accessibilité" },
    { id: "shortcuts", label: "Raccourcis" },
    { id: "password", label: "Mot de passe" },
    ...(canBrand ? [{ id: "branding", label: "Personnalisation entreprise" }] : []),
  ];

  return (
    <div style={{ display: "grid", gap: 16, maxWidth: 720 }}>
      <h1 style={{ margin: 0 }}>Administration</h1>
      <Tabs tabs={tabs} active={tab} onChange={(id) => setTab(id as typeof tab)} />

      {tab === "appearance" && (
        <Card pad="lg" className="nx-field" style={{ gap: 18 }}>
          <Field label="Thème">
            <div style={{ display: "flex", gap: 6 }}>
              {THEMES.map((t) => (
                <Button key={t.id} size="sm" variant={prefs.theme === t.id ? "primary" : "ghost"}
                        onClick={() => update({ theme: t.id })}>{t.label}</Button>
              ))}
            </div>
          </Field>

          <Field label="Densité" hint="La densité dense convient aux écrans opérationnels (§2, §8).">
            <div style={{ display: "flex", gap: 6 }}>
              {DENSITIES.map((d) => (
                <Button key={d.id} size="sm" variant={prefs.density === d.id ? "primary" : "ghost"}
                        onClick={() => update({ density: d.id })} title={d.hint}>{d.label}</Button>
              ))}
            </div>
          </Field>

          <Field label="Langue">
            <Select value={prefs.locale} onChange={(e) => update({ locale: e.target.value })}>
              {LOCALES.map((l) => <option key={l.id} value={l.id}>{l.label}</option>)}
            </Select>
          </Field>

          <Field label="Tableau de bord par défaut">
            <div style={{ display: "flex", gap: 6 }}>
              {(["summary", "detailed"] as const).map((m) => (
                <Button key={m} size="sm" variant={prefs.dashboardMode === m ? "primary" : "ghost"}
                        onClick={() => update({ dashboardMode: m })}>
                  {m === "summary" ? "Résumé" : "Détaillé"}
                </Button>
              ))}
            </div>
          </Field>

          <label style={{ display: "flex", gap: 10, alignItems: "center" }}>
            <input type="checkbox" checked={prefs.reducedMotion}
                   onChange={(e) => update({ reducedMotion: e.target.checked })} />
            Réduire les animations <HelpTip text="Respecte aussi le réglage système « prefers-reduced-motion » (§28)." />
          </label>

          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-xs)" }}>
            Ces préférences sont propres à votre compte et suivies sur tous vos appareils.
          </div>
        </Card>
      )}

      {tab === "a11y" && (
        <Card pad="lg" className="nx-field" style={{ gap: 18 }}>
          <SectionLabel>Accessibilité (WCAG 2.2 AA — §31-33)</SectionLabel>

          <label style={{ display: "flex", gap: 10, alignItems: "center" }}>
            <input type="checkbox" checked={prefs.highContrast}
                   onChange={(e) => update({ highContrast: e.target.checked })} />
            Contraste élevé <HelpTip text="Bords plus nets, encre renforcée, focus épais. La couleur ne porte jamais seule un état (§6)." />
          </label>

          <Field label="Taille du texte" hint="Agrandit toute l'interface (85 % à 150 %).">
            <div style={{ display: "flex", gap: 6 }}>
              {TEXT_SCALES.map((t) => (
                <Button key={t.v} size="sm" variant={Math.abs(prefs.textScale - t.v) < 0.02 ? "primary" : "ghost"}
                        onClick={() => update({ textScale: t.v })}>{t.label}</Button>
              ))}
            </div>
          </Field>

          <label style={{ display: "flex", gap: 10, alignItems: "center" }}>
            <input type="checkbox" checked={prefs.reducedMotion}
                   onChange={(e) => update({ reducedMotion: e.target.checked })} />
            Réduire les animations <HelpTip text="Respecte aussi le réglage système « prefers-reduced-motion » (§28, §49)." />
          </label>

          <Field label="Fuseau horaire" hint="Utilisé pour l'affichage des dates et heures (§34).">
            <Select value={prefs.timezone} onChange={(e) => update({ timezone: e.target.value })}>
              {TIMEZONES.map((z) => <option key={z} value={z}>{z}</option>)}
            </Select>
          </Field>
        </Card>
      )}

      {tab === "shortcuts" && <ShortcutsManager />}

      {tab === "password" && <PasswordForm />}

      {tab === "branding" && canBrand && <BrandingForm />}

      <DesignSystemFooter />
    </div>
  );
}

function DesignSystemFooter() {
  const [ds, setDs] = useState<DesignSystemVersion | null>(null);
  useEffect(() => { getDesignSystemVersion().then(setDs).catch(() => {}); }, []);
  if (!ds) return null;
  return (
    <div style={{ color: "var(--nx-text-3)", fontSize: "var(--nx-fs-xs)" }}>
      Design system NEXORA ONE v{ds.version} · {ds.wcagTarget} · {ds.componentCount} composants ·{" "}
      {ds.tokenGroups.reduce((s, g) => s + g.count, 0)} tokens
    </div>
  );
}

function ShortcutsManager() {
  const toast = useToast();
  const [items, setItems] = useState<Shortcut[]>([]);
  const [chord, setChord] = useState("");
  const [action, setAction] = useState("");
  const [label, setLabel] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => { listShortcuts().then(setItems).catch(() => {}); }, []);
  useEffect(load, [load]);

  async function add() {
    setBusy(true); setError(null);
    try {
      await saveShortcut({ chord: chord.trim(), action: action.trim(), label: label.trim() });
      setChord(""); setAction(""); setLabel(""); load();
      toast.success("Raccourci ajouté.");
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Erreur");
    } finally { setBusy(false); }
  }

  return (
    <Card pad="lg" className="nx-field" style={{ gap: 16 }}>
      <SectionLabel>Raccourcis clavier &amp; actions rapides (§11, §20, §37-38)</SectionLabel>
      <div style={{ display: "grid", gap: 6 }}>
        {items.map((s) => (
          <div key={s.id} style={{ display: "flex", alignItems: "center", gap: 10, fontSize: "var(--nx-fs-sm)" }}>
            <kbd style={{ background: "var(--nx-surface-2)", border: "1px solid var(--nx-border)", borderRadius: 6, padding: "1px 6px" }}>{s.chord}</kbd>
            <span style={{ flex: 1 }}>{s.label} <span style={{ color: "var(--nx-text-3)" }}>→ {s.action}</span></span>
            <Button size="sm" variant="ghost" onClick={() => deleteShortcut(s.id).then(load)}>Retirer</Button>
          </div>
        ))}
        {items.length === 0 && <div style={{ color: "var(--nx-text-2)" }}>Aucun raccourci personnalisé.</div>}
      </div>
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "end" }}>
        <Field label="Combinaison"><TextInput value={chord} placeholder="g v" maxLength={12} style={{ maxWidth: 90 }} onChange={(e) => setChord(e.target.value)} /></Field>
        <Field label="Libellé"><TextInput value={label} placeholder="Aller au POS" maxLength={60} style={{ maxWidth: 200 }} onChange={(e) => setLabel(e.target.value)} /></Field>
        <Field label="Action (route)"><TextInput value={action} placeholder="/pos" maxLength={120} style={{ maxWidth: 180 }} onChange={(e) => setAction(e.target.value)} /></Field>
        <Button loading={busy} disabled={!chord.trim() || !action.trim() || !label.trim()} onClick={add}>Ajouter</Button>
      </div>
      {error && <div className="nx-error-text" role="alert">{error}</div>}
    </Card>
  );
}

function BrandingForm() {
  const { branding, updateBranding } = usePreferences();
  const toast = useToast();
  const [form, setForm] = useState<Partial<CompanyBranding>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => { if (branding) setForm(branding); }, [branding]);

  const set = <K extends keyof CompanyBranding>(k: K, v: CompanyBranding[K]) =>
    setForm((f) => ({ ...f, [k]: v }));

  async function save() {
    setBusy(true); setError(null);
    try {
      await updateBranding({
        displayName: form.displayName ?? null,
        accentColor: form.accentColor ?? null,
        logoUrl: form.logoUrl ?? null,
        receiptFormat: form.receiptFormat,
        documentFooter: form.documentFooter ?? null,
      });
      toast.success("Personnalisation enregistrée.");
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Erreur");
    } finally {
      setBusy(false);
    }
  }

  const accent = form.accentColor && /^#[0-9A-Fa-f]{6}$/.test(form.accentColor) ? form.accentColor : "#00F5D4";

  return (
    <Card pad="lg" className="nx-field" style={{ gap: 18 }}>
      <SectionLabel>Identité de l'entreprise (§35)</SectionLabel>

      <Field label="Nom affiché" hint="Utilisé dans l'en-tête, les reçus et les documents.">
        <TextInput value={form.displayName ?? ""} maxLength={120}
                   onChange={(e) => set("displayName", e.target.value)} />
      </Field>

      <Field label="Couleur d'accent"
             hint="Format #RRGGBB. Refusée si le contraste devient insuffisant.">
        <div style={{ display: "flex", gap: 10, alignItems: "center" }}>
          <input type="color" value={accent} onChange={(e) => set("accentColor", e.target.value.toUpperCase())}
                 style={{ width: 44, height: 38, border: "1px solid var(--nx-border-strong)", borderRadius: 8, background: "none" }} />
          <TextInput value={form.accentColor ?? ""} placeholder="#00F5D4" style={{ maxWidth: 160 }}
                     onChange={(e) => set("accentColor", e.target.value)} />
          <Badge style={{ background: accent, color: "#04211d" }}>Aperçu</Badge>
        </div>
      </Field>

      <Field label="Logo (URL https ou data:image)"
             hint="Le logo n'est jamais requis — Nexora One affiche son monogramme par défaut.">
        <TextInput value={form.logoUrl ?? ""} onChange={(e) => set("logoUrl", e.target.value)} />
      </Field>

      <Field label="Format de reçu POS">
        <Select value={form.receiptFormat ?? "standard"}
                onChange={(e) => set("receiptFormat", e.target.value as CompanyBranding["receiptFormat"])}>
          <option value="standard">Standard</option>
          <option value="compact">Compact</option>
          <option value="detailed">Détaillé</option>
        </Select>
      </Field>

      <Field label="Pied de page des documents">
        <TextInput value={form.documentFooter ?? ""} maxLength={400}
                   onChange={(e) => set("documentFooter", e.target.value)} />
      </Field>

      {error && <div className="nx-error-text" role="alert">{error}</div>}
      <div><Button loading={busy} onClick={save}>Enregistrer</Button></div>
    </Card>
  );
}

/** Changement de mot de passe (recette Phase 55 AUTH-007). Toutes les sessions tombent ensuite,
 *  y compris celle-ci : on renvoie l'utilisateur à l'écran de connexion. */
function PasswordForm() {
  const { logout } = useAuth();
  const toast = useToast();
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function save() {
    setError(null);
    if (next !== confirm) { setError("Les deux saisies du nouveau mot de passe diffèrent."); return; }
    setBusy(true);
    try {
      await changePassword(current, next);
      toast.success("Mot de passe modifié — reconnectez-vous avec le nouveau mot de passe.");
      logout();
    } catch (e) {
      setError(e instanceof ApiError ? e.problem.detail ?? e.problem.title : "Modification impossible");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card>
      <SectionLabel>Changer mon mot de passe</SectionLabel>
      <div style={{ display: "grid", gap: 12, maxWidth: 380 }}>
        <Field label="Mot de passe actuel" required>
          <TextInput type="password" value={current} autoComplete="current-password" onChange={(e) => setCurrent(e.target.value)} />
        </Field>
        <Field label="Nouveau mot de passe" required hint="10 caractères minimum, au moins une lettre et un chiffre.">
          <TextInput type="password" value={next} autoComplete="new-password" onChange={(e) => setNext(e.target.value)} />
        </Field>
        <Field label="Confirmer le nouveau mot de passe" required error={error}>
          <TextInput type="password" value={confirm} autoComplete="new-password" invalid={Boolean(error)} onChange={(e) => setConfirm(e.target.value)} />
        </Field>
        <div style={{ color: "var(--nx-text-2)", fontSize: 13 }}>
          Toutes vos sessions ouvertes (autres appareils compris) seront fermées.
        </div>
        <Button onClick={save} loading={busy} disabled={!current || !next || !confirm}>Modifier le mot de passe</Button>
      </div>
    </Card>
  );
}
