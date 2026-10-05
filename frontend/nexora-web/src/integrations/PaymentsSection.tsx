import { useEffect, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { newOperationId, num, useSubmit } from "@/shared/forms";
import { dateTime, money } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { listCustomers, type Customer } from "@/parties/partiesApi";
import { getFinancialAccounts } from "@/finance/financeApi";
import {
  INTENT_STATUS, TEST_STATUS, createIntent, expireStaleIntents, intentActions, listConnectors, listIntents, listPaymentProviders,
  needsCredentials, refundIntent, setConnectorCredentials, setConnectorStatus, simulateProviderResponse, testConnector, upsertConnector,
  verifyIntent, type Connector, type PaymentIntent,
} from "@/integrations/integrationsApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const row = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 8 } as const;

/** SIMULATION ou RÉEL, toujours visible (plan §31). */
export function ModeBadge({ mode }: { mode: "simulation" | "real" }) {
  return mode === "simulation"
    ? <Badge tone="warning">SIMULATION</Badge>
    : <Badge tone="success">RÉEL</Badge>;
}

/** Connecteurs et paiements en ligne (finalisation phase 15). */
export function PaymentsSection({ onChanged }: { onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const connectors = useAsync(listConnectors, [version]);
  const canPayments = hasPermission("integrations.payments");
  const intents = useAsync(() => (canPayments ? listIntents() : Promise.resolve([] as PaymentIntent[])), [version]);
  const canConfigure = hasPermission("integrations.configure");
  const [creating, setCreating] = useState(false);
  const [credentialsFor, setCredentialsFor] = useState<Connector | null>(null);
  const [requesting, setRequesting] = useState(false);
  const [refunding, setRefunding] = useState<PaymentIntent | null>(null);
  const { busy, error, submit } = useSubmit();
  const reload = () => { setVersion((v) => v + 1); onChanged(); };
  const byId = new Map((connectors.data ?? []).map((c) => [c.id, c]));

  async function test(c: Connector) {
    const r = await submit(() => testConnector(c.id));
    if (!r) return;
    const label = TEST_STATUS[r.status]?.label ?? r.status;
    if (r.ok) toast.success(`${c.name} — ${label}${r.mode === "simulation" ? " (SIMULATION)" : ""} : ${r.detail}`);
    else toast.error(`${c.name} — ${label} : ${r.detail}`);
    reload();
  }

  return (
    <div style={{ display: "grid", gap: 16, gridTemplateColumns: "minmax(0, 1fr)" }}>
      <Card>
        <SectionLabel action={canConfigure ? <Button size="sm" onClick={() => setCreating(true)}>Nouveau connecteur</Button> : undefined}>Connecteurs</SectionLabel>
        {(connectors.error || error) && <div role="alert" className="nx-error-text">{connectors.error ?? error}</div>}
        <DataTable exportTitle="Connecteurs"
          columns={[
            { key: "n", header: "Connecteur", render: (c) => <><b>{c.name}</b><div style={hint}>{c.connectorType} · {c.provider} · {c.environment === "production" ? "production" : "test"}</div></> },
            { key: "m", header: "Mode", render: (c) => (c.connectorType === "payment" && c.providerSupported ? <ModeBadge mode={c.mode} /> : <Badge>Sans adaptateur</Badge>) },
            { key: "s", header: "État", render: (c) => <Badge tone={c.status === "active" ? "success" : c.status === "error" ? "error" : undefined}>{c.status === "active" ? "Actif" : c.status === "error" ? "En erreur" : "Inactif"}</Badge> },
            { key: "t", header: "Dernier test", render: (c) => (c.lastTestStatus
              ? <><Badge tone={TEST_STATUS[c.lastTestStatus].tone}>{TEST_STATUS[c.lastTestStatus].label}</Badge>
                  <div style={hint}>{c.lastTestMessage}{c.lastTestedAt ? ` — ${dateTime(c.lastTestedAt)}` : ""}</div></>
              : <span style={hint}>Jamais testé</span>) },
            { key: "c", header: "Identifiants", render: (c) => (needsCredentials(c) ? (c.credentialsConfigured ? `Configurés (${c.credentialsHint})` : "À saisir") : "—") },
            { key: "a", header: "", render: (c) => (canConfigure ? (
              <span style={{ display: "inline-flex", gap: 4, flexWrap: "wrap" }}>
                <Button size="sm" variant="subtle" disabled={busy} onClick={() => test(c)}>Tester la connexion</Button>
                {needsCredentials(c) && <Button size="sm" variant="ghost" onClick={() => setCredentialsFor(c)}>Identifiants</Button>}
                <Button size="sm" variant="ghost" disabled={busy} onClick={async () => {
                  if (await submit(() => setConnectorStatus(c.id, c.status === "active" ? "inactive" : "active").then(() => true))) {
                    toast.success(c.status === "active" ? "Connecteur désactivé." : "Connecteur activé."); reload();
                  }
                }}>{c.status === "active" ? "Désactiver" : "Activer"}</Button>
              </span>) : null) },
          ]}
          rows={connectors.data ?? []} rowKey={(c) => c.id} empty={connectors.loading ? "Chargement…" : "Aucun connecteur."} />
        <div style={hint}>
          « Tester la connexion » interroge réellement le fournisseur. Un connecteur de SIMULATION ne contacte aucun service et ne
          transporte aucun argent réel ; il est interdit en production. Un fournisseur réel ne s'active qu'après un test réussi.
        </div>
      </Card>

      {canPayments && (
        <Card>
          <SectionLabel action={<span style={{ display: "inline-flex", gap: 6 }}>
            <Button size="sm" onClick={() => setRequesting(true)}>Demander un paiement</Button>
            <Button size="sm" variant="ghost" disabled={busy} onClick={async () => {
              const r = await submit(() => expireStaleIntents());
              if (r) { toast.success(`${r.expired} paiement(s) expiré(s).`); reload(); }
            }}>Expirer les échus</Button>
          </span>}>Paiements en ligne</SectionLabel>
          {intents.error && <div role="alert" className="nx-error-text">{intents.error}</div>}
          <DataTable exportTitle="Paiements en ligne"
            columns={[
              { key: "d", header: "Créé", render: (i) => dateTime(i.createdAt) },
              { key: "m", header: "Mode", render: (i) => <ModeBadge mode={i.mode} /> },
              { key: "c", header: "Connecteur", render: (i) => byId.get(i.connectorId)?.name ?? "—" },
              { key: "a", header: "Montant", numeric: true, render: (i) => money(i.amount, i.currency) },
              { key: "s", header: "État", render: (i) => <><Badge tone={INTENT_STATUS[i.status]?.tone}>{INTENT_STATUS[i.status]?.label ?? i.status}</Badge>
                {i.failureReason && <div style={hint}>{i.failureReason}</div>}</> },
              { key: "r", header: "Référence", render: (i) => <span style={{ fontFamily: "monospace", fontSize: 12 }}>{i.providerReference ?? "—"}</span> },
              { key: "x", header: "", render: (i) => {
                const can = intentActions(i, byId.get(i.connectorId)?.environment ?? "test");
                return (
                  <span style={{ display: "inline-flex", gap: 4, flexWrap: "wrap" }}>
                    {i.redirectUrl && (i.status === "pending") && <a className="nx-btn nx-btn--ghost nx-btn--sm" href={i.redirectUrl} target="_blank" rel="noreferrer">Lien de paiement</a>}
                    {can.verify && <Button size="sm" variant="subtle" disabled={busy} onClick={async () => {
                      const r = await submit(() => verifyIntent(i.id));
                      if (r) { r.status === "confirmed" ? toast.success("Paiement reçu et enregistré.") : toast.push(r.failureReason ?? "Pas encore payé.", "info"); reload(); }
                    }}>Vérifier auprès du fournisseur</Button>}
                    {can.simulate && <>
                      <Button size="sm" variant="ghost" disabled={busy} onClick={async () => {
                        if (await submit(() => simulateProviderResponse(i.id, "confirmed", i.amount))) { toast.success("SIMULATION : paiement confirmé."); reload(); }
                      }}>Simuler : payé</Button>
                      <Button size="sm" variant="ghost" disabled={busy} onClick={async () => {
                        if (await submit(() => simulateProviderResponse(i.id, "failed", null))) { toast.success("SIMULATION : paiement refusé."); reload(); }
                      }}>Simuler : refusé</Button>
                    </>}
                    {can.refund && hasPermission("pos.refund") && <Button size="sm" variant="ghost" disabled={busy} onClick={() => setRefunding(i)}>Rembourser</Button>}
                  </span>);
              } },
            ]}
            rows={intents.data ?? []} rowKey={(i) => i.id} empty={intents.loading ? "Chargement…" : "Aucun paiement en ligne."} />
          <div style={hint}>
            Un paiement RÉEL n'est enregistré qu'après confirmation par le fournisseur (« Vérifier auprès du fournisseur ») et au montant attendu.
            Les boutons « Simuler » n'existent que pour la SIMULATION.
          </div>
        </Card>
      )}

      {creating && <ConnectorDialog onClose={() => setCreating(false)} onDone={(name) => { setCreating(false); reload(); toast.success(`Connecteur « ${name} » créé (inactif) : testez-le avant de l'activer.`); }} />}
      {credentialsFor && <CredentialsDialog connector={credentialsFor} onClose={() => setCredentialsFor(null)}
        onDone={() => { setCredentialsFor(null); reload(); toast.success("Identifiants enregistrés (chiffrés). Testez la connexion."); }} />}
      {requesting && <RequestPaymentDialog connectors={(connectors.data ?? []).filter((c) => c.connectorType === "payment" && c.status === "active")}
        onClose={() => setRequesting(false)} onDone={(i) => { setRequesting(false); reload();
          toast.success(i.mode === "simulation" ? "SIMULATION : paiement créé, à confirmer à la main." : "Paiement créé : envoyez le lien au client, puis vérifiez."); }} />}
      {refunding && <ConfirmDialog title={`Rembourser ${money(refunding.amount, refunding.currency)}`} requireReason danger confirmLabel="Rembourser"
        message={refunding.mode === "simulation"
          ? "SIMULATION : le versement client est annulé (écritures contre-passées). Remboursement total uniquement."
          : "Le fournisseur sera sollicité ; s'il ne propose pas de remboursement par API, la demande est refusée avec la marche à suivre."}
        onCancel={() => setRefunding(null)}
        onConfirm={async (reason) => {
          const i = refunding; setRefunding(null);
          if (await submit(() => refundIntent(i.id, i.amount, reason))) { toast.success("Paiement remboursé : versement annulé."); reload(); }
        }} />}
    </div>
  );
}

function ConnectorDialog({ onClose, onDone }: { onClose: () => void; onDone: (name: string) => void }) {
  const providers = useAsync(listPaymentProviders, []);
  const [name, setName] = useState("");
  const [code, setCode] = useState("");
  const [provider, setProvider] = useState("moncash");
  const [environment, setEnvironment] = useState("test");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "integrations.code": "code", "integrations.name": "name", "integrations.simulation_in_production": "environment", "integrations.provider_unknown": "provider",
  });
  const isSim = providers.data?.find((p) => p.key === provider)?.isSimulation ?? false;
  useEffect(() => { if (isSim && environment === "production") setEnvironment("test"); }, [isSim, environment]);
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!name.trim()) return fail("name", "Nom obligatoire.");
    if (!/^[a-z0-9][a-z0-9-]{1,59}$/.test(code.trim())) return fail("code", "2 à 60 caractères : minuscules, chiffres, tirets.");
    if (await submit(() => upsertConnector({ code: code.trim(), name: name.trim(), connectorType: "payment", provider, environment, config: null }).then(() => true))) onDone(name.trim());
  }
  return (
    <Modal title="Nouveau connecteur de paiement" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Créer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={row}>
          <Field label="Nom" required error={fieldErrors.name}><TextInput value={name} maxLength={80} placeholder="MonCash boutique" onChange={(e) => setName(e.target.value)} /></Field>
          <Field label="Code" required error={fieldErrors.code}><TextInput value={code} maxLength={60} placeholder="moncash-boutique" onChange={(e) => setCode(e.target.value.toLowerCase())} /></Field>
        </div>
        <div style={row}>
          <Field label="Fournisseur" error={fieldErrors.provider}><Select value={provider} onChange={(e) => setProvider(e.target.value)}>
            {(providers.data ?? []).map((p) => <option key={p.key} value={p.key}>{p.name}{p.isSimulation ? " (SIMULATION)" : ""}</option>)}
          </Select></Field>
          <Field label="Environnement" error={fieldErrors.environment} hint={isSim ? "La simulation n'existe qu'en test." : "Test : bac à sable du fournisseur."}>
            <Select value={environment} onChange={(e) => setEnvironment(e.target.value)}>
              <option value="test">Test (bac à sable)</option>
              {!isSim && <option value="production">Production</option>}
            </Select>
          </Field>
        </div>
        {provider === "card" && <div style={hint}>Aucun acquéreur de cartes n'est intégré : ce connecteur restera « non configuré ».</div>}
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function CredentialsDialog({ connector, onClose, onDone }: { connector: Connector; onClose: () => void; onDone: () => void }) {
  const [clientId, setClientId] = useState("");
  const [secret, setSecret] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "integrations.client_id": "clientId", "integrations.client_secret": "secret" });
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (clientId.trim().length < 4) return fail("clientId", "Client ID obligatoire.");
    if (secret.trim().length < 4) return fail("secret", "Client Secret obligatoire.");
    if (await submit(() => setConnectorCredentials(connector.id, clientId.trim(), secret.trim()).then(() => true))) onDone();
  }
  return (
    <Modal title={`Identifiants — ${connector.name}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(480px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={hint}>Générés dans le portail MonCash Business ({connector.environment === "production" ? "production" : "bac à sable"}). Chiffrés à l'enregistrement, ils ne sont plus jamais affichés.
          {connector.credentialsConfigured && ` Actuels : ${connector.credentialsHint}.`}</div>
        <Field label="Client ID" required error={fieldErrors.clientId}><TextInput value={clientId} autoComplete="off" onChange={(e) => setClientId(e.target.value)} /></Field>
        <Field label="Client Secret" required error={fieldErrors.secret}><TextInput type="password" value={secret} autoComplete="new-password" onChange={(e) => setSecret(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function RequestPaymentDialog({ connectors, onClose, onDone }: { connectors: Connector[]; onClose: () => void; onDone: (i: PaymentIntent) => void }) {
  const accounts = useAsync(getFinancialAccounts, []);
  const [connectorId, setConnectorId] = useState(connectors[0]?.id ?? "");
  const [q, setQ] = useState("");
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [customer, setCustomer] = useState<Customer | null>(null);
  const [searched, setSearched] = useState("");
  const [amount, setAmount] = useState("");
  const [account, setAccount] = useState("");
  const [reference, setReference] = useState("");
  const [key] = useState(newOperationId);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "integrations.amount": "amount", "integrations.currency_unsupported": "amount" });
  const conn = connectors.find((c) => c.id === connectorId);
  useEffect(() => {
    if (q.trim().length < 2) { setCustomers([]); return; }
    const t = setTimeout(() => {
      listCustomers(q, 8).then(setCustomers).catch(() => setCustomers([])).finally(() => setSearched(q));
    }, 250);
    return () => clearTimeout(t);
  }, [q]);
  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!conn) return fail("connector", "Aucun connecteur de paiement actif.");
    if (!customer) return fail("customer", "Choisissez le client.");
    const a = num(amount);
    if (a == null || a <= 0) return fail("amount", "Montant positif obligatoire.");
    if (!account) return fail("account", "Choisissez le compte qui recevra l'argent.");
    const i = await submit(() => createIntent({ connectorId: conn.id, idempotencyKey: key, amount: a, currency: "HTG", method: "mobile_money", purpose: "receivable",
      customerId: customer.id, settlementAccountId: account, saleId: null, allocations: null, expiresInMinutes: 60, reference: reference.trim() || null }));
    if (i) onDone(i);
  }
  return (
    <Modal title="Demander un paiement en ligne" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy || !conn} onClick={() => save()}>Créer le paiement</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(540px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {connectors.length === 0 && <div className="nx-error-text">Aucun connecteur de paiement actif : créez-en un, testez-le, puis activez-le.</div>}
        <Field label="Connecteur" error={fieldErrors.connector}><Select value={connectorId} onChange={(e) => setConnectorId(e.target.value)}>
          {connectors.map((c) => <option key={c.id} value={c.id}>{c.name} — {c.mode === "simulation" ? "SIMULATION" : "RÉEL"}</option>)}
        </Select></Field>
        {conn?.mode === "simulation" && <div style={hint}>SIMULATION : aucun argent réel ; le paiement se confirme à la main.</div>}
        <Field label="Client" required error={fieldErrors.customer}>
          {customer
            ? <div style={{ display: "flex", gap: 8, alignItems: "center" }}><b>{customer.name}</b><Button type="button" size="sm" variant="ghost" onClick={() => setCustomer(null)}>Changer</Button></div>
            : <TextInput value={q} placeholder="Nom, téléphone ou numéro client" onChange={(e) => setQ(e.target.value)} />}
        </Field>
        {!customer && q.trim().length >= 2 && searched === q && customers.length === 0 && <div style={hint}>Aucun client trouvé dans cette entreprise.</div>}
        {!customer && customers.length > 0 && (
          <div role="listbox" aria-label="Clients trouvés" style={{ display: "grid", gap: 4 }}>
            {customers.map((c) => <button key={c.id} type="button" className="nx-btn" style={{ justifyContent: "flex-start" }} onClick={() => { setCustomer(c); setQ(""); }}>{c.name} · {c.customerNumber}</button>)}
          </div>
        )}
        <div style={row}>
          <Field label="Montant (HTG)" required error={fieldErrors.amount}><TextInput inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>
          <Field label="Compte d'encaissement" required error={fieldErrors.account}><Select value={account} onChange={(e) => setAccount(e.target.value)}>
            <option value="">— Choisir —</option>
            {(accounts.data ?? []).filter((a) => a.status === "active" && a.currency === "HTG").map((a) => <option key={a.accountId} value={a.accountId}>{a.name}</option>)}
          </Select></Field>
        </div>
        {accounts.error && <div style={hint}>Comptes indisponibles : {accounts.error}</div>}
        <Field label="Référence" hint="Ex. n° de facture"><TextInput value={reference} maxLength={60} onChange={(e) => setReference(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
