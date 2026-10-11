import { useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { dateShort, money } from "@/shared/format";
import { Button, DataTable, Drawer, Field, KpiCard, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { apiFetch } from "@/core/apiClient";
import { createSupplier, updateSupplier, type SupplierInput } from "@/parties/partiesApi";
import { createPaymentTerms, listPaymentTerms, setSupplierTerms, supplierOverview } from "@/purchasing/purchasingApi";

interface SupplierRecord {
  id: string; supplierNumber: string; name: string; type: "individual" | "business"; phone: string | null; email: string | null;
  address: string | null; taxIdentifier: string | null; status: string; currency: string;
}

/** Fiche fournisseur : synthèse (achats, dettes, derniers prix), identité modifiable, conditions commerciales. */
export function SupplierSheet({ supplierId, onClose, onChanged }: { supplierId: string; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const overview = useAsync(() => supplierOverview(supplierId), [supplierId]);
  const record = useAsync(() => apiFetch<SupplierRecord>(`/suppliers/${supplierId}`), [supplierId]);
  const [editing, setEditing] = useState<"identity" | "terms" | null>(null);
  const o = overview.data;
  const reload = () => { overview.reload(); record.reload(); onChanged(); };

  return (
    <Drawer title={o ? o.name : "Fournisseur"} onClose={onClose}>
      {(overview.error || record.error) && <div role="alert" className="nx-error-text">{overview.error ?? record.error}</div>}
      {!o ? <div>Chargement…</div> : (
        <div style={{ display: "grid", gap: 14 }}>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))", gap: 10 }}>
            <KpiCard label="Achats 12 mois" value={money(o.purchasedLast12Months, o.currency)} />
            <KpiCard label="Dettes ouvertes" value={money(o.openPayables, o.currency)} />
            <KpiCard label="Dont en retard" value={money(o.overduePayables, o.currency)} tone={o.overduePayables > 0 ? "warning" : undefined} />
            <KpiCard label="Commandes en cours" value={String(o.openOrders)} />
          </div>
          <div style={{ display: "grid", gap: 4 }}>
            {record.data && <div><code>{record.data.supplierNumber}</code> · {record.data.phone ?? "—"} · {record.data.email ?? "—"}</div>}
            <div>Contact : {o.contactName ?? "—"}</div>
            <div>Conditions : {o.paymentTermsName ? `${o.paymentTermsName} (${o.paymentTermsDays} j)` : "aucune (échéance saisie sur la facture)"}</div>
            <div>Limite de crédit : {o.creditLimit > 0 ? money(o.creditLimit, o.currency) : "non définie"}</div>
            <div>Dernière réception : {o.lastReceiptAt ? dateShort(o.lastReceiptAt) : "—"}</div>
            {o.notes && <div style={{ color: "var(--nx-text-2)", whiteSpace: "pre-wrap" }}>{o.notes}</div>}
          </div>
          {hasPermission("suppliers.update") && (
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              <Button size="sm" variant="subtle" disabled={!record.data} onClick={() => setEditing("identity")}>Modifier la fiche</Button>
              <Button size="sm" variant="subtle" onClick={() => setEditing("terms")}>Conditions commerciales</Button>
            </div>
          )}
          <SectionLabel>Derniers prix d'achat constatés</SectionLabel>
          <DataTable exportTitle={`Prix d'achat — ${o.name}`}
            columns={[
              { key: "d", header: "Date", render: (p) => dateShort(p.effectiveAt) },
              { key: "a", header: "Article", render: (p) => <><code>{p.sku}</code> {p.productName}</> },
              { key: "c", header: "Coût unitaire", numeric: true, render: (p) => money(p.unitCost, p.currency) },
              { key: "n", header: "Réception", render: (p) => <code>{p.documentNumber}</code> },
            ]}
            rows={o.priceHistory} rowKey={(p) => `${p.productId}-${p.effectiveAt}-${p.documentNumber}`} empty="Aucune réception de ce fournisseur." />
        </div>
      )}
      {editing === "identity" && record.data && (
        <SupplierIdentityDialog supplier={record.data} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />
      )}
      {editing === "terms" && o && (
        <SupplierTermsDialog supplierId={supplierId} initial={{ creditLimit: o.creditLimit, paymentTermsId: o.paymentTermsId, contactName: o.contactName, notes: o.notes }}
                             currency={o.currency} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />
      )}
    </Drawer>
  );
}

/** Création (supplier = null) ou modification de l'identité d'un fournisseur. */
export function SupplierIdentityDialog({ supplier, onClose, onSaved }: {
  supplier: SupplierRecord | null; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const [form, setForm] = useState<SupplierInput>({
    name: supplier?.name ?? "", type: supplier?.type ?? "business", phone: supplier?.phone ?? "", email: supplier?.email ?? "",
    address: supplier?.address ?? "", taxIdentifier: supplier?.taxIdentifier ?? "",
  });
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "party.name": "name", "party.email": "email" });
  const set = (patch: Partial<SupplierInput>) => setForm((f) => ({ ...f, ...patch }));

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!form.name.trim()) return fail("name", "Le nom est obligatoire.");
    if (form.email && !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(form.email)) return fail("email", "Adresse e-mail invalide.");
    const clean: SupplierInput = {
      ...form, name: form.name.trim(), phone: form.phone?.trim() || null, email: form.email?.trim() || null,
      address: form.address?.trim() || null, taxIdentifier: form.taxIdentifier?.trim() || null,
    };
    const ok = await submit(async () => { if (supplier) await updateSupplier(supplier.id, clean); else await createSupplier(clean); return true; });
    if (ok) { toast.success(supplier ? "Fiche fournisseur mise à jour." : "Fournisseur créé."); onSaved(); }
  }

  return (
    <Modal title={supplier ? `Modifier ${supplier.name}` : "Nouveau fournisseur"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Nom" required error={fieldErrors.name}><TextInput value={form.name} maxLength={200} onChange={(e) => set({ name: e.target.value })} /></Field>
        <Field label="Type">
          <Select value={form.type} onChange={(e) => set({ type: e.target.value as SupplierInput["type"] })}>
            <option value="business">Entreprise</option><option value="individual">Particulier</option>
          </Select>
        </Field>
        <Field label="Téléphone"><TextInput value={form.phone ?? ""} onChange={(e) => set({ phone: e.target.value })} /></Field>
        <Field label="E-mail" error={fieldErrors.email}><TextInput type="email" value={form.email ?? ""} onChange={(e) => set({ email: e.target.value })} /></Field>
        <Field label="Adresse"><TextInput value={form.address ?? ""} onChange={(e) => set({ address: e.target.value })} /></Field>
        <Field label="NIF / identifiant fiscal"><TextInput value={form.taxIdentifier ?? ""} onChange={(e) => set({ taxIdentifier: e.target.value })} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

function SupplierTermsDialog({ supplierId, initial, currency, onClose, onSaved }: {
  supplierId: string; initial: { creditLimit: number; paymentTermsId: string | null; contactName: string | null; notes: string | null };
  currency: string; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const terms = useAsync(listPaymentTerms, []);
  const [creditLimit, setCreditLimit] = useState(String(initial.creditLimit || ""));
  const [termsId, setTermsId] = useState(initial.paymentTermsId ?? "");
  const [contact, setContact] = useState(initial.contactName ?? "");
  const [notes, setNotes] = useState(initial.notes ?? "");
  const [newTerms, setNewTerms] = useState<{ name: string; days: string } | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "purchasing.credit_limit": "creditLimit", "purchasing.terms_unknown": "terms", "purchasing.terms_exists": "newTerms",
    "purchasing.terms_name": "newTerms", "purchasing.terms_days": "newTerms",
  });

  async function addTerms() {
    if (!newTerms) return;
    const days = num(newTerms.days);
    if (!newTerms.name.trim()) return fail("newTerms", "Nom des conditions obligatoire.");
    if (days == null || days < 0 || !Number.isInteger(days)) return fail("newTerms", "Nombre de jours entier positif.");
    const id = await submit(() => createPaymentTerms({ name: newTerms.name.trim(), days, earlyPaymentDiscountRate: 0, earlyPaymentDiscountDays: 0, latePenaltyRate: 0, description: null }));
    if (id) { terms.reload(); setTermsId(id); setNewTerms(null); }
  }

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const limit = creditLimit.trim() === "" ? 0 : num(creditLimit);
    if (limit == null || limit < 0) return fail("creditLimit", "Montant positif ou vide.");
    const ok = await submit(async () => {
      await setSupplierTerms(supplierId, { creditLimit: limit, paymentTermsId: termsId || null, contactName: contact.trim() || null, notes: notes.trim() || null });
      return true;
    });
    if (ok) { toast.success("Conditions enregistrées."); onSaved(); }
  }

  return (
    <Modal title="Conditions commerciales" onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 90vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <Field label="Conditions de paiement" error={fieldErrors.terms} hint="Détermine l'échéance des factures saisies sans échéance.">
          <Select value={termsId} onChange={(e) => setTermsId(e.target.value)}>
            <option value="">Aucune</option>
            {(terms.data ?? []).map((t) => <option key={t.id} value={t.id}>{t.name} — {t.days} j</option>)}
          </Select>
        </Field>
        {newTerms ? (
          <div style={{ display: "grid", gridTemplateColumns: "2fr 1fr auto", gap: 8, alignItems: "end" }}>
            <Field label="Nouvelles conditions" error={fieldErrors.newTerms}>
              <TextInput value={newTerms.name} placeholder="30 jours fin de mois" onChange={(e) => setNewTerms({ ...newTerms, name: e.target.value })} />
            </Field>
            <Field label="Jours"><TextInput type="number" min={0} step={1} value={newTerms.days} onChange={(e) => setNewTerms({ ...newTerms, days: e.target.value })} /></Field>
            <Button size="sm" disabled={busy} onClick={addTerms}>Créer</Button>
          </div>
        ) : <div><Button size="sm" variant="ghost" onClick={() => setNewTerms({ name: "", days: "30" })}>+ Nouvelles conditions</Button></div>}
        <Field label={`Limite de crédit (${currency})`} error={fieldErrors.creditLimit} hint="Encours maximum accordé par le fournisseur.">
          <TextInput type="number" min={0} step="0.01" value={creditLimit} onChange={(e) => setCreditLimit(e.target.value)} />
        </Field>
        <Field label="Contact"><TextInput value={contact} maxLength={200} onChange={(e) => setContact(e.target.value)} /></Field>
        <Field label="Notes"><textarea className="nx-textarea" rows={3} value={notes} maxLength={2000} onChange={(e) => setNotes(e.target.value)} /></Field>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}
