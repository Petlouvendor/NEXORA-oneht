import { useMemo, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { errorMessage, useAsync } from "@/shared/useAsync";
import {
  Badge, Button, Card, ConfirmDialog, DataTable, Field, Modal, SectionLabel, Select, StatePill, TextInput,
} from "@/shared/ui";
import {
  convertUnits, createBrand, createCategory, createConversion, createCustomerGroup, createPriceList, createUnit,
  deleteConversion, listConversions, updateCategory, type Category, type ConversionResult,
} from "@/catalog/catalogApi";
import { num, sortedCategories, useSubmit, type CatalogRefs } from "@/catalog/catalogShared";

const row2: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 12 };
const CATEGORY_STATUS: Record<string, string> = { active: "Active", inactive: "Inactive", archived: "Archivée" };

// ============================================================================ Catégories & marques

export function CategoriesPanel({ refs, onChanged }: { refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const [editing, setEditing] = useState<Category | "new" | null>(null);
  const tree = useMemo(() => sortedCategories(refs.categories), [refs.categories]);
  const depth = (path: string) => path.split(" › ").length - 1;

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={hasPermission("catalog.create")
          ? <Button size="sm" onClick={() => setEditing("new")}>Nouvelle catégorie</Button> : undefined}>
          Catégories ({refs.categories.length})
        </SectionLabel>
        <DataTable
          exportTitle="Catégories"
          columns={[
            { key: "n", header: "Catégorie", render: (c) => <span style={{ paddingLeft: depth(c.path) * 18 }}>{depth(c.path) > 0 && "↳ "}{c.name}</span> },
            { key: "c", header: "Code", render: (c) => (c.code ? <code>{c.code}</code> : "—") },
            { key: "s", header: "Statut", render: (c) => <StatePill status={c.status} /> },
            {
              key: "a", header: "", render: (c) => hasPermission("catalog.update")
                ? <Button size="sm" variant="ghost" onClick={() => setEditing(c)}>Modifier</Button> : null,
            },
          ]}
          rows={tree}
          rowKey={(c) => c.id}
          empty="Aucune catégorie. Créez-en pour classer les produits, filtrer les rapports et cibler les promotions."
        />
      </Card>
      <BrandsCard refs={refs} onChanged={onChanged} />
      {editing && (
        <CategoryDialog category={editing === "new" ? null : editing} refs={refs}
                        onClose={() => setEditing(null)} onSaved={() => { setEditing(null); onChanged(); }} />
      )}
    </div>
  );
}

function CategoryDialog({ category, refs, onClose, onSaved }: {
  category: Category | null; refs: CatalogRefs; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const [f, setF] = useState({
    name: category?.name ?? "", code: category?.code ?? "", parentId: category?.parentId ?? "", status: category?.status ?? "active",
  });
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "catalog.category_name_required": "name", "catalog.category_code_exists": "code",
    "catalog.category_cycle": "parentId", "catalog.category_unknown": "parentId",
  });
  // Une catégorie ne peut pas être rangée sous elle-même ni sous l'une de ses sous-catégories.
  const parents = useMemo(() => {
    if (!category) return sortedCategories(refs.categories);
    const blocked = new Set([category.id]);
    let grew = true;
    while (grew) {
      grew = false;
      for (const c of refs.categories) if (c.parentId && blocked.has(c.parentId) && !blocked.has(c.id)) { blocked.add(c.id); grew = true; }
    }
    return sortedCategories(refs.categories).filter((c) => !blocked.has(c.id));
  }, [category, refs.categories]);

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.name.trim()) return fail("name", "Le nom est obligatoire.");
    const body = { name: f.name.trim(), code: f.code.trim() || null, parentId: f.parentId || null };
    const ok = await submit(async () => {
      if (category) await updateCategory(category.id, { ...body, status: f.status });
      else await createCategory(body);
      return true;
    });
    if (ok) { toast.success(category ? "Catégorie modifiée." : "Catégorie créée."); onSaved(); }
  }

  return (
    <Modal title={category ? `Modifier « ${category.name} »` : "Nouvelle catégorie"} onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <Field label="Nom" required error={fieldErrors.name}>
          <TextInput value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} invalid={!!fieldErrors.name} autoFocus />
        </Field>
        <div style={row2}>
          <Field label="Code" error={fieldErrors.code}><TextInput value={f.code} onChange={(e) => setF({ ...f, code: e.target.value })} /></Field>
          <Field label="Catégorie parente" error={fieldErrors.parentId}>
            <Select value={f.parentId} onChange={(e) => setF({ ...f, parentId: e.target.value })}>
              <option value="">(racine)</option>
              {parents.map((c) => <option key={c.id} value={c.id}>{c.path}</option>)}
            </Select>
          </Field>
          {category && (
            <Field label="Statut">
              <Select value={f.status} onChange={(e) => setF({ ...f, status: e.target.value })}>
                {Object.entries(CATEGORY_STATUS).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </Select>
            </Field>
          )}
        </div>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy}>{category ? "Enregistrer" : "Créer"}</Button>
        </div>
      </form>
    </Modal>
  );
}

function BrandsCard({ refs, onChanged }: { refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [name, setName] = useState("");
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "catalog.brand_exists": "name", "catalog.brand_name": "name" });

  async function add(e: FormEvent) {
    e.preventDefault();
    if (!name.trim()) return fail("name", "Le nom est obligatoire.");
    const ok = await submit(async () => { await createBrand(name.trim()); return true; });
    if (ok) { toast.success("Marque créée."); setName(""); onChanged(); }
  }

  return (
    <Card>
      <SectionLabel>Marques ({refs.brands.length})</SectionLabel>
      {hasPermission("catalog.create") && (
        <form onSubmit={add} style={{ display: "flex", gap: 8, alignItems: "flex-end", flexWrap: "wrap", marginBottom: 12 }} noValidate>
          <div style={{ flex: "1 1 240px" }}>
            <Field label="Nouvelle marque" error={fieldErrors.name}>
              <TextInput value={name} onChange={(e) => setName(e.target.value)} invalid={!!fieldErrors.name} />
            </Field>
          </div>
          <Button type="submit" size="sm" loading={busy}>Ajouter</Button>
        </form>
      )}
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
        {refs.brands.length === 0 ? <span style={{ color: "var(--nx-text-2)" }}>Aucune marque.</span>
          : refs.brands.map((b) => <Badge key={b.id}>{b.name}</Badge>)}
      </div>
    </Card>
  );
}

// ============================================================================ Unités & conversions

export function UnitsPanel({ refs, onChanged }: { refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canCreate = hasPermission("catalog.create");
  const canUpdate = hasPermission("catalog.update");
  const conversions = useAsync(() => listConversions(), [refs.units.length]);
  const generic = (conversions.data ?? []).filter((c) => c.productId === null);

  const [u, setU] = useState({ code: "", name: "", precision: "0", isWeight: false });
  const unitForm = useSubmit({ "catalog.unit_exists": "code", "catalog.unit_code": "code", "catalog.unit_name": "name", "catalog.unit_precision": "precision" });
  const [c, setC] = useState({ fromUnitId: "", factor: "", toUnitId: "" });
  const convForm = useSubmit({ "catalog.conversion_factor": "factor", "catalog.conversion_exists": "fromUnitId", "catalog.conversion_same_unit": "toUnitId" });
  const [removing, setRemoving] = useState<string | null>(null);

  async function addUnit(e: FormEvent) {
    e.preventDefault();
    if (!u.code.trim()) return unitForm.fail("code", "Le code est obligatoire.");
    if (!u.name.trim()) return unitForm.fail("name", "Le nom est obligatoire.");
    const precision = num(u.precision) ?? 0;
    if (precision < 0 || precision > 6 || !Number.isInteger(precision)) return unitForm.fail("precision", "De 0 à 6 décimales.");
    const ok = await unitForm.submit(async () => {
      await createUnit({ code: u.code.trim().toUpperCase(), name: u.name.trim(), precision, isWeight: u.isWeight });
      return true;
    });
    if (ok) { toast.success("Unité créée."); setU({ code: "", name: "", precision: "0", isWeight: false }); onChanged(); }
  }

  async function addConversion(e: FormEvent) {
    e.preventDefault();
    if (!c.fromUnitId || !c.toUnitId) return convForm.fail("fromUnitId", "Choisissez les deux unités.");
    if (c.fromUnitId === c.toUnitId) return convForm.fail("toUnitId", "Les deux unités doivent être différentes.");
    const factor = num(c.factor);
    if (factor === null || factor <= 0) return convForm.fail("factor", "Le facteur doit être positif.");
    const ok = await convForm.submit(async () => {
      await createConversion({ productId: null, fromUnitId: c.fromUnitId, toUnitId: c.toUnitId, factor });
      return true;
    });
    if (ok) { toast.success("Conversion créée."); setC({ fromUnitId: "", factor: "", toUnitId: "" }); conversions.reload(); }
  }

  async function remove(id: string) {
    setRemoving(null);
    try { await deleteConversion(id); toast.success("Conversion supprimée."); conversions.reload(); }
    catch (e) { toast.error(errorMessage(e)); }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Unités ({refs.units.length})</SectionLabel>
        <DataTable
          exportTitle="Unités"
          columns={[
            { key: "c", header: "Code", render: (x) => <code>{x.code}</code> },
            { key: "n", header: "Nom", render: (x) => x.name },
            { key: "p", header: "Décimales", numeric: true, render: (x) => x.precision },
            { key: "w", header: "", render: (x) => (x.isWeight ? <Badge tone="info">poids</Badge> : null) },
          ]}
          rows={refs.units}
          rowKey={(x) => x.id}
          empty="Aucune unité."
        />
        {canCreate && (
          <form onSubmit={addUnit} style={{ ...row2, marginTop: 12, alignItems: "end" }} noValidate>
            <Field label="Code" required error={unitForm.fieldErrors.code}><TextInput value={u.code} placeholder="CRT" onChange={(e) => setU({ ...u, code: e.target.value })} /></Field>
            <Field label="Nom" required error={unitForm.fieldErrors.name}><TextInput value={u.name} placeholder="Carton" onChange={(e) => setU({ ...u, name: e.target.value })} /></Field>
            <Field label="Décimales" error={unitForm.fieldErrors.precision} hint="0 pour une pièce, 3 pour le kg">
              <TextInput type="number" min={0} max={6} step={1} value={u.precision} onChange={(e) => setU({ ...u, precision: e.target.value })} />
            </Field>
            <label style={{ display: "flex", gap: 8, alignItems: "center", paddingBottom: 8 }}>
              <input type="checkbox" checked={u.isWeight} onChange={(e) => setU({ ...u, isWeight: e.target.checked })} /> Unité de poids
            </label>
            <Button type="submit" size="sm" loading={unitForm.busy}>Créer l'unité</Button>
          </form>
        )}
        {unitForm.error && <div role="alert" className="nx-error-text">{unitForm.error}</div>}
      </Card>

      <Card>
        <SectionLabel>Conversions générales</SectionLabel>
        <p style={{ margin: "0 0 10px", fontSize: 13, color: "var(--nx-text-2)" }}>
          Valables pour tous les produits, sauf conversion propre définie sur la fiche produit. Toute conversion est calculée par
          le serveur ; il n'y a jamais d'enchaînement implicite (carton → paquet → pièce doit être déclaré directement).
        </p>
        {conversions.error && <div role="alert" className="nx-error-text">{conversions.error}</div>}
        {generic.length === 0 && !conversions.loading && <div style={{ color: "var(--nx-text-2)" }}>Aucune conversion générale.</div>}
        {generic.map((x) => (
          <div key={x.id} style={{ display: "flex", gap: 10, alignItems: "center", padding: "4px 0" }}>
            <span>1 <b>{x.fromUnitCode}</b> = <b>{x.factor}</b> {x.toUnitCode}</span>
            {canUpdate && <Button size="sm" variant="ghost" onClick={() => setRemoving(x.id)}>Supprimer</Button>}
          </div>
        ))}
        {canUpdate && refs.units.length >= 2 && (
          <form onSubmit={addConversion} style={{ ...row2, marginTop: 12, alignItems: "end" }} noValidate>
            <Field label="1 unité de" required error={convForm.fieldErrors.fromUnitId}>
              <Select value={c.fromUnitId} onChange={(e) => setC({ ...c, fromUnitId: e.target.value })}>
                <option value="">—</option>
                {refs.units.map((x) => <option key={x.id} value={x.id}>{x.code}</option>)}
              </Select>
            </Field>
            <Field label="= facteur" required error={convForm.fieldErrors.factor}>
              <TextInput type="number" min={0} step="any" value={c.factor} placeholder="24" onChange={(e) => setC({ ...c, factor: e.target.value })} />
            </Field>
            <Field label="unités de" required error={convForm.fieldErrors.toUnitId}>
              <Select value={c.toUnitId} onChange={(e) => setC({ ...c, toUnitId: e.target.value })}>
                <option value="">—</option>
                {refs.units.map((x) => <option key={x.id} value={x.id}>{x.code}</option>)}
              </Select>
            </Field>
            <Button type="submit" size="sm" loading={convForm.busy}>Ajouter</Button>
          </form>
        )}
        {convForm.error && <div role="alert" className="nx-error-text">{convForm.error}</div>}
      </Card>

      <ConverterCard refs={refs} />

      {removing && (
        <ConfirmDialog title="Supprimer la conversion" danger confirmLabel="Supprimer"
                       message="Les opérations déjà enregistrées ne sont pas modifiées. Les suivantes n'utiliseront plus cette conversion."
                       onConfirm={() => void remove(removing)} onCancel={() => setRemoving(null)} />
      )}
    </div>
  );
}

/** Essai de conversion — résultat et règle utilisée viennent du serveur. */
function ConverterCard({ refs }: { refs: CatalogRefs }) {
  const [f, setF] = useState({ fromUnitId: "", toUnitId: "", quantity: "1" });
  const [result, setResult] = useState<ConversionResult | null>(null);
  const { busy, error, submit } = useSubmit();
  const RULE: Record<string, string> = {
    identity: "même unité", product: "conversion du produit", product_inverse: "conversion du produit (sens inverse)",
    generic: "conversion générale", generic_inverse: "conversion générale (sens inverse)",
  };

  async function run(e: FormEvent) {
    e.preventDefault();
    const quantity = num(f.quantity);
    if (!f.fromUnitId || !f.toUnitId || quantity === null) return;
    setResult(null);
    const r = await submit(() => convertUnits({ fromUnitId: f.fromUnitId, toUnitId: f.toUnitId, quantity }));
    if (r) setResult(r);
  }
  const code = (id: string) => refs.units.find((u) => u.id === id)?.code ?? "";

  return (
    <Card>
      <SectionLabel>Tester une conversion</SectionLabel>
      <form onSubmit={run} style={{ ...row2, alignItems: "end" }} noValidate>
        <Field label="Quantité"><TextInput type="number" step="any" value={f.quantity} onChange={(e) => setF({ ...f, quantity: e.target.value })} /></Field>
        <Field label="De">
          <Select value={f.fromUnitId} onChange={(e) => setF({ ...f, fromUnitId: e.target.value })}>
            <option value="">—</option>{refs.units.map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}
          </Select>
        </Field>
        <Field label="Vers">
          <Select value={f.toUnitId} onChange={(e) => setF({ ...f, toUnitId: e.target.value })}>
            <option value="">—</option>{refs.units.map((u) => <option key={u.id} value={u.id}>{u.code}</option>)}
          </Select>
        </Field>
        <Button type="submit" size="sm" variant="subtle" loading={busy} disabled={!f.fromUnitId || !f.toUnitId}>Convertir</Button>
      </form>
      {result && (
        <div style={{ marginTop: 10 }} aria-live="polite">
          <b>{result.quantity} {code(f.fromUnitId)}</b> = <b>{result.converted} {code(f.toUnitId)}</b>
          <span style={{ color: "var(--nx-text-2)" }}> — {RULE[result.rule] ?? result.rule}</span>
        </div>
      )}
      {error && <div role="alert" className="nx-error-text" style={{ marginTop: 8 }}>{error}</div>}
    </Card>
  );
}

// ============================================================================ Listes de prix & groupes de clients

const LIST_TYPES: Record<string, string> = {
  retail: "Détail", wholesale: "Grossiste", member: "Membres", promotional: "Promotionnelle", cost: "Coûts", custom: "Personnalisée",
};

export function PricingPanel({ refs, onChanged }: { refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canPrice = hasPermission("catalog.manage_price");
  const [l, setL] = useState({ code: "", name: "", listType: "wholesale", currency: "", customerGroupId: "", priority: "0" });
  const listForm = useSubmit({ "catalog.price_list_exists": "code", "catalog.price_list_code_required": "code", "catalog.price_list_type": "listType" });
  const [g, setG] = useState({ name: "", description: "" });
  const groupForm = useSubmit({ "catalog.customer_group_exists": "name", "catalog.customer_group_name": "name" });

  async function addList(e: FormEvent) {
    e.preventDefault();
    if (!l.code.trim()) return listForm.fail("code", "Le code est obligatoire.");
    if (!l.name.trim()) return listForm.fail("name", "Le nom est obligatoire.");
    const ok = await listForm.submit(async () => {
      await createPriceList({
        code: l.code.trim().toUpperCase(), name: l.name.trim(), listType: l.listType, currency: l.currency || null,
        customerGroupId: l.customerGroupId || null, priority: Math.trunc(num(l.priority) ?? 0),
      });
      return true;
    });
    if (ok) { toast.success("Liste de prix créée."); setL({ ...l, code: "", name: "" }); onChanged(); }
  }

  async function addGroup(e: FormEvent) {
    e.preventDefault();
    if (!g.name.trim()) return groupForm.fail("name", "Le nom est obligatoire.");
    const ok = await groupForm.submit(async () => { await createCustomerGroup(g.name.trim(), g.description.trim() || null); return true; });
    if (ok) { toast.success("Groupe de clients créé."); setG({ name: "", description: "" }); onChanged(); }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel>Listes de prix ({refs.priceLists.length})</SectionLabel>
        <p style={{ margin: "0 0 10px", fontSize: 13, color: "var(--nx-text-2)" }}>
          Un prix de liste ne s'applique qu'avec sa liste (devis, commande, simulateur) ; la caisse utilise le prix général et les prix de groupe de clients.
        </p>
        <DataTable
          exportTitle="Listes de prix"
          columns={[
            { key: "c", header: "Code", render: (x) => <code>{x.code}</code> },
            { key: "n", header: "Nom", render: (x) => x.name },
            { key: "t", header: "Type", render: (x) => LIST_TYPES[x.listType] ?? x.listType },
            { key: "d", header: "Devise", render: (x) => x.currency ?? "toutes" },
            { key: "g", header: "Groupe", render: (x) => x.customerGroup ?? "—" },
            { key: "p", header: "Priorité", numeric: true, render: (x) => x.priority },
            { key: "s", header: "Statut", render: (x) => <StatePill status={x.status} /> },
          ]}
          rows={refs.priceLists}
          rowKey={(x) => x.id}
          empty="Aucune liste de prix."
        />
        {canPrice && (
          <form onSubmit={addList} style={{ ...row2, marginTop: 12, alignItems: "end" }} noValidate>
            <Field label="Code" required error={listForm.fieldErrors.code}><TextInput value={l.code} placeholder="GROS" onChange={(e) => setL({ ...l, code: e.target.value })} /></Field>
            <Field label="Nom" required error={listForm.fieldErrors.name}><TextInput value={l.name} onChange={(e) => setL({ ...l, name: e.target.value })} /></Field>
            <Field label="Type" error={listForm.fieldErrors.listType}>
              <Select value={l.listType} onChange={(e) => setL({ ...l, listType: e.target.value })}>
                {Object.entries(LIST_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
              </Select>
            </Field>
            <Field label="Devise">
              <Select value={l.currency} onChange={(e) => setL({ ...l, currency: e.target.value })}>
                <option value="">(toutes)</option>
                {refs.currencies.map((x) => <option key={x.code} value={x.code}>{x.code}</option>)}
              </Select>
            </Field>
            <Field label="Groupe de clients">
              <Select value={l.customerGroupId} onChange={(e) => setL({ ...l, customerGroupId: e.target.value })}>
                <option value="">(aucun)</option>
                {refs.customerGroups.map((x) => <option key={x.id} value={x.id}>{x.name}</option>)}
              </Select>
            </Field>
            <Field label="Priorité"><TextInput type="number" step={1} value={l.priority} onChange={(e) => setL({ ...l, priority: e.target.value })} /></Field>
            <Button type="submit" size="sm" loading={listForm.busy}>Créer la liste</Button>
          </form>
        )}
        {listForm.error && <div role="alert" className="nx-error-text">{listForm.error}</div>}
      </Card>

      <Card>
        <SectionLabel>Groupes de clients ({refs.customerGroups.length})</SectionLabel>
        <p style={{ margin: "0 0 10px", fontSize: 13, color: "var(--nx-text-2)" }}>
          Un prix de groupe s'applique automatiquement en caisse aux clients rattachés à ce groupe.
        </p>
        <DataTable
          exportTitle={false}
          columns={[
            { key: "n", header: "Groupe", render: (x) => x.name },
            { key: "d", header: "Description", render: (x) => x.description ?? "—" },
            { key: "s", header: "Statut", render: (x) => <StatePill status={x.status} /> },
          ]}
          rows={refs.customerGroups}
          rowKey={(x) => x.id}
          empty="Aucun groupe de clients."
        />
        {canPrice && (
          <form onSubmit={addGroup} style={{ ...row2, marginTop: 12, alignItems: "end" }} noValidate>
            <Field label="Nom" required error={groupForm.fieldErrors.name}><TextInput value={g.name} onChange={(e) => setG({ ...g, name: e.target.value })} /></Field>
            <Field label="Description"><TextInput value={g.description} onChange={(e) => setG({ ...g, description: e.target.value })} /></Field>
            <Button type="submit" size="sm" loading={groupForm.busy}>Créer le groupe</Button>
          </form>
        )}
        {groupForm.error && <div role="alert" className="nx-error-text">{groupForm.error}</div>}
      </Card>
    </div>
  );
}
