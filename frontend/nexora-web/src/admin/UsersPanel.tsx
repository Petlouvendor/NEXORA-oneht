import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateShort, dateTime, relative, todayIso } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, EmptyState, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  createUser, getRoles, grantAssignment, listAssignments, listUsers, revokeAssignment, setUserStatus,
  type Assignment, type UserSummary,
} from "@/admin/adminApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const STATUS: Record<string, string> = { active: "Actif", suspended: "Suspendu", locked: "Verrouillé" };
// Rôles proposés quand la liste des rôles n'est pas lisible (permission admin.roles absente).
const FALLBACK_ROLES = ["salesperson", "cashier", "stock_manager", "purchasing_manager", "accountant", "manager", "auditor"];
const PROTECTED = ["super_administrator", "group_administrator"];

function useAssignableRoles() {
  const { hasPermission } = useAuth();
  const roles = useAsync(() => (hasPermission("admin.roles") ? getRoles() : Promise.resolve(null)), []);
  return roles.data
    ? roles.data.filter((r) => r.status === "active" && !PROTECTED.includes(r.name)).map((r) => ({ name: r.name, description: r.description }))
    : FALLBACK_ROLES.map((name) => ({ name, description: null as string | null }));
}

/** Utilisateurs : création (avec un premier rôle), rôles attribués (durée, motif), suspension. */
export function UsersPanel() {
  const { hasPermission, user } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const users = useAsync(() => (hasPermission("admin.manage_users") ? listUsers() : Promise.resolve([])), [version]);
  const [creating, setCreating] = useState(false);
  const [openUser, setOpenUser] = useState<UserSummary | null>(null);
  const [suspending, setSuspending] = useState<UserSummary | null>(null);
  const { error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);

  if (!hasPermission("admin.manage_users")) return <Card><EmptyState title="Utilisateurs" hint="Réservé à la permission admin.manage_users." /></Card>;

  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setCreating(true)}>Nouvel utilisateur</Button>}>Utilisateurs</SectionLabel>
      {(error || users.error) && <div role="alert" className="nx-error-text">{error ?? users.error}</div>}
      <DataTable exportTitle="Utilisateurs"
        columns={[
          { key: "n", header: "Nom", render: (u) => <button type="button" className="nx-linkbtn" onClick={() => setOpenUser(u)}>{u.fullName ?? u.email}</button> },
          { key: "e", header: "E-mail", render: (u) => u.email },
          { key: "s", header: "Statut", render: (u) => <Badge tone={u.status === "active" ? "success" : "warning"}>{STATUS[u.status] ?? u.status}</Badge> },
          { key: "m", header: "Double authentification", render: (u) => (u.mfaEnabled ? "Oui" : "Non") },
          { key: "l", header: "Dernière connexion", render: (u) => (u.lastLoginAt ? relative(u.lastLoginAt) : "Jamais") },
          { key: "x", header: "", render: (u) => u.id === user?.id ? null : (
            <div style={{ display: "flex", gap: 4, justifyContent: "flex-end" }}>
              <Button size="sm" variant="subtle" onClick={() => setOpenUser(u)}>Rôles</Button>
              {u.status === "active"
                ? <Button size="sm" variant="ghost" onClick={() => setSuspending(u)}>Suspendre</Button>
                : <Button size="sm" variant="ghost" onClick={async () => {
                  if (await submit(() => setUserStatus(u.id, "active").then(() => true))) { toast.success("Compte réactivé."); reload(); }
                }}>Réactiver</Button>}
            </div>) },
        ]}
        rows={users.data ?? []} rowKey={(u) => u.id} empty={users.loading ? "Chargement…" : "Aucun utilisateur dans votre périmètre."} />
      {creating && <CreateUserDialog onClose={() => setCreating(false)} onDone={() => { setCreating(false); reload(); }} />}
      {openUser && <AssignmentsDrawer key={openUser.id} user={openUser} onClose={() => setOpenUser(null)} />}
      {suspending && <ConfirmDialog title={`Suspendre ${suspending.email}`} danger confirmLabel="Suspendre"
        message="Toutes ses sessions sont fermées immédiatement ; il ne peut plus se connecter jusqu'à sa réactivation."
        onCancel={() => setSuspending(null)}
        onConfirm={async () => {
          const u = suspending; setSuspending(null);
          if (await submit(() => setUserStatus(u.id, "suspended").then(() => true))) { toast.success("Compte suspendu."); reload(); }
        }} />}
    </Card>
  );
}

function CreateUserDialog({ onClose, onDone }: { onClose: () => void; onDone: () => void }) {
  const { activeCompanyId } = useAuth();
  const toast = useToast();
  const roles = useAssignableRoles();
  const [email, setEmail] = useState("");
  const [fullName, setFullName] = useState("");
  const [password, setPassword] = useState("");
  const [role, setRole] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "identity.email": "email", "identity.email_exists": "email", "identity.full_name": "fullName", "identity.password": "password",
    "identity.escalation": "role", "identity.role": "role",
  });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim())) return fail("email", "Adresse e-mail invalide.");
    if (fullName.trim().length < 2) return fail("fullName", "Nom complet requis.");
    if (password.length < 10 || !/[A-Za-z]/.test(password) || !/\d/.test(password)) return fail("password", "10 caractères minimum, avec au moins une lettre et un chiffre.");
    if (!role) return fail("role", "Choisissez un rôle.");
    const ok = await submit(() => createUser({
      email: email.trim(), fullName: fullName.trim(), password,
      initialAssignment: { roleName: role, scopeType: "COMPANY", companyId: activeCompanyId, reason: "Création de compte" },
    }).then(() => true));
    if (ok) { toast.success(`Compte ${email.trim()} créé. Transmettez le mot de passe provisoire par un canal sûr.`); onDone(); }
  }

  return (
    <Modal title="Nouvel utilisateur" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer le compte</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(440px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="E-mail" required error={fieldErrors.email}><TextInput type="email" value={email} onChange={(e) => setEmail(e.target.value)} /></Field>
        <Field label="Nom complet" required error={fieldErrors.fullName}><TextInput value={fullName} maxLength={120} onChange={(e) => setFullName(e.target.value)} /></Field>
        <Field label="Mot de passe provisoire" required error={fieldErrors.password} hint="10 caractères minimum, au moins une lettre et un chiffre.">
          <TextInput type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
        </Field>
        <Field label="Rôle" required error={fieldErrors.role} hint="Vous ne pouvez attribuer qu'un rôle dont vous détenez toutes les permissions.">
          <Select value={role} onChange={(e) => setRole(e.target.value)}>
            <option value="">— Choisir —</option>
            {roles.map((r) => <option key={r.name} value={r.name}>{r.name}{r.description ? ` — ${r.description}` : ""}</option>)}
          </Select>
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Rôles attribués à un utilisateur dans l'entreprise active : ajout (éventuellement temporaire), retrait motivé. */
function AssignmentsDrawer({ user, onClose }: { user: UserSummary; onClose: () => void }) {
  const { activeCompanyId } = useAuth();
  const toast = useToast();
  const roles = useAssignableRoles();
  const [version, setVersion] = useState(0);
  const assignments = useAsync(() => listAssignments(user.id), [user.id, version]);
  const [role, setRole] = useState("");
  const [until, setUntil] = useState("");
  const [reason, setReason] = useState("");
  const [revoking, setRevoking] = useState<Assignment | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "identity.escalation": "role", "identity.role": "role", "identity.validity": "until" });
  const reload = () => setVersion((v) => v + 1);

  async function grant(e?: FormEvent) {
    e?.preventDefault();
    if (!role) return fail("role", "Choisissez un rôle.");
    if (until && until <= todayIso()) return fail("until", "La fin doit être dans le futur.");
    if (reason.trim().length < 3) return fail("reason", "Motif obligatoire.");
    const ok = await submit(() => grantAssignment(user.id, {
      roleName: role, scopeType: "COMPANY", companyId: activeCompanyId, establishmentId: null,
      validFrom: null, validUntil: until ? new Date(`${until}T23:59:59`).toISOString() : null, reason: reason.trim(),
    }).then(() => true));
    if (ok) { toast.success(`Rôle « ${role} » attribué${until ? ` jusqu'au ${dateShort(until)}` : ""}.`); setRole(""); setUntil(""); setReason(""); reload(); }
  }

  return (
    <Drawer title={`Rôles — ${user.fullName ?? user.email}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 14 }}>
        {(error || assignments.error) && <div role="alert" className="nx-error-text">{error ?? assignments.error}</div>}
        <DataTable exportTitle={`Rôles — ${user.email}`}
          columns={[
            { key: "r", header: "Rôle", render: (a) => <b>{a.roleName}</b> },
            { key: "p", header: "Périmètre", render: (a) => (a.scopeType === "GROUP" ? "Groupe" : a.scopeType === "ESTABLISHMENT" ? "Établissement" : "Entreprise") },
            { key: "v", header: "Validité", render: (a) => a.validUntil ? `jusqu'au ${dateShort(a.validUntil)}` : "Permanente" },
            { key: "s", header: "État", render: (a) => <Badge tone={a.active ? "success" : "warning"}>{a.active ? "En vigueur" : a.revokedAt ? `Retiré le ${dateShort(a.revokedAt)}` : "Expiré"}</Badge> },
            { key: "m", header: "Motif", render: (a) => a.reason ?? "—" },
            { key: "d", header: "Attribué le", render: (a) => dateTime(a.grantedAt) },
            { key: "x", header: "", render: (a) => a.active ? <Button size="sm" variant="ghost" onClick={() => setRevoking(a)}>Retirer</Button> : null },
          ]}
          rows={assignments.data ?? []} rowKey={(a) => a.id} empty={assignments.loading ? "Chargement…" : "Aucun rôle."} />
        <form onSubmit={grant} style={{ display: "grid", gap: 8 }}>
            <SectionLabel>Attribuer un rôle</SectionLabel>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 8 }}>
              <Field label="Rôle" required error={fieldErrors.role}>
                <Select value={role} onChange={(e) => setRole(e.target.value)}>
                  <option value="">— Choisir —</option>
                  {roles.map((r) => <option key={r.name} value={r.name}>{r.name}</option>)}
                </Select>
              </Field>
              <Field label="Jusqu'au" error={fieldErrors.until} hint="Vide : permanent."><TextInput type="date" min={todayIso()} value={until} onChange={(e) => setUntil(e.target.value)} /></Field>
            </div>
            <Field label="Motif" required error={fieldErrors.reason}><TextInput value={reason} maxLength={300} placeholder="Ex. remplacement pendant les congés" onChange={(e) => setReason(e.target.value)} /></Field>
            <div><Button type="submit" size="sm" disabled={busy}>Attribuer</Button></div>
            <div style={hint}>Un rôle ne s'attribue que si vous détenez toutes ses permissions. Effet immédiat.</div>
          </form>
      </div>
      {revoking && <ConfirmDialog title={`Retirer le rôle « ${revoking.roleName} »`} requireReason danger confirmLabel="Retirer"
        message="L'utilisateur perd immédiatement les permissions de ce rôle." onCancel={() => setRevoking(null)}
        onConfirm={async (r) => {
          const a = revoking; setRevoking(null);
          if (await submit(() => revokeAssignment(a.id, r).then(() => true))) { toast.success("Rôle retiré."); reload(); }
        }} />}
    </Drawer>
  );
}
