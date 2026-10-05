namespace Nexora.Application.Security;

/// <summary>
/// Codes de permission — miroir du catalogue seedé en base (migration V021).
/// Format : module.action. Deny-by-default : une action non couverte est refusée (Phase 3 §10).
/// </summary>
public static class Permissions
{
    public static class Sales
    {
        public const string View = "sales.view";
        public const string Create = "sales.create";
        public const string Credit = "sales.credit";
    }

    public static class Pos
    {
        public const string View = "pos.view";
        public const string Discount = "pos.discount";
        public const string Refund = "pos.refund";
        public const string Void = "pos.void";
        public const string CloseSession = "pos.close_session";
        public const string Sell = "pos.sell";
        public const string SellCredit = "pos.sell_credit";
        public const string ChangePrice = "pos.change_price";
        public const string CreditOverride = "pos.credit_override";
        public const string Return = "pos.return";
        public const string VoidSale = "pos.void_sale";
        public const string OpenSession = "pos.open_session";
        public const string CashInOut = "pos.cash_in_out";
        public const string ViewCustomerBalance = "pos.view_customer_balance";
        public const string ManageOrder = "pos.manage_order";

        // Validations Manager du guide fonctionnel (§8, §10, §12, §13) — V064.
        // Refund = valider une demande de retour / exécuter un retour-remboursement.
        public const string ApproveDiscount = "pos.approve_discount";
        public const string ManageSessions = "pos.manage_sessions";
    }

    public static class Finance
    {
        public const string View = "finance.view";
        public const string Create = "finance.create";
        public const string Pay = "finance.pay";
        public const string Allocate = "finance.allocate";
        public const string ManageCash = "finance.manage_cash";
        public const string Transfer = "finance.transfer";
        public const string Reconcile = "finance.reconcile";
        public const string Export = "finance.export";
        /// <summary>Paramétrage de la trésorerie : comptes, catégories de dépense (phase 7 ; permission existante en base).</summary>
        public const string Update = "finance.update";

        // Finance & trésorerie — cockpit de pilotage (Phase 28 §32, §38)
        public const string ClosePeriod = "finance.close_period";
        public const string ManageSuspense = "finance.manage_suspense";
    }

    public static class Accounting
    {
        public const string View = "accounting.view";
        public const string Create = "accounting.create";
        public const string Post = "accounting.post";
        public const string Validate = "accounting.validate";
        public const string Reverse = "accounting.reverse";
        public const string ManageChart = "accounting.manage_chart";
        public const string ClosePeriod = "accounting.close_period";
        public const string ReopenPeriod = "accounting.reopen_period";
        public const string CarryForward = "accounting.carry_forward";
        public const string Export = "accounting.export";
    }

    public static class Admin
    {
        public const string ManageUsers = "admin.manage_users";
        public const string ManageRoles = "admin.manage_roles";
        public const string ConfigureCompany = "admin.configure_company";

        // Administration, Paramétrage & Automatisation (Phase 14 §4, §11)
        public const string Settings = "admin.settings";
        public const string Workflows = "admin.workflows";
        public const string Automations = "admin.automations";
        public const string ConfigVersions = "admin.config_versions";
        public const string Roles = "admin.roles";
    }

    public static class Organizations
    {
        public const string View = "organizations.view";
        public const string Create = "organizations.create";
        public const string Update = "organizations.update";
    }

    public static class Catalog
    {
        public const string View = "catalog.view";
        public const string Create = "catalog.create";
        public const string Update = "catalog.update";
        public const string ManagePrice = "catalog.manage_price";
        public const string ViewCost = "catalog.view_cost";
        public const string ManagePromotion = "catalog.manage_promotion";
        public const string ManageTax = "catalog.manage_tax";
        public const string Import = "catalog.import";
    }

    /// <summary>Configuration & adaptabilité (Phase 32 §8, §23, §52).</summary>
    public static class Config
    {
        public const string ApplyProfile = "config.apply_profile";
        public const string Transfer = "config.transfer";
        public const string Validate = "config.validate";
    }

    /// <summary>Facturation & documents commerciaux (Phase 30 §47).</summary>
    public static class Documents
    {
        public const string View = "documents.view";
        public const string CreateQuote = "documents.create_quote";
        public const string Issue = "documents.issue";
        public const string Cancel = "documents.cancel";
        public const string Send = "documents.send";
        public const string ManageSequence = "documents.manage_sequence";
        /// <summary>Tous les documents émis de l'entreprise ; sans elle, seulement les siens (guide §7.2) — V069.</summary>
        public const string ViewAll = "documents.view_all";
    }

    public static class Credit
    {
        public const string SetLimit = "credit.set_limit";
        public const string Collect = "credit.collect";
        public const string Dispute = "credit.dispute";
        public const string Block = "credit.block";
        public const string WriteOff = "credit.write_off";
        public const string ViewPortfolio = "credit.view_portfolio";

        // Module clients & comptes clients (V073, §18).
        public const string ReceivePayment = "credit.receive_payment";
        public const string VoidPayment = "credit.void_payment";
        public const string Refund = "credit.refund";
        // Crédit hors vente — reprise de solde (V074, §22).
        public const string Create = "credit.create";
    }

    // Amorcées dès la V021 et attribuées aux rôles, mais jamais branchées avant la recette Phase 55.
    public static class Customers
    {
        public const string View = "customers.view";
        public const string Create = "customers.create";
        public const string Update = "customers.update";
        public const string Export = "customers.export";

        // Dépenses imputées (V073, §10, §18).
        public const string CreateExpense = "customers.create_expense";
        public const string ApproveExpense = "customers.approve_expense";
    }

    // Module paie, RH & avances (V076, docs/module-paie-rh-avances.md §5-§6).
    public static class Hr
    {
        public const string ViewEmployees = "hr.view_employees";
        public const string ManageEmployees = "hr.manage_employees";
        public const string ManageTime = "hr.manage_time";
        public const string ApproveRequests = "hr.approve_requests";
    }

    public static class Payroll
    {
        public const string ViewSalaries = "payroll.view_salaries";
        public const string ManageCompensation = "payroll.manage_compensation";
        public const string ViewSensitive = "payroll.view_sensitive";
        public const string Calculate = "payroll.calculate";
        public const string Review = "payroll.review";
        public const string Validate = "payroll.validate";
        public const string Approve = "payroll.approve";
        public const string Pay = "payroll.pay";
        public const string Close = "payroll.close";
        public const string Adjust = "payroll.adjust";
        public const string ApproveAdjustment = "payroll.approve_adjustment";
        public const string AdvanceRequest = "payroll.advance_request";
        public const string AdvanceApprove = "payroll.advance_approve";
        public const string AdvancePay = "payroll.advance_pay";
        public const string AdvanceSchedule = "payroll.advance_schedule";
        public const string ExpenseApprove = "payroll.expense_approve";
        public const string Configure = "payroll.configure";
        public const string Reports = "payroll.reports";
        public const string Audit = "payroll.audit";
        public const string SelfService = "payroll.self_service";
    }

    public static class Suppliers
    {
        public const string View = "suppliers.view";
        public const string Create = "suppliers.create";
        public const string Update = "suppliers.update";
    }

    public static class Inventory
    {
        public const string View = "inventory.view";
        public const string Adjust = "inventory.adjust";
        public const string Transfer = "inventory.transfer";
        public const string Count = "inventory.count";
        public const string ValidateAdjustment = "inventory.validate_adjustment";
        public const string WriteOff = "inventory.write_off";
        public const string ManageLot = "inventory.manage_lot";
        public const string ViewValuation = "inventory.view_valuation";
        public const string ManageWarehouse = "inventory.manage_warehouse";
    }

    public static class Purchasing
    {
        public const string View = "purchasing.view";
        public const string Order = "purchasing.order";
        public const string Receive = "purchasing.receive";
        public const string Invoice = "purchasing.invoice";
        public const string Return = "purchasing.return";
        public const string ApproveOrder = "purchasing.approve_order";
        public const string Match = "purchasing.match";
        public const string ApproveInvoice = "purchasing.approve_invoice";
        public const string Pay = "purchasing.pay";
        public const string ViewPayables = "purchasing.view_payables";
    }

    /// <summary>Dashboard, Rapports & Pilotage (Phase 7 §36).</summary>
    public static class Reporting
    {
        public const string View = "reporting.view";
        public const string Export = "reporting.export";
        public const string ViewFinancial = "reporting.view_financial";
        /// <summary>Chiffre d'affaires et ventes de l'entreprise (guide §18) — V069.</summary>
        public const string ViewSales = "reporting.view_sales";
        public const string ExportFinancial = "reporting.export_financial";
        public const string ViewGroup = "reporting.view_group";
        public const string Schedule = "reporting.schedule";
        public const string ManageTargets = "reporting.manage_targets";
        public const string Ask = "reporting.ask";

        // Reporting & analytics — couche de confiance (Phase 31 §44-46)
        public const string DataQuality = "reporting.data_quality";

        // Business Intelligence, Reporting & Pilotage (Phase 42 §33-41)
        public const string ManageBudgets = "reporting.manage_budgets";
        public const string Forecast = "reporting.forecast";
        public const string ManageInsights = "reporting.manage_insights";
    }

    /// <summary>Intelligence métier & recommandations (Phase 43 §48).</summary>
    public static class Intelligence
    {
        public const string View = "intelligence.view";
        public const string ManageRules = "intelligence.manage_rules";
        public const string ActOnRecommendation = "intelligence.act_on_recommendation";
        public const string UseAssistant = "intelligence.use_assistant";
    }

    /// <summary>Mobile, terrain & multi-appareils (Phase 44 §48) — tournées, interventions, diagnostics.</summary>
    public static class FieldOps
    {
        public const string View = "field_ops.view";
        public const string Manage = "field_ops.manage";
    }

    /// <summary>GED, documents, archivage & signature (Phase 45 §33).</summary>
    public static class Ged
    {
        public const string View = "ged.view";
        public const string Upload = "ged.upload";
        public const string Manage = "ged.manage";
        public const string Sign = "ged.sign";
        public const string ManageRetention = "ged.manage_retention";
    }

    /// <summary>Modules sectoriels (Phase 8 §20).</summary>
    public static class Sectors
    {
        public const string Configure = "sectors.configure";
        public const string CustomFields = "sectors.custom_fields";
        public const string Terminology = "sectors.terminology";
    }

    /// <summary>Module Restaurant (Phase 8 §6).</summary>
    public static class Restaurant
    {
        public const string Manage = "restaurant.manage";
        public const string Serve = "restaurant.serve";
        public const string Kitchen = "restaurant.kitchen";
    }

    public static class Recipes
    {
        public const string Manage = "recipes.manage";
    }

    /// <summary>Multi-entreprises, Groupe & Consolidation (Phase 9 §21).</summary>
    public static class Group
    {
        public const string View = "group.view";
        public const string Manage = "group.manage";
        public const string Mappings = "group.mappings";
        public const string Intercompany = "group.intercompany";
        public const string Tasks = "group.tasks";
        public const string Companies = "group.companies";
    }

    public static class Consolidation
    {
        public const string Run = "consolidation.run";
        public const string Validate = "consolidation.validate";
        public const string Publish = "consolidation.publish";
    }

    /// <summary>
    /// Permission de gouvernance de groupe : n'a d'effet que portée par un rôle attribué au niveau
    /// du groupe (ou de la plateforme). Attribuée dans une seule société, elle reste inerte.
    /// </summary>
    public static bool IsGroupLevel(string code) =>
        code.StartsWith("group.", StringComparison.Ordinal) || code.StartsWith("consolidation.", StringComparison.Ordinal);

    /// <summary>Local, Offline, Hybride & Cloud (Phase 10 §30-31).</summary>
    public static class Sync
    {
        public const string View = "sync.view";
        public const string Manage = "sync.manage";
        public const string Devices = "sync.devices";
    }

    public static class Offline
    {
        public const string Configure = "offline.configure";
    }

    /// <summary>Continuité, sauvegarde & reprise (Phase 36 §39, §46).</summary>
    public static class Continuity
    {
        public const string View = "continuity.view";
        public const string Manage = "continuity.manage";
        public const string Restore = "continuity.restore";
    }

    /// <summary>Sécurité, Audit, Conformité & Anti-fraude (Phase 11 §8, §41).</summary>
    public static class Security
    {
        public const string AuditView = "security.audit_view";
        public const string FraudReview = "security.fraud_review";
        public const string Approvals = "security.approvals";
        public const string Sessions = "security.sessions";
        public const string Configure = "security.configure";
        public const string ResetMfa = "security.reset_mfa";

        /// <summary>Accès d'urgence « break-glass » (Phase 47 §12) — jamais auto-approuvé.</summary>
        public const string RequestEmergencyAccess = "security.request_emergency_access";
        public const string ApproveEmergencyAccess = "security.approve_emergency_access";
    }

    /// <summary>Intégrations, Paiements & Écosystème (Phase 12 §5, §38 ; Phase 35 §13-14).</summary>
    public static class Integrations
    {
        public const string View = "integrations.view";
        public const string Configure = "integrations.configure";
        public const string Payments = "integrations.payments";
        public const string Webhooks = "integrations.webhooks";
        public const string Bank = "integrations.bank";
        public const string ManageApiKeys = "integrations.manage_api_keys";

        /// <summary>API, intégrations & écosystème — marketplace partenaires (Phase 46 §43-44).</summary>
        public const string ManageApps = "integrations.manage_apps";
    }

    /// <summary>Notifications, communication & automatisations (Phase 34 §22-23, §34).</summary>
    public static class Notifications
    {
        public const string ManageTemplates = "notifications.manage_templates";
        public const string ManageDelivery = "notifications.manage_delivery";
    }

    /// <summary>
    /// Qualité, exploitation & préparation production (Phase 37 §40, §51, §54) ; administration
    /// opérationnelle & maintenance (Phase 41 §31, §44 — jobs, files, purge, correction de données).
    /// </summary>
    public static class Operations
    {
        public const string View = "operations.view";
        public const string Manage = "operations.manage";
        public const string CorrectData = "operations.correct_data";
    }

    /// <summary>Migration, importation & onboarding avancé (Phase 39 §42-43).</summary>
    public static class Migration
    {
        public const string View = "migration.view";
        public const string Manage = "migration.manage";
    }

    /// <summary>Administration SaaS & monétisation (Phase 38 §35-36). Réservé aux rôles plateforme.</summary>
    public static class Platform
    {
        public const string View = "platform.view";
        public const string ManageCustomers = "platform.manage_customers";
        public const string ManageCatalogue = "platform.manage_catalogue";
        public const string ManageSubscriptions = "platform.manage_subscriptions";
        public const string ManageBilling = "platform.manage_billing";
        public const string RecordPayment = "platform.record_payment";
        public const string ManageLicenses = "platform.manage_licenses";
        public const string Support = "platform.support";
        public const string SupportAccess = "platform.support_access";
        public const string Security = "platform.security";

        /// <summary>Portail client self-service — restreint à sa propre organisation (§45-46).</summary>
        public const string Portal = "platform.portal";

        /// <summary>Curation et revue du catalogue marketplace (Phase 46 §41-42, §45).</summary>
        public const string ManageMarketplace = "platform.manage_marketplace";
    }
}
