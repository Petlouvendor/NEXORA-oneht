import { useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { Badge, Button, Card, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { getDefinitions, getEffectiveSettings, setSetting, type EffectiveSetting, type SettingDefinition } from "@/admin/adminApi";

const SOURCE: Record<string, string> = { definition: "Valeur par défaut", system: "Système", group: "Groupe", company: "Entreprise", establishment: "Établissement" };
const show = (v: unknown) => (typeof v === "boolean" ? (v ? "Oui" : "Non") : v === null || v === undefined ? "—" : typeof v === "object" ? JSON.stringify(v) : String(v));

/** Paramètres de l'entreprise : valeur effective, origine, modification typée ; motif obligatoire pour un paramètre sensible. */
export function SettingsPanel() {
  const [version, setVersion] = useState(0);
  const settings = useAsync(getEffectiveSettings, [version]);
  const defs = useAsync(getDefinitions, []);
  const [editing, setEditing] = useState<{ s: EffectiveSetting; d: SettingDefinition } | null>(null);
  const def = (key: string) => defs.data?.find((d) => d.key === key);

  return (
    <Card>
      <SectionLabel>Paramètres</SectionLabel>
      {settings.error && <div role="alert" className="nx-error-text">{settings.error}</div>}
      <DataTable exportTitle="Paramètres"
        columns={[
          { key: "c", header: "Catégorie", render: (s) => s.category },
          { key: "k", header: "Paramètre", render: (s) => <>{def(s.key)?.description ?? s.key}<div style={{ fontSize: 12, color: "var(--nx-text-2)" }}><code>{s.key}</code></div></> },
          { key: "v", header: "Valeur", render: (s) => <b>{show(s.value)}</b> },
          { key: "o", header: "Origine", render: (s) => <Badge tone={s.source === "definition" ? undefined : "info"}>{SOURCE[s.source] ?? s.source}</Badge> },
          { key: "f", header: "", render: (s) => <>{def(s.key)?.isSensitive && <Badge tone="warning">Sensible</Badge>}</> },
          { key: "x", header: "", render: (s) => def(s.key) && <Button size="sm" variant="subtle" onClick={() => setEditing({ s, d: def(s.key)! })}>Modifier</Button> },
        ]}
        rows={settings.data ?? []} rowKey={(s) => s.key} empty={settings.loading ? "Chargement…" : "—"} />
      {editing && <SettingDialog setting={editing.s} definition={editing.d} onClose={() => setEditing(null)}
                                 onDone={() => { setEditing(null); setVersion((v) => v + 1); }} />}
    </Card>
  );
}

function SettingDialog({ setting, definition: d, onClose, onDone }: {
  setting: EffectiveSetting; definition: SettingDefinition; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [raw, setRaw] = useState(typeof setting.value === "object" && setting.value !== null ? JSON.stringify(setting.value) : String(setting.value ?? ""));
  const [note, setNote] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "settings.value": "value", "settings.reason_required": "note" });
  const allowed = Array.isArray(d.allowedValues) ? d.allowedValues.map(String) : [];

  async function save(e?: FormEvent) {
    e?.preventDefault();
    let value: unknown = raw;
    if (d.valueType === "boolean") value = raw === "true";
    else if (d.valueType === "number") {
      const n = Number(raw.replace(",", "."));
      if (raw.trim() === "" || !Number.isFinite(n)) return fail("value", "Nombre attendu.");
      value = n;
    } else if (d.valueType === "json") {
      try { value = JSON.parse(raw); } catch { return fail("value", "JSON invalide."); }
    }
    if (d.isSensitive && note.trim().length < 3) return fail("note", "Paramètre sensible : motif obligatoire.");
    const r = await submit(() => setSetting({ key: setting.key, scope: "company", scopeId: null, value, note: note.trim() || null }));
    if (!r) return;
    r.warnings.forEach((w) => toast.push(w, "warning"));
    toast.success("Paramètre enregistré pour l'entreprise.");
    onDone();
  }

  return (
    <Modal title={d.description || setting.key} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}><code>{setting.key}</code> · valeur actuelle : {show(setting.value)} ({SOURCE[setting.source] ?? setting.source})</div>
        <Field label="Nouvelle valeur" required error={fieldErrors.value}>
          {d.valueType === "boolean"
            ? <Select value={raw} onChange={(e) => setRaw(e.target.value)}><option value="true">Oui</option><option value="false">Non</option></Select>
            : allowed.length > 0
              ? <Select value={raw} onChange={(e) => setRaw(e.target.value)}>{allowed.map((a) => <option key={a} value={a}>{a}</option>)}</Select>
              : <TextInput value={raw} inputMode={d.valueType === "number" ? "decimal" : undefined} onChange={(e) => setRaw(e.target.value)} />}
        </Field>
        <Field label="Motif" required={d.isSensitive} error={fieldErrors.note}><TextInput value={note} maxLength={300} onChange={(e) => setNote(e.target.value)} /></Field>
        {d.affectsFuture && <div style={{ fontSize: 12, color: "var(--nx-text-2)" }}>S'applique aux opérations futures ; l'historique n'est pas modifié.</div>}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
