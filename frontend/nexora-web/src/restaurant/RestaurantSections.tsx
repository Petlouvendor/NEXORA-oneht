import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { useToast } from "@/shared/Toast";
import { useAsync } from "@/shared/useAsync";
import { num, useSubmit } from "@/shared/forms";
import { money, number } from "@/shared/format";
import { Badge, Button, Card, ConfirmDialog, DataTable, Drawer, Field, Modal, SectionLabel, Select, TextInput } from "@/shared/ui";
import { getCurrentSession, previewCart, searchProducts, type CartPreview, type CurrentSession, type PosProduct } from "@/pos/posApi";
import { TenderPanel } from "@/pos/TenderPanel";
import { getTenderOptions, newTenderLine, summarizeTenders, toTenderInputs, type TenderLine, type TenderMethodOption } from "@/pos/tenders";
import {
  KITCHEN_STATUS, NEXT_KITCHEN, addItems, advanceKitchen, cancelOrder, closeOrder, createArea, createTable, defineRecipe, getOrder,
  getRecipeOptions, getSetup, isNewWave, kitchenBoard, listAreas, listRecipes, listTables, minutesSince, parseModifiers, placeOrder,
  type OrderItem, type Recipe, type RecipeComponentInput, type RecipeOptions, type RestaurantOrder, type RestaurantTable,
} from "@/restaurant/restaurantApi";

const hint = { fontSize: 12, color: "var(--nx-text-2)" } as const;
const newKey = () => (typeof crypto !== "undefined" && "randomUUID" in crypto ? crypto.randomUUID() : `k${Date.now()}${Math.random()}`);

// ------------------------------------------------------------------ Saisie d'articles

interface ItemDraft { key: string; product: PosProduct; quantity: string; modifiers: string }

/** Recherche d'articles vendables (même recherche que la caisse) + quantités + précisions cuisine. */
function ItemsEditor({ items, onChange, locationId }: { items: ItemDraft[]; onChange: (items: ItemDraft[]) => void; locationId: string | null }) {
  const [q, setQ] = useState("");
  const [hits, setHits] = useState<PosProduct[]>([]);
  useEffect(() => {
    if (q.trim().length < 2) { setHits([]); return; }
    const ctrl = new AbortController();
    const t = setTimeout(() => {
      searchProducts(q.trim(), locationId, ctrl.signal).then((r) => setHits(r.items.slice(0, 8))).catch(() => { /* recherche annulée */ });
    }, 250);
    return () => { clearTimeout(t); ctrl.abort(); };
  }, [q, locationId]);
  const set = (key: string, patch: Partial<ItemDraft>) => onChange(items.map((i) => (i.key === key ? { ...i, ...patch } : i)));
  return (
    <div style={{ display: "grid", gap: 8 }}>
      <Field label="Ajouter un article"><TextInput value={q} placeholder="Nom, code ou code-barres" onChange={(e) => setQ(e.target.value)} /></Field>
      {hits.length > 0 && (
        <div role="listbox" aria-label="Articles trouvés" style={{ display: "grid", gap: 4 }}>
          {hits.map((p) => (
            <button key={p.productId} type="button" className="nx-btn" style={{ justifyContent: "space-between", display: "flex", textAlign: "left" }}
              onClick={() => { onChange([...items, { key: newKey(), product: p, quantity: "1", modifiers: "" }]); setQ(""); setHits([]); }}>
              <span>{p.name}</span><span style={hint}>{p.salePrice != null ? money(p.salePrice, p.currency ?? undefined) : "sans prix"}</span>
            </button>
          ))}
        </div>
      )}
      {items.map((i) => (
        <div key={i.key} style={{ border: "1px solid var(--nx-border)", borderRadius: 8, padding: 8, display: "grid", gap: 6 }}>
          <div style={{ display: "flex", justifyContent: "space-between", gap: 8 }}>
            <b>{i.product.name}</b>
            <Button type="button" size="sm" variant="ghost" onClick={() => onChange(items.filter((x) => x.key !== i.key))}>Retirer</Button>
          </div>
          <div style={{ display: "grid", gridTemplateColumns: "90px 1fr", gap: 6 }}>
            <TextInput aria-label={`Quantité ${i.product.name}`} type="number" min={1} step="1" inputMode="numeric" value={i.quantity} onChange={(e) => set(i.key, { quantity: e.target.value })} />
            <TextInput aria-label={`Précisions ${i.product.name}`} value={i.modifiers} maxLength={300} placeholder="Précisions cuisine, séparées par des virgules (sans oignon, bien cuit)"
              onChange={(e) => set(i.key, { modifiers: e.target.value })} />
          </div>
        </div>
      ))}
    </div>
  );
}

function toItems(drafts: ItemDraft[]): OrderItem[] | string {
  if (drafts.length === 0) return "Ajoutez au moins un article.";
  const out: OrderItem[] = [];
  for (const d of drafts) {
    const qn = num(d.quantity);
    if (qn == null || qn <= 0 || qn > 999) return `Quantité invalide pour ${d.product.name}.`;
    const mods = parseModifiers(d.modifiers);
    out.push({ productId: d.product.productId, quantity: qn, lineDiscount: 0, modifiers: mods.length ? mods : null });
  }
  return out;
}

// ------------------------------------------------------------------ Salle

export function FloorSection() {
  const { hasPermission } = useAuth();
  const [version, setVersion] = useState(0);
  const tables = useAsync(listTables, [version]);
  const [opening, setOpening] = useState<RestaurantTable | null>(null);
  const [openSale, setOpenSale] = useState<string | null>(null);
  const reload = () => setVersion((v) => v + 1);
  useEffect(() => { const t = setInterval(reload, 20_000); return () => clearInterval(t); }, []);

  const groups = useMemo(() => {
    const m = new Map<string, RestaurantTable[]>();
    for (const t of tables.data ?? []) {
      const k = t.areaName ?? "Sans salle";
      (m.get(k) ?? m.set(k, []).get(k)!).push(t);
    }
    return [...m.entries()];
  }, [tables.data]);
  const busy = (tables.data ?? []).filter((t) => t.currentSaleId).length;

  return (
    <div style={{ display: "grid", gap: 12 }}>
      {tables.error && <div role="alert" className="nx-error-text">{tables.error}</div>}
      <div style={hint}>{busy} table(s) occupée(s) sur {(tables.data ?? []).length}. Touchez une table libre pour ouvrir une commande.</div>
      {(tables.data ?? []).length === 0 && !tables.loading && (
        <Card>Aucune table. {hasPermission("restaurant.manage") ? "Créez les salles et les tables dans l'onglet Configuration." : "Un responsable doit créer les tables."}</Card>
      )}
      {groups.map(([area, list]) => (
        <Card key={area}>
          <SectionLabel>{area}</SectionLabel>
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(150px, 1fr))", gap: 10 }}>
            {list.map((t) => {
              const occupied = !!t.currentSaleId;
              const k = t.kitchenStatus ? KITCHEN_STATUS[t.kitchenStatus] : null;
              return (
                <button key={t.id} type="button" aria-label={`Table ${t.name}`} disabled={t.status === "inactive"}
                  onClick={() => (occupied ? setOpenSale(t.currentSaleId) : setOpening(t))}
                  style={{
                    textAlign: "left", minHeight: 96, padding: 10, borderRadius: 10, cursor: "pointer", font: "inherit", color: "inherit",
                    border: `2px solid ${occupied ? "var(--nx-brand)" : "var(--nx-border)"}`,
                    background: occupied ? "color-mix(in srgb, var(--nx-brand) 10%, transparent)" : "var(--nx-surface)",
                    display: "grid", gap: 4, alignContent: "start",
                  }}>
                  <b>{t.name}</b>
                  <span style={hint}>{occupied ? `${t.covers ?? "?"} / ${t.seats} couverts` : `${t.seats} places · libre`}</span>
                  {occupied && <span>{money(t.runningTotal ?? 0)}</span>}
                  {k && <span><Badge tone={k.tone}>{k.label}</Badge> <span style={hint}>{minutesSince(t.openedAt)} min</span></span>}
                </button>
              );
            })}
          </div>
        </Card>
      ))}
      {opening && <OpenOrderDialog table={opening} onClose={() => setOpening(null)} onDone={(o) => { setOpening(null); reload(); setOpenSale(o.saleId); }} />}
      {openSale && <OrderDrawer key={openSale} saleId={openSale} onClose={() => setOpenSale(null)} onChanged={reload} />}
    </div>
  );
}

function OpenOrderDialog({ table, onClose, onDone }: { table: RestaurantTable; onClose: () => void; onDone: (o: RestaurantOrder) => void }) {
  const toast = useToast();
  const [covers, setCovers] = useState(String(Math.min(2, table.seats)));
  const [notes, setNotes] = useState("");
  const [items, setItems] = useState<ItemDraft[]>([]);
  const { busy, error, fieldErrors, submit, fail, setError } = useSubmit({ "restaurant.covers": "covers" });

  async function save(e?: FormEvent) {
    e?.preventDefault();
    const c = num(covers);
    if (c == null || !Number.isInteger(c) || c < 1 || c > 99) return fail("covers", "Nombre de couverts entre 1 et 99.");
    const it = toItems(items);
    if (typeof it === "string") { setError(it); return; }
    const o = await submit(() => placeOrder({ tableId: table.id, covers: c, waiterUserId: null, notes: notes.trim() || null, items: it }));
    if (o) { toast.success(`Commande envoyée en cuisine — ${table.name}.`); onDone(o); }
  }

  return (
    <Modal title={`Nouvelle commande — ${table.name}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Envoyer en cuisine</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "120px 1fr", gap: 8 }}>
          <Field label="Couverts" required error={fieldErrors.covers}><TextInput type="number" min={1} max={99} inputMode="numeric" value={covers} onChange={(e) => setCovers(e.target.value)} /></Field>
          <Field label="Note pour la cuisine"><TextInput value={notes} maxLength={300} placeholder="Ex. anniversaire, allergie aux noix" onChange={(e) => setNotes(e.target.value)} /></Field>
        </div>
        <ItemsEditor items={items} onChange={setItems} locationId={null} />
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Commande d'une table : articles et précisions, état cuisine, ajout d'articles, service, paiement, annulation. */
export function OrderDrawer({ saleId, onClose, onChanged }: { saleId: string; onClose: () => void; onChanged: () => void }) {
  const { hasPermission } = useAuth();
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const order = useAsync(() => getOrder(saleId), [saleId, version]);
  const [dialog, setDialog] = useState<"add" | "pay" | "cancel" | null>(null);
  const { busy, error, submit } = useSubmit();
  const o = order.data;
  const refresh = () => { setVersion((v) => v + 1); onChanged(); };
  const open = o?.status === "parked";
  const next = o ? NEXT_KITCHEN[o.kitchenStatus] : undefined;

  return (
    <Drawer title={o ? `${o.tableName} — ${o.documentNumber}` : "Commande"} onClose={onClose}>
      {(order.error || error) && <div role="alert" className="nx-error-text">{order.error ?? error}</div>}
      {o && (
        <div style={{ display: "grid", gap: 12 }}>
          <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "center" }}>
            <Badge tone={KITCHEN_STATUS[o.kitchenStatus].tone}>{KITCHEN_STATUS[o.kitchenStatus].label}</Badge>
            <span style={hint}>{o.covers} couvert(s){o.waiterName ? ` · ${o.waiterName}` : ""} · ouverte depuis {minutesSince(o.openedAt)} min</span>
          </div>
          {o.notes && <div style={hint}>Note : {o.notes}</div>}
          <DataTable exportTitle={`Commande ${o.documentNumber}`}
            columns={[
              { key: "q", header: "Qté", numeric: true, render: (l) => number(l.quantity, 3) },
              { key: "d", header: "Article", render: (l) => <>{l.description}{l.modifiers && l.modifiers.length > 0 && <div style={hint}>{l.modifiers.join(" · ")}</div>}</> },
              { key: "p", header: "Prix", numeric: true, render: (l) => money(l.unitPrice, o.currency ?? undefined) },
              { key: "t", header: "Total", numeric: true, render: (l) => money(l.lineTotal - l.discount, o.currency ?? undefined) },
            ]}
            rows={o.lines} rowKey={(l) => l.lineId ?? `${l.productId}-${l.addedAt}`} />
          <div style={{ fontSize: 18 }}>Total provisoire : <b>{money(o.runningTotal, o.currency ?? undefined)}</b></div>
          <div style={hint}>Taxes et promotions sont calculées au moment du paiement.</div>
          {open && (
            <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
              <Button disabled={busy} onClick={() => setDialog("add")}>Ajouter des articles</Button>
              {next && hasPermission("restaurant.kitchen") && (
                <Button variant="subtle" disabled={busy} onClick={async () => {
                  if (await submit(() => advanceKitchen(o.saleId, next.status).then(() => true))) refresh();
                }}>{next.status === "served" ? "Marquer servi" : next.label}</Button>
              )}
              {hasPermission("pos.sell") && <Button variant="subtle" disabled={busy} onClick={() => setDialog("pay")}>Encaisser</Button>}
              <Button variant="ghost" disabled={busy} onClick={() => setDialog("cancel")}>Annuler la commande</Button>
            </div>
          )}
          {!open && <div style={hint}>Commande {o.status === "void" ? "close" : o.status}.</div>}
        </div>
      )}
      {o && dialog === "add" && <AddItemsDialog order={o} onClose={() => setDialog(null)} onDone={() => { setDialog(null); refresh(); }} />}
      {o && dialog === "pay" && <PayDialog order={o} onClose={() => setDialog(null)} onDone={() => { setDialog(null); onChanged(); onClose(); }} />}
      {o && dialog === "cancel" && <ConfirmDialog title={`Annuler la commande — ${o.tableName}`} requireReason danger confirmLabel="Annuler la commande"
        message="La commande n'est pas facturée et la table est libérée. Le motif est conservé."
        onCancel={() => setDialog(null)}
        onConfirm={async (reason) => {
          setDialog(null);
          if (await submit(() => cancelOrder(o.saleId, reason).then(() => true))) { toast.success("Commande annulée."); onChanged(); onClose(); }
        }} />}
    </Drawer>
  );
}

function AddItemsDialog({ order, onClose, onDone }: { order: RestaurantOrder; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [items, setItems] = useState<ItemDraft[]>([]);
  const { busy, error, submit, setError } = useSubmit();
  async function save(e?: FormEvent) {
    e?.preventDefault();
    const it = toItems(items);
    if (typeof it === "string") { setError(it); return; }
    if (await submit(() => addItems(order.saleId, it))) { toast.success("Articles ajoutés et envoyés en cuisine."); onDone(); }
  }
  return (
    <Modal title={`Ajouter — ${order.tableName}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Envoyer en cuisine</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(520px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <ItemsEditor items={items} onChange={setItems} locationId={order.inventoryLocationId} />
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

/** Paiement : montant exact calculé par le serveur, moyens de la caisse, session de caisse ouverte exigée. */
function PayDialog({ order, onClose, onDone }: { order: RestaurantOrder; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const currency = order.currency ?? "HTG";
  const [operationKey] = useState(newKey);
  const [preview, setPreview] = useState<CartPreview | null>(null);
  const [methods, setMethods] = useState<TenderMethodOption[]>([]);
  const [session, setSession] = useState<CurrentSession | null | undefined>(undefined);
  const [lines, setLines] = useState<TenderLine[]>([]);
  const [tip, setTip] = useState("");
  const { busy, error, submit, setError } = useSubmit();
  const loaded = useRef(false);

  useEffect(() => {
    if (loaded.current) return;
    loaded.current = true;
    Promise.all([
      order.inventoryLocationId
        ? previewCart({ inventoryLocationId: order.inventoryLocationId, customerId: null, currency,
            lines: order.lines.map((l) => ({ productId: l.productId, quantity: l.quantity, lineDiscount: l.discount })) })
        : Promise.reject(new Error("Emplacement de stock inconnu.")),
      getTenderOptions(currency),
      getCurrentSession().then((s) => s ?? null),
    ]).then(([p, m, s]) => {
      setPreview(p); setMethods(m); setSession(s);
      const cash = m.find((x) => x.type === "cash") ?? null;
      setLines([{ ...newTenderLine(cash), amount: String(p.total) }]);
    }).catch((e) => setError(e instanceof Error ? e.message : "Chargement impossible."));
  }, [order, currency, setError]);

  const due = preview?.total ?? 0;
  const summary = summarizeTenders(lines, methods, due);
  const noSession = session === null || (session && session.status !== "open");

  async function pay() {
    if (!preview || !session || noSession) return;
    if (summary.remaining > 0) { setError(`Il manque ${money(summary.remaining, currency)}.`); return; }
    if (summary.nonCashOver) { setError("Un paiement hors espèces ne peut pas dépasser le montant dû."); return; }
    if (summary.missingAccount.length || summary.missingReference.length) { setError("Complétez les paiements (compte, référence)."); return; }
    const t = num(tip);
    if (tip.trim() && (t == null || t < 0)) { setError("Pourboire invalide."); return; }
    const r = await submit(() => closeOrder({
      saleId: order.saleId, tenders: toTenderInputs(lines, methods, session.financialAccountId ?? ""),
      amountTendered: summary.paid, tipAmount: t ?? 0, cashSessionId: session.sessionId,
    }, operationKey));
    if (r) { toast.success(`Réglé — ticket ${r.documentNumber}${r.changeGiven > 0 ? `, rendu ${money(r.changeGiven, currency)}` : ""}. Table libérée.`); onDone(); }
  }

  return (
    <Modal title={`Encaisser — ${order.tableName}`} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy || !preview || noSession} onClick={pay}>Encaisser {preview ? money(due, currency) : ""}</Button></>}>
      <div style={{ display: "grid", gap: 10, minWidth: "min(520px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        {noSession && <div className="nx-error-text">Aucune session de caisse ouverte : ouvrez-en une sur l'écran <Link to="/pos">POS</Link> pour encaisser.</div>}
        {preview && (
          <div style={{ display: "grid", gap: 2 }}>
            <div>Sous-total {money(preview.subtotal, currency)}{preview.discountTotal ? ` · remises −${money(preview.discountTotal, currency)}` : ""} · taxes {money(preview.taxTotal, currency)}</div>
            <div style={{ fontSize: 22 }}>À payer : <b>{money(preview.total, currency)}</b></div>
          </div>
        )}
        {preview && methods.length > 0 && <TenderPanel lines={lines} onChange={setLines} methods={methods} due={due} currency={currency} summary={summary} disabled={busy} />}
        <Field label="Pourboire" hint="Pour mémoire : il n'est pas encaissé ni comptabilisé par NEXORA.">
          <TextInput type="number" min={0} step="0.01" inputMode="decimal" value={tip} onChange={(e) => setTip(e.target.value)} />
        </Field>
      </div>
    </Modal>
  );
}

// ------------------------------------------------------------------ Cuisine

export function KitchenSection() {
  const [version, setVersion] = useState(0);
  const board = useAsync(kitchenBoard, [version]);
  const [now, setNow] = useState(Date.now());
  const { busy, error, submit } = useSubmit();
  useEffect(() => { const t = setInterval(() => { setVersion((v) => v + 1); setNow(Date.now()); }, 10_000); return () => clearInterval(t); }, []);

  return (
    <div style={{ display: "grid", gap: 12 }}>
      {(board.error || error) && <div role="alert" className="nx-error-text">{board.error ?? error}</div>}
      <div style={hint}>Actualisé toutes les 10 secondes. Les articles ajoutés après le lancement sont signalés « nouveau ».</div>
      {(board.data ?? []).length === 0 && !board.loading && <Card>Aucun ticket en cours.</Card>}
      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))", gap: 12 }}>
        {(board.data ?? []).map((o) => {
          const age = minutesSince(o.openedAt, now);
          const next = NEXT_KITCHEN[o.kitchenStatus];
          return (
            <Card key={o.saleId} style={{ display: "grid", gap: 8, borderTop: `4px solid ${age >= 20 ? "var(--nx-neg)" : age >= 10 ? "var(--nx-warning, #b45309)" : "var(--nx-brand)"}` }}>
              <div style={{ display: "flex", justifyContent: "space-between", gap: 8 }}>
                <b>{o.tableName}</b>
                <span><Badge tone={KITCHEN_STATUS[o.kitchenStatus].tone}>{KITCHEN_STATUS[o.kitchenStatus].label}</Badge> <span style={hint}>{age} min</span></span>
              </div>
              {o.notes && <div style={{ fontStyle: "italic" }}>{o.notes}</div>}
              <ul style={{ margin: 0, paddingLeft: 18, display: "grid", gap: 4 }}>
                {o.lines.map((l) => (
                  <li key={l.lineId ?? l.productId}>
                    <b>{number(l.quantity, 3)} × {l.description}</b>{isNewWave(l, o) && <> <Badge tone="warning">nouveau</Badge></>}
                    {l.modifiers && l.modifiers.length > 0 && <div style={hint}>{l.modifiers.join(" · ")}</div>}
                  </li>
                ))}
              </ul>
              {next && <Button disabled={busy} style={{ minHeight: 44 }}
                onClick={async () => { if (await submit(() => advanceKitchen(o.saleId, next.status).then(() => true))) setVersion((v) => v + 1); }}>{next.label}</Button>}
            </Card>
          );
        })}
      </div>
    </div>
  );
}

// ------------------------------------------------------------------ Recettes

export function RecipesSection() {
  const [version, setVersion] = useState(0);
  const recipes = useAsync(listRecipes, [version]);
  const options = useAsync(getRecipeOptions, [version]);
  const [editing, setEditing] = useState<Recipe | "new" | null>(null);
  return (
    <Card>
      <SectionLabel action={<Button size="sm" disabled={!options.data} onClick={() => setEditing("new")}>Nouvelle recette</Button>}>Recettes</SectionLabel>
      <div style={{ ...hint, marginBottom: 8 }}>
        La vente d'un plat (produit de type composé, non stocké) sort ses ingrédients du stock selon sa recette. Les quantités sont
        dans l'unité de stock de chaque ingrédient.
      </div>
      {recipes.error && <div role="alert" className="nx-error-text">{recipes.error}</div>}
      <DataTable exportTitle="Recettes"
        columns={[
          { key: "p", header: "Plat", render: (r) => <button type="button" className="nx-linkbtn" onClick={() => setEditing(r)}>{r.productName ?? r.name}</button> },
          { key: "n", header: "Ingrédients", render: (r) => r.components.map((c) => `${number(c.quantity, 3)} ${c.unitCode ?? ""} ${c.componentName}`).join(", ") },
          { key: "c", header: "Coût estimé", numeric: true, render: (r) => money(r.estimatedCost) },
        ]}
        rows={recipes.data ?? []} rowKey={(r) => r.id} empty={recipes.loading ? "Chargement…" : "Aucune recette."} />
      {editing && options.data && <RecipeDialog recipe={editing === "new" ? null : editing} options={options.data}
        onClose={() => setEditing(null)} onDone={() => { setEditing(null); setVersion((v) => v + 1); }} />}
    </Card>
  );
}

type CompDraft = { key: string; productId: string; quantity: string; wastage: string; station: string };

function RecipeDialog({ recipe, options, onClose, onDone }: { recipe: Recipe | null; options: RecipeOptions; onClose: () => void; onDone: () => void }) {
  const toast = useToast();
  const [dish, setDish] = useState(recipe?.productId ?? "");
  const [name, setName] = useState(recipe?.name ?? "");
  const [comps, setComps] = useState<CompDraft[]>(recipe
    ? recipe.components.map((c) => ({ key: newKey(), productId: c.componentProductId, quantity: String(c.quantity), wastage: String(c.wastagePercent), station: c.kitchenStation ?? "" }))
    : [{ key: newKey(), productId: "", quantity: "", wastage: "0", station: "" }]);
  const { busy, error, fieldErrors, submit, fail, setError } = useSubmit({ "recipes.stocked_dish": "dish", "recipes.product": "dish", "recipes.name": "name" });
  const unitOf = (productId: string) => options.ingredients.find((i) => i.id === productId)?.unitId ?? null;
  const unitCode = (id: string | null) => options.units.find((u) => u.id === id)?.code ?? "";
  const set = (key: string, patch: Partial<CompDraft>) => setComps((l) => l.map((c) => (c.key === key ? { ...c, ...patch } : c)));

  async function save(e?: FormEvent) {
    e?.preventDefault();
    if (!dish) return fail("dish", "Choisissez le plat.");
    if (!name.trim()) return fail("name", "Nom obligatoire.");
    const filled = comps.filter((c) => c.productId);
    if (filled.length === 0) { setError("Ajoutez au moins un ingrédient."); return; }
    const out: RecipeComponentInput[] = [];
    for (const c of filled) {
      const qn = num(c.quantity), w = num(c.wastage || "0");
      const ingName = options.ingredients.find((i) => i.id === c.productId)?.name ?? "ingrédient";
      if (qn == null || qn <= 0) { setError(`Quantité invalide pour ${ingName}.`); return; }
      if (w == null || w < 0 || w >= 100) { setError(`Perte entre 0 et 99 % pour ${ingName}.`); return; }
      const unit = unitOf(c.productId);
      if (!unit) { setError(`« ${ingName} » n'a pas d'unité de stock.`); return; }
      out.push({ componentProductId: c.productId, quantity: qn, unitId: unit, wastagePercent: w, kitchenStation: c.station.trim() || null });
    }
    const yieldUnit = recipe?.yieldUnitId ?? options.units[0]?.id;
    if (!yieldUnit) { setError("Aucune unité définie dans le catalogue."); return; }
    const ok = await submit(() => defineRecipe({ productId: dish, name: name.trim(), yieldUnitId: yieldUnit, yieldQuantity: 1, components: out }).then(() => true));
    if (ok) { toast.success("Recette enregistrée : elle s'applique aux prochaines ventes."); onDone(); }
  }

  return (
    <Modal title={recipe ? `Recette — ${recipe.productName ?? recipe.name}` : "Nouvelle recette"} onClose={onClose}
      footer={<><Button variant="ghost" onClick={onClose}>Annuler</Button><Button disabled={busy} onClick={() => save()}>Enregistrer</Button></>}>
      <form noValidate onSubmit={save} style={{ display: "grid", gap: 10, minWidth: "min(640px, 94vw)" }}>
        {error && <div role="alert" className="nx-error-text">{error}</div>}
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 8 }}>
          <Field label="Plat" required error={fieldErrors.dish} hint="Produits non stockés (composé, service).">
            <Select value={dish} disabled={!!recipe} onChange={(e) => { setDish(e.target.value); if (!name) setName(options.dishes.find((d) => d.id === e.target.value)?.name ?? ""); }}>
              <option value="">— Choisir —</option>
              {options.dishes.map((d) => <option key={d.id} value={d.id}>{d.name}{d.hasRecipe && !recipe ? " (a déjà une recette)" : ""}</option>)}
            </Select>
          </Field>
          <Field label="Nom de la recette" required error={fieldErrors.name}><TextInput value={name} maxLength={120} onChange={(e) => setName(e.target.value)} /></Field>
        </div>
        <SectionLabel>Ingrédients pour un plat</SectionLabel>
        {comps.map((c, i) => (
          <div key={c.key} style={{ display: "grid", gridTemplateColumns: "minmax(160px, 2fr) 100px 80px minmax(80px, 1fr) auto", gap: 6, alignItems: "end" }}>
            <Field label={i === 0 ? "Ingrédient" : ""}>
              <Select aria-label={`Ingrédient ${i + 1}`} value={c.productId} onChange={(e) => set(c.key, { productId: e.target.value })}>
                <option value="">— Choisir —</option>
                {options.ingredients.map((o) => <option key={o.id} value={o.id}>{o.name}</option>)}
              </Select>
            </Field>
            <Field label={i === 0 ? "Quantité" : ""}>
              <TextInput aria-label={`Quantité ingrédient ${i + 1}`} inputMode="decimal" value={c.quantity} placeholder={unitCode(unitOf(c.productId))} onChange={(e) => set(c.key, { quantity: e.target.value })} />
            </Field>
            <Field label={i === 0 ? "Perte %" : ""}><TextInput aria-label={`Perte ingrédient ${i + 1}`} inputMode="decimal" value={c.wastage} onChange={(e) => set(c.key, { wastage: e.target.value })} /></Field>
            <Field label={i === 0 ? "Poste" : ""}><TextInput aria-label={`Poste ingrédient ${i + 1}`} value={c.station} maxLength={40} placeholder="chaud, froid…" onChange={(e) => set(c.key, { station: e.target.value })} /></Field>
            <Button type="button" size="sm" variant="ghost" disabled={comps.length === 1} onClick={() => setComps((l) => l.filter((x) => x.key !== c.key))}>✕</Button>
          </div>
        ))}
        <div><Button type="button" size="sm" variant="subtle" onClick={() => setComps((l) => [...l, { key: newKey(), productId: "", quantity: "", wastage: "0", station: "" }])}>+ Ingrédient</Button></div>
        <div style={hint}>Unité : celle du stock de l'ingrédient (affichée dans le champ quantité). Aucune conversion n'est appliquée.</div>
        <button type="submit" hidden />
      </form>
    </Modal>
  );
}

// ------------------------------------------------------------------ Configuration

export function SetupSection() {
  const toast = useToast();
  const [version, setVersion] = useState(0);
  const setup = useAsync(getSetup, []);
  const areas = useAsync(listAreas, [version]);
  const tables = useAsync(listTables, [version]);
  const [establishment, setEstablishment] = useState("");
  const [areaName, setAreaName] = useState("");
  const [areaCode, setAreaCode] = useState("");
  const [tableArea, setTableArea] = useState("");
  const [tableName, setTableName] = useState("");
  const [tableCode, setTableCode] = useState("");
  const [seats, setSeats] = useState("4");
  const area = useSubmit({ "restaurant.code_exists": "areaCode", "restaurant.name": "areaName", "restaurant.code": "areaCode" });
  const table = useSubmit({ "restaurant.code_exists": "tableCode", "restaurant.name": "tableName", "restaurant.code": "tableCode", "restaurant.seats": "seats" });
  const est = establishment || setup.data?.establishments.find((e) => e.hasStockLocation)?.id || "";
  const reload = () => setVersion((v) => v + 1);

  async function addArea(e: FormEvent) {
    e.preventDefault();
    if (!est) return area.fail("areaName", "Choisissez l'établissement.");
    if (!areaName.trim()) return area.fail("areaName", "Nom obligatoire.");
    if (!areaCode.trim()) return area.fail("areaCode", "Code obligatoire.");
    if (await area.submit(() => createArea({ establishmentId: est, name: areaName.trim(), code: areaCode.trim() }))) {
      toast.success(`Salle « ${areaName.trim()} » créée.`); setAreaName(""); setAreaCode(""); reload();
    }
  }
  async function addTable(e: FormEvent) {
    e.preventDefault();
    const s = num(seats);
    if (!est) return table.fail("tableName", "Choisissez l'établissement.");
    if (!tableName.trim()) return table.fail("tableName", "Nom obligatoire.");
    if (!tableCode.trim()) return table.fail("tableCode", "Code obligatoire.");
    if (s == null || !Number.isInteger(s) || s < 1 || s > 99) return table.fail("seats", "Entre 1 et 99.");
    if (await table.submit(() => createTable({ establishmentId: est, areaId: tableArea || null, name: tableName.trim(), code: tableCode.trim(), seats: s }))) {
      toast.success(`Table « ${tableName.trim()} » créée.`); setTableName(""); setTableCode(""); reload();
    }
  }

  return (
    <div style={{ display: "grid", gap: 16 }}>
      <Card>
        <Field label="Établissement">
          <Select value={est} onChange={(e) => setEstablishment(e.target.value)}>
            <option value="">— Choisir —</option>
            {(setup.data?.establishments ?? []).map((x) => <option key={x.id} value={x.id}>{x.name}{x.hasStockLocation ? "" : " (sans emplacement de stock : commandes impossibles)"}</option>)}
          </Select>
        </Field>
      </Card>
      <Card>
        <SectionLabel>Salles</SectionLabel>
        {area.error && <div role="alert" className="nx-error-text">{area.error}</div>}
        <form noValidate onSubmit={addArea} style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
          <Field label="Nom" error={area.fieldErrors.areaName}><TextInput value={areaName} maxLength={80} placeholder="Ex. Terrasse" onChange={(e) => setAreaName(e.target.value)} /></Field>
          <Field label="Code" error={area.fieldErrors.areaCode}><TextInput value={areaCode} maxLength={20} placeholder="TER" onChange={(e) => setAreaCode(e.target.value)} /></Field>
          <Button type="submit" size="sm" disabled={area.busy || !setup.data}>Créer la salle</Button>
        </form>
        <DataTable exportTitle="Salles" columns={[
          { key: "n", header: "Salle", render: (a) => a.name }, { key: "c", header: "Code", render: (a) => a.code },
          { key: "t", header: "Tables", numeric: true, render: (a) => a.tableCount },
        ]} rows={areas.data ?? []} rowKey={(a) => a.id} empty="Aucune salle." />
      </Card>
      <Card>
        <SectionLabel>Tables</SectionLabel>
        {table.error && <div role="alert" className="nx-error-text">{table.error}</div>}
        <form noValidate onSubmit={addTable} style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
          <Field label="Salle">
            <Select value={tableArea} onChange={(e) => setTableArea(e.target.value)}>
              <option value="">Sans salle</option>
              {(areas.data ?? []).filter((a) => !est || a.establishmentId === est).map((a) => <option key={a.id} value={a.id}>{a.name}</option>)}
            </Select>
          </Field>
          <Field label="Nom" error={table.fieldErrors.tableName}><TextInput value={tableName} maxLength={80} placeholder="Table 1" onChange={(e) => setTableName(e.target.value)} /></Field>
          <Field label="Code" error={table.fieldErrors.tableCode}><TextInput value={tableCode} maxLength={20} placeholder="T1" onChange={(e) => setTableCode(e.target.value)} /></Field>
          <Field label="Places" error={table.fieldErrors.seats}><TextInput type="number" min={1} max={99} value={seats} onChange={(e) => setSeats(e.target.value)} /></Field>
          <Button type="submit" size="sm" disabled={table.busy || !setup.data}>Créer la table</Button>
        </form>
        <DataTable exportTitle="Tables" columns={[
          { key: "n", header: "Table", render: (t) => t.name }, { key: "c", header: "Code", render: (t) => t.code },
          { key: "a", header: "Salle", render: (t) => t.areaName ?? "—" }, { key: "s", header: "Places", numeric: true, render: (t) => t.seats },
          { key: "o", header: "État", render: (t) => <Badge tone={t.currentSaleId ? "info" : undefined}>{t.currentSaleId ? "Occupée" : t.status === "available" ? "Libre" : t.status}</Badge> },
        ]} rows={tables.data ?? []} rowKey={(t) => t.id} empty="Aucune table." />
      </Card>
    </div>
  );
}
