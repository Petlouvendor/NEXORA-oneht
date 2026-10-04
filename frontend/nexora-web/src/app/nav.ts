import type { IconName } from "@/shared/icons";

export interface NavItem {
  to: string;
  label: string;
  icon: IconName;
  permission?: string;      // masqué si absent (§9)
  end?: boolean;
}

export interface NavGroup {
  title: string;
  items: NavItem[];
}

/**
 * Navigation globale — structure NEXORA ONE (brief §5) : Dashboard, Ventes, POS, Stock, Achats, Clients,
 * Fournisseurs, Finance, RH, Rapports, Intelligence, Automatisation, Paramètres. Adaptative au rôle :
 * chaque entrée est filtrée par la permission de son écran (la même que la route, voir router.tsx) —
 * un caissier ne voit pas les modules d'un directeur, un groupe vide disparaît.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    title: "NEXORA ONE",
    items: [
      { to: "/", label: "Dashboard", icon: "Home", end: true },
    ],
  },
  {
    title: "Commerce",
    items: [
      { to: "/orders", label: "Ventes & commandes", icon: "Cart", permission: "pos.manage_order" },
      { to: "/pos", label: "POS", icon: "Pos", permission: "pos.sell" },
      { to: "/restaurant", label: "Restaurant", icon: "Utensils", permission: "restaurant.serve" },
      { to: "/documents", label: "Factures & documents", icon: "Ledger", permission: "documents.view" },
      { to: "/customers", label: "Clients", icon: "Users", permission: "customers.view" },
      { to: "/suppliers", label: "Fournisseurs", icon: "Truck", permission: "suppliers.view" },
    ],
  },
  {
    title: "Opérations",
    items: [
      { to: "/inventory", label: "Stock", icon: "Box", permission: "inventory.view" },
      { to: "/catalog", label: "Catalogue", icon: "Layers", permission: "catalog.view" },
      { to: "/purchasing", label: "Achats", icon: "Cart", permission: "purchasing.view" },
      { to: "/field-ops", label: "Terrain", icon: "MapPin", permission: "field_ops.view" },
      { to: "/ged", label: "GED", icon: "Folder", permission: "ged.view" },
    ],
  },
  {
    title: "Finance",
    items: [
      { to: "/finance", label: "Finance & trésorerie", icon: "Wallet", permission: "finance.view" },
      { to: "/credit", label: "Crédit & recouvrement", icon: "Users", permission: "credit.view_portfolio" },
      { to: "/accounting", label: "Comptabilité", icon: "Ledger", permission: "accounting.view" },
    ],
  },
  {
    title: "RH",
    items: [
      { to: "/hr", label: "RH & Paie", icon: "Briefcase", permission: "hr.view_employees" },
      { to: "/me/hr", label: "Mon espace RH", icon: "IdCard", permission: "payroll.self_service" },
    ],
  },
  {
    title: "Pilotage",
    items: [
      { to: "/reports", label: "Rapports", icon: "Chart", permission: "reporting.view" },
      { to: "/intelligence", label: "Intelligence", icon: "Sparkles", permission: "intelligence.view" },
      { to: "/automation", label: "Automatisation", icon: "Automation", permission: "admin.automations" },
    ],
  },
  {
    title: "Paramètres",
    items: [
      { to: "/settings", label: "Préférences", icon: "Sliders" },
      { to: "/notifications", label: "Notifications", icon: "Bell" },
      { to: "/admin", label: "Administration", icon: "Cog", permission: "admin.settings" },
      { to: "/security", label: "Sécurité", icon: "Shield", permission: "security.audit_view" },
      { to: "/sectors", label: "Secteurs & modules", icon: "Sliders", permission: "sectors.configure" },
      { to: "/group", label: "Entreprises / Groupe", icon: "Group", permission: "group.view" },
      { to: "/integrations", label: "Intégrations", icon: "Plug", permission: "integrations.view" },
      { to: "/sync", label: "Synchronisation", icon: "Sync", permission: "sync.view" },
      { to: "/migration", label: "Migration & onboarding", icon: "Truck", permission: "migration.view" },
      { to: "/continuity", label: "Continuité", icon: "Sync", permission: "continuity.view" },
      { to: "/operations", label: "Exploitation", icon: "Activity", permission: "operations.view" },
      { to: "/billing", label: "Mon abonnement", icon: "Wallet", permission: "platform.portal" },
      { to: "/platform", label: "Plateforme NEXORA", icon: "Layers", permission: "platform.view" },
    ],
  },
];

export const ALL_NAV: NavItem[] = NAV_GROUPS.flatMap((g) => g.items);
