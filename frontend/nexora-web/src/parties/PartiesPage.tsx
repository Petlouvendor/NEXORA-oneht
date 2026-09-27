import { useCallback, useEffect, useState, type FormEvent } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { ApiError } from "@/core/apiClient";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import {
  Badge, Button, Card, DataTable, EmptyState, Field, SectionLabel, Select, Tabs, TextInput, type Column,
} from "@/shared/ui";
import { money } from "@/shared/format";
import { norm } from "@/app/paletteModel";
import {
  createCustomer, createSupplier, listCustomers, listSuppliers, updateCustomer,
  type Customer, type CustomerInput, type SupplierSummary,
} from "@/parties/partiesApi";

const errorText = (e: unknown) => (e instanceof ApiError ? e.problem.detail ?? e.problem.title : "Opération impossible");

/**
 * Clients & fournisseurs (recette Phase 55 CLI-001/002, ACH-001). Les tiers n'étaient créables
 * que par import (Phase 39) : cet écran donne enfin la saisie directe, bornée par les permissions
 * `customers.*` / `suppliers.*` — ouvrir une limite de crédit exige en plus `credit.set_limit`.
 */
/** `initialTab`/`?tab=` et `?q=` : ouverture directe depuis le menu « Fournisseurs » ou la recherche Ctrl+K (NEXORA ONE §5). */
export function PartiesPage({ initialTab }: { initialTab?: "customers" | "suppliers" } = {}) {
  const { hasPermission } = useAuth();
  const [params] = useSearchParams();
  const canCustomers = hasPermission("customers.view");
  const canSuppliers = hasPermission("suppliers.view") && hasPermission("purchasing.view");
  const tabs = [
    ...(canCustomers ? [{ id: "customers", label: "Clients" }] : []),
    ...(canSuppliers ? [{ id: "suppliers", label: "Fournisseurs" }] : []),
  ];
  const wanted = params.get("tab") ?? initialTab;
  const [tab, setTab] = useState(tabs.find((t) => t.id === wanted)?.id ?? tabs[0]?.id ?? "customers");
  useEffect(() => { if (wanted && tabs.some((t) => t.id === wanted)) setTab(wanted); }, [wanted]); // eslint-disable-line react-hooks/exhaustive-deps
  const initialQuery = params.get("q") ?? "";

  if (tabs.length === 0) {
    return <Card><EmptyState title="Clients & fournisseurs" hint="Vous n'avez pas accès aux fiches tiers." /></Card>;
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>{tab === "suppliers" ? "Fournisseurs" : "Clients"}</h1>
      <Tabs tabs={tabs} active={tab} onChange={setTab} />
      {tab === "customers" && canCustomers && <CustomersPanel initialQuery={initialQuery} />}
      {tab === "suppliers" && canSuppliers && <SuppliersPanel initialQuery={initialQuery} />}
    </div>
  );
}

const EMPTY: CustomerInput = { name: "", type: "individual", phone: "", email: "", address: "", taxIdentifier: "", creditLimit: 0 };

function CustomersPanel({ initialQuery = "" }: { initialQuery?: string }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canCreate = hasPermission("customers.create");
  const canUpdate = hasPermission("customers.update");
  const canCredit = hasPermission("credit.set_limit");
  const [rows, setRows] = useState<Customer[]>([]);
  const [q, setQ] = useState(initialQuery);
  const [editing, setEditing] = useState<Customer | "new" | null>(null);
  const [form, setForm] = useState<CustomerInput>(EMPTY);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => {
    listCustomers(q).then(setRows).catch((e) => setError(errorText(e)));
  }, [q]);
  useEffect(() => { const t = setTimeout(load, 250); return () => clearTimeout(t); }, [load]);

  function open(target: Customer | "new") {
    setError(null);
    setEditing(target);
    setForm(target === "new" ? EMPTY : {
      name: target.name, type: target.type, phone: target.phone ?? "", email: target.email ?? "",
      address: target.address ?? "", taxIdentifier: target.taxIdentifier ?? "", creditLimit: target.creditLimit ?? 0,
    });
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      // Sans droit sur le crédit, la limite n'est jamais envoyée (elle reste inchangée côté serveur).
      const body: CustomerInput = { ...form, creditLimit: canCredit ? Number(form.creditLimit) || 0 : undefined };
      if (editing === "new") {
        const c = await createCustomer(body);
        toast.success(`Client ${c.customerNumber} créé.`);
      } else if (editing) {
        await updateCustomer(editing.id, body);
        toast.success("Fiche client enregistrée.");
      }
      setEditing(null);
      load();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const set = (k: keyof CustomerInput) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }));

  const columns: Array<Column<Customer>> = [
    { key: "n", header: "N°", render: (r) => r.customerNumber },
    { key: "name", header: "Nom", render: (r) => <Link to={`/customers/${r.id}`}>{r.name}</Link> },
    { key: "contact", header: "Contact", render: (r) => [r.phone, r.email].filter(Boolean).join(" · ") || "—" },
    { key: "status", header: "Statut", render: (r) => <Badge tone={r.status === "active" ? "success" : "warning"}>{r.status}</Badge> },
    { key: "limit", header: "Limite de crédit", numeric: true, render: (r) => (r.creditLimit == null ? "—" : money(r.creditLimit, r.currency)) },
    { key: "open", header: "Encours", numeric: true, render: (r) => (r.openReceivables == null ? "—" : money(r.openReceivables, r.currency)) },
    { key: "act", header: "", render: (r) => (
      <div style={{ display: "flex", gap: 4 }}>
        <Link to={`/customers/${r.id}`} className="nx-btn nx-btn--ghost nx-btn--sm">Fiche</Link>
        {canUpdate && <Button variant="ghost" size="sm" onClick={() => open(r)}>Modifier</Button>}
      </div>
    ) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <div style={{ display: "flex", gap: 12, alignItems: "end", flexWrap: "wrap" }}>
          <Field label="Rechercher (nom, n°, téléphone, email)">
            <TextInput value={q} onChange={(e) => setQ(e.target.value)} style={{ minWidth: 280 }} />
          </Field>
          {canCreate && <Button onClick={() => open("new")}>Nouveau client</Button>}
        </div>
      </Card>

      {editing && (
        <Card>
          <form onSubmit={save} style={{ display: "grid", gap: 12, maxWidth: 520 }}>
            <SectionLabel>{editing === "new" ? "Nouveau client" : `Client ${editing.customerNumber}`}</SectionLabel>
            <Field label="Nom" required><TextInput value={form.name} onChange={set("name")} required maxLength={200} /></Field>
            <Field label="Type">
              <Select value={form.type} onChange={(e) => setForm((f) => ({ ...f, type: e.target.value as CustomerInput["type"] }))}>
                <option value="individual">Particulier</option>
                <option value="business">Entreprise</option>
              </Select>
            </Field>
            <Field label="Téléphone"><TextInput value={form.phone ?? ""} onChange={set("phone")} inputMode="tel" /></Field>
            <Field label="Email"><TextInput value={form.email ?? ""} onChange={set("email")} inputMode="email" type="email" /></Field>
            <Field label="Adresse"><TextInput value={form.address ?? ""} onChange={set("address")} /></Field>
            <Field label="Identifiant fiscal"><TextInput value={form.taxIdentifier ?? ""} onChange={set("taxIdentifier")} /></Field>
            <Field label="Limite de crédit"
                   hint={canCredit ? "0 = client au comptant." : "Réservé aux titulaires de la permission credit.set_limit."}>
              <TextInput type="number" min={0} step="0.01" value={String(form.creditLimit ?? 0)} disabled={!canCredit}
                         onChange={(e) => setForm((f) => ({ ...f, creditLimit: Number(e.target.value) }))} />
            </Field>
            {error && <div role="alert" className="nx-error-text">{error}</div>}
            <div style={{ display: "flex", gap: 8 }}>
              <Button type="submit" loading={busy}>Enregistrer</Button>
              <Button type="button" variant="ghost" onClick={() => setEditing(null)}>Annuler</Button>
            </div>
          </form>
        </Card>
      )}

      <Card>
        {!editing && error && <div role="alert" className="nx-error-text">{error}</div>}
        <DataTable columns={columns} rows={rows} rowKey={(r) => r.id}
                   empty={<EmptyState title="Aucun client" hint={q ? "Aucun résultat pour cette recherche." : "Créez votre premier client."} />} />
      </Card>
    </div>
  );
}

function SuppliersPanel({ initialQuery = "" }: { initialQuery?: string }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canCreate = hasPermission("suppliers.create");
  const [rows, setRows] = useState<SupplierSummary[]>([]);
  const [q, setQ] = useState(initialQuery);
  const shown = rows.filter((r) => !q.trim() || norm(`${r.name} ${r.supplierNumber}`).includes(norm(q.trim())));
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState("");
  const [phone, setPhone] = useState("");
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const load = useCallback(() => { listSuppliers().then(setRows).catch((e) => setError(errorText(e))); }, []);
  useEffect(load, [load]);

  async function save(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await createSupplier({ name, type: "business", phone: phone || null, email: email || null });
      toast.success("Fournisseur créé.");
      setCreating(false);
      setName(""); setPhone(""); setEmail("");
      load();
    } catch (err) {
      setError(errorText(err));
    } finally {
      setBusy(false);
    }
  }

  const columns: Array<Column<SupplierSummary>> = [
    { key: "n", header: "N°", render: (r) => r.supplierNumber },
    { key: "name", header: "Nom", render: (r) => <Link to={`/customers/${r.id}`}>{r.name}</Link> },
    { key: "status", header: "Statut", render: (r) => <Badge tone={r.status === "active" ? "success" : "warning"}>{r.status}</Badge> },
    { key: "open", header: "Dettes ouvertes", numeric: true, render: (r) => money(r.openPayables, r.currency) },
  ];

  return (
    <div style={{ display: "grid", gap: 16 }}>
      {canCreate && !creating && <div><Button onClick={() => setCreating(true)}>Nouveau fournisseur</Button></div>}
      {creating && (
        <Card>
          <form onSubmit={save} style={{ display: "grid", gap: 12, maxWidth: 520 }}>
            <SectionLabel>Nouveau fournisseur</SectionLabel>
            <Field label="Raison sociale" required><TextInput value={name} onChange={(e) => setName(e.target.value)} required maxLength={200} /></Field>
            <Field label="Téléphone"><TextInput value={phone} onChange={(e) => setPhone(e.target.value)} inputMode="tel" /></Field>
            <Field label="Email"><TextInput value={email} onChange={(e) => setEmail(e.target.value)} type="email" /></Field>
            {error && <div role="alert" className="nx-error-text">{error}</div>}
            <div style={{ display: "flex", gap: 8 }}>
              <Button type="submit" loading={busy}>Créer</Button>
              <Button type="button" variant="ghost" onClick={() => setCreating(false)}>Annuler</Button>
            </div>
          </form>
        </Card>
      )}
      <Card>
        {!creating && error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ maxWidth: 360, marginBottom: 12 }}>
          <TextInput value={q} onChange={(e) => setQ(e.target.value)} placeholder="Rechercher un fournisseur…" aria-label="Rechercher un fournisseur" />
        </div>
        <DataTable columns={columns} rows={shown} rowKey={(r) => r.id}
                   empty={<EmptyState title="Aucun fournisseur" hint="Créez votre premier fournisseur." />} />
      </Card>
    </div>
  );
}
