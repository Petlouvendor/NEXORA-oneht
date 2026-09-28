import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useAuth } from "@/auth/AuthContext";
import { Card, Tabs } from "@/shared/ui";
import { OrdersPanel } from "@/sales/OrdersPanel";
import { QuotesPanel } from "@/sales/QuotesPanel";
import { useSalesRefs } from "@/sales/salesShared";

/**
 * Ventes — devis et commandes clients (finalisation, phase 4) : devis → commande → acompte → réservation →
 * livraison (vente réelle, acompte imputé) → facture. Les documents émis se retrouvent aussi dans « Documents ».
 */
export function OrdersPage() {
  const { hasPermission } = useAuth();
  const [params, setParams] = useSearchParams();
  const canOrders = hasPermission("pos.manage_order");
  const canQuotes = hasPermission("documents.view");
  const tab = params.get("tab") ?? (canOrders ? "orders" : "quotes");
  const [openOrder, setOpenOrder] = useState<string | null>(params.get("order"));
  const refs = useSalesRefs();

  if (!canOrders && !canQuotes) return <Card>Les ventes ne sont pas accessibles avec vos permissions.</Card>;

  const tabs = [
    ...(canOrders ? [{ id: "orders", label: "Commandes clients" }] : []),
    ...(canQuotes ? [{ id: "quotes", label: "Devis" }] : []),
  ];
  return (
    <div style={{ display: "grid", gap: 16 }}>
      <h1 style={{ margin: 0 }}>Ventes : devis & commandes</h1>
      <Card><Tabs tabs={tabs} active={tab} onChange={(t) => setParams({ tab: t })} /></Card>
      {tab === "orders" && canOrders && <OrdersPanel refs={refs} openId={openOrder} onOpen={setOpenOrder} />}
      {tab === "quotes" && canQuotes && (
        <QuotesPanel refs={refs} onOrderCreated={(id) => { setParams({ tab: "orders" }); setOpenOrder(id); }} />
      )}
    </div>
  );
}
