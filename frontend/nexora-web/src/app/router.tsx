import { Suspense } from "react";
import { createBrowserRouter, Navigate } from "react-router-dom";
import { AppLayout } from "@/app/AppLayout";
import { lazyPage } from "@/app/lazyPage";
import { ProtectedRoute, RequirePermission } from "@/auth/ProtectedRoute";
import { ALL_NAV } from "@/app/nav";
import { LoginPage } from "@/auth/LoginPage";
// La caisse reste dans le paquet principal : elle doit s'ouvrir hors ligne sans télécharger d'écran à la demande.
import { PosPage } from "@/pos/PosPage";

// Finalisation phase 20 (§49) : tous les autres écrans sont chargés à la demande.
const SetupPage = lazyPage(() => import("@/setup/SetupPage"), "SetupPage");
const DashboardPage = lazyPage(() => import("@/dashboard/DashboardPage"), "DashboardPage");
const AdminCenterPage = lazyPage(() => import("@/admin/AdminCenterPage"), "AdminCenterPage");
const CataloguePage = lazyPage(() => import("@/catalog/CataloguePage"), "CataloguePage");
const StockCenterPage = lazyPage(() => import("@/inventory/StockCenterPage"), "StockCenterPage");
const OrdersPage = lazyPage(() => import("@/sales/OrdersPage"), "OrdersPage");
const PurchasingPage = lazyPage(() => import("@/purchasing/PurchasingPage"), "PurchasingPage");
const PartiesPage = lazyPage(() => import("@/parties/PartiesPage"), "PartiesPage");
const CustomerAccountPage = lazyPage(() => import("@/customers/CustomerAccountPage"), "CustomerAccountPage");
const RecoveryPage = lazyPage(() => import("@/credit/RecoveryPage"), "RecoveryPage");
const TreasuryCockpitPage = lazyPage(() => import("@/finance/TreasuryCockpitPage"), "TreasuryCockpitPage");
const LedgerPage = lazyPage(() => import("@/accounting/LedgerPage"), "LedgerPage");
const DocumentsPage = lazyPage(() => import("@/documents/DocumentsPage"), "DocumentsPage");
const AnalyticsPage = lazyPage(() => import("@/reports/AnalyticsPage"), "AnalyticsPage");
const IntelligencePage = lazyPage(() => import("@/intelligence/IntelligencePage"), "IntelligencePage");
const FieldOpsPage = lazyPage(() => import("@/mobile/FieldOpsPage"), "FieldOpsPage");
const RestaurantPage = lazyPage(() => import("@/restaurant/RestaurantPage"), "RestaurantPage");
const GedPage = lazyPage(() => import("@/ged/GedPage"), "GedPage");
const HrPage = lazyPage(() => import("@/hr/HrPage"), "HrPage");
const EmployeePage = lazyPage(() => import("@/hr/EmployeePage"), "EmployeePage");
const PayrollRunPage = lazyPage(() => import("@/hr/PayrollRunPage"), "PayrollRunPage");
const AdvancePage = lazyPage(() => import("@/hr/MyHrPage"), "AdvancePage");
const MyHrPage = lazyPage(() => import("@/hr/MyHrPage"), "MyHrPage");
const GroupPage = lazyPage(() => import("@/group/GroupPage"), "GroupPage");
const NotificationsPage = lazyPage(() => import("@/notifications/NotificationsPage"), "NotificationsPage");
const ContinuityPage = lazyPage(() => import("@/continuity/ContinuityPage"), "ContinuityPage");
const OperationsPage = lazyPage(() => import("@/operations/OperationsPage"), "OperationsPage");
const PlatformConsolePage = lazyPage(() => import("@/platform/PlatformConsolePage"), "PlatformConsolePage");
const BillingPortalPage = lazyPage(() => import("@/billing/BillingPortalPage"), "BillingPortalPage");
const MigrationWizardPage = lazyPage(() => import("@/migration/MigrationWizardPage"), "MigrationWizardPage");
const IntegrationsCenterPage = lazyPage(() => import("@/integrations/IntegrationsCenterPage"), "IntegrationsCenterPage");
const SectorSettingsPage = lazyPage(() => import("@/sectors/SectorSettingsPage"), "SectorSettingsPage");
const SecurityPage = lazyPage(() => import("@/security/SecurityPage"), "SecurityPage");
const SettingsPage = lazyPage(() => import("@/settings/SettingsPage"), "SettingsPage");
const SyncCenterPage = lazyPage(() => import("@/sync/SyncCenterPage"), "SyncCenterPage");
const SaleDetailPage = lazyPage(() => import("@/sales/SaleDetailPage"), "SaleDetailPage");

/** Chaque écran exige la permission de son entrée de menu (guide §18, §27). */
function guarded(to: string, element: React.ReactElement) {
  const permission = ALL_NAV.find((n) => n.to === to)?.permission;
  return <RequirePermission permission={permission}>{element}</RequirePermission>;
}

export const router = createBrowserRouter([
  { path: "/setup", element: <Suspense fallback={<div role="status">Chargement…</div>}><SetupPage /></Suspense> },   // assistant (§20)
  { path: "/login", element: <LoginPage /> },
  { path: "/reset-password", element: <LoginPage /> },
  {
    path: "/",
    element: (
      <ProtectedRoute>
        <AppLayout />
      </ProtectedRoute>
    ),
    children: [
      { index: true, element: <DashboardPage /> },
      { path: "pos", element: guarded("/pos", <PosPage />) },
      { path: "catalog", element: guarded("/catalog", <CataloguePage />) },
      { path: "orders", element: guarded("/orders", <OrdersPage />) },
      { path: "documents", element: guarded("/documents", <DocumentsPage />) },
      { path: "ged", element: guarded("/ged", <GedPage />) },
      { path: "purchasing", element: guarded("/purchasing", <PurchasingPage />) },
      { path: "customers", element: guarded("/customers", <PartiesPage />) },
      { path: "suppliers", element: guarded("/suppliers", <PartiesPage initialTab="suppliers" />) },
      { path: "customers/:id", element: guarded("/customers", <CustomerAccountPage />) },
      { path: "field-ops", element: guarded("/field-ops", <FieldOpsPage />) },
      { path: "restaurant", element: guarded("/restaurant", <RestaurantPage />) },
      { path: "hr", element: guarded("/hr", <HrPage />) },
      { path: "hr/employees/:id", element: guarded("/hr", <EmployeePage />) },
      { path: "hr/payroll/:runId", element: guarded("/hr", <PayrollRunPage />) },
      { path: "hr/advances/:id", element: guarded("/hr", <AdvancePage />) },
      { path: "me/hr", element: guarded("/me/hr", <MyHrPage />) },
      { path: "inventory", element: guarded("/inventory", <StockCenterPage />) },
      { path: "credit", element: guarded("/credit", <RecoveryPage />) },
      { path: "finance", element: guarded("/finance", <TreasuryCockpitPage />) },
      { path: "accounting", element: guarded("/accounting", <LedgerPage />) },
      { path: "reports", element: guarded("/reports", <AnalyticsPage />) },
      { path: "intelligence", element: guarded("/intelligence", <IntelligencePage />) },
      { path: "group", element: guarded("/group", <GroupPage />) },
      { path: "sync", element: guarded("/sync", <SyncCenterPage />) },
      { path: "integrations", element: guarded("/integrations", <IntegrationsCenterPage />) },
      { path: "security", element: guarded("/security", <SecurityPage />) },
      { path: "notifications", element: guarded("/notifications", <NotificationsPage />) },
      { path: "continuity", element: guarded("/continuity", <ContinuityPage />) },
      { path: "operations", element: guarded("/operations", <OperationsPage />) },
      { path: "platform", element: guarded("/platform", <PlatformConsolePage />) },
      { path: "billing", element: guarded("/billing", <BillingPortalPage />) },
      { path: "migration", element: guarded("/migration", <MigrationWizardPage />) },
      { path: "sectors", element: guarded("/sectors", <SectorSettingsPage />) },
      { path: "admin", element: guarded("/admin", <AdminCenterPage />) },
      { path: "automation", element: guarded("/automation", <AdminCenterPage initialTab="automations" />) },
      // Détail d'une vente (recherche Ctrl+K, fil d'activité) — même droit que GET /sales/{id}.
      { path: "sales/:id", element: <RequirePermission permission="pos.view"><SaleDetailPage /></RequirePermission> },
      { path: "settings", element: guarded("/settings", <SettingsPage />) },
    ],
  },
  { path: "*", element: <Navigate to="/" replace /> },
]);
