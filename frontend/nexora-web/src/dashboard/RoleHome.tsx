import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { Badge, Button, Card, EmptyState } from "@/shared/ui";
import { Icon } from "@/shared/icons";
import { dateTime, money } from "@/shared/format";
import {
  getCurrentSession, listDiscountRequests, listPendingClosings, listReturnRequests, STATUS_LABEL,
  type CurrentSession, type DiscountRequest, type ReturnRequest,
} from "@/pos/posApi";
import { statusTone } from "@/pos/ReturnsPanel";

/**
 * Validations Manager en attente (guide §18, §20) — n'apparaît qu'aux détenteurs d'une permission
 * d'approbation ; chaque compteur n'interroge que l'API autorisée.
 */
export function PendingApprovalsCard() {
  const { hasPermission } = useAuth();
  const canRefund = hasPermission("pos.refund");
  const canDiscount = hasPermission("pos.approve_discount");
  const canSessions = hasPermission("pos.manage_sessions");
  const [counts, setCounts] = useState({ returns: 0, discounts: 0, closings: 0 });

  useEffect(() => {
    if (canRefund) listReturnRequests("pending_approval").then((r) => setCounts((c) => ({ ...c, returns: r.length }))).catch(() => {});
    if (canDiscount) listDiscountRequests("pending_approval").then((r) => setCounts((c) => ({ ...c, discounts: r.length }))).catch(() => {});
    if (canSessions) listPendingClosings().then((r) => setCounts((c) => ({ ...c, closings: r.length }))).catch(() => {});
  }, [canRefund, canDiscount, canSessions]);

  if (!canRefund && !canDiscount && !canSessions) return null;
  const total = counts.returns + counts.discounts + counts.closings;
  return (
    <Card style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
      <div>
        <div style={{ fontWeight: 700 }}><Icon.Shield size={16} /> Validations en attente</div>
        <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
          {total === 0 ? "Aucune demande à traiter." :
            [counts.returns && `${counts.returns} retour(s)`, counts.discounts && `${counts.discounts} remise(s) exceptionnelle(s)`,
             counts.closings && `${counts.closings} écart(s) de caisse`].filter(Boolean).join(" · ")}
        </div>
      </div>
      {total > 0 && <Link to="/pos?approvals=1"><Button size="sm">Traiter</Button></Link>}
    </Card>
  );
}

/**
 * Accueil d'un utilisateur sans pilotage (guide §18) : le caissier voit SA caisse et SES demandes —
 * jamais de chiffre d'affaires global, de marge, de trésorerie ni de stock global (§7.2).
 */
export function RoleHome() {
  const { hasPermission } = useAuth();
  const seller = hasPermission("pos.sell");
  const [session, setSession] = useState<CurrentSession | null>(null);
  const [returns, setReturns] = useState<ReturnRequest[]>([]);
  const [discounts, setDiscounts] = useState<DiscountRequest[]>([]);

  useEffect(() => {
    if (!seller) return;
    getCurrentSession().then((s) => setSession(s ?? null)).catch(() => {});
    listReturnRequests().then((r) => setReturns(r.slice(0, 5))).catch(() => {});
    listDiscountRequests().then((r) => setDiscounts(r.slice(0, 5))).catch(() => {});
  }, [seller]);

  if (!seller) {
    return (
      <Card><EmptyState title="Pilotage non disponible"
            hint="Le tableau de bord de pilotage n'est pas accessible avec vos permissions actuelles." /></Card>
    );
  }

  return (
    <div style={{ display: "grid", gap: 12 }}>
      <Card style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
        <div>
          <div style={{ fontWeight: 700 }}><Icon.Pos size={16} /> Ma caisse</div>
          <div style={{ color: "var(--nx-text-2)", fontSize: "var(--nx-fs-sm)" }}>
            {session
              ? `${session.registerName} · ouverte ${dateTime(session.openedAt)} · ${session.saleCount} vente(s)`
              : "Aucune session ouverte."}
          </div>
          {session?.status === "closing" && <Badge tone="warning">Clôture en attente de validation</Badge>}
        </div>
        <Link to="/pos"><Button>{session ? "Continuer la vente" : "Ouvrir la caisse"}</Button></Link>
      </Card>

      <PendingApprovalsCard />

      <Card style={{ display: "grid", gap: 8 }}>
        <div style={{ fontWeight: 700 }}>Mes demandes</div>
        {returns.length + discounts.length === 0 && <EmptyState title="Aucune demande en cours" />}
        {returns.map((r) => (
          <div key={r.id} style={{ display: "flex", justifyContent: "space-between", gap: 8, fontSize: "var(--nx-fs-sm)" }}>
            <span>Retour {r.requestNumber} · vente {r.saleNumber} · {money(r.estimatedRefund, r.currency)}</span>
            <Badge tone={statusTone(r.status)}>{STATUS_LABEL[r.status] ?? r.status}</Badge>
          </div>
        ))}
        {discounts.map((d) => (
          <div key={d.id} style={{ display: "flex", justifyContent: "space-between", gap: 8, fontSize: "var(--nx-fs-sm)" }}>
            <span>Remise {d.requestNumber} · {d.productName} · {d.requestedPercent} %</span>
            <Badge tone={statusTone(d.status)}>{STATUS_LABEL[d.status] ?? d.status}</Badge>
          </div>
        ))}
      </Card>
    </div>
  );
}
