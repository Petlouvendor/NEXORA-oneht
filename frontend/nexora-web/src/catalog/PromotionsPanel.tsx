import { useMemo, useState, type FormEvent } from "react";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { errorMessage, useAsync } from "@/shared/useAsync";
import { Badge, Button, Card, DataTable, Field, Modal, SectionLabel, Select, StatePill, TextInput } from "@/shared/ui";
import { dateShort, money } from "@/shared/format";
import {
  createCoupon, createPromotion, listCoupons, listProducts, listPromotions, setCouponStatus, setPromotionStatus,
  updatePromotion, type Promotion, type PromotionTarget, type SavePromotion,
} from "@/catalog/catalogApi";
import { num, sortedCategories, useSubmit, type CatalogRefs } from "@/catalog/catalogShared";

const row2: React.CSSProperties = { display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))", gap: 12 };

export const PROMO_TYPES: Record<string, string> = {
  percentage: "Remise en %",
  amount: "Remise par unité",
  price_override: "Prix unitaire imposé",
  buy_x_get_y: "X achetés, Y offerts (2 pour 1…)",
  bundle: "Lot à prix fixe (3 pour 250…)",
};

/** Résumé lisible d'une règle — mêmes paramètres que ceux du calcul serveur (PromotionCalculator). */
export function describePromotion(p: Pick<Promotion, "promoType" | "value" | "conditions">, currency: string) {
  const c = p.conditions;
  const min = c?.minQuantity ? ` dès ${c.minQuantity} unités` : "";
  switch (p.promoType) {
    case "percentage": return `−${p.value} %${min}`;
    case "amount": return `−${money(p.value, currency)} par unité${min}`;
    case "price_override": return `${money(p.value, currency)} l'unité${min}`;
    case "buy_x_get_y": return `${c?.buyQuantity ?? "?"} acheté(s) + ${c?.getQuantity ?? "?"} offert(s)${min}`;
    case "bundle": return `${c?.bundleQuantity ?? "?"} pour ${money(p.value, currency)}${min}`;
    default: return p.promoType;
  }
}

const TARGET_LABEL: Record<string, string> = { all: "Tout le catalogue", product: "Produit", category: "Catégorie", brand: "Marque" };

export function PromotionsPanel({ refs }: { refs: CatalogRefs }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const canManage = hasPermission("catalog.manage_promotion");
  const promos = useAsync(listPromotions, []);
  const [editing, setEditing] = useState<Promotion | "new" | null>(null);
  const [busyId, setBusyId] = useState<string | null>(null);

  async function toggle(p: Promotion) {
    setBusyId(p.id);
    try {
      await setPromotionStatus(p.id, p.status === "active" ? "inactive" : "active");
      toast.success(p.status === "active" ? "Promotion désactivée." : "Promotion activée.");
      promos.reload();
    } catch (e) { toast.error(errorMessage(e)); }
    finally { setBusyId(null); }
  }

  const period = (p: Promotion) => {
    if (!p.startsAt && !p.endsAt) return "Permanente";
    const d = (x: string | null) => (x ? dateShort(x) : "…");
    return `${d(p.startsAt)} → ${d(p.endsAt)}`;
  };

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <SectionLabel action={canManage ? <Button size="sm" onClick={() => setEditing("new")}>Nouvelle promotion</Button> : undefined}>
          Promotions
        </SectionLabel>
        <p style={{ margin: "0 0 10px", fontSize: 13, color: "var(--nx-text-2)" }}>
          Appliquées automatiquement en caisse : pour chaque ligne, la promotion active de plus haute priorité est retenue.
          Le simulateur de prix montre le même calcul.
        </p>
        {promos.error && <div role="alert" className="nx-error-text">{promos.error}</div>}
        <DataTable
          exportTitle="Promotions"
          columns={[
            { key: "n", header: "Promotion", render: (p) => p.name },
            { key: "r", header: "Règle", render: (p) => describePromotion(p, refs.defaultCurrency) },
            {
              key: "t", header: "Cible", render: (p) => (p.targets ?? []).map((t, i) => (
                <Badge key={i} style={{ marginRight: 4 }}>{TARGET_LABEL[t.targetType]}{t.label ? ` : ${t.label}` : ""}</Badge>
              )),
            },
            { key: "pe", header: "Période", render: period },
            { key: "pr", header: "Priorité", numeric: true, render: (p) => p.priority },
            { key: "s", header: "Statut", render: (p) => <StatePill status={p.status} /> },
            {
              key: "a", header: "", render: (p) => canManage ? (
                <div style={{ display: "flex", gap: 4 }}>
                  <Button size="sm" variant="ghost" onClick={() => setEditing(p)}>Modifier</Button>
                  <Button size="sm" variant="ghost" loading={busyId === p.id} onClick={() => void toggle(p)}>
                    {p.status === "active" ? "Désactiver" : "Activer"}
                  </Button>
                </div>
              ) : null,
            },
          ]}
          rows={promos.data ?? []}
          rowKey={(p) => p.id}
          empty={promos.loading ? "Chargement…" : "Aucune promotion."}
        />
      </Card>
      {canManage && <CouponsCard />}
      {editing && (
        <PromotionDialog promotion={editing === "new" ? null : editing} refs={refs}
                         onClose={() => setEditing(null)} onSaved={() => { setEditing(null); promos.reload(); }} />
      )}
    </div>
  );
}

function PromotionDialog({ promotion, refs, onClose, onSaved }: {
  promotion: Promotion | null; refs: CatalogRefs; onClose: () => void; onSaved: () => void;
}) {
  const toast = useToast();
  const c = promotion?.conditions;
  // Valeur d'un champ datetime-local : heure locale de l'utilisateur, pas UTC.
  const toLocal = (iso: string | null) => {
    if (!iso) return "";
    const d = new Date(iso);
    return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  };
  const [f, setF] = useState({
    name: promotion?.name ?? "", promoType: promotion?.promoType ?? "percentage", value: String(promotion?.value ?? ""),
    priority: String(promotion?.priority ?? 0), startsAt: toLocal(promotion?.startsAt ?? null), endsAt: toLocal(promotion?.endsAt ?? null),
    minQuantity: String(c?.minQuantity ?? ""), buyQuantity: String(c?.buyQuantity ?? "1"), getQuantity: String(c?.getQuantity ?? "1"),
    bundleQuantity: String(c?.bundleQuantity ?? "3"),
  });
  const [targets, setTargets] = useState<PromotionTarget[]>(promotion?.targets ?? []);
  const [targetType, setTargetType] = useState<PromotionTarget["targetType"]>("product");
  const [targetId, setTargetId] = useState("");
  const [productQuery, setProductQuery] = useState("");
  const products = useAsync(() => (productQuery.trim().length >= 2 ? listProducts({ q: productQuery.trim(), limit: 20 }) : Promise.resolve([])), [productQuery]);
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "catalog.promotion_name": "name", "catalog.promotion_value": "value", "catalog.promotion_type": "promoType",
    "catalog.promotion_period": "endsAt", "catalog.promotion_conditions": "conditions", "catalog.promotion_targets": "targets",
  });
  const cats = useMemo(() => sortedCategories(refs.categories), [refs.categories]);

  function addTarget() {
    if (targetType === "all") { setTargets([{ targetType: "all", productId: null, categoryId: null, brandId: null, label: null }]); return; }
    if (!targetId) return;
    const label = targetType === "product" ? products.data?.find((p) => p.id === targetId)?.name
      : targetType === "category" ? cats.find((x) => x.id === targetId)?.path : refs.brands.find((b) => b.id === targetId)?.name;
    const t: PromotionTarget = {
      targetType, label: label ?? null,
      productId: targetType === "product" ? targetId : null,
      categoryId: targetType === "category" ? targetId : null,
      brandId: targetType === "brand" ? targetId : null,
    };
    setTargets((xs) => [...xs.filter((x) => x.targetType !== "all" && JSON.stringify(x) !== JSON.stringify(t)), t]);
    setTargetId("");
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!f.name.trim()) return fail("name", "Le nom est obligatoire.");
    const value = num(f.value) ?? 0;
    if (f.promoType === "percentage" && (value <= 0 || value > 100)) return fail("value", "Pourcentage entre 0 et 100.");
    if (["amount", "price_override", "bundle"].includes(f.promoType) && value <= 0) return fail("value", "Montant positif requis.");
    if (targets.length === 0) return fail("targets", "Ajoutez au moins une cible.");
    if (f.startsAt && f.endsAt && f.endsAt <= f.startsAt) return fail("endsAt", "La fin doit suivre le début.");
    const body: SavePromotion = {
      name: f.name.trim(), promoType: f.promoType, value: f.promoType === "buy_x_get_y" ? 0 : value,
      priority: Math.trunc(num(f.priority) ?? 0),
      startsAt: f.startsAt ? new Date(f.startsAt).toISOString() : null,
      endsAt: f.endsAt ? new Date(f.endsAt).toISOString() : null,
      conditions: {
        minQuantity: num(f.minQuantity),
        buyQuantity: f.promoType === "buy_x_get_y" ? num(f.buyQuantity) : null,
        getQuantity: f.promoType === "buy_x_get_y" ? num(f.getQuantity) : null,
        bundleQuantity: f.promoType === "bundle" ? num(f.bundleQuantity) : null,
      },
      targets: targets.map(({ label: _label, ...t }) => t),
    };
    const ok = await submit(async () => {
      if (promotion) await updatePromotion(promotion.id, body);
      else await createPromotion(body);
      return true;
    });
    if (ok) { toast.success(promotion ? "Promotion modifiée." : "Promotion créée et active."); onSaved(); }
  }

  const valueLabel = f.promoType === "percentage" ? "Remise (%)" : f.promoType === "amount" ? "Remise par unité"
    : f.promoType === "price_override" ? "Prix unitaire imposé" : f.promoType === "bundle" ? "Prix du lot" : "";

  return (
    <Modal title={promotion ? `Modifier « ${promotion.name} »` : "Nouvelle promotion"} onClose={onClose}>
      <form onSubmit={save} style={{ display: "grid", gap: 12 }} noValidate>
        <div style={row2}>
          <Field label="Nom" required error={fieldErrors.name}>
            <TextInput value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} autoFocus />
          </Field>
          <Field label="Type" error={fieldErrors.promoType}>
            <Select value={f.promoType} onChange={(e) => setF({ ...f, promoType: e.target.value })}>
              {Object.entries(PROMO_TYPES).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
          {f.promoType !== "buy_x_get_y" && (
            <Field label={valueLabel} required error={fieldErrors.value}>
              <TextInput type="number" min={0} step="0.01" value={f.value} onChange={(e) => setF({ ...f, value: e.target.value })} />
            </Field>
          )}
        </div>
        <div style={row2}>
          {f.promoType === "buy_x_get_y" && (
            <>
              <Field label="Quantité achetée" required><TextInput type="number" min={1} step={1} value={f.buyQuantity} onChange={(e) => setF({ ...f, buyQuantity: e.target.value })} /></Field>
              <Field label="Quantité offerte" required><TextInput type="number" min={1} step={1} value={f.getQuantity} onChange={(e) => setF({ ...f, getQuantity: e.target.value })} /></Field>
            </>
          )}
          {f.promoType === "bundle" && (
            <Field label="Taille du lot" required><TextInput type="number" min={2} step={1} value={f.bundleQuantity} onChange={(e) => setF({ ...f, bundleQuantity: e.target.value })} /></Field>
          )}
          <Field label="Quantité minimale" hint="Vide = dès 1 unité"><TextInput type="number" min={0} step="any" value={f.minQuantity} onChange={(e) => setF({ ...f, minQuantity: e.target.value })} /></Field>
          <Field label="Priorité" hint="La plus haute gagne"><TextInput type="number" step={1} value={f.priority} onChange={(e) => setF({ ...f, priority: e.target.value })} /></Field>
        </div>
        {fieldErrors.conditions && <div role="alert" className="nx-error-text">{fieldErrors.conditions}</div>}
        <div style={row2}>
          <Field label="Début" hint="Vide = immédiat"><TextInput type="datetime-local" value={f.startsAt} onChange={(e) => setF({ ...f, startsAt: e.target.value })} /></Field>
          <Field label="Fin" error={fieldErrors.endsAt} hint="Vide = sans fin"><TextInput type="datetime-local" value={f.endsAt} onChange={(e) => setF({ ...f, endsAt: e.target.value })} /></Field>
        </div>

        <SectionLabel>Cibles</SectionLabel>
        <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
          {targets.length === 0 && <span style={{ color: "var(--nx-text-2)" }}>Aucune cible.</span>}
          {targets.map((t, i) => (
            <Badge key={i} tone="info">
              {TARGET_LABEL[t.targetType]}{t.label ? ` : ${t.label}` : ""}
              <button type="button" aria-label="Retirer la cible" onClick={() => setTargets(targets.filter((_, j) => j !== i))}
                      style={{ marginLeft: 6, border: "none", background: "transparent", color: "inherit", cursor: "pointer" }}>×</button>
            </Badge>
          ))}
        </div>
        <div style={{ ...row2, alignItems: "end" }}>
          <Field label="Type de cible">
            <Select value={targetType} onChange={(e) => { setTargetType(e.target.value as PromotionTarget["targetType"]); setTargetId(""); }}>
              {Object.entries(TARGET_LABEL).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
            </Select>
          </Field>
          {targetType === "product" && (
            <Field label="Rechercher un produit">
              <TextInput value={productQuery} placeholder="nom ou SKU (2 caractères min.)" onChange={(e) => setProductQuery(e.target.value)} />
            </Field>
          )}
          {targetType !== "all" && (
            <Field label="Élément">
              <Select value={targetId} onChange={(e) => setTargetId(e.target.value)}>
                <option value="">—</option>
                {targetType === "product" && (products.data ?? []).map((p) => <option key={p.id} value={p.id}>{p.name} ({p.sku})</option>)}
                {targetType === "category" && cats.map((x) => <option key={x.id} value={x.id}>{x.path}</option>)}
                {targetType === "brand" && refs.brands.map((b) => <option key={b.id} value={b.id}>{b.name}</option>)}
              </Select>
            </Field>
          )}
          <Button type="button" size="sm" variant="subtle" onClick={addTarget} disabled={targetType !== "all" && !targetId}>Ajouter la cible</Button>
        </div>
        {fieldErrors.targets && <div role="alert" className="nx-error-text">{fieldErrors.targets}</div>}

        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end" }}>
          <Button type="button" variant="ghost" onClick={onClose} disabled={busy}>Annuler</Button>
          <Button type="submit" loading={busy}>{promotion ? "Enregistrer" : "Créer la promotion"}</Button>
        </div>
      </form>
    </Modal>
  );
}

function CouponsCard() {
  const toast = useToast();
  const coupons = useAsync(listCoupons, []);
  const [f, setF] = useState({ code: "", discountType: "percentage" as "percentage" | "amount", discountValue: "", usageLimit: "", validFrom: "", validTo: "" });
  const { busy, error, fieldErrors, submit, fail } = useSubmit({
    "catalog.coupon_code": "code", "catalog.coupon_exists": "code", "catalog.coupon_value": "discountValue",
    "catalog.coupon_limit": "usageLimit", "catalog.coupon_period": "validTo",
  });
  const [busyId, setBusyId] = useState<string | null>(null);

  async function add(e: FormEvent) {
    e.preventDefault();
    if (f.code.trim().length < 3) return fail("code", "3 caractères au moins.");
    const value = num(f.discountValue);
    if (value === null || value <= 0) return fail("discountValue", "Remise positive requise.");
    const limit = num(f.usageLimit);
    if (limit !== null && (limit <= 0 || !Number.isInteger(limit))) return fail("usageLimit", "Nombre entier positif ou vide.");
    const ok = await submit(async () => {
      await createCoupon({
        code: f.code.trim(), discountType: f.discountType, discountValue: value, usageLimit: limit,
        validFrom: f.validFrom ? new Date(f.validFrom).toISOString() : null, validTo: f.validTo ? new Date(f.validTo).toISOString() : null,
      });
      return true;
    });
    if (ok) { toast.success("Coupon créé."); setF({ ...f, code: "", discountValue: "", usageLimit: "" }); coupons.reload(); }
  }

  async function toggle(id: string, status: string) {
    setBusyId(id);
    try { await setCouponStatus(id, status === "active" ? "inactive" : "active"); coupons.reload(); }
    catch (e) { toast.error(errorMessage(e)); }
    finally { setBusyId(null); }
  }

  return (
    <Card>
      <SectionLabel>Coupons</SectionLabel>
      <p style={{ margin: "0 0 10px", fontSize: 13, color: "var(--nx-text-2)" }}>
        Saisis en caisse ; vérifiés par le serveur (période, limite d'utilisation) et consommés à l'encaissement.
      </p>
      {coupons.error && <div role="alert" className="nx-error-text">{coupons.error}</div>}
      <DataTable
        exportTitle="Coupons"
        columns={[
          { key: "c", header: "Code", render: (x) => <code>{x.code}</code> },
          { key: "v", header: "Remise", render: (x) => (x.discountType === "percentage" ? `${x.discountValue} %` : money(x.discountValue)) },
          { key: "u", header: "Utilisations", numeric: true, render: (x) => `${x.usedCount}${x.usageLimit ? ` / ${x.usageLimit}` : ""}` },
          { key: "p", header: "Validité", render: (x) => (x.validFrom || x.validTo
            ? `${x.validFrom ? dateShort(x.validFrom) : "…"} → ${x.validTo ? dateShort(x.validTo) : "…"}` : "Permanente") },
          { key: "s", header: "Statut", render: (x) => <StatePill status={x.usageLimit && x.usedCount >= x.usageLimit ? "exhausted" : x.status} /> },
          {
            key: "a", header: "", render: (x) => (
              <Button size="sm" variant="ghost" loading={busyId === x.id} onClick={() => void toggle(x.id, x.status)}>
                {x.status === "active" ? "Désactiver" : "Activer"}
              </Button>
            ),
          },
        ]}
        rows={coupons.data ?? []}
        rowKey={(x) => x.id}
        empty={coupons.loading ? "Chargement…" : "Aucun coupon."}
      />
      <form onSubmit={add} style={{ ...row2, marginTop: 12, alignItems: "end" }} noValidate>
        <Field label="Code" required error={fieldErrors.code}><TextInput value={f.code} placeholder="BIENVENUE10" onChange={(e) => setF({ ...f, code: e.target.value })} /></Field>
        <Field label="Type">
          <Select value={f.discountType} onChange={(e) => setF({ ...f, discountType: e.target.value as "percentage" | "amount" })}>
            <option value="percentage">Pourcentage</option>
            <option value="amount">Montant</option>
          </Select>
        </Field>
        <Field label="Remise" required error={fieldErrors.discountValue}><TextInput type="number" min={0} step="0.01" value={f.discountValue} onChange={(e) => setF({ ...f, discountValue: e.target.value })} /></Field>
        <Field label="Limite d'utilisations" error={fieldErrors.usageLimit} hint="Vide = illimité"><TextInput type="number" min={1} step={1} value={f.usageLimit} onChange={(e) => setF({ ...f, usageLimit: e.target.value })} /></Field>
        <Field label="Valable du"><TextInput type="datetime-local" value={f.validFrom} onChange={(e) => setF({ ...f, validFrom: e.target.value })} /></Field>
        <Field label="au" error={fieldErrors.validTo}><TextInput type="datetime-local" value={f.validTo} onChange={(e) => setF({ ...f, validTo: e.target.value })} /></Field>
        <Button type="submit" size="sm" loading={busy}>Créer le coupon</Button>
      </form>
      {error && <div role="alert" className="nx-error-text">{error}</div>}
    </Card>
  );
}
