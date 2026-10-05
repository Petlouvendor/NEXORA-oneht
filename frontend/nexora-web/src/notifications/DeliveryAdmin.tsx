import { useMemo, useState, type FormEvent } from "react";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { useSubmit } from "@/shared/forms";
import { dateTime } from "@/shared/format";
import { Badge, Button, Card, DataTable, Drawer, Field, KpiCard, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import {
  CHANNEL_LABEL, OUTBOX_STATUS, TEST_LABEL, addressHint, canRetry, deliverySummary, dispatchNow, listOutbox, listProviders, listTemplates,
  outboxLog, renderTemplate, requeueNotConfigured, retryOutbox, sendTestMessage, testProvider, upsertTemplate,
  type ChannelProvider, type MessageTemplate, type OutboxEntry,
} from "@/notifications/notificationsApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;

/** Envois : fournisseurs, tests réels, file, journal et relances (finalisation phase 16). */
export function DeliveryAdmin() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const providers = useAsync(listProviders, [version]);
  const summary = useAsync(deliverySummary, [version]);
  const [status, setStatus] = useState("");
  const [channel, setChannel] = useState("");
  const outbox = useAsync(() => listOutbox(status || undefined, channel || undefined), [status, channel, version]);
  const [testing, setTesting] = useState<ChannelProvider | null>(null);
  const [detail, setDetail] = useState<OutboxEntry | null>(null);
  const { busy, error, submit } = useSubmit();
  const reload = () => setVersion((v) => v + 1);
  const configured = useMemo(() => Object.fromEntries((providers.data ?? []).map((p) => [p.channel, p.configured])), [providers.data]);
  const s = summary.data;

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      {(providers.error || error) && <div role="alert" className="nx-error-text">{providers.error ?? error}</div>}
      <Card>
        <SectionLabel>Fournisseurs d'envoi</SectionLabel>
        <div style={{ ...hint, marginBottom: 8 }}>
          Les fournisseurs sont configurés sur le serveur (section <code>Notifications</code>). Sans fournisseur, rien n'est envoyé :
          le message reste « fournisseur non configuré » et se relance une fois le fournisseur configuré.
        </div>
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))", gap: 12 }}>
          {(providers.data ?? []).map((p) => (
            <div key={p.channel} className="nx-card" style={{ display: "grid", gap: 6, alignContent: "start" }}>
              <div style={{ display: "flex", justifyContent: "space-between", gap: 8, alignItems: "center" }}>
                <b>{CHANNEL_LABEL[p.channel] ?? p.channel}</b>
                <Badge tone={p.configured ? "success" : "warning"}>{p.configured ? p.provider : "Fournisseur non configuré"}</Badge>
              </div>
              <div style={hint}>{p.configurationSummary}</div>
              {p.lastTestStatus && <div><Badge tone={TEST_LABEL[p.lastTestStatus].tone}>{TEST_LABEL[p.lastTestStatus].label}</Badge>
                <div style={hint}>{p.lastTestMessage} — {dateTime(p.lastTestedAt)}</div></div>}
              {p.notConfiguredWaiting > 0 && <div style={hint}>{p.notConfiguredWaiting} message(s) en attente d'un fournisseur.</div>}
              <div style={{ display: "flex", gap: 4, flexWrap: "wrap" }}>
                <Button size="sm" variant="subtle" disabled={busy} onClick={async () => {
                  const r = await submit(() => testProvider(p.channel));
                  if (r) { (r.status === "CONNECTED" ? toast.success : toast.error)(`${CHANNEL_LABEL[p.channel]} — ${TEST_LABEL[r.status].label} : ${r.message}`); reload(); }
                }}>Tester la connexion</Button>
                {p.configured && <Button size="sm" variant="ghost" onClick={() => setTesting(p)}>Message de test</Button>}
                {p.configured && p.notConfiguredWaiting > 0 && <Button size="sm" variant="ghost" disabled={busy} onClick={async () => {
                  const n = await submit(() => requeueNotConfigured(p.channel));
                  if (n != null) { toast.success(`${n} message(s) remis en file.`); reload(); }
                }}>Relancer les messages en attente</Button>}
              </div>
            </div>
          ))}
        </div>
      </Card>

      {s && (
        <Card>
          <SectionLabel action={<Button size="sm" disabled={busy} onClick={async () => {
            const r = await submit(() => dispatchNow());
            if (r) { toast.success(`${r.dispatched} envoi(s) traité(s).`); reload(); }
          }}>Traiter la file maintenant</Button>}>File d'envoi</SectionLabel>
          {!s.schedulerEnabled && <div className="nx-error-text" style={{ marginBottom: 8 }}>
            Le planificateur est arrêté sur ce serveur : les envois restent en file tant que personne ne les traite.
            {s.oldestPendingAt ? ` Plus ancien en attente : ${dateTime(s.oldestPendingAt)}.` : ""}
          </div>}
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 10 }}>
            <KpiCard label="En file" value={s.pending + s.sending} />
            <KpiCard label="Envoyés" value={s.sent} />
            <KpiCard label="Nouvel essai prévu" value={s.failed} tone={s.failed ? "warning" : undefined} />
            <KpiCard label="Abandonnés" value={s.dead} tone={s.dead ? "warning" : undefined} />
            <KpiCard label="Sans fournisseur" value={s.notConfigured} tone={s.notConfigured ? "warning" : undefined} />
          </div>
        </Card>
      )}

      <Card>
        <SectionLabel>Journal des envois</SectionLabel>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap", marginBottom: 8 }}>
          <Field label="État"><Select value={status} onChange={(e) => setStatus(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(OUTBOX_STATUS).map(([k, v]) => <option key={k} value={k}>{v.label}</option>)}
          </Select></Field>
          <Field label="Canal"><Select value={channel} onChange={(e) => setChannel(e.target.value)}>
            <option value="">Tous</option>
            {Object.entries(CHANNEL_LABEL).filter(([k]) => k !== "in_app").map(([k, v]) => <option key={k} value={k}>{v}</option>)}
          </Select></Field>
        </div>
        {outbox.error && <div role="alert" className="nx-error-text">{outbox.error}</div>}
        <DataTable exportTitle="Journal des envois"
          columns={[
            { key: "d", header: "Créé", render: (e) => dateTime(e.createdAt) },
            { key: "c", header: "Canal", render: (e) => CHANNEL_LABEL[e.channel] ?? e.channel },
            { key: "r", header: "Destinataire", render: (e) => <>{e.recipientName ?? "—"}<div style={hint}>{e.addressMasked ?? "sans adresse"}</div></> },
            { key: "o", header: "Objet", render: (e) => e.subject },
            { key: "s", header: "État", render: (e) => <><Badge tone={OUTBOX_STATUS[e.status]?.tone}>{OUTBOX_STATUS[e.status]?.label ?? e.status}</Badge>
              {e.lastError && <div style={hint}>{e.lastError}</div>}</> },
            { key: "a", header: "Essais", numeric: true, render: (e) => `${e.attempts}/${e.maxAttempts}` },
            { key: "x", header: "", render: (e) => (
              <span style={{ display: "inline-flex", gap: 4 }}>
                <Button size="sm" variant="ghost" onClick={() => setDetail(e)}>Détail</Button>
                {canRetry(e, configured) && <Button size="sm" variant="subtle" disabled={busy} onClick={async () => {
                  if (await submit(() => retryOutbox(e.id).then(() => true))) { toast.success("Envoi remis en file."); reload(); }
                }}>Relancer</Button>}
              </span>) },
          ]}
          rows={outbox.data ?? []} rowKey={(e) => e.id} empty={outbox.loading ? "Chargement…" : "Aucun envoi."} />
      </Card>
      {testing && <TestMessageDialog provider={testing} onClose={() => setTesting(null)} onDone={(e) => {
        setTesting(null); reload();
        if (e.status === "sent") toast.success(`Message de test remis au fournisseur ${e.provider}.`);
        else toast.error(`${OUTBOX_STATUS[e.status]?.label ?? e.status} : ${e.lastError ?? ""}`);
      }} />}
      {detail && <DeliveryDetail entry={detail} onClose={() => setDetail(null)} />}
    </div>
  );
}

function TestMessageDialog({ provider, onClose, onDone }: { provider: ChannelProvider; onClose: () => void; onDone: (e: OutboxEntry) => void }) {
  const [address, setAddress] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "notifications.test_address": "address" });
  async function send(e?: FormEvent) {
    e?.preventDefault();
    if (!address.trim()) return fail("address", "Adresse obligatoire.");
    const r = await submit(() => sendTestMessage(provider.channel, address.trim()));
    if (r) onDone(r);
  }
  return (
    <Modal title={`Message de test — ${CHANNEL_LABEL[provider.channel]}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => send()}>Envoyer</Button></>}>
      <form noValidate onSubmit={send} style={{ display: "grid", gap: 10 }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={hint}>Un vrai message est remis au fournisseur ({provider.provider}) ; son résultat est inscrit au journal.</div>
        <Field label="Destinataire" required error={fieldErrors.address}>
          <TextInput value={address} placeholder={addressHint(provider.channel)} onChange={(e) => setAddress(e.target.value)} />
        </Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function DeliveryDetail({ entry, onClose }: { entry: OutboxEntry; onClose: () => void }) {
  const log = useAsync(() => outboxLog(entry.id), [entry.id]);
  return (
    <Drawer title={`Envoi — ${entry.subject}`} onClose={onClose}>
      <div style={{ display: "grid", gap: 10 }}>
        <div><Badge tone={OUTBOX_STATUS[entry.status]?.tone}>{OUTBOX_STATUS[entry.status]?.label ?? entry.status}</Badge></div>
        <div style={hint}>{CHANNEL_LABEL[entry.channel]} · {entry.recipientName ?? "—"} · {entry.addressMasked ?? "sans adresse"} · catégorie {entry.category}</div>
        {entry.status === "failed" && <div style={hint}>Prochain essai : {dateTime(entry.nextAttemptAt)}</div>}
        {log.error && <div role="alert" className="nx-error-text">{log.error}</div>}
        <DataTable exportTitle={`Tentatives — ${entry.subject}`}
          columns={[
            { key: "n", header: "Essai", numeric: true, render: (l) => l.attempt },
            { key: "d", header: "Quand", render: (l) => dateTime(l.createdAt) },
            { key: "p", header: "Fournisseur", render: (l) => l.provider ?? "—" },
            { key: "s", header: "Résultat", render: (l) => <Badge tone={OUTBOX_STATUS[l.status]?.tone}>{OUTBOX_STATUS[l.status]?.label ?? l.status}</Badge> },
            { key: "e", header: "Erreur", render: (l) => l.error ?? "—" },
            { key: "t", header: "Durée", numeric: true, render: (l) => (l.durationMs != null ? `${l.durationMs} ms` : "—") },
          ]}
          rows={log.data ?? []} rowKey={(l) => `${l.attempt}-${l.createdAt}`} empty={log.loading ? "Chargement…" : "Aucune tentative pour l'instant."} />
      </div>
    </Drawer>
  );
}

/** Modèles de message : modèles système en lecture seule, copie propre à l'entreprise, aperçu avec variables. */
export function TemplatesAdmin() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const templates = useAsync(listTemplates, [version]);
  const [editing, setEditing] = useState<MessageTemplate | "new" | null>(null);
  return (
    <Card>
      <SectionLabel action={<Button size="sm" onClick={() => setEditing("new")}>Nouveau modèle</Button>}>Modèles de message</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        Variables entre doubles accolades, ex. <code>{"{{customer.name}}"}</code> : seules les variables fournies sont remplacées, sans exécution
        de code. Modifier un modèle système crée la version propre à l'entreprise.
      </div>
      {templates.error && <div role="alert" className="nx-error-text">{templates.error}</div>}
      <DataTable exportTitle="Modèles de message"
        columns={[
          { key: "k", header: "Modèle", render: (t) => <button type="button" className="nx-linkbtn" onClick={() => setEditing(t)}>{t.key}</button> },
          { key: "c", header: "Canal", render: (t) => CHANNEL_LABEL[t.channel] ?? t.channel },
          { key: "l", header: "Langue", render: (t) => t.locale },
          { key: "s", header: "Objet", render: (t) => t.subject },
          { key: "o", header: "Origine", render: (t) => (t.isSystem ? <Badge>Système</Badge> : <Badge tone="info">Entreprise · v{t.version}</Badge>) },
        ]}
        rows={templates.data ?? []} rowKey={(t) => t.id} empty={templates.loading ? "Chargement…" : "Aucun modèle."} />
      {editing && <TemplateDialog template={editing === "new" ? null : editing} onClose={() => setEditing(null)}
        onDone={() => { setEditing(null); setVersion((v) => v + 1); toast.success("Modèle enregistré."); }} />}
    </Card>
  );
}

function TemplateDialog({ template, onClose, onDone }: { template: MessageTemplate | null; onClose: () => void; onDone: () => void }) {
  const [key, setKey] = useState(template?.key ?? "");
  const [channel, setChannel] = useState(template?.channel ?? "email");
  const [locale, setLocale] = useState(template?.locale ?? "fr");
  const [subject, setSubject] = useState(template?.subject ?? "");
  const [body, setBody] = useState(template?.body ?? "");
  const [preview, setPreview] = useState<{ subject: string; body: string; missingVariables: string[] } | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "notifications.template_key": "key", "notifications.template_body": "body" });
  const variables = useMemo(() => [...new Set([...`${subject} ${body}`.matchAll(/\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}/g)].map((m) => m[1]))], [subject, body]);
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!/^[a-z0-9][a-z0-9._-]{1,79}$/.test(key.trim())) return fail("key", "Clé : minuscules, chiffres, points, tirets.");
    if (!subject.trim()) return fail("subject", "Objet obligatoire.");
    if (!body.trim()) return fail("body", "Contenu obligatoire.");
    if (await submit(() => upsertTemplate({ key: key.trim(), channel, locale, subject, body, description: template?.description ?? null, variables, status: "published" }).then(() => true))) onDone();
  }
  async function showPreview() {
    if (!template && !key.trim()) return fail("key", "Enregistrez d'abord le modèle pour l'aperçu.");
    const r = await submit(() => renderTemplate({ key: key.trim(), channel, locale, variables: Object.fromEntries(variables.map((v) => [v, `‹${v}›`])) }));
    if (r) setPreview(r);
  }
  return (
    <Modal title={template ? `Modèle ${template.key}` : "Nouveau modèle"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button>
        {template && <Button variant="subtle" disabled={busy} onClick={showPreview}>Aperçu (version enregistrée)</Button>}
        <Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(620px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: 8 }}>
          <Field label="Clé" required error={fieldErrors.key}><TextInput value={key} disabled={!!template} onChange={(e) => setKey(e.target.value)} /></Field>
          <Field label="Canal"><Select value={channel} disabled={!!template} onChange={(e) => setChannel(e.target.value)}>
            {["in_app", "email", "sms", "push", "whatsapp", "webhook"].map((c) => <option key={c} value={c}>{CHANNEL_LABEL[c]}</option>)}
          </Select></Field>
          <Field label="Langue"><Select value={locale} disabled={!!template} onChange={(e) => setLocale(e.target.value)}>
            <option value="fr">Français</option><option value="ht">Kreyòl</option><option value="en">English</option>
          </Select></Field>
        </div>
        <Field label="Objet" required error={fieldErrors.subject}><TextInput value={subject} maxLength={200} onChange={(e) => setSubject(e.target.value)} /></Field>
        <Field label="Contenu" required error={fieldErrors.body}>
          <textarea className="nx-textarea" rows={8} value={body} onChange={(e) => setBody(e.target.value)} />
        </Field>
        <div style={hint}>Variables utilisées : {variables.length ? variables.join(", ") : "aucune"}.</div>
        {template?.isSystem && <div style={hint}>Modèle système : l'enregistrement crée la version propre à votre entreprise.</div>}
        {preview && (
          <div className="nx-card">
            <b>{preview.subject}</b>
            <div style={{ whiteSpace: "pre-wrap", marginTop: 6 }}>{preview.body}</div>
            {preview.missingVariables.length > 0 && <div style={hint}>Variables non fournies : {preview.missingVariables.join(", ")}</div>}
          </div>
        )}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
