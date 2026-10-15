import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useSearchParams } from "react-router-dom";
import {
  checkout, getCurrentSession, getDiscountRequest, getMyLimits, previewCart, requestDiscount, STATUS_LABEL, type CartPreview,
  type CheckoutCommand, type CheckoutResult, type CurrentSession, type DiscountRequest, type PosLimits, type PosProduct,
} from "@/pos/posApi";
import { ProductSearch } from "@/pos/ProductSearch";
import { SessionPanel } from "@/pos/SessionPanel";
import { ReturnsPanel, statusTone } from "@/pos/ReturnsPanel";
import { ApprovalsPanel } from "@/pos/ApprovalsPanel";
import { ManagerOverrideDialog } from "@/pos/ManagerOverrideDialog";
import { CustomerPicker } from "@/pos/CustomerPicker";
import { CreditOverrideRequestDialog, CreditOverridesPanel, useCreditOverride } from "@/pos/CreditOverridePanels";
import type { CustomerLookup } from "@/customers/customersApi";
import { ApiError } from "@/core/apiClient";
import { pushBatch } from "@/sync/syncApi";
import { kindsFor, toSyncOperations } from "@/shared/syncOperations";
import { useOnline } from "@/shared/useOnline";
import { useOutbox } from "@/shared/useOutbox";
import { autoFlushOnReconnect, enqueue, flushOutbox, outboxProtection, pruneSynced, type BatchSender } from "@/shared/offlineQueue";
import { useToast } from "@/shared/Toast";
import { Badge, Button, Card, ConfirmDialog, Field, IconButton, Modal, StatePill, Tabs, TextInput } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { money, relative } from "@/shared/format";
import { printDocument } from "@/shared/printApi";
import { TenderPanel } from "@/pos/TenderPanel";
import { lineAmounts, unitPriceExcludingTax } from "@/pos/cartMath";
import {
  getTenderOptions, newTenderLine, summarizeTenders, toTenderInputs, type TenderLine, type TenderMethodOption,
} from "@/pos/tenders";
import { useAuth } from "@/auth/AuthContext";
import { dateTime } from "@/shared/format";
import {
  ensureDevice, forgetDevice, getOfflineProfile, lastServerContact, loadProfile, markServerContact, offlineRefusal, saveProfile,
  storedDeviceId, type StoredProfile,
} from "@/pos/offlineStore";

/**
 * Écran POS (guide fonctionnel développeur §7-§13, §21, §24). Un outil de vente rapide et contrôlé :
 * session de caisse obligatoire, recherche courte (jamais le catalogue complet), prix fourni par le
 * serveur et non modifiable, remise bornée par la limite du rôle — au-delà, demande Manager —, retour
 * et remboursement uniquement sur validation Manager. Aucune donnée de pilotage n'est affichée.
 * Le serveur reste l'autorité : prix, remises, stock et droits y sont recontrôlés à l'encaissement.
 */
interface CartLine {
  productId: string;
  name: string;
  quantity: number;
  unitPrice: number;      // indicatif : prix de vente HORS TAXE en vigueur, recalculé par le serveur
  discount: number;       // montant de remise saisi (≥ 0), sur le hors taxe comme au serveur
  taxRate: number;        // taux de la taxe par défaut de l'article (0 sans taxe)
}

export function PosPage() {
  const [params] = useSearchParams();
  const focusApproval = params.get("approvals");
  const [tab, setTab] = useState(focusApproval ? "approvals" : "sale");
  const [limits, setLimits] = useState<PosLimits | null>(null);
  const [session, setSession] = useState<CurrentSession | null>(null);
  const [sessionLoaded, setSessionLoaded] = useState(false);
  const { user, activeCompanyId } = useAuth();
  // Phase 6 : profil hors ligne (catalogue, politique, session, moyens) conservé chiffré sur ce poste.
  const [profile, setProfile] = useState<StoredProfile | null>(null);

  const reloadSession = useCallback(() => {
    getCurrentSession()
      .then((s) => { setSession(s ?? null); markServerContact(); })
      .catch(async (e) => {
        // Hors ligne : la dernière session connue de CE caissier permet de continuer à vendre (file d'attente).
        if (e instanceof ApiError && e.isOffline && user && activeCompanyId) {
          const cached = await loadProfile(user.id, activeCompanyId);
          setProfile(cached);
          setSession(cached?.session ?? null);
        } else setSession(null);
      })
      .finally(() => setSessionLoaded(true));
  }, [user, activeCompanyId]);

  useEffect(() => {
    if (!user || !activeCompanyId) return;
    let alive = true;
    loadProfile(user.id, activeCompanyId).then((p) => { if (alive) setProfile((cur) => cur ?? p); }).catch(() => {});
    return () => { alive = false; };
  }, [user, activeCompanyId]);

  useEffect(() => {
    getMyLimits().then(setLimits).catch(() => setLimits(null));
    reloadSession();
  }, [reloadSession]);

  const approver = Boolean(limits && (limits.canRefund || limits.canApproveDiscount || limits.canManageSessions || limits.canOverrideCredit));
  const tabs = [
    { id: "sale", label: "Vente" },
    ...(limits?.canRequestReturn ? [{ id: "returns", label: "Retours" }] : []),
    ...(approver ? [{ id: "approvals", label: "Validations" }] : []),
  ];

  return (
    <div style={{ display: "grid", gap: 12 }}>
      <Tabs tabs={tabs} active={tab} onChange={setTab} />
      {tab === "sale" && sessionLoaded && (
        <SaleScreen limits={limits} session={session} onSessionChanged={reloadSession} profile={profile} onProfile={setProfile} />
      )}
      {tab === "returns" && <ReturnsPanel session={session} />}
      {tab === "approvals" && limits && approver && (
        <div style={{ display: "grid", gap: 12 }}>
          {limits.canOverrideCredit && <CreditOverridesPanel focusId={params.get("id")} />}
          {(limits.canRefund || limits.canApproveDiscount || limits.canManageSessions) && <ApprovalsPanel limits={limits} focusId={params.get("id")} />}
        </div>
      )}
    </div>
  );
}

function SaleScreen({ limits, session, onSessionChanged, profile, onProfile }: {
  limits: PosLimits | null; session: CurrentSession | null; onSessionChanged: () => void;
  profile: StoredProfile | null; onProfile: (p: StoredProfile) => void;
}) {
  const { user, activeCompanyId } = useAuth();
  const online = useOnline();
  const toast = useToast();
  const { entries: queue, pending: pendingCount } = useOutbox();
  const [cart, setCart] = useState<CartLine[]>([]);
  // UUID d'opération du panier : clé d'idempotence de l'encaissement et lien des remises validées.
  const [cartId, setCartId] = useState(() => crypto.randomUUID());
  const [approvals, setApprovals] = useState<Record<string, DiscountRequest>>({});
  // Phase 5 : encaissement en plusieurs moyens (espèces, carte, mobile money…).
  const [tenderMethods, setTenderMethods] = useState<TenderMethodOption[]>([]);
  const [tenderLines, setTenderLines] = useState<TenderLine[]>(() => [newTenderLine(null)]);
  const [busy, setBusy] = useState(false);
  const [syncing, setSyncing] = useState(false);
  const [receipt, setReceipt] = useState<CheckoutResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [asking, setAsking] = useState<CartLine | null>(null);
  const [onSite, setOnSite] = useState<DiscountRequest | null>(null);
  const [syncDevice, setSyncDevice] = useState(storedDeviceId);
  const [deviceRevoked, setDeviceRevoked] = useState(false);
  // Module clients §17, §34 : client de la vente ; à crédit, le reste non encaissé passe sur son compte.
  const [customer, setCustomer] = useState<CustomerLookup | null>(null);
  const [creditMode, setCreditMode] = useState(false);
  // Module clients §7, §15 : avoir du client utilisé en paiement ; §12 : dépassement de crédit approuvé.
  const [storeCredit, setStoreCredit] = useState(0);
  const [overrideAsk, setOverrideAsk] = useState<string | null>(null);
  const [overrideOnSite, setOverrideOnSite] = useState(false);
  const { request: override, setRequest: setOverride, refresh: refreshOverride } = useCreditOverride(null);
  const searchRef = useRef<HTMLInputElement>(null);

  const currency = session?.currency ?? "HTG";
  const canSell = session?.status === "open" && Boolean(session.inventoryLocationId && session.financialAccountId);

  // Moyens d'encaissement de la devise de la caisse. Hors ligne : ceux du profil, limités à ceux que la
  // politique autorise hors ligne ; à défaut, espèces seules.
  useEffect(() => {
    if (session?.status !== "open") return;
    let alive = true;
    const apply = (m: TenderMethodOption[]) => {
      if (!alive) return;
      setTenderMethods(m);
      const cash = m.find((x) => x.type === "cash") ?? null;
      setTenderLines((ls) => (ls.length === 1 && !ls[0].amount && ls[0].methodId === null ? [newTenderLine(cash)] : ls));
    };
    getTenderOptions(currency)
      .then(apply)
      .catch(() => {
        if (!profile) return;
        const allowed = profile.profile.policy.allowedPaymentMethods.map((x) => x.toLowerCase());
        apply(profile.tenderMethods.filter((m) => allowed.length === 0 || allowed.includes(m.type.toLowerCase())));
      });
    return () => { alive = false; };
  }, [currency, session?.status, online, profile]);

  // Phase 6 : en ligne, l'appareil s'enregistre seul et le profil hors ligne est rafraîchi (toutes les 10 min).
  useEffect(() => {
    if (!online || session?.status !== "open" || !user || !activeCompanyId) return;
    let alive = true;
    const refresh = async () => {
      try {
        const device = await ensureDevice(`Caisse ${session.registerName}`);
        if (alive) { setSyncDevice(device.id); setDeviceRevoked(false); }
      } catch (e) {
        if (e instanceof ApiError && (e.problem.title === "sync.device_revoked" || e.problem.title === "sync.device_suspended")) {
          forgetDevice();
          if (alive) { setSyncDevice(""); setDeviceRevoked(true); }
        }
      }
      try {
        const [p, methods] = await Promise.all([getOfflineProfile(session.inventoryLocationId), getTenderOptions(currency)]);
        const stored: StoredProfile = {
          userId: user.id, companyId: activeCompanyId, savedAt: new Date().toISOString(), profile: p, session, tenderMethods: methods,
        };
        await saveProfile(stored);
        markServerContact();
        if (alive) onProfile(stored);
      } catch { /* profil non rafraîchi : le précédent reste utilisable */ }
    };
    void refresh();
    const t = setInterval(() => void refresh(), 10 * 60_000);
    return () => { alive = false; clearInterval(t); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [online, session?.sessionId, session?.status, user?.id, activeCompanyId, currency]);

  // Envoi d'un lot de synchronisation (Phase 10 §14) — jamais de confirmation simulée (§21).
  const sender = useCallback<BatchSender>(async (batch) => {
    if (!syncDevice) throw new Error("Appareil de synchronisation non configuré.");
    // Phase 12 : traduction commune à la caisse et au terrain (vente, versement client) — jamais un faux type.
    const res = await pushBatch(syncDevice, await toSyncOperations(batch));
    return res.operations.map((o) => ({
      id: o.operationUuid,
      status: o.status === "SYNCED" ? "synced" : o.status === "CONFLICT" ? "conflict" : "failed",
      message: o.message,
    }));
  }, [syncDevice]);

  useEffect(() => autoFlushOnReconnect(sender, kindsFor("pos")), [sender]);

  // Suivi des demandes de remise en attente (§12) : le panier se débloque dès la décision du Manager.
  useEffect(() => {
    const pending = Object.values(approvals).filter((a) => a.status === "pending_approval");
    if (pending.length === 0) return;
    const t = setInterval(() => {
      pending.forEach((a) => getDiscountRequest(a.id)
        .then((fresh) => {
          setApprovals((m) => ({ ...m, [fresh.productId]: fresh }));
          if (fresh.status === "approved") toast.success(`Remise sur ${fresh.productName} validée par ${fresh.decidedByName ?? "le Manager"}.`);
          if (fresh.status === "rejected") toast.push(`Remise sur ${fresh.productName} refusée : ${fresh.decisionNote ?? ""}`, "warning");
        })
        .catch(() => { /* nouvel essai au tour suivant */ }));
    }, 5000);
    return () => clearInterval(t);
  }, [approvals, toast]);

  async function syncNow() {
    setSyncing(true);
    try {
      const r = await flushOutbox(sender, kindsFor("pos"));
      if (r.synced) toast.success(`${r.synced} vente(s) synchronisée(s).`);
      if (r.conflict) toast.push(`${r.conflict} conflit(s) à traiter dans le centre de synchronisation.`, "warning");
      if (r.failed) toast.error(`${r.failed} opération(s) en échec.`);
      pruneSynced();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Synchronisation impossible.");
    } finally {
      setSyncing(false);
    }
  }

  const limit = limits?.discountLimitPercent ?? null;
  const lineGross = (l: CartLine) => Math.round(l.quantity * l.unitPrice * 100) / 100;
  const linePercent = (l: CartLine) => (lineGross(l) > 0 ? (l.discount / lineGross(l)) * 100 : 0);
  const needsApproval = (l: CartLine) => l.discount > 0 && limit !== null && linePercent(l) > limit + 0.0001;
  const approvedFor = (l: CartLine) => {
    const a = approvals[l.productId];
    return a?.status === "approved" && a.requestedPercent + 0.0001 >= linePercent(l);
  };
  const blocked = cart.some((l) => needsApproval(l) && !approvedFor(l));

  // Montant dû : calculé par le serveur dès qu'il répond (prix, promotions, taxes — exactement l'encaissement) ;
  // sinon estimation locale TTC sans promotion (hors ligne), signalée comme telle.
  const estimate = useMemo(() => cart.reduce((acc, l) => {
    const a = lineAmounts(l.quantity, l.unitPrice, l.discount, l.taxRate);
    return { total: Math.round((acc.total + a.total) * 100) / 100, tax: Math.round((acc.tax + a.tax) * 100) / 100 };
  }, { total: 0, tax: 0 }), [cart]);
  const cartKey = JSON.stringify([customer?.id ?? null, cart.map((l) => [l.productId, l.quantity, l.discount])]);
  const [preview, setPreview] = useState<{ key: string; data: CartPreview | null } | null>(null);
  useEffect(() => {
    if (!online || !session?.inventoryLocationId || cart.length === 0) return;
    const ctrl = new AbortController();
    const t = setTimeout(() => {
      previewCart({
        inventoryLocationId: session.inventoryLocationId!, customerId: customer?.id ?? null, currency,
        lines: cart.map((l) => ({ productId: l.productId, quantity: l.quantity, lineDiscount: l.discount })),
      }, ctrl.signal).then((data) => setPreview({ key: cartKey, data }))
        .catch(() => { if (!ctrl.signal.aborted) setPreview({ key: cartKey, data: null }); });   // échec : estimation locale
    }, 250);
    return () => { clearTimeout(t); ctrl.abort(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cartKey, online, session?.inventoryLocationId, currency]);
  const serverCart = preview && preview.key === cartKey ? preview.data : null;
  const previewPending = online && cart.length > 0 && Boolean(session?.inventoryLocationId) && preview?.key !== cartKey;
  const totals = serverCart ? { total: serverCart.total, tax: serverCart.taxTotal } : estimate;
  const promotions = serverCart ? Math.round(serverCart.lines.reduce((a, l) => a + l.promotionDiscount, 0) * 100) / 100 : 0;
  const total = totals.total;
  const lineTotalOf = (l: CartLine) => serverCart?.lines.find((x) => x.productId === l.productId)?.lineTotal
    ?? lineAmounts(l.quantity, l.unitPrice, l.discount, l.taxRate).total;
  const canCredit = Boolean(limits?.canSellCredit && customer?.creditEnabled);
  const onCredit = creditMode && canCredit;
  const availableStore = customer?.availableStoreCredit ?? 0;
  const useStore = Math.round(Math.max(0, Math.min(storeCredit, availableStore, total)) * 100) / 100;
  const due = Math.max(0, Math.round((total - useStore) * 100) / 100);
  // Rien saisi et vente au comptant : paiement exact en espèces (geste le plus fréquent). À crédit, rien
  // saisi = vente entièrement à crédit.
  const entered = summarizeTenders(tenderLines, tenderMethods, due);
  const effectiveLines = entered.paid === 0 && !onCredit && due > 0
    ? tenderLines.map((l, i) => (i === tenderLines.findIndex((x) => x.methodId === null || tenderMethods.find((m) => m.id === x.methodId)?.type === "cash")
      ? { ...l, amount: String(due) } : l))
    : tenderLines;
  const tenderSummary = summarizeTenders(effectiveLines, tenderMethods, due);
  const paid = tenderSummary.paid;
  const change = tenderSummary.change;
  const creditPart = onCredit ? Math.max(0, Math.round((due - paid) * 100) / 100) : 0;
  const insufficient = !onCredit && paid + 0.001 < due;
  const tenderInvalid = tenderSummary.nonCashOver || tenderSummary.missingReference.length > 0 || tenderSummary.missingAccount.length > 0;
  const overAvailable = onCredit && customer?.availableCredit != null && creditPart > customer.availableCredit + 0.001;

  function add(p: PosProduct) {
    setError(null);
    if (p.salePrice == null) {
      setError(`« ${p.name} » n'a pas de prix de vente en vigueur.`);
      return;
    }
    if (p.availability === "out") toast.push(`« ${p.name} » est indiqué en rupture : le serveur confirmera le stock.`, "warning");
    const units = p.packSize && p.packSize > 0 ? p.packSize : 1;   // code de carton : son contenu entier
    setCart((c) => {
      const i = c.findIndex((l) => l.productId === p.productId);
      if (i >= 0) return c.map((l, idx) => (idx === i ? { ...l, quantity: l.quantity + units } : l));
      return [...c, { productId: p.productId, name: p.name, quantity: units, unitPrice: unitPriceExcludingTax(p), discount: 0, taxRate: p.taxRate ?? 0 }];
    });
  }
  const updateLine = (i: number, patch: Partial<CartLine>) =>
    setCart((c) => c.map((l, idx) => (idx === i ? { ...l, ...patch } : l)));
  const removeLine = (i: number) => setCart((c) => c.filter((_, idx) => idx !== i));

  function resetCart() {
    setCart([]);
    setTenderLines([newTenderLine(tenderMethods.find((m) => m.type === "cash") ?? null)]);
    setCustomer(null);
    setCreditMode(false);
    setStoreCredit(0);
    setOverrideAsk(null);
    setOverride(null);
    setApprovals({});
    setCartId(crypto.randomUUID());
    searchRef.current?.focus();
  }

  async function pay() {
    if (!session) return;
    setConfirming(false);
    setError(null);
    setBusy(true);
    const cmd: CheckoutCommand = {
      operationUuid: cartId,
      inventoryLocationId: session.inventoryLocationId ?? "",
      cashSessionId: session.sessionId,
      customerId: customer?.id ?? null,
      currency,
      lines: cart.map((l) => ({ productId: l.productId, quantity: l.quantity, lineDiscount: l.discount })),
      // Un encaissement par moyen ; à crédit, seul le montant réellement reçu est encaissé (le serveur calcule le reste dû).
      tenders: toTenderInputs(effectiveLines, tenderMethods, session.financialAccountId ?? ""),
      amountTendered: paid,
      storeCreditAmount: useStore,
    };
    try {
      const result = await checkout(cmd);
      markServerContact();
      setReceipt(result);
      toast.success(`Vente ${result.documentNumber} enregistrée.`);
      resetCart();
      onSessionChanged();
    } catch (err) {
      // Serveur injoignable → la vente est mise en file, jamais déclarée confirmée (§21).
      if (err instanceof ApiError && (err.isOffline || !online)) {
        // Phase 6 : la politique hors ligne connue est appliquée AVANT de mettre la vente en file (le serveur la
        // réapplique à la synchronisation) — sinon la vente partirait en conflit après coup.
        const policy = profile?.profile.policy;
        const refusal = policy ? offlineRefusal(policy, {
          hasDiscount: cart.some((l) => l.discount > 0),
          creditAmount: creditPart,
          tenderTypes: cmd.tenders.map((t) => tenderMethods.find((m) => m.id === t.paymentMethodId)?.type ?? "cash"),
        }, lastServerContact()) : null;
        if (refusal) {
          setError(`Hors ligne — vente non enregistrée : ${refusal}`);
          return;
        }
        enqueue({ id: cmd.operationUuid, kind: "sale.checkout", payload: cmd });
        setReceipt(null);
        resetCart();
        toast.push("Hors ligne — vente mise en file d'attente, non encore confirmée. Elle sera synchronisée à la reconnexion.", "warning");
      } else {
        setError(err instanceof ApiError ? err.message : "Échec de l'encaissement");
        // §12 : limite dépassée → proposer le circuit d'approbation pour CE panier.
        if (err instanceof ApiError && err.problem.title === "pos.credit_limit" && customer && creditPart > 0 && limits?.canSellCredit)
          setOverrideAsk(err.message);
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ display: "grid", gridTemplateColumns: "minmax(0,1fr) 340px", gap: 16, alignItems: "start" }}>
      {/* ---- Recherche + panier ---- */}
      <div style={{ display: "grid", gap: 12 }}>
        <div style={{ display: "flex", gap: 8, alignItems: "flex-start" }}>
          <ProductSearch locationId={session?.inventoryLocationId ?? null} onPick={add} disabled={!canSell} inputRef={searchRef}
                         offline={!online} offlineCatalog={profile?.profile.catalog ?? null} />
          {!online && <Badge tone="warning" style={{ whiteSpace: "nowrap" }}><Icon.Offline size={13} /> Hors ligne</Badge>}
          {!online && (profile
            ? <Badge tone="info" style={{ whiteSpace: "nowrap" }}>
                <span title={profile.profile.truncated ? `Catalogue limité aux ${profile.profile.maxItems} premiers articles` : undefined}>
                  Catalogue du {dateTime(profile.savedAt)}{profile.profile.truncated ? " (partiel)" : ""}
                </span>
              </Badge>
            : <Badge tone="error" style={{ whiteSpace: "nowrap" }}>Aucun catalogue hors ligne sur ce poste</Badge>)}
          {deviceRevoked && <Badge tone="error" style={{ whiteSpace: "nowrap" }}>Appareil révoqué : pas de vente hors ligne</Badge>}
          {pendingCount > 0 && <Badge tone="info" style={{ whiteSpace: "nowrap" }}><Icon.Sync size={13} /> {pendingCount} non synchronisée(s)</Badge>}
          {outboxProtection() === "plaintext" && (
            <Badge tone="warning" style={{ whiteSpace: "nowrap" }}>
              <span title="Le navigateur ne peut chiffrer la file hors ligne que si l'application est servie en HTTPS.">File hors ligne non chiffrée</span>
            </Badge>
          )}
        </div>

        <Card style={{ padding: 0, overflow: "hidden" }}>
          <table className="nx-table">
            <thead>
              <tr>
                <th>Article</th><th className="nx-num" style={{ width: 78 }}>Qté</th>
                <th className="nx-num" style={{ width: 100 }}>Prix</th>
                <th className="nx-num" style={{ width: 150 }}>Remise{limit !== null && limits?.canDiscount ? ` (≤ ${limit} %)` : ""}</th>
                <th className="nx-num" style={{ width: 110 }}>Total TTC</th><th style={{ width: 44 }} />
              </tr>
            </thead>
            <tbody>
              {cart.map((l, i) => {
                const over = needsApproval(l);
                const req = approvals[l.productId];
                return (
                  <tr key={l.productId}>
                    <td>{l.name}</td>
                    <td><TextInput type="number" min={1} value={l.quantity} style={{ textAlign: "right" }} aria-label={`Quantité — ${l.name}`}
                                   onChange={(e) => updateLine(i, { quantity: Math.max(1, Number(e.target.value) || 1) })} /></td>
                    {/* §12 : le prix vient du serveur ; le caissier ne le saisit pas. */}
                    <td className="nx-num">{money(l.unitPrice, currency)}
                      {l.taxRate > 0 && <div style={{ fontSize: "var(--nx-fs-xs)", color: "var(--nx-text-2)" }}>HT · TVA {l.taxRate} %</div>}
                    </td>
                    <td>
                      {limits?.canDiscount ? (
                        <div style={{ display: "grid", gap: 4 }}>
                          <TextInput type="number" min={0} value={l.discount} style={{ textAlign: "right" }} aria-label={`Remise — ${l.name}`}
                                     invalid={over && !approvedFor(l)}
                                     onChange={(e) => updateLine(i, { discount: Math.max(0, Math.min(lineGross(l), Number(e.target.value) || 0)) })} />
                          {over && !approvedFor(l) && (req?.status === "pending_approval"
                            ? <>
                                <Badge tone="warning">Validation en attente</Badge>
                                <Button size="sm" variant="ghost" onClick={() => setOnSite(req)}>Responsable présent</Button>
                              </>
                            : <Button size="sm" variant="ghost" onClick={() => setAsking(l)}>Demander validation</Button>)}
                          {over && req && req.status !== "pending_approval" && (
                            <Badge tone={statusTone(req.status)}>{STATUS_LABEL[req.status] ?? req.status}</Badge>
                          )}
                        </div>
                      ) : <span className="nx-num">—</span>}
                    </td>
                    <td className="nx-num">{money(lineTotalOf(l), currency)}
                      {serverCart?.lines.find((x) => x.productId === l.productId && x.promotionDiscount > 0) && (
                        <div style={{ fontSize: "var(--nx-fs-xs)", color: "var(--nx-brand)" }}>
                          {serverCart.lines.find((x) => x.productId === l.productId)!.promotionLabel ?? "Promotion"}
                        </div>)}
                    </td>
                    <td><IconButton label="Retirer la ligne" onClick={() => removeLine(i)}><Icon.X size={14} /></IconButton></td>
                  </tr>
                );
              })}
              {cart.length === 0 && (
                <tr><td colSpan={6} style={{ padding: 28, textAlign: "center", color: "var(--nx-text-2)" }}>
                  {canSell ? "Panier vide — scannez ou recherchez un article." : "Ouvrez une session de caisse pour vendre."}
                </td></tr>
              )}
            </tbody>
          </table>
        </Card>
        {cart.length > 0 && (
          <div><Button size="sm" variant="ghost" onClick={resetCart}>Vider le panier</Button></div>
        )}
      </div>

      {/* ---- Session, total, paiement ---- */}
      <div style={{ display: "grid", gap: 12, position: "sticky", top: 78 }}>
        <SessionPanel session={session} onChanged={onSessionChanged} />

        <CustomerPicker customer={customer} disabled={busy}
                        onChange={(c) => { setCustomer(c); setStoreCredit(0); if (!c) setCreditMode(false); }}
                        cashAccountId={session?.status === "open" ? session.financialAccountId : null}
                        cashSessionId={session?.status === "open" ? session.sessionId : null} />

        <Card style={{ textAlign: "center" }}>
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-xs)", textTransform: "uppercase", letterSpacing: 0.6 }}>
            Montant dû
          </div>
          <div className="nx-numeric" style={{ fontSize: 40, fontWeight: 800, lineHeight: 1.15 }}>{money(total, currency)}</div>
          {totals.tax > 0 && <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>dont taxes {money(totals.tax, currency)}</div>}
          {promotions > 0 && <div style={{ color: "var(--nx-brand)", fontSize: "var(--nx-fs-sm)" }}>promotions déduites {money(promotions, currency)}</div>}
          {cart.length > 0 && !serverCart && (
            <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-xs)" }}>
              {previewPending ? "Calcul du montant exact…" : "Estimation (serveur injoignable) : les promotions éventuelles seront appliquées à l'enregistrement."}
            </div>)}
        </Card>

        <Card className="nx-field" style={{ gap: 12 }}>
          {availableStore > 0 && (
            <Field label={`Payer avec l'avoir (disponible ${money(availableStore, currency)})`}>
              <TextInput type="number" min={0} max={Math.min(availableStore, total)} className="nx-input nx-numeric" value={storeCredit || ""}
                         onChange={(e) => setStoreCredit(Math.max(0, Number(e.target.value) || 0))} />
            </Field>
          )}
          {useStore > 0 && (
            <div style={{ display: "flex", justifyContent: "space-between" }}>
              <span>Reste à payer</span><b className="nx-numeric">{money(due, currency)}</b>
            </div>
          )}
          <TenderPanel lines={tenderLines} onChange={setTenderLines} methods={tenderMethods} due={due} currency={currency}
                       summary={entered} disabled={busy} />
          <div style={{ display: "flex", justifyContent: "space-between" }}>
            <span>Total encaissé</span><b className="nx-numeric">{money(paid, currency)}</b>
          </div>
          {tenderSummary.remaining > 0 && !onCredit && (
            <div style={{ display: "flex", justifyContent: "space-between" }}>
              <span>Reste à encaisser</span><b className="nx-numeric" style={{ color: "var(--nx-warning)" }}>{money(tenderSummary.remaining, currency)}</b>
            </div>
          )}
          {tenderSummary.nonCashOver && (
            <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
              <Icon.Alert size={14} /> Carte, mobile money ou virement dépassent le reste dû : la monnaie ne se rend qu'en espèces.
            </div>
          )}
          {tenderSummary.missingReference.length > 0 && (
            <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
              <Icon.Alert size={14} /> Saisissez la référence du paiement (n° d'autorisation ou de transaction).
            </div>
          )}
          <div style={{ display: "flex", justifyContent: "space-between", fontWeight: 700 }}>
            <span>Rendu monnaie</span>
            <span className="nx-numeric" style={{ color: "var(--nx-brand)" }}>{money(change, currency)}</span>
          </div>
          {canCredit && (
            <label style={{ display: "flex", gap: 8, alignItems: "center" }}>
              <input type="checkbox" checked={creditMode} onChange={(e) => setCreditMode(e.target.checked)} />
              Vente à crédit (le reste passe sur le compte du client)
            </label>
          )}
          {onCredit && (
            <div style={{ display: "flex", justifyContent: "space-between", fontWeight: 700 }}>
              <span>Reste à crédit</span>
              <span className="nx-numeric" style={{ color: overAvailable ? "var(--nx-warning)" : undefined }}>{money(creditPart, currency)}</span>
            </div>
          )}
          {override && (
            <div style={{ fontSize: "var(--nx-fs-sm)", display: "grid", gap: 4 }}>
              {override.status === "pending" && <>
                <Badge tone="warning">Dépassement en attente de validation ({override.currentLevel}/{override.requiredLevels})</Badge>
                <Button size="sm" variant="ghost" onClick={() => setOverrideOnSite(true)}>Responsable présent</Button>
              </>}
              {override.status === "approved" && <Badge tone="success">Dépassement approuvé{override.decidedByName ? ` par ${override.decidedByName}` : ""} — encaissez</Badge>}
              {override.status === "rejected" && <Badge tone="error">Dépassement refusé : {override.decisionNote ?? ""}</Badge>}
            </div>
          )}
          {overAvailable && (
            <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
              <Icon.Alert size={14} /> Au-delà du crédit disponible : la vente sera refusée sans autorisation d'un responsable.
            </div>
          )}
          {insufficient && cart.length > 0 && (
            <div style={{ color: "var(--nx-warning)", fontWeight: 600 }}><Icon.Alert size={14} /> Paiement insuffisant</div>
          )}
          {blocked && (
            <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
              <Icon.Alert size={14} /> Une remise dépasse votre limite : validation Manager requise avant l'encaissement.
            </div>
          )}
          {error && <div className="nx-error-text" role="alert">{error}</div>}
        </Card>

        {queue.filter((e) => e.status !== "synced").length > 0 && (
          <Card className="nx-field" style={{ gap: 8 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
              <div style={{ fontSize: "var(--nx-fs-xs)", textTransform: "uppercase", color: "var(--nx-text-2)" }}>Ventes non synchronisées</div>
              <Button size="sm" variant="ghost" loading={syncing} disabled={syncing || !online} onClick={() => void syncNow()}>
                <Icon.Sync size={14} /> Synchroniser
              </Button>
            </div>
            {queue.filter((e) => e.status !== "synced").slice(0, 6).map((e) => (
              <div key={e.id} style={{ display: "flex", justifyContent: "space-between", alignItems: "center", fontSize: "var(--nx-fs-sm)" }}>
                <span style={{ color: "var(--nx-text-2)" }}>{e.id.slice(0, 8)} · {relative(e.createdAt)}</span>
                <StatePill status={e.status} />
              </div>
            ))}
            {queue.filter((e) => e.status !== "synced" && e.lastError).slice(0, 3).map((e) => (
              <div key={`err-${e.id}`} className="nx-err" style={{ fontSize: "var(--nx-fs-xs)" }}>{e.id.slice(0, 8)} : {e.lastError}</div>
            ))}
            {!syncDevice && (
              <div style={{ color: "var(--nx-warning)", fontSize: "var(--nx-fs-sm)" }}>
                <Icon.Alert size={14} /> Poste non enregistré pour la synchronisation : il le sera automatiquement au retour du réseau.
              </div>
            )}
          </Card>
        )}

        <Button block loading={busy} disabled={busy || cart.length === 0 || !canSell || blocked || insufficient || tenderInvalid || previewPending}
                style={{ fontSize: "var(--nx-fs-lg)", minHeight: 56 }} onClick={() => setConfirming(true)}>
          <Icon.Wallet size={18} /> Encaisser {money(total, currency)}
        </Button>

        {receipt && <Receipt receipt={receipt} currency={currency} onClose={() => setReceipt(null)} />}
      </div>

      {confirming && (
        <ConfirmDialog
          title="Confirmer l'encaissement"
          message="Cette opération enregistre la vente et met à jour le stock, la caisse et la comptabilité. Les prix et remises sont recontrôlés par le serveur."
          impact={
            <div style={{ display: "grid", gap: 4 }}>
              <div style={{ display: "flex", justifyContent: "space-between" }}><span>Total</span><b className="nx-numeric">{money(total, currency)}</b></div>
              {toTenderInputs(effectiveLines, tenderMethods, session?.financialAccountId ?? "").map((t, i) => (
                <div key={i} style={{ display: "flex", justifyContent: "space-between" }}>
                  <span>{tenderMethods.find((m) => m.id === t.paymentMethodId)?.name ?? "Espèces"}{t.reference ? ` · ${t.reference}` : ""}</span>
                  <span className="nx-numeric">{money(t.amount, currency)}</span>
                </div>
              ))}
              <div style={{ display: "flex", justifyContent: "space-between" }}><span>Total reçu</span><span className="nx-numeric">{money(paid, currency)}</span></div>
              <div style={{ display: "flex", justifyContent: "space-between" }}><span>Rendu</span><span className="nx-numeric">{money(change, currency)}</span></div>
              {customer && <div style={{ display: "flex", justifyContent: "space-between" }}><span>Client</span><span>{customer.displayName}</span></div>}
              {useStore > 0 && <div style={{ display: "flex", justifyContent: "space-between" }}><span>Payé par avoir</span><span className="nx-numeric">{money(useStore, currency)}</span></div>}
              {creditPart > 0 && <div style={{ display: "flex", justifyContent: "space-between" }}><span>À crédit</span><b className="nx-numeric">{money(creditPart, currency)}</b></div>}
              {creditPart > 0 && !online && <div style={{ color: "var(--nx-warning)" }}>Hors ligne : le crédit sera soumis à la politique hors ligne à la synchronisation.</div>}
            </div>
          }
          confirmLabel="Valider la vente"
          onConfirm={() => void pay()}
          onCancel={() => setConfirming(false)}
        />
      )}

      {overrideAsk && customer && (
        <CreditOverrideRequestDialog cartId={cartId} customerId={customer.id} customerName={customer.displayName}
                                     amount={creditPart} currency={currency} limitMessage={overrideAsk}
                                     onClose={() => setOverrideAsk(null)}
                                     onRequested={(r, validateNow) => {
                                       setOverride(r);
                                       setOverrideAsk(null);
                                       setError(null);
                                       if (r.status === "approved") toast.success("Dépassement déjà approuvé : encaissez.");
                                       else if (validateNow) setOverrideOnSite(true);
                                       else toast.push("Demande de dépassement envoyée au responsable.", "info");
                                     }} />
      )}

      {overrideOnSite && override && (
        <ManagerOverrideDialog
          title="Validation responsable — dépassement de crédit"
          summary={<span>Crédit de <b>{money(override.amount, override.currency)}</b> pour <b>{override.customerName}</b> au-delà du permis.
            {" "}{override.limitMessage} Motif : {override.reason}.</span>}
          approveAction="approve_credit_override" rejectAction="reject_credit_override" targetId={override.id}
          onClose={() => setOverrideOnSite(false)}
          onDone={() => { refreshOverride(); setOverrideOnSite(false); }}
        />
      )}

      {asking && session && (
        <DiscountRequestDialog
          line={asking} cartId={cartId} currency={currency} locationId={session.inventoryLocationId ?? ""}
          onClose={() => setAsking(null)}
          onSent={(r, validateNow) => {
            setApprovals((m) => ({ ...m, [r.productId]: r }));
            setAsking(null);
            if (validateNow) setOnSite(r);
          }}
        />
      )}

      {onSite && (
        <ManagerOverrideDialog
          title="Validation responsable — remise exceptionnelle"
          summary={<span>Remise de <b>{money(onSite.requestedAmount, onSite.currency)}</b> ({onSite.requestedPercent} %) sur
            <b> {onSite.productName}</b> × {onSite.quantity}. Motif du caissier : {onSite.reason}.</span>}
          approveAction="approve_discount" rejectAction="reject_discount" targetId={onSite.id}
          onClose={() => setOnSite(null)}
          onDone={() => {
            getDiscountRequest(onSite.id).then((fresh) => setApprovals((m) => ({ ...m, [fresh.productId]: fresh }))).catch(() => {});
            setOnSite(null);
          }}
        />
      )}
    </div>
  );
}

/** Demande de remise exceptionnelle (§12) : montant brut recalculé par le serveur, motif obligatoire. */
function DiscountRequestDialog({ line, cartId, currency, locationId, onClose, onSent }: {
  line: CartLine; cartId: string; currency: string; locationId: string;
  onClose: () => void; onSent: (r: DiscountRequest, validateNow: boolean) => void;
}) {
  const toast = useToast();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function send(validateNow: boolean) {
    setBusy(true);
    setError(null);
    try {
      const r = await requestDiscount({
        operationUuid: cartId, productId: line.productId, quantity: line.quantity,
        requestedDiscount: line.discount, currency, inventoryLocationId: locationId, reason: reason.trim(),
      });
      if (!validateNow) toast.push(`Demande ${r.requestNumber} envoyée au Manager.`, "info");
      onSent(r, validateNow);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Demande impossible.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal title="Remise exceptionnelle" onClose={onClose}
           footer={<>
             <Button variant="ghost" onClick={onClose}>Annuler</Button>
             <Button variant="ghost" loading={busy} disabled={busy || !reason.trim()} onClick={() => void send(false)}>Envoyer au Manager</Button>
             <Button loading={busy} disabled={busy || !reason.trim()} onClick={() => void send(true)}>Responsable présent</Button>
           </>}>
      <div style={{ display: "grid", gap: 10 }}>
        <p style={{ margin: 0 }}>
          Remise de <b>{money(line.discount, currency)}</b> sur <b>{line.name}</b> × {line.quantity}. Elle dépasse votre limite :
          un Manager doit la valider. La vente reste en attente jusqu'à sa décision.
        </p>
        <Field label="Motif" required>
          <TextInput autoFocus value={reason} onChange={(e) => setReason(e.target.value)} placeholder="Ex. emballage abîmé" />
        </Field>
        {error && <div className="nx-error-text" role="alert">{error}</div>}
      </div>
    </Modal>
  );
}

/** Ticket de caisse (§7.1) : totaux de la transaction en cours uniquement. */
function Receipt({ receipt, currency, onClose }: { receipt: CheckoutResult; currency: string; onClose: () => void }) {
  return (
    <Card style={{ background: "var(--nx-success-bg)", borderColor: "transparent", display: "grid", gap: 6 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", fontWeight: 700 }}>
        <span><Icon.Check size={16} /> Vente {receipt.documentNumber}</span>
        <IconButton label="Masquer le ticket" onClick={onClose}><Icon.X size={14} /></IconButton>
      </div>
      <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
        Total {money(receipt.total, currency)}
        {receipt.discountTotal > 0 && ` · remises ${money(receipt.discountTotal, currency)}`}
        {` · rendu ${money(receipt.changeGiven, currency)}`}
        {(receipt.storeCreditUsed ?? 0) > 0 && ` · payé par avoir ${money(receipt.storeCreditUsed ?? 0, currency)}`}
        {receipt.creditAmount > 0 && ` · à crédit ${money(receipt.creditAmount, currency)}`}
      </div>
      <PrintTicket saleId={receipt.saleId} />
    </Card>
  );
}

/** Vrai ticket 80 mm de la vente (émis par le serveur : totaux, paiements, part à crédit, reste à verser
 * du client) ; une réimpression porte la mention « DUPLICATA ». */
function PrintTicket({ saleId }: { saleId: string }) {
  const toast = useToast();
  const [busy, setBusy] = useState<"ticket" | "pdf" | null>(null);
  const print = (format: "ticket" | "pdf") => {
    setBusy(format);
    printDocument("sale", saleId, format)
      .catch((e) => toast.error(e instanceof ApiError ? e.message : "Impression impossible."))
      .finally(() => setBusy(null));
  };
  return (
    <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
      <Button size="sm" loading={busy === "ticket"} onClick={() => print("ticket")}><Icon.Printer size={14} /> Imprimer le ticket</Button>
      <Button size="sm" variant="ghost" loading={busy === "pdf"} onClick={() => print("pdf")}>Format A4</Button>
    </div>
  );
}
