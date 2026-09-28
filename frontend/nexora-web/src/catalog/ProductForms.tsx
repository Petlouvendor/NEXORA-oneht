import { useEffect, useMemo, useState, type FormEvent, type ReactNode } from "react";
import { dateShort } from "@/shared/format";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { errorMessage, useAsync } from "@/shared/useAsync";
import {
  Badge, Button, ConfirmDialog, DataTable, Drawer, Field, Modal, Money, SectionLabel, Select, Skeleton, StatePill, TextInput,
} from "@/shared/ui";
import { listSuppliers } from "@/parties/partiesApi";
import {
  addBarcode, addProductSupplier, createConversion, createProduct, deleteConversion, getProduct, loadProductImage,
  setProductPrice, setProductStatus, setReorderRule, updateProduct, uploadProductImage,
  type ProductDetail, type SetPriceInput,
} from "@/catalog/catalogApi";
import {
  categoryPath, fileToBase64, num, PRODUCT_TYPE_LABEL, sortedCategories, STATUS_LABEL, useSubmit, type CatalogRefs,
} from "@/catalog/catalogShared";

/** Transitions du workflow produit (miroir de ProductStatus.CanTransition côté serveur, qui reste l'autorité). */
export const STATUS_NEXT: Record<string, string[]> = {
  draft: ["review", "active"],
  review: ["active", "draft"],
  active: ["suspended", "inactive", "archived"],
  suspended: ["active", "archived"],
  inactive: ["active", "archived"],
  archived: [],
};

const PRODUCT_FIELDS: Record<string, string> = {
  "catalog.sku_required": "sku", "catalog.sku_exists": "sku",
  "catalog.name_required": "name", "catalog.barcode_exists": "barcode", "catalog.plu_exists": "pluCode",
  "catalog.unit_unknown": "unitId", "catalog.product_type": "productType", "catalog.tax_unknown": "defaultTaxId",
  "catalog.category_unknown": "categoryId",
};

const row2: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 12 };

// ============================================================================ Création

interface NewProduct {
  sku: string; name: string; shortName: string; productType: string; unitId: string; categoryId: string;
  brandId: string; defaultTaxId: string; barcode: string; pluCode: string; description: string;
  trackLots: boolean; isWeighed: boolean; status: string;
  salePrice: string; purchasePrice: string; currency: string; taxIncluded: boolean;
  minQuantity: string; reorderPoint: string; maxQuantity: string;
}

/** Création d'un produit : fiche, puis (facultatif) prix de vente, prix d'achat et seuils — chaque
 * étape passe par sa propre route serveur ; un échec partiel est signalé, jamais masqué. */
export function CreateProductModal({ refs, onClose, onCreated }: {
  refs: CatalogRefs; onClose: () => void; onCreated: (id: string) => void;
}) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canPrice = hasPermission("catalog.manage_price");
  const canCost = hasPermission("catalog.view_cost");
  const canReorder = hasPermission("inventory.adjust");
  const defaultUnit = refs.units.find((u) => u.code.toUpperCase() === "U" || u.code.toUpperCase() === "PCE")?.id ?? refs.units[0]?.id ?? "";
  const [f, setF] = useState<NewProduct>({
    sku: "", name: "", shortName: "", productType: "stockable", unitId: defaultUnit, categoryId: "", brandId: "",
    defaultTaxId: "", barcode: "", pluCode: "", description: "", trackLots: false, isWeighed: false, status: "active",
    salePrice: "", purchasePrice: "", currency: refs.defaultCurrency, taxIncluded: false,
    minQuantity: "", reorderPoint: "", maxQuantity: "",
  });
  const { busy, error, fieldErrors, submit, fail } = useSubmit(PRODUCT_FIELDS);
  const set = <K extends keyof NewProduct>(k: K, v: NewProduct[K]) => setF((x) => ({ ...x, [k]: v }));
  const cats = useMemo(() => sortedCategories(refs.categories.filter((c) => c.status === "active")), [refs.categories]);

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.sku.trim()) return fail("sku", "Le SKU est obligatoire.");
    if (!f.name.trim()) return fail("name", "Le nom est obligatoire.");
    if (!f.unitId) return fail("unitId", "Choisissez une unité (créez-en une dans l'onglet Unités au besoin).");
    const sale = num(f.salePrice), purchase = num(f.purchasePrice);
    if (f.salePrice && (sale === null || sale < 0)) return fail("salePrice", "Prix de vente invalide.");
    if (f.purchasePrice && (purchase === null || purchase < 0)) return fail("purchasePrice", "Prix d'achat invalide.");

    const id = await submit(() => createProduct({
      sku: f.sku.trim(), name: f.name.trim(), shortName: f.shortName.trim() || null, description: f.description.trim() || null,
      unitId: f.unitId, productType: f.productType, barcode: f.barcode.trim() || null, categoryId: f.categoryId || null,
      brandId: f.brandId || null, defaultTaxId: f.defaultTaxId || null, trackLots: f.trackLots, isWeighed: f.isWeighed,
      pluCode: f.pluCode.trim() || null, status: f.status,
    }));
    if (!id) return;

    // Compléments : un échec ici n'annule pas la fiche créée — il est signalé pour être corrigé sur la fiche.
    const problems: string[] = [];
    const price = (priceType: SetPriceInput["priceType"], amount: number) => setProductPrice(id, {
      priceType, amount, currency: f.currency, quantityMin: 0, customerGroupId: null, validFrom: null,
      priceListId: null, taxIncluded: priceType === "sale" && f.taxIncluded, minPrice: null,
    });
    if (canPrice && sale !== null) await price("sale", sale).catch((x) => problems.push(`prix de vente : ${errorMessage(x)}`));
    if (canPrice && canCost && purchase !== null) await price("purchase", purchase).catch((x) => problems.push(`prix d'achat : ${errorMessage(x)}`));
    const min = num(f.minQuantity), point = num(f.reorderPoint), max = num(f.maxQuantity);
    if (canReorder && (min !== null || point !== null || max !== null)) {
      await setReorderRule(id, {
        minQuantity: min ?? 0, reorderPoint: point ?? 0, maxQuantity: max ?? 0, reorderQuantity: 0, leadTimeDays: 0, autoOrder: false,
      }).catch((x) => problems.push(`seuils : ${errorMessage(x)}`));
    }
    if (problems.length) toast.push(`Produit créé. À corriger sur la fiche — ${problems.join(" ; ")}`, "warning");
    else toast.success(`Produit « ${f.name.trim()} » créé.`);
    onCreated(id);
  }

  return (
    <Modal title="Nouveau produit" onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 14 }} noValidate>
        <div style={row2}>
          <Field label="SKU" required error={fieldErrors.sku} hint="Référence unique, non modifiable ensuite.">
            <TextInput value={f.sku} onChange={(e) => set("sku", e.target.value)} invalid={!!fieldErrors.sku} autoFocus />
          </Field>
          <Field label="Nom" required error={fieldErrors.name}>
            <TextInput value={f.name} maxLength={200} onChange={(e) => set("name", e.target.value)} invalid={!!fieldErrors.name} />
          </Field>
          <Field label="Nom court (ticket, POS)">
            <TextInput value={f.shortName} onChange={(e) => set("shortName", e.target.value)} />
          </Field>
        </div>
        <div style={row2}>
          <Field label="Type" required error={fieldErrors.productType}>
            <Select value={f.productType} onChange={(e) => set("productType", e.target.value)}>
              {Object.entries(PRODUCT_TYPE_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
          <Field label="Unité de base" required error={fieldErrors.unitId} hint="Unité du stock ; non modifiable ensuite.">
            <Select value={f.unitId} onChange={(e) => set("unitId", e.target.value)}>
              <option value="">—</option>
              {refs.units.map((u) => <option key={u.id} value={u.id}>{u.code} — {u.name}</option>)}
            </Select>
          </Field>
          <Field label="Statut initial">
            <Select value={f.status} onChange={(e) => set("status", e.target.value)}>
              <option value="active">Actif (vendable)</option>
              <option value="draft">Brouillon</option>
            </Select>
          </Field>
        </div>
        <div style={row2}>
          <Field label="Catégorie" error={fieldErrors.categoryId}>
            <Select value={f.categoryId} onChange={(e) => set("categoryId", e.target.value)}>
              <option value="">(aucune)</option>
              {cats.map((c) => <option key={c.id} value={c.id}>{c.path}</option>)}
            </Select>
          </Field>
          <Field label="Marque">
            <Select value={f.brandId} onChange={(e) => set("brandId", e.target.value)}>
              <option value="">(aucune)</option>
              {refs.brands.filter((b) => b.status === "active").map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
            </Select>
          </Field>
          <Field label="Taxe par défaut" error={fieldErrors.defaultTaxId}>
            <Select value={f.defaultTaxId} onChange={(e) => set("defaultTaxId", e.target.value)}>
              <option value="">Hors taxe</option>
              {refs.taxes.filter((t) => t.status === "active").map((t) => <option key={t.id} value={t.id}>{t.name} ({t.rate} %)</option>)}
            </Select>
          </Field>
        </div>
        <div style={row2}>
          <Field label="Code-barres principal" error={fieldErrors.barcode}>
            <TextInput value={f.barcode} onChange={(e) => set("barcode", e.target.value)} invalid={!!fieldErrors.barcode} />
          </Field>
          <Field label="Code PLU (balance)" error={fieldErrors.pluCode}>
            <TextInput value={f.pluCode} onChange={(e) => set("pluCode", e.target.value)} invalid={!!fieldErrors.pluCode} />
          </Field>
          <div style={{ display: "grid", gap: 6, alignContent: "end", paddingBottom: 6 }}>
            <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
              <input type="checkbox" checked={f.trackLots} onChange={(e) => set("trackLots", e.target.checked)} /> Suivi par lots / péremption
            </label>
            <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
              <input type="checkbox" checked={f.isWeighed} onChange={(e) => set("isWeighed", e.target.checked)} /> Vendu au poids
            </label>
          </div>
        </div>
        <Field label="Description">
          <textarea className="nx-textarea" rows={2} value={f.description} onChange={(e) => set("description", e.target.value)} />
        </Field>

        {canPrice && (
          <>
            <SectionLabel>Prix (facultatif — historisés, jamais réécrits)</SectionLabel>
            <div style={row2}>
              <Field label="Prix de vente" error={fieldErrors.salePrice}>
                <TextInput type="number" min={0} step="0.01" inputMode="decimal" value={f.salePrice}
                           onChange={(e) => set("salePrice", e.target.value)} invalid={!!fieldErrors.salePrice} />
              </Field>
              {canCost && (
                <Field label="Prix d'achat" error={fieldErrors.purchasePrice}>
                  <TextInput type="number" min={0} step="0.01" inputMode="decimal" value={f.purchasePrice}
                             onChange={(e) => set("purchasePrice", e.target.value)} invalid={!!fieldErrors.purchasePrice} />
                </Field>
              )}
              <Field label="Devise">
                <Select value={f.currency} onChange={(e) => set("currency", e.target.value)}>
                  {refs.currencies.map((c) => <option key={c.code} value={c.code}>{c.code}</option>)}
                </Select>
              </Field>
              <label style={{ display: "flex", gap: 8, alignItems: "center", alignSelf: "end", paddingBottom: 8 }}>
                <input type="checkbox" checked={f.taxIncluded} onChange={(e) => set("taxIncluded", e.target.checked)} /> Prix de vente TTC
              </label>
            </div>
          </>
        )}

        {canReorder && f.productType !== "service" && (
          <>
            <SectionLabel>Seuils de stock (facultatif)</SectionLabel>
            <div style={row2}>
              <Field label="Stock minimum"><TextInput type="number" min={0} step="any" value={f.minQuantity} onChange={(e) => set("minQuantity", e.target.value)} /></Field>
              <Field label="Point de commande"><TextInput type="number" min={0} step="any" value={f.reorderPoint} onChange={(e) => set("reorderPoint", e.target.value)} /></Field>
              <Field label="Stock maximum" hint="0 ou vide = sans plafond"><TextInput type="number" min={0} step="any" value={f.maxQuantity} onChange={(e) => set("maxQuantity", e.target.value)} /></Field>
            </div>
          </>
        )}

        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy}>Créer le produit</Button>
        </div>
      </form>
    </Modal>
  );
}

// ============================================================================ Fiche produit

/** Fiche produit complète. Chaque section n'apparaît qu'avec la permission correspondante ; le serveur
 * reste l'autorité (il masque de lui-même coûts et fournisseurs sans catalog.view_cost). */
export function ProductDrawer({ productId, refs, onClose, onChanged }: {
  productId: string; refs: CatalogRefs; onClose: () => void; onChanged: () => void;
}) {
  const detail = useAsync(() => getProduct(productId), [productId]);
  const p = detail.data;
  const refresh = () => { detail.reload(); onChanged(); };

  return (
    <Drawer title={p?.name ?? "Produit"} onClose={onClose}>
      {detail.loading && !p && <Skeleton rows={6} />}
      {detail.error && <div role="alert" className="nx-error-text">{detail.error}</div>}
      {p && (
        <div style={{ display: "grid", gap: 18 }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <code>{p.sku}</code>
            <StatePill status={p.status} />
            <Badge>{PRODUCT_TYPE_LABEL[p.productType] ?? p.productType}</Badge>
            {p.unitCode && <Badge tone="info">Unité {p.unitCode}</Badge>}
            {p.pluCode && <Badge tone="info">PLU {p.pluCode}</Badge>}
            {p.trackLots && <Badge>Lots</Badge>}
          </div>
          <ImageSection product={p} onChanged={refresh} />
          <IdentitySection product={p} refs={refs} onSaved={refresh} />
          <StatusSection product={p} onChanged={refresh} />
          <PricesSection product={p} refs={refs} onChanged={refresh} />
          <BarcodesSection product={p} onChanged={refresh} />
          <SuppliersSection product={p} refs={refs} onChanged={refresh} />
          {p.productType !== "service" && <ReorderSection product={p} onSaved={refresh} />}
          <ConversionsSection product={p} refs={refs} onChanged={refresh} />
        </div>
      )}
    </Drawer>
  );
}

function Section({ title, children, action }: { title: string; children: ReactNode; action?: ReactNode }) {
  return (
    <section style={{ display: "grid", gap: 8 }}>
      <SectionLabel action={action}>{title}</SectionLabel>
      {children}
    </section>
  );
}

// ---------------------------------------------------------------- Image

function ImageSection({ product, onChanged }: { product: ProductDetail; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [url, setUrl] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const { busy, error, submit } = useSubmit();

  useEffect(() => {
    let revoked: string | null = null;
    setUrl(null);
    setLoadError(null);
    if (product.hasImage) {
      loadProductImage(product.id)
        .then((u) => { revoked = u; setUrl(u); })
        .catch((e) => setLoadError(errorMessage(e, "Image indisponible")));
    }
    return () => { if (revoked) URL.revokeObjectURL(revoked); };
  }, [product.id, product.hasImage]);

  async function pick(file: File | undefined) {
    if (!file) return;
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type)) { toast.error("Format accepté : PNG, JPEG ou WebP."); return; }
    if (file.size > 2 * 1024 * 1024) { toast.error("L'image doit peser 2 Mo au plus."); return; }
    const ok = await submit(async () => { await uploadProductImage(product.id, await fileToBase64(file), file.type); return true; });
    if (ok) { toast.success("Image enregistrée."); onChanged(); }
  }

  const canEdit = hasPermission("catalog.update");
  if (!product.hasImage && !canEdit && !product.imageUrl) return null;
  return (
    <Section title="Image">
      <div style={{ display: "flex", gap: 14, alignItems: "center", flexWrap: "wrap" }}>
        <div style={{
          width: 96, height: 96, borderRadius: 12, border: "1px solid var(--nx-border)", overflow: "hidden",
          display: "grid", placeItems: "center", background: "var(--nx-surface-2)", color: "var(--nx-text-3)", fontSize: 12,
        }}>
          {url ? <img src={url} alt={`Image de ${product.name}`} style={{ width: "100%", height: "100%", objectFit: "cover" }} />
            : product.hasImage && !loadError ? "…" : "Aucune image"}
        </div>
        {canEdit && (
          <label className="nx-btn nx-btn--subtle nx-btn--sm" style={{ cursor: busy ? "wait" : "pointer" }}>
            {busy ? "Envoi…" : product.hasImage ? "Remplacer l'image" : "Ajouter une image"}
            <input type="file" accept="image/png,image/jpeg,image/webp" hidden disabled={busy}
                   onChange={(e) => { void pick(e.target.files?.[0]); e.target.value = ""; }} />
          </label>
        )}
      </div>
      {(error || loadError) && <div role="alert" className="nx-error-text">{error ?? loadError}</div>}
    </Section>
  );
}

// ---------------------------------------------------------------- Identité

function IdentitySection({ product, refs, onSaved }: { product: ProductDetail; refs: CatalogRefs; onSaved: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [f, setF] = useState({
    name: product.name, shortName: product.shortName ?? "", pluCode: product.pluCode ?? "", description: product.description ?? "",
    categoryId: product.categoryId ?? "", brandId: product.brandId ?? "", defaultTaxId: product.defaultTaxId ?? "",
  });
  const { busy, error, fieldErrors, submit, fail } = useSubmit(PRODUCT_FIELDS);
  const cats = useMemo(() => sortedCategories(refs.categories), [refs.categories]);
  const tax = refs.taxes.find((t) => t.id === product.defaultTaxId);

  if (!open) {
    return (
      <Section title="Identité" action={hasPermission("catalog.update") ? <Button size="sm" variant="subtle" onClick={() => setOpen(true)}>Modifier</Button> : undefined}>
        <dl style={{ display: "grid", gridTemplateColumns: "140px 1fr", gap: "4px 12px", margin: 0 }}>
          <dt>Catégorie</dt><dd style={{ margin: 0 }}>{categoryPath(refs.categories, product.categoryId) || "—"}</dd>
          <dt>Marque</dt><dd style={{ margin: 0 }}>{product.brandName ?? "—"}</dd>
          <dt>Taxe</dt><dd style={{ margin: 0 }}>{tax ? `${tax.name} (${tax.rate} %)` : "Hors taxe"}</dd>
          <dt>Nom court</dt><dd style={{ margin: 0 }}>{product.shortName ?? "—"}</dd>
          <dt>Description</dt><dd style={{ margin: 0 }}>{product.description ?? "—"}</dd>
        </dl>
      </Section>
    );
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.name.trim()) return fail("name", "Le nom est obligatoire.");
    const ok = await submit(async () => {
      await updateProduct(product.id, {
        name: f.name.trim(), shortName: f.shortName.trim() || null, pluCode: f.pluCode.trim() || null,
        description: f.description.trim() || null, categoryId: f.categoryId || null, brandId: f.brandId || null,
        defaultTaxId: f.defaultTaxId || null,
      });
      return true;
    });
    if (ok) { toast.success("Fiche enregistrée."); setOpen(false); onSaved(); }
  }

  return (
    <Section title="Modifier la fiche">
      <form onSubmit={save} style={{ display: "grid", gap: 10 }} noValidate>
        <Field label="Nom" required error={fieldErrors.name}>
          <TextInput value={f.name} maxLength={200} onChange={(e) => setF({ ...f, name: e.target.value })} invalid={!!fieldErrors.name} />
        </Field>
        <div style={row2}>
          <Field label="Nom court"><TextInput value={f.shortName} onChange={(e) => setF({ ...f, shortName: e.target.value })} /></Field>
          <Field label="Code PLU" error={fieldErrors.pluCode}><TextInput value={f.pluCode} onChange={(e) => setF({ ...f, pluCode: e.target.value })} /></Field>
        </div>
        <div style={row2}>
          <Field label="Catégorie" error={fieldErrors.categoryId}>
            <Select value={f.categoryId} onChange={(e) => setF({ ...f, categoryId: e.target.value })}>
              <option value="">(inchangée)</option>
              {cats.map((c) => <option key={c.id} value={c.id}>{c.path}</option>)}
            </Select>
          </Field>
          <Field label="Marque">
            <Select value={f.brandId} onChange={(e) => setF({ ...f, brandId: e.target.value })}>
              <option value="">(inchangée)</option>
              {refs.brands.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
            </Select>
          </Field>
          <Field label="Taxe par défaut" error={fieldErrors.defaultTaxId}>
            <Select value={f.defaultTaxId} onChange={(e) => setF({ ...f, defaultTaxId: e.target.value })}>
              <option value="">(inchangée)</option>
              {refs.taxes.map((t) => <option key={t.id} value={t.id}>{t.name} ({t.rate} %)</option>)}
            </Select>
          </Field>
        </div>
        <Field label="Description">
          <textarea className="nx-textarea" rows={2} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} />
        </Field>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8 }}>
          <Button size="sm" type="submit" loading={busy}>Enregistrer</Button>
          <Button size="sm" type="button" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Annuler</Button>
        </div>
      </form>
    </Section>
  );
}

// ---------------------------------------------------------------- Workflow

function StatusSection({ product, onChanged }: { product: ProductDetail; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [confirm, setConfirm] = useState<string | null>(null);
  const { busy, error, submit } = useSubmit();
  const next = STATUS_NEXT[product.status] ?? [];
  if (!hasPermission("catalog.update") || next.length === 0) return null;

  async function apply(status: string) {
    setConfirm(null);
    const ok = await submit(async () => { await setProductStatus(product.id, status); return true; });
    if (ok) { toast.success(`Statut : ${STATUS_LABEL[status] ?? status}.`); onChanged(); }
  }
  // Retirer un produit de la vente est confirmé ; l'archivage est définitif.
  const sensitive = (s: string) => s === "archived" || s === "suspended" || s === "inactive";

  return (
    <Section title="Statut">
      <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
        {next.map((s) => (
          <Button key={s} size="sm" variant={s === "archived" ? "danger" : "subtle"} disabled={busy}
                  onClick={() => (sensitive(s) ? setConfirm(s) : void apply(s))}>
            {STATUS_LABEL[s] ?? s}
          </Button>
        ))}
      </div>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
      {confirm && (
        <ConfirmDialog
          title={`Passer « ${product.name} » à ${STATUS_LABEL[confirm]?.toLowerCase() ?? confirm}`}
          message={confirm === "archived"
            ? "Un produit archivé ne peut plus être vendu ni réactivé. Son historique (ventes, stock, prix) est conservé."
            : "Le produit ne sera plus proposé à la vente tant qu'il n'est pas réactivé."}
          danger={confirm === "archived"}
          confirmLabel="Confirmer"
          onConfirm={() => void apply(confirm)}
          onCancel={() => setConfirm(null)}
        />
      )}
    </Section>
  );
}

// ---------------------------------------------------------------- Prix

function PricesSection({ product, refs, onChanged }: { product: ProductDetail; refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canPrice = hasPermission("catalog.manage_price");
  const canCost = hasPermission("catalog.view_cost");
  const [open, setOpen] = useState(false);
  const empty = {
    priceType: "sale" as SetPriceInput["priceType"], amount: "", currency: refs.defaultCurrency, quantityMin: "0",
    priceListId: "", customerGroupId: "", taxIncluded: false, minPrice: "", validFrom: "",
  };
  const [f, setF] = useState(empty);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "catalog.price_negative": "amount", "catalog.price_quantity": "quantityMin" });

  const active = product.prices.filter((x) => !x.validTo);
  const sale = active.find((x) => x.priceType === "sale" && !x.priceListCode && !x.customerGroup && x.quantityMin === 0);
  // Coût de référence dans la même devise : prix d'achat actif, sinon celui du fournisseur principal.
  const costAmount = active.find((x) => x.priceType === "purchase" && x.currency === sale?.currency)?.amount
    ?? product.suppliers.find((s) => s.isPreferred && s.currency === sale?.currency)?.purchasePrice;
  const margin = sale && costAmount !== undefined && sale.amount > 0 ? ((sale.amount - costAmount) / sale.amount) * 100 : null;

  async function save(e: FormEvent) {
    e.preventDefault();
    const amount = num(f.amount), qmin = num(f.quantityMin) ?? 0, minPrice = num(f.minPrice);
    if (amount === null || amount < 0) return fail("amount", "Montant invalide.");
    if (qmin < 0) return fail("quantityMin", "Quantité invalide.");
    if (f.minPrice && (minPrice === null || minPrice < 0 || minPrice > amount)) return fail("minPrice", "Le prix plancher doit être compris entre 0 et le prix.");
    const ok = await submit(async () => {
      await setProductPrice(product.id, {
        priceType: f.priceType, amount, currency: f.currency, quantityMin: qmin,
        priceListId: f.priceListId || null, customerGroupId: f.customerGroupId || null,
        taxIncluded: f.taxIncluded, minPrice, validFrom: f.validFrom ? new Date(f.validFrom).toISOString() : null,
      });
      return true;
    });
    if (ok) { toast.success("Prix enregistré — l'ancien prix de même portée est clos, pas effacé."); setF(empty); setOpen(false); onChanged(); }
  }

  return (
    <Section title={`Prix (${active.length} actifs)`}
             action={canPrice && !open ? <Button size="sm" variant="subtle" onClick={() => setOpen(true)}>Nouveau prix</Button> : undefined}>
      {canCost && margin !== null && (
        <div style={{ fontSize: 13, color: "var(--nx-text-2)" }}>
          Marge indicative sur le prix de base : <strong style={{ color: margin < 0 ? "var(--nx-neg)" : "var(--nx-text)" }}>{margin.toFixed(1)} %</strong>
          {" "}(coût de référence, hors frais — le coût réel du stock est le coût moyen pondéré).
        </div>
      )}
      <DataTable
        exportTitle={false}
        columns={[
          { key: "t", header: "Portée", render: (x) => x.priceListCode ?? x.customerGroup ?? (x.priceType === "sale" ? "Vente" : x.priceType === "purchase" ? "Achat" : "Promo") },
          { key: "min", header: "Qté ≥", numeric: true, render: (x) => x.quantityMin },
          { key: "a", header: "Montant", numeric: true, render: (x) => <Money value={x.amount} currency={x.currency} /> },
          { key: "tax", header: "Base", render: (x) => (x.taxIncluded ? "TTC" : "HT") },
          { key: "v", header: "Depuis", render: (x) => dateShort(x.validFrom) },
          { key: "c", header: "", render: (x) => (x.validTo ? <Badge tone="warning">clos {dateShort(x.validTo)}</Badge> : <Badge tone="success">actif</Badge>) },
        ]}
        rows={product.prices}
        rowKey={(x) => x.id}
        empty="Aucun prix — le produit se vendra à 0 tant qu'un prix de vente n'est pas défini."
      />
      {open && (
        <form onSubmit={save} className="nx-card" style={{ display: "grid", gap: 10 }} noValidate>
          <div style={row2}>
            <Field label="Type">
              <Select value={f.priceType} onChange={(e) => setF({ ...f, priceType: e.target.value as SetPriceInput["priceType"] })}>
                <option value="sale">Vente</option>
                {canCost && <option value="purchase">Achat</option>}
              </Select>
            </Field>
            <Field label="Montant" required error={fieldErrors.amount}>
              <TextInput type="number" min={0} step="0.01" value={f.amount} onChange={(e) => setF({ ...f, amount: e.target.value })} invalid={!!fieldErrors.amount} autoFocus />
            </Field>
            <Field label="Devise">
              <Select value={f.currency} onChange={(e) => setF({ ...f, currency: e.target.value })}>
                {refs.currencies.map((c) => <option key={c.code} value={c.code}>{c.code}</option>)}
              </Select>
            </Field>
          </div>
          {f.priceType === "sale" && (
            <>
              <div style={row2}>
                <Field label="À partir de (quantité)" error={fieldErrors.quantityMin} hint="0 = prix unitaire ; 10 = palier dès 10 unités">
                  <TextInput type="number" min={0} step="any" value={f.quantityMin} onChange={(e) => setF({ ...f, quantityMin: e.target.value })} />
                </Field>
                <Field label="Liste tarifaire">
                  <Select value={f.priceListId} onChange={(e) => setF({ ...f, priceListId: e.target.value })}>
                    <option value="">(prix général)</option>
                    {refs.priceLists.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
                  </Select>
                </Field>
                <Field label="Groupe de clients">
                  <Select value={f.customerGroupId} onChange={(e) => setF({ ...f, customerGroupId: e.target.value })}>
                    <option value="">(tous)</option>
                    {refs.customerGroups.map((g) => <option key={g.id} value={g.id}>{g.name}</option>)}
                  </Select>
                </Field>
              </div>
              <div style={row2}>
                <Field label="Prix plancher" error={fieldErrors.minPrice} hint="Aucune remise ne peut descendre en dessous.">
                  <TextInput type="number" min={0} step="0.01" value={f.minPrice} onChange={(e) => setF({ ...f, minPrice: e.target.value })} />
                </Field>
                <Field label="Valable à partir du" hint="Vide = immédiatement">
                  <TextInput type="datetime-local" value={f.validFrom} onChange={(e) => setF({ ...f, validFrom: e.target.value })} />
                </Field>
                <label style={{ display: "flex", gap: 8, alignItems: "center", alignSelf: "end", paddingBottom: 8 }}>
                  <input type="checkbox" checked={f.taxIncluded} onChange={(e) => setF({ ...f, taxIncluded: e.target.checked })} /> Montant TTC
                </label>
              </div>
            </>
          )}
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <Button size="sm" type="submit" loading={busy}>Enregistrer le prix</Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Annuler</Button>
          </div>
        </form>
      )}
    </Section>
  );
}

// ---------------------------------------------------------------- Codes-barres

function BarcodesSection({ product, onChanged }: { product: ProductDetail; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [f, setF] = useState({ barcode: "", packSize: "1", isPrimary: false });
  const { busy, error, fieldErrors, submit, fail } = useSubmit({ "catalog.barcode_exists": "barcode", "catalog.barcode_required": "barcode" });

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.barcode.trim()) return fail("barcode", "Le code-barres est obligatoire.");
    const pack = num(f.packSize) ?? 1;
    if (pack <= 0) return fail("packSize", "Le colisage doit être positif.");
    const ok = await submit(async () => {
      await addBarcode(product.id, { barcode: f.barcode.trim(), packSize: pack, barcodeType: null, isPrimary: f.isPrimary });
      return true;
    });
    if (ok) { toast.success("Code-barres ajouté."); setF({ barcode: "", packSize: "1", isPrimary: false }); setOpen(false); onChanged(); }
  }

  return (
    <Section title={`Codes-barres (${product.barcodes.length})`}
             action={hasPermission("catalog.update") && !open ? <Button size="sm" variant="subtle" onClick={() => setOpen(true)}>Ajouter</Button> : undefined}>
      <DataTable
        exportTitle={false}
        columns={[
          { key: "b", header: "Code", render: (b) => <code>{b.barcode}</code> },
          { key: "p", header: "Colisage", numeric: true, render: (b) => (b.packSize === 1 ? "unité" : `× ${b.packSize}`) },
          { key: "f", header: "", render: (b) => (b.isPrimary ? <Badge tone="info">principal</Badge> : null) },
          { key: "s", header: "Statut", render: (b) => <StatePill status={b.status} /> },
        ]}
        rows={product.barcodes}
        rowKey={(b) => b.id}
        empty="Aucun code-barres secondaire."
      />
      {open && (
        <form onSubmit={save} className="nx-card" style={{ display: "grid", gap: 10 }} noValidate>
          <div style={row2}>
            <Field label="Code-barres" required error={fieldErrors.barcode}>
              <TextInput value={f.barcode} onChange={(e) => setF({ ...f, barcode: e.target.value })} invalid={!!fieldErrors.barcode} autoFocus />
            </Field>
            <Field label="Colisage" error={fieldErrors.packSize} hint="Unités de base par scan (ex. 24 pour un carton).">
              <TextInput type="number" min={1} step="any" value={f.packSize} onChange={(e) => setF({ ...f, packSize: e.target.value })} />
            </Field>
            <label style={{ display: "flex", gap: 8, alignItems: "center", alignSelf: "end", paddingBottom: 8 }}>
              <input type="checkbox" checked={f.isPrimary} onChange={(e) => setF({ ...f, isPrimary: e.target.checked })} /> Principal
            </label>
          </div>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <Button size="sm" type="submit" loading={busy}>Ajouter</Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Annuler</Button>
          </div>
        </form>
      )}
    </Section>
  );
}

// ---------------------------------------------------------------- Fournisseurs

function SuppliersSection({ product, refs, onChanged }: { product: ProductDetail; refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canCost = hasPermission("catalog.view_cost");
  const canLink = canCost && hasPermission("catalog.update") && hasPermission("suppliers.view");
  const [open, setOpen] = useState(false);
  const suppliers = useAsync(() => (open ? listSuppliers() : Promise.resolve([])), [open]);
  const [f, setF] = useState({ supplierId: "", supplierSku: "", purchasePrice: "", currency: refs.defaultCurrency, leadTimeDays: "0", isPreferred: true });
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "catalog.supplier_unknown": "supplierId", "catalog.supplier_linked": "supplierId", "catalog.supplier_values": "purchasePrice",
  });
  if (!canCost) return null;
  const linked = new Set(product.suppliers.map((s) => s.supplierName));

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.supplierId) return fail("supplierId", "Choisissez un fournisseur.");
    const price = num(f.purchasePrice), lead = num(f.leadTimeDays) ?? 0;
    if (price === null || price < 0) return fail("purchasePrice", "Prix d'achat invalide.");
    if (lead < 0 || !Number.isInteger(lead)) return fail("leadTimeDays", "Délai en jours entiers.");
    const ok = await submit(async () => {
      await addProductSupplier(product.id, {
        supplierId: f.supplierId, supplierSku: f.supplierSku.trim() || null, purchasePrice: price,
        currency: f.currency, leadTimeDays: lead, isPreferred: f.isPreferred,
      });
      return true;
    });
    if (ok) { toast.success("Fournisseur associé."); setOpen(false); onChanged(); }
  }

  return (
    <Section title={`Fournisseurs (${product.suppliers.length})`}
             action={canLink && !open ? <Button size="sm" variant="subtle" onClick={() => setOpen(true)}>Associer</Button> : undefined}>
      <DataTable
        exportTitle={false}
        columns={[
          { key: "n", header: "Fournisseur", render: (sp) => sp.supplierName },
          { key: "r", header: "Réf. fournisseur", render: (sp) => sp.supplierSku ?? "—" },
          { key: "p", header: "Coût", numeric: true, render: (sp) => <Money value={sp.purchasePrice} currency={sp.currency} /> },
          { key: "d", header: "Délai (j)", numeric: true, render: (sp) => sp.leadTimeDays },
          { key: "f", header: "", render: (sp) => (sp.isPreferred ? <Badge tone="success">principal</Badge> : null) },
        ]}
        rows={product.suppliers}
        rowKey={(sp) => sp.id}
        empty="Aucun fournisseur associé."
      />
      {open && (
        <form onSubmit={save} className="nx-card" style={{ display: "grid", gap: 10 }} noValidate>
          <div style={row2}>
            <Field label="Fournisseur" required error={fieldErrors.supplierId}>
              <Select value={f.supplierId} onChange={(e) => setF({ ...f, supplierId: e.target.value })}>
                <option value="">{suppliers.loading ? "Chargement…" : "—"}</option>
                {(suppliers.data ?? []).filter((s) => s.status === "active" && !linked.has(s.name))
                  .map((s) => <option key={s.id} value={s.id}>{s.name} ({s.supplierNumber})</option>)}
              </Select>
            </Field>
            <Field label="Réf. chez le fournisseur"><TextInput value={f.supplierSku} onChange={(e) => setF({ ...f, supplierSku: e.target.value })} /></Field>
          </div>
          <div style={row2}>
            <Field label="Prix d'achat" required error={fieldErrors.purchasePrice}>
              <TextInput type="number" min={0} step="0.01" value={f.purchasePrice} onChange={(e) => setF({ ...f, purchasePrice: e.target.value })} />
            </Field>
            <Field label="Devise">
              <Select value={f.currency} onChange={(e) => setF({ ...f, currency: e.target.value })}>
                {refs.currencies.map((c) => <option key={c.code} value={c.code}>{c.code}</option>)}
              </Select>
            </Field>
            <Field label="Délai de livraison (jours)" error={fieldErrors.leadTimeDays}>
              <TextInput type="number" min={0} step={1} value={f.leadTimeDays} onChange={(e) => setF({ ...f, leadTimeDays: e.target.value })} />
            </Field>
            <label style={{ display: "flex", gap: 8, alignItems: "center", alignSelf: "end", paddingBottom: 8 }}>
              <input type="checkbox" checked={f.isPreferred} onChange={(e) => setF({ ...f, isPreferred: e.target.checked })} /> Fournisseur principal
            </label>
          </div>
          {(error || suppliers.error) && <div role="alert" className="nx-error-text">{error ?? suppliers.error}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <Button size="sm" type="submit" loading={busy}>Associer</Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Annuler</Button>
          </div>
        </form>
      )}
    </Section>
  );
}

// ---------------------------------------------------------------- Seuils

function ReorderSection({ product, onSaved }: { product: ProductDetail; onSaved: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const r = product.reorderRule;
  const [f, setF] = useState({
    minQuantity: String(r?.minQuantity ?? ""), reorderPoint: String(r?.reorderPoint ?? ""), maxQuantity: String(r?.maxQuantity ?? ""),
    reorderQuantity: String(r?.reorderQuantity ?? ""), leadTimeDays: String(r?.leadTimeDays ?? ""),
  });
  const [open, setOpen] = useState(false);
  const { busy, error, submit, fail, fieldErrors } = useSubmit({ "catalog.reorder_bounds": "maxQuantity", "catalog.reorder_negative": "minQuantity" });
  const canEdit = hasPermission("inventory.adjust");
  if (!canEdit && !r) return null;

  async function save(e: FormEvent) {
    e.preventDefault();
    const v = {
      minQuantity: num(f.minQuantity) ?? 0, reorderPoint: num(f.reorderPoint) ?? 0, maxQuantity: num(f.maxQuantity) ?? 0,
      reorderQuantity: num(f.reorderQuantity) ?? 0, leadTimeDays: num(f.leadTimeDays) ?? 0,
    };
    if (Object.values(v).some((x) => x < 0)) return fail("minQuantity", "Les valeurs ne peuvent être négatives.");
    if (v.maxQuantity > 0 && (v.minQuantity > v.maxQuantity || v.reorderPoint > v.maxQuantity))
      return fail("maxQuantity", "Le maximum doit être supérieur au minimum et au point de commande.");
    const ok = await submit(async () => { await setReorderRule(product.id, { ...v, autoOrder: false }); return true; });
    if (ok) { toast.success("Seuils enregistrés."); setOpen(false); onSaved(); }
  }

  return (
    <Section title="Seuils de stock (tous emplacements)"
             action={canEdit && !open ? <Button size="sm" variant="subtle" onClick={() => setOpen(true)}>{r ? "Modifier" : "Définir"}</Button> : undefined}>
      {!open && (r
        ? <div style={{ fontSize: 14 }}>Min <b>{r.minQuantity}</b> · point de commande <b>{r.reorderPoint}</b> · max <b>{r.maxQuantity || "∞"}</b>
            {r.reorderQuantity > 0 && <> · commander <b>{r.reorderQuantity}</b></>}{r.leadTimeDays > 0 && <> · délai {r.leadTimeDays} j</>}</div>
        : <div style={{ color: "var(--nx-text-2)", fontSize: 14 }}>Aucun seuil : pas d'alerte de stock faible ni de suggestion de réapprovisionnement.</div>)}
      {open && (
        <form onSubmit={save} className="nx-card" style={{ display: "grid", gap: 10 }} noValidate>
          <div style={row2}>
            <Field label="Stock minimum" error={fieldErrors.minQuantity}><TextInput type="number" min={0} step="any" value={f.minQuantity} onChange={(e) => setF({ ...f, minQuantity: e.target.value })} /></Field>
            <Field label="Point de commande"><TextInput type="number" min={0} step="any" value={f.reorderPoint} onChange={(e) => setF({ ...f, reorderPoint: e.target.value })} /></Field>
            <Field label="Stock maximum" error={fieldErrors.maxQuantity} hint="0 = sans plafond"><TextInput type="number" min={0} step="any" value={f.maxQuantity} onChange={(e) => setF({ ...f, maxQuantity: e.target.value })} /></Field>
            <Field label="Quantité à commander"><TextInput type="number" min={0} step="any" value={f.reorderQuantity} onChange={(e) => setF({ ...f, reorderQuantity: e.target.value })} /></Field>
            <Field label="Délai (jours)"><TextInput type="number" min={0} step={1} value={f.leadTimeDays} onChange={(e) => setF({ ...f, leadTimeDays: e.target.value })} /></Field>
          </div>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <Button size="sm" type="submit" loading={busy}>Enregistrer</Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Annuler</Button>
          </div>
        </form>
      )}
    </Section>
  );
}

// ---------------------------------------------------------------- Conversions

function ConversionsSection({ product, refs, onChanged }: { product: ProductDetail; refs: CatalogRefs; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [open, setOpen] = useState(false);
  const [f, setF] = useState({ fromUnitId: "", factor: "" });
  const [removing, setRemoving] = useState<string | null>(null);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "catalog.conversion_factor": "factor", "catalog.conversion_exists": "fromUnitId", "catalog.conversion_same_unit": "fromUnitId",
  });
  const canEdit = hasPermission("catalog.update");
  const list = product.conversions ?? [];
  if (!canEdit && list.length === 0) return null;

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.fromUnitId) return fail("fromUnitId", "Choisissez l'unité d'emballage.");
    const factor = num(f.factor);
    if (factor === null || factor <= 0) return fail("factor", "Le nombre d'unités de base doit être positif.");
    if (!product.unitId) return;
    const ok = await submit(async () => {
      await createConversion({ productId: product.id, fromUnitId: f.fromUnitId, toUnitId: product.unitId!, factor });
      return true;
    });
    if (ok) { toast.success("Conversion ajoutée."); setF({ fromUnitId: "", factor: "" }); setOpen(false); onChanged(); }
  }

  async function remove(id: string) {
    setRemoving(null);
    const ok = await submit(async () => { await deleteConversion(id); return true; });
    if (ok) { toast.success("Conversion supprimée."); onChanged(); }
  }

  return (
    <Section title="Conditionnements propres au produit"
             action={canEdit && !open ? <Button size="sm" variant="subtle" onClick={() => setOpen(true)}>Ajouter</Button> : undefined}>
      {list.length === 0 && !open && (
        <div style={{ color: "var(--nx-text-2)", fontSize: 14 }}>
          Aucune conversion propre : les conversions générales (onglet Unités) s'appliquent.
        </div>
      )}
      {list.map((c) => (
        <div key={c.id} style={{ display: "flex", gap: 10, alignItems: "center" }}>
          <span>1 <b>{c.fromUnitCode}</b> = <b>{c.factor}</b> {c.toUnitCode}</span>
          {canEdit && <Button size="sm" variant="ghost" disabled={busy} onClick={() => setRemoving(c.id)}>Supprimer</Button>}
        </div>
      ))}
      {open && (
        <form onSubmit={save} className="nx-card" style={{ display: "grid", gap: 10 }} noValidate>
          <div style={row2}>
            <Field label="1 unité de" required error={fieldErrors.fromUnitId}>
              <Select value={f.fromUnitId} onChange={(e) => setF({ ...f, fromUnitId: e.target.value })}>
                <option value="">—</option>
                {refs.units.filter((u) => u.id !== product.unitId).map((u) => <option key={u.id} value={u.id}>{u.code} — {u.name}</option>)}
              </Select>
            </Field>
            <Field label={`= combien de ${product.unitCode ?? "unités de base"}`} required error={fieldErrors.factor}>
              <TextInput type="number" min={0} step="any" value={f.factor} onChange={(e) => setF({ ...f, factor: e.target.value })} placeholder="24" />
            </Field>
          </div>
          {error && <div role="alert" className="nx-error-text">{error}</div>}
          <div style={{ display: "flex", gap: 8 }}>
            <Button size="sm" type="submit" loading={busy}>Ajouter</Button>
            <Button size="sm" type="button" variant="ghost" onClick={() => setOpen(false)} disabled={busy}>Annuler</Button>
          </div>
        </form>
      )}
      {!open && error && <div role="alert" className="nx-error-text">{error}</div>}
      {removing && (
        <ConfirmDialog title="Supprimer la conversion" message="Les opérations déjà enregistrées ne sont pas modifiées ; les suivantes utiliseront la conversion générale, si elle existe."
                       danger confirmLabel="Supprimer" onConfirm={() => void remove(removing)} onCancel={() => setRemoving(null)} />
      )}
    </Section>
  );
}
