import { useMemo, useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { Badge, Button, Card, ConfirmDialog, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  createRole, disableRole, enableRole, getPermissionCatalog, getRoles, setRolePermissions, updateRole,
  type AdminRole, type PermissionEntry,
} from "@/admin/adminApi";
import { groupPermissions, notGrantable } from "@/admin/adminRules";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;

/** Rôles : système (lecture, base de copie) et propres à l'entreprise (création, permissions, désactivation). */
export function RolesPanel() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const roles = useAsync(getRoles, [version]);
  const catalog = useAsync(getPermissionCatalog, []);
  const [editing, setEditing] = useState<AdminRole | "new" | null>(null);
  const [viewing, setViewing] = useState<AdminRole | null>(null);
  const [disabling, setDisabling] = useState<AdminRole | null>(null);
  const { error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);
  const list = roles.data ?? [];

  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setEditing("new")}>Nouveau rôle</Button>}>Rôles</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        Les rôles système ne se modifient pas : créez un rôle de l'entreprise à partir de l'un d'eux. Vous ne pouvez accorder
        à un rôle que des permissions que vous détenez vous-même.
      </div>
      {(error || roles.error) && <div role="alert" className="nx-error-text">{error ?? roles.error}</div>}
      <DataTable exportTitle="Rôles"
        columns={[
          { key: "n", header: "Rôle", render: (r) => <><b>{r.name}</b>{r.description && <div style={hint}>{r.description}</div>}</> },
          { key: "t", header: "Type", render: (r) => <Badge tone={r.systemRole ? undefined : "info"}>{r.systemRole ? "Système" : "Entreprise"}</Badge> },
          { key: "p", header: "Permissions", numeric: true, render: (r) => r.permissions.length },
          { key: "a", header: "Utilisateurs", numeric: true, render: (r) => r.assignmentCount },
          { key: "s", header: "Statut", render: (r) => <Badge tone={r.status === "active" ? "success" : "warning"}>{r.status === "active" ? "Actif" : "Désactivé"}</Badge> },
          { key: "x", header: "", render: (r) => (
            <div style={{ display: "flex", gap: 4, justifyContent: "flex-end", flexWrap: "wrap" }}>
              <Button size="sm" variant="ghost" onClick={() => setViewing(r)}>Permissions</Button>
              {!r.systemRole && r.status === "active" && <>
                <Button size="sm" variant="subtle" onClick={() => setEditing(r)}>Modifier</Button>
                <Button size="sm" variant="ghost" onClick={() => setDisabling(r)}>Désactiver</Button>
              </>}
              {!r.systemRole && r.status !== "active" && <Button size="sm" variant="ghost"
                onClick={async () => { if (await submit(() => enableRole(r.id).then(() => true))) { toast.success("Rôle réactivé."); reload(); } }}>Réactiver</Button>}
            </div>) },
        ]}
        rows={list} rowKey={(r) => r.id} empty={roles.loading ? "Chargement…" : "Aucun rôle."} />
      {editing && <RoleDialog role={editing === "new" ? null : editing} roles={list} catalog={catalog.data ?? []}
                              onClose={() => setEditing(null)} onDone={() => { setEditing(null); reload(); }} />}
      {viewing && <PermissionsView role={viewing} catalog={catalog.data ?? []} onClose={() => setViewing(null)} />}
      {disabling && <ConfirmDialog title={`Désactiver le rôle « ${disabling.name} »`} danger confirmLabel="Désactiver"
        message={disabling.assignmentCount > 0
          ? `${disabling.assignmentCount} utilisateur(s) ont ce rôle : ils perdront immédiatement les permissions qu'il accorde.`
          : "Le rôle ne pourra plus être attribué."}
        onCancel={() => setDisabling(null)}
        onConfirm={async () => {
          const r = disabling; setDisabling(null);
          if (await submit(() => disableRole(r.id, r.assignmentCount > 0).then(() => true))) { toast.success("Rôle désactivé."); reload(); }
        }} />}
    </Card>
  );
}

/** Lecture des permissions d'un rôle, groupées par module. */
function PermissionsView({ role, catalog, onClose }: { role: AdminRole; catalog: PermissionEntry[]; onClose: () => void }) {
  const groups = groupPermissions(catalog.filter((p) => role.permissions.includes(p.code)));
  return (
    <Modal title={`Permissions — ${role.name}`} onClose={onClose} footer={<Button onClick={onClose}>Fermer</Button>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(560px, 92vw)", maxHeight: "60vh", overflow: "auto" }}>
        {groups.length === 0 && <div style={hint}>Aucune permission.</div>}
        {groups.map((g) => (
          <div key={g.module}>
            <b>{g.label}</b>
            <ul style={{ margin: "4px 0 0", paddingLeft: 18 }}>
              {g.entries.map((p) => <li key={p.code}>{p.name} <code style={hint}>{p.code}</code></li>)}
            </ul>
          </div>
        ))}
      </div>
    </Modal>
  );
}

/** Création (depuis un rôle de base ou de zéro) ou modification : nom, description, permissions par module. */
function RoleDialog({ role, roles, catalog, onClose, onDone }: {
  role: AdminRole | null; roles: AdminRole[]; catalog: PermissionEntry[]; onClose: () => void; onDone: () => void;
}) {
  const toast = useToast();
  const [name, setName] = useState(role?.name ?? "");
  const [description, setDescription] = useState(role?.description ?? "");
  const [selected, setSelected] = useState<Set<string>>(new Set(role?.permissions ?? []));
  const [filter, setFilter] = useState("");
  const { busy, error, fieldErrors, submit, fail, setError } = useSubmit({ "roles.name": "name", "roles.duplicate": "name" });
  const groups = useMemo(() => groupPermissions(catalog, filter), [catalog, filter]);
  const original = role?.permissions ?? [];
  const toggle = (code: string) => setSelected((s) => { const n = new Set(s); if (n.has(code)) n.delete(code); else n.add(code); return n; });
  const copyFrom = (id: string) => { const base = roles.find((r) => r.id === id); if (base) setSelected(new Set(base.permissions)); };

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (name.trim().length < 2) return fail("name", "Nom obligatoire (2 caractères au moins).");
    if (selected.size === 0) { setError("Cochez au moins une permission."); return; }
    const refused = notGrantable(selected, catalog, original);
    if (refused.length) { setError(`Vous ne détenez pas : ${refused.slice(0, 5).join(", ")}${refused.length > 5 ? "…" : ""}.`); return; }
    const ok = await submit(async () => {
      if (role) {
        await updateRole(role.id, { name: name.trim(), description: description.trim() || null });
        await setRolePermissions(role.id, [...selected]);
      } else await createRole({ name: name.trim(), description: description.trim() || null, baseRoleId: null, permissions: [...selected] });
      return true;
    });
    if (ok) { toast.success(role ? "Rôle modifié : les utilisateurs qui l'ont voient leurs droits changer immédiatement." : `Rôle « ${name.trim()} » créé.`); onDone(); }
  }

  return (
    <Modal title={role ? `Modifier le rôle « ${role.name} »` : "Nouveau rôle"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(720px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))", gap: 8 }}>
          <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={60} placeholder="Ex. Manager magasin" onChange={(e) => setName(e.target.value)} /></Field>
          <Field label="Description"><TextInput value={description} maxLength={300} onChange={(e) => setDescription(e.target.value)} /></Field>
          <Field label="Partir des permissions de">
            <Select aria-label="Partir des permissions de" value="" onChange={(e) => copyFrom(e.target.value)}>
              <option value="">— Choisir un rôle —</option>
              {roles.filter((r) => r.id !== role?.id).map((r) => <option key={r.id} value={r.id}>{r.name}</option>)}
            </Select>
          </Field>
        </div>
        <div style={{ display: "flex", gap: 8, alignItems: "flex-end", flexWrap: "wrap" }}>
          <Field label="Filtrer les permissions"><TextInput value={filter} placeholder="Ex. ventes, stock, transfer" onChange={(e) => setFilter(e.target.value)} /></Field>
          <span style={hint}>{selected.size} permission(s) cochée(s). Les permissions grisées ne vous appartiennent pas.</span>
        </div>
        <div style={{ maxHeight: "45vh", overflow: "auto", border: "1px solid var(--nx-border)", borderRadius: 8, padding: 8, display: "grid", gap: 10 }}>
          {groups.map((g) => {
            const grantable = g.entries.filter((p) => p.grantable || original.includes(p.code));
            const all = grantable.length > 0 && grantable.every((p) => selected.has(p.code));
            return (
              <fieldset key={g.module} style={{ border: 0, padding: 0, margin: 0 }}>
                <legend style={{ display: "flex", gap: 8, alignItems: "center" }}>
                  <b>{g.label}</b>
                  {grantable.length > 0 && <label style={{ ...hint, display: "flex", gap: 4, alignItems: "center" }}>
                    <input type="checkbox" checked={all} onChange={() => setSelected((s) => {
                      const n = new Set(s); for (const p of grantable) { if (all) n.delete(p.code); else n.add(p.code); } return n;
                    })} /> tout
                  </label>}
                </legend>
                <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))", gap: 2 }}>
                  {g.entries.map((p) => {
                    const can = p.grantable || original.includes(p.code);
                    return (
                      <label key={p.code} title={p.code} style={{ display: "flex", gap: 6, alignItems: "center", fontSize: 13, opacity: can ? 1 : 0.5 }}>
                        <input type="checkbox" aria-label={p.code} disabled={!can && !selected.has(p.code)} checked={selected.has(p.code)} onChange={() => toggle(p.code)} />
                        {p.name}
                      </label>
                    );
                  })}
                </div>
              </fieldset>
            );
          })}
        </div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
