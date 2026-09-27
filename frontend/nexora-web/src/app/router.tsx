import { createBrowserRouter, Navigate } from "react-router-dom";
import { AppLayout } from "@/app/AppLayout";
import { ProtectedRoute, RequirePermission } from "@/auth/ProtectedRoute";
import { ALL_NAV } from "@/app/nav";
import { LoginPage } from "@/auth/LoginPage";
import { SetupPage } from "@/setup/SetupPage";
import { DashboardPage } from "@/dashboard/DashboardPage";
import { AdminCenterPage } from "@/admin/AdminCenterPage";
import { CataloguePage } from "@/catalog/CataloguePage";
import { StockCenterPage } from "@/inventory/StockCenterPage";
import { OrdersPage } from "@/sales/OrdersPage";
import { PurchasingPage } from "@/purchasing/PurchasingPage";
import { PartiesPage } from "@/parties/PartiesPage";
import { CustomerAccountPage } from "@/customers/CustomerAccountPage";
import { RecoveryPage } from "@/credit/RecoveryPage";
import { TreasuryCockpitPage } from "@/finance/TreasuryCockpitPage";
import { LedgerPage } from "@/accounting/LedgerPage";
import { DocumentsPage } from "@/documents/DocumentsPage";
import { AnalyticsPage } from "@/reports/AnalyticsPage";
import { IntelligencePage } from "@/intelligence/IntelligencePage";
import { FieldOpsPage } from "@/mobile/FieldOpsPage";
import { GedPage } from "@/ged/GedPage";
import { HrPage } from "@/hr/HrPage";
import { EmployeePage } from "@/hr/EmployeePage";
import { PayrollRunPage } from "@/hr/PayrollRunPage";
import { AdvancePage, MyHrPage } from "@/hr/MyHrPage";
import { GroupPage } from "@/group/GroupPage";
import { NotificationsPage } from "@/notifications/NotificationsPage";
import { ContinuityPage } from "@/continuity/ContinuityPage";
import { OperationsPage } from "@/operations/OperationsPage";
import { PlatformConsolePage } from "@/platform/PlatformConsolePage";
import { BillingPortalPage } from "@/billing/BillingPortalPage";
import { MigrationWizardPage } from "@/migration/MigrationWizardPage";
import { IntegrationsCenterPage } from "@/integrations/IntegrationsCenterPage";
import { PosPage } from "@/pos/PosPage";
import { SectorSettingsPage } from "@/sectors/SectorSettingsPage";
import { SecurityPage } from "@/security/SecurityPage";
import { SettingsPage } from "@/settings/SettingsPage";
import { SyncCenterPage } from "@/sync/SyncCenterPage";
import { SaleDetailPage } from "@/sales/SaleDetailPage";

/** Chaque écran exige la permission de son entrée de menu (guide §18, §27). */
function guarded(to: string, element: React.ReactElement) {
  const permission = ALL_NAV.find((n) => n.to === to)?.permission;
  return <RequirePermission permission={permission}>{element}</RequirePermission>;
}

export const router = createBrowserRouter([
  { path: "/setup", element: <SetupPage /> },   // assistant d'installation (§20)
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
