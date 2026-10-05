using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Catalog;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Finance.Reports;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Administration;
using Nexora.Application.Group;
using Nexora.Application.Identity;
using Nexora.Application.Integrations;
using Nexora.Application.Ged;
using Nexora.Application.Intelligence;
using Nexora.Application.Marketplace;
using Nexora.Application.Mobile;
using Nexora.Application.Preferences;
using Nexora.Application.Reporting;
using Nexora.Application.Restaurant;
using Nexora.Application.Sales;
using Nexora.Application.Sectors;
using Nexora.Application.Security;
using Nexora.Application.Sync;
using Nexora.Infrastructure.Catalog;
using Nexora.Infrastructure.Finance;
using Nexora.Infrastructure.Administration;
using Nexora.Infrastructure.Group;
using Nexora.Infrastructure.Integrations;
using Nexora.Infrastructure.Ged;
using Nexora.Infrastructure.Intelligence;
using Nexora.Infrastructure.Inventory;
using Nexora.Infrastructure.Marketplace;
using Nexora.Infrastructure.Mobile;
using Nexora.Infrastructure.Preferences;
using Nexora.Infrastructure.Purchasing;
using Nexora.Infrastructure.Reporting;
using Nexora.Infrastructure.Restaurant;
using Nexora.Infrastructure.Sales;
using Nexora.Infrastructure.Sectors;
using Nexora.Infrastructure.Security;
using Nexora.Infrastructure.Sync;
using Nexora.Application.Experience;
using Nexora.Infrastructure.Experience;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Persistence;
using Nexora.Infrastructure.Scheduling;
using Nexora.Infrastructure.Tenancy;
using Nexora.Infrastructure.Time;

namespace Nexora.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var db = config.GetSection(NexoraDbOptions.SectionName).Get<NexoraDbOptions>() ?? new NexoraDbOptions();
        services.Configure<NexoraDbOptions>(config.GetSection(NexoraDbOptions.SectionName));
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));

        // Contexte de requête (scoped) — partagé par le middleware API et l'intercepteur RLS.
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());

        services.AddScoped<RlsConnectionInterceptor>();
        services.AddScoped<IBranchScopeResolver, Nexora.Infrastructure.Identity.SqlBranchScopeResolver>();

        // Chaîne réelle via configuration / secret manager (§18). Valeur de repli inerte
        // pour permettre le démarrage de l'hôte en test sans base.
        var connectionString = string.IsNullOrWhiteSpace(db.ConnectionString)
            ? "Host=localhost;Port=5432;Database=nexora;Username=nexora_app;Search Path=nexora,public"
            : db.ConnectionString;

        services.AddDbContext<NexoraDbContext>((sp, options) =>
        {
            options.UseNpgsql(connectionString, npgsql =>
            {
                // Pas de retry auto : les transactions sont gérées manuellement (UnitOfWork)
                // et l'idempotence est portée par l'identifiant d'opération (Phase 4 §33).
                npgsql.CommandTimeout(db.CommandTimeoutSeconds);
                npgsql.MigrationsHistoryTable("__ef_migrations_history_unused", "nexora");
            });
            options.AddInterceptors(sp.GetRequiredService<RlsConnectionInterceptor>());
        });

        // Assistant d'installation (résumé fonctionnel §20) — SetupState est enregistré par l'hôte.
        services.AddScoped<Nexora.Application.Setup.ISetupInstaller, Nexora.Infrastructure.Setup.SetupInstaller>();

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ICompanyRepository, CompanyRepository>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IUserAssignmentService, SqlUserAssignmentService>();
        services.AddScoped<Nexora.Application.Parties.ICustomerService, Nexora.Infrastructure.Parties.SqlCustomerService>();
        services.AddScoped<Nexora.Application.Parties.ISupplierService, Nexora.Infrastructure.Parties.SqlSupplierService>();
        services.AddScoped<Nexora.Application.Identity.IAccountService, SqlAccountService>();
        services.AddScoped<IScopeQueryService, SqlScopeQueryService>();
        services.AddScoped<IMfaService, SqlMfaService>();

        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<IPasswordHasher>(new BcryptPasswordHasher());
        services.AddScoped<ITokenService, JwtTokenService>();

        // Cache de révocation de session (Production Readiness — REQ-02).
        // Singleton : IMemoryCache est Singleton et les opérations Remove/Set sont thread-safe.
        services.AddMemoryCache();   // requis par SessionCacheInvalidator hors du serveur web (planificateur, tests)
        services.AddSingleton<ISessionCacheInvalidator, SessionCacheInvalidator>();

        // Moteur financier & comptable (Phase 4)
        services.AddScoped<IAccountResolver, EfAccountResolver>();
        services.AddScoped<ICompanyCurrencyContext, EfCompanyCurrencyContext>();
        services.AddScoped<IExchangeRateProvider, EfExchangeRateProvider>();
        services.AddScoped<IAccountingPeriods, EfAccountingPeriods>();
        services.AddScoped<IDocumentNumberGenerator, EfDocumentNumberGenerator>();
        services.AddScoped<IJournalEntryStore, EfJournalEntryStore>();
        services.AddScoped<IReceivablePayableStore, EfReceivablePayableStore>();
        services.AddScoped<IPaymentStore, EfPaymentStore>();
        services.AddScoped<IFinancialAccountStore, EfFinancialAccountStore>();
        services.AddScoped<IFinancialReports, SqlFinancialReports>();

        // Finance & trésorerie — cockpit de pilotage (Phase 28)
        services.AddScoped<Nexora.Application.Finance.Treasury.ITreasuryQueries, SqlTreasuryQueries>();
        services.AddScoped<Nexora.Application.Finance.Treasury.ISuspenseService, SqlSuspenseService>();
        services.AddScoped<Nexora.Application.Finance.Treasury.IFinancialCloseService, SqlFinancialCloseService>();

        // Comptabilité générale — workflow manuel & clôture d'exercice (Phase 29)
        services.AddScoped<Nexora.Application.Finance.Ledger.IChartQueries, SqlChartQueries>();
        services.AddScoped<Nexora.Application.Finance.Ledger.IAccountingControls, SqlAccountingControls>();
        services.AddScoped<Nexora.Application.Finance.Ledger.IManualJournalService, SqlManualJournalService>();
        services.AddScoped<Nexora.Application.Finance.Ledger.IFiscalYearService, SqlFiscalYearService>();

        // Catalogue, stocks & achats (Phase 5) — moteur de prix explicable (Phase 23)
        services.AddScoped<IStockLedger, EfStockLedger>();
        services.AddScoped<ICatalogStore, EfCatalogStore>();
        services.AddScoped<ICatalogPricingEngine, CatalogPricingEngine>();
        services.AddScoped<ICatalogReferenceService, Nexora.Infrastructure.Catalog.SqlCatalogReferenceService>();
        services.AddScoped<IUnitConverter, Nexora.Infrastructure.Catalog.SqlUnitConverter>();
        services.AddScoped<IPromotionService, Nexora.Infrastructure.Catalog.SqlPromotionService>();
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<IPurchasingStore, EfPurchasingStore>();
        services.AddScoped<IPurchasingQueries, SqlPurchasingQueries>();
        services.AddScoped<IInventoryOpsStore, EfInventoryOpsStore>();
        services.AddScoped<IInventoryDocumentQueries, Nexora.Infrastructure.Inventory.SqlInventoryDocumentQueries>();
        services.AddScoped<IPurchasingDocumentQueries, Nexora.Infrastructure.Purchasing.SqlPurchasingDocumentQueries>();
        services.AddScoped<ISupplierTermsService, Nexora.Infrastructure.Purchasing.SqlSupplierTermsService>();
        services.AddScoped<IReorderService, SqlReorderService>();

        // Stock, entrepôts, lots & inventaires (Phase 24)
        services.AddScoped<IStockAvailabilityPolicy, SqlStockAvailabilityPolicy>();
        services.AddScoped<ILotService, SqlLotService>();
        services.AddScoped<IStockQueries, SqlStockQueries>();
        services.AddScoped<IWarehouseService, SqlWarehouseService>();

        // Ventes & POS (Phase 6)
        services.AddScoped<ISalesPricing, SqlSalesPricing>();
        services.AddScoped<ISalePricingGuard, SqlSalePricingGuard>();
        services.AddScoped<IPosLimits, SqlPosLimits>();
        services.AddScoped<ICreditControl, SqlCreditControl>();
        services.AddScoped<ISalesStore, EfSalesStore>();
        services.AddScoped<ITenderOptionsQuery, Nexora.Infrastructure.Sales.SqlTenderOptionsQuery>();
        services.AddScoped<IPosOfflineService, Nexora.Infrastructure.Sales.SqlPosOfflineService>();
        services.AddScoped<Nexora.Application.Finance.ITreasuryManagementService, Nexora.Infrastructure.Finance.SqlTreasuryManagementService>();
        services.AddScoped<Nexora.Application.Finance.IExpenseService, Nexora.Infrastructure.Finance.SqlExpenseService>();
        services.AddScoped<Nexora.Application.Finance.Ledger.IChartManagementService, Nexora.Infrastructure.Finance.SqlChartManagementService>();
        services.AddScoped<ISalesReturnStore, EfSalesReturnStore>();
        // Guide fonctionnel développeur — validations Manager au POS (§10, §12, §13, §20).
        services.AddScoped<IPosApprovalNotifier, PosApprovalNotifier>();
        services.AddScoped<ICashSessionQueries, SqlCashSessionQueries>();
        services.AddScoped<IPosCatalogQueries, SqlPosCatalogQueries>();
        services.AddScoped<Nexora.Application.Finance.IExpenseRegister, Nexora.Infrastructure.Finance.SqlExpenseRegister>();
        services.AddScoped<IManagerOverrideService, ManagerOverrideService>();
        services.AddScoped<IReturnRequestService, SqlReturnRequestService>();
        services.AddScoped<IDiscountRequestService, SqlDiscountRequestService>();
        services.AddScoped<ICashSessionStore, EfCashSessionStore>();
        services.AddScoped<IRecipeExpansion, SqlRecipeExpansion>();
        services.AddScoped<IOrderStore, EfOrderStore>();
        services.AddScoped<ISalesQueries, SqlSalesQueries>();

        // Facturation & documents commerciaux (Phase 30)
        services.AddScoped<IQuoteStore, Nexora.Infrastructure.Documents.EfQuoteStore>();
        services.AddScoped<Nexora.Application.Documents.IDocumentService, Nexora.Infrastructure.Documents.SqlDocumentService>();
        services.AddScoped<Nexora.Application.Documents.IDocumentQueries, Nexora.Infrastructure.Documents.SqlDocumentQueries>();

        // Recouvrement & crédit client (Phase 27)
        services.AddScoped<Nexora.Application.Credit.ICollectionsStore, Nexora.Infrastructure.Credit.SqlCollectionsStore>();
        services.AddScoped<Nexora.Application.Credit.ICreditPortfolioQueries, Nexora.Infrastructure.Credit.SqlCreditPortfolioQueries>();

        // Module clients & comptes clients (V073) : versements, avoirs, remboursements, dépenses imputées,
        // recouvrement, journal financier client, rapports et alertes crédit.
        services.AddScoped<Nexora.Infrastructure.Customers.CustomerAccountNotifier>();
        services.AddScoped<Nexora.Application.Customers.ICustomerCreditMemoWriter, Nexora.Infrastructure.Customers.SqlCustomerCreditMemoWriter>();
        services.AddScoped<Nexora.Application.Customers.ICustomerAccountQueries, Nexora.Infrastructure.Customers.SqlCustomerAccountQueries>();
        services.AddScoped<Nexora.Application.Customers.ICustomerAccountService, Nexora.Infrastructure.Customers.SqlCustomerAccountService>();
        services.AddScoped<Nexora.Application.Customers.ICustomerRefundService, Nexora.Infrastructure.Customers.SqlCustomerRefundService>();
        services.AddScoped<Nexora.Application.Customers.ICustomerExpenseService, Nexora.Infrastructure.Customers.SqlCustomerExpenseService>();
        services.AddScoped<Nexora.Application.Customers.ICollectionActionService, Nexora.Infrastructure.Customers.SqlCollectionActionService>();
        services.AddScoped<Nexora.Application.Customers.ICustomerReports, Nexora.Infrastructure.Customers.SqlCustomerReports>();
        services.AddScoped<Nexora.Application.Customers.ICreditAlertScanner, Nexora.Infrastructure.Customers.SqlCreditAlertScanner>();
        services.AddScoped<Nexora.Application.Customers.ICreditOverrideService, Nexora.Infrastructure.Customers.SqlCreditOverrideService>();

        // Module paie, RH & avances sur salaire (V075-V076, docs/module-paie-rh-avances.md).
        services.AddScoped<Nexora.Infrastructure.Payroll.PayrollConfigurationReader>();
        services.AddScoped<Nexora.Infrastructure.Payroll.PayrollAccess>();
        services.AddScoped<Nexora.Infrastructure.Payroll.PayrollNotifier>();
        services.AddSingleton<Nexora.Infrastructure.Payroll.PersonalDataProtector>();
        services.AddScoped<Nexora.Infrastructure.Payroll.SqlEmployeeService>();
        services.AddScoped<Nexora.Application.Payroll.IEmployeeService>(sp => sp.GetRequiredService<Nexora.Infrastructure.Payroll.SqlEmployeeService>());
        services.AddScoped<Nexora.Application.Payroll.IHrTimeService, Nexora.Infrastructure.Payroll.SqlHrTimeService>();
        services.AddScoped<Nexora.Application.Payroll.IPayrollRuleService, Nexora.Infrastructure.Payroll.SqlPayrollRuleService>();
        services.AddScoped<Nexora.Application.Payroll.IPayrollService, Nexora.Infrastructure.Payroll.SqlPayrollService>();
        services.AddScoped<Nexora.Application.Payroll.ISalaryAdvanceService, Nexora.Infrastructure.Payroll.SqlSalaryAdvanceService>();
        services.AddScoped<Nexora.Application.Payroll.IExpenseClaimService, Nexora.Infrastructure.Payroll.SqlExpenseClaimService>();
        services.AddScoped<Nexora.Application.Payroll.IPayrollAdjustmentService, Nexora.Infrastructure.Payroll.SqlPayrollAdjustmentService>();
        services.AddScoped<Nexora.Application.Payroll.IHrSelfService, Nexora.Infrastructure.Payroll.SqlHrSelfService>();
        services.AddScoped<Nexora.Infrastructure.Payroll.SqlPayrollReports>();
        services.AddScoped<Nexora.Application.Payroll.IPayrollReports>(sp => sp.GetRequiredService<Nexora.Infrastructure.Payroll.SqlPayrollReports>());
        services.AddScoped<Nexora.Application.Payroll.IPayrollAlerts>(sp => sp.GetRequiredService<Nexora.Infrastructure.Payroll.SqlPayrollReports>());

        // Modules sectoriels (Phase 8)
        services.AddScoped<ISectorConfigService, SqlSectorConfigService>();
        services.AddScoped<ICustomFieldService, SqlCustomFieldService>();
        services.AddScoped<IRecipeService, SqlRecipeService>();
        services.AddScoped<IRestaurantService, EfRestaurantService>();

        // Multi-entreprises, Groupe & Consolidation (Phase 9)
        services.AddScoped<IGroupLedgerService, SqlGroupLedgerService>();
        services.AddScoped<IIntercompanyService, SqlIntercompanyService>();
        services.AddScoped<IConsolidationService, SqlConsolidationService>();
        services.AddScoped<IGroupControlCenter, SqlGroupControlCenter>();
        services.AddScoped<IGroupTaskService, SqlGroupTaskService>();
        services.AddScoped<ICompanyProvisioningService, SqlCompanyProvisioningService>();

        // Local, Offline, Hybride & Cloud (Phase 10)
        services.AddScoped<ISyncService, SqlSyncService>();
        services.AddScoped<IDeviceRegistryService, SqlDeviceRegistryService>();
        services.AddScoped<IOfflinePolicyService, SqlOfflinePolicyService>();

        // Sécurité, Audit, Conformité & Anti-fraude (Phase 11) — évènements de sécurité (Phase 33)
        services.AddScoped<ISecurityEventLog, SqlSecurityEventLog>();
        services.AddScoped<IAuditQueryService, SqlAuditQueryService>();
        services.AddScoped<IFraudDetectionService, SqlFraudDetectionService>();
        services.AddScoped<IApprovalWorkflowService, SqlApprovalWorkflowService>();
        services.AddScoped<IStepUpAuthService, SqlStepUpAuthService>();
        services.AddScoped<ISessionSecurityService, SqlSessionSecurityService>();
        services.AddScoped<ISecurityPolicyService, SqlSecurityPolicyService>();

        // Sécurité, IAM & Conformité (Phase 47)
        services.AddSingleton<IMfaSecretProtector, AesGcmMfaSecretProtector>();
        services.AddScoped<ISecurityFindingService, SqlSecurityFindingService>();
        services.AddScoped<ISecurityCertificateService, SqlSecurityCertificateService>();
        services.AddScoped<IComplianceRegistryService, SqlComplianceRegistryService>();
        services.AddScoped<IEmergencyAccessService, SqlEmergencyAccessService>();
        services.AddScoped<ISecurityScoreService, SqlSecurityScoreService>();

        // UX/UI, Design System & Expérience Premium (Phase 13)
        services.AddScoped<IUserPreferenceService, SqlUserPreferenceService>();
        services.AddScoped<ICompanyBrandingService, SqlCompanyBrandingService>();
        services.AddScoped<IDashboardLayoutService, SqlDashboardLayoutService>();
        services.AddScoped<ISavedFilterService, SqlSavedFilterService>();
        services.AddScoped<IShortcutService, SqlShortcutService>();
        services.AddSingleton<IDesignSystemService, DesignSystemService>();

        // Notifications, communication & automatisations (Phase 34)
        services.AddScoped<Nexora.Infrastructure.Notifications.SqlMessageTemplateService>();
        services.AddScoped<Nexora.Application.Notifications.IMessageTemplateService>(
            sp => sp.GetRequiredService<Nexora.Infrastructure.Notifications.SqlMessageTemplateService>());
        services.AddScoped<Nexora.Application.Notifications.INotificationPreferenceService,
            Nexora.Infrastructure.Notifications.SqlNotificationPreferenceService>();
        services.AddScoped<Nexora.Application.Notifications.INotificationService,
            Nexora.Infrastructure.Notifications.SqlNotificationService>();
        services.AddScoped<Nexora.Application.Notifications.INotificationDispatcher,
            Nexora.Infrastructure.Notifications.NotificationDispatcher>();

        // --- Canaux de notification (Phase 34 §3) ----------------------------------------
        // Options lues depuis la configuration (appsettings / secrets / variables d'env).
        services.Configure<Nexora.Infrastructure.Notifications.SmtpOptions>(
            config.GetSection(Nexora.Infrastructure.Notifications.SmtpOptions.SectionName));
        services.Configure<Nexora.Infrastructure.Notifications.TwilioOptions>(
            config.GetSection(Nexora.Infrastructure.Notifications.TwilioOptions.SectionName));
        services.Configure<Nexora.Infrastructure.Notifications.FcmOptions>(
            config.GetSection(Nexora.Infrastructure.Notifications.FcmOptions.SectionName));

        // Email : SmtpEmailChannel si Notifications:Smtp:Enabled=true, sinon log seulement.
        // Les deux sont enregistrés ; SmtpEmailChannel.SendAsync retourne Fail quand désactivé,
        // ce qui place le message en dead-letter immédiatement — comportement explicite et traçable.
        services.AddScoped<Nexora.Application.Notifications.INotificationChannel,
            Nexora.Infrastructure.Notifications.SmtpEmailChannel>();

        // SMS : TwilioSmsChannel (Fail si Notifications:Twilio:Enabled=false).
        services.AddScoped<Nexora.Application.Notifications.INotificationChannel,
            Nexora.Infrastructure.Notifications.TwilioSmsChannel>();

        // Canaux sans fournisseur intégré (webhook de notification, WhatsApp) : « non configuré », jamais « envoyé ».
        // Les webhooks sortants de l'API (Phase 12) passent par HttpOutboundWebhookDispatcher, pas par ce canal.
        services.AddSingleton<Nexora.Application.Notifications.INotificationChannel>(
            new Nexora.Infrastructure.Notifications.UnavailableChannel("webhook", "webhook de notification"));
        services.AddSingleton<Nexora.Application.Notifications.INotificationChannel>(
            new Nexora.Infrastructure.Notifications.UnavailableChannel("whatsapp", "WhatsApp"));
        services.AddScoped<Nexora.Application.Notifications.INotificationDeliveryAdmin,
            Nexora.Infrastructure.Notifications.SqlNotificationDeliveryAdmin>();

        // Push : FcmPushChannel (Singleton car FirebaseApp est un objet global process-unique).
        // Fail si Notifications:Fcm:Enabled=false.
        services.AddSingleton<Nexora.Infrastructure.Notifications.FcmPushChannel>();
        services.AddSingleton<Nexora.Application.Notifications.INotificationChannel>(
            sp => sp.GetRequiredService<Nexora.Infrastructure.Notifications.FcmPushChannel>());

        // Planificateur de traitements asynchrones (Phase 16 §22) — activé par configuration
        services.Configure<SchedulerOptions>(config.GetSection(SchedulerOptions.SectionName));
        services.AddScoped<SchedulerRunner>();
        // Advisory lock distribué (Production Readiness — REQ-01) : coordonne les instances.
        services.AddSingleton<PostgresAdvisoryLock>();

        // Administration, Paramétrage & Automatisation (Phase 14)
        services.AddScoped<ISettingsService, SqlSettingsService>();
        services.AddScoped<ICustomRoleService, SqlCustomRoleService>();
        services.AddScoped<IWorkflowService, SqlWorkflowService>();
        services.AddScoped<IAutomationService, SqlAutomationService>();
        services.AddScoped<IConfigVersionService, SqlConfigVersionService>();
        services.AddScoped<IAdminTaskService, SqlAdminTaskService>();

        // Configuration & adaptabilité — profils sectoriels & transfert (Phase 32)
        services.AddScoped<Nexora.Application.Configuration.IConfigGuardrails, Nexora.Infrastructure.Configuration.SqlConfigGuardrails>();
        services.AddScoped<Nexora.Application.Configuration.ISectorProfileService, Nexora.Infrastructure.Configuration.SqlSectorProfileService>();
        services.AddScoped<Nexora.Application.Configuration.IConfigTransferService, Nexora.Infrastructure.Configuration.SqlConfigTransferService>();

        // Intégrations, Paiements & Écosystème (Phase 12)
        services.AddSingleton<IOutboundWebhookDispatcher, HttpOutboundWebhookDispatcher>();
        // Fournisseurs de paiement (finalisation phase 15) : simulation, MonCash, carte — transport HTTP substituable.
        services.AddSingleton(sp => new ProviderHttp(sp.GetRequiredService<IConfiguration>(), sp.GetService<PaymentProviderHandler>()));
        services.AddSingleton<IPaymentProvider, SimulatedPaymentProvider>();
        services.AddSingleton<IPaymentProvider, MonCashPaymentProvider>();
        services.AddSingleton<IPaymentProvider, CardPaymentProvider>();
        services.AddSingleton<IPaymentProviderRegistry, PaymentProviderRegistry>();
        services.AddScoped<IIntegrationRegistry, SqlIntegrationRegistry>();
        services.AddScoped<IOutboundWebhookService, SqlOutboundWebhookService>();
        services.AddScoped<IPaymentGateway, SqlPaymentGateway>();
        services.AddScoped<IWebhookIngestService, SqlWebhookIngestService>();
        services.AddScoped<IPaymentReconciliationService, SqlPaymentReconciliationService>();
        services.AddScoped<IBankReconciliationService, SqlBankReconciliationService>();
        services.AddScoped<IIntegrationCenter, SqlIntegrationCenter>();

        // Plateforme ouverte — clés d'API (Phase 35 §13-14)
        services.AddScoped<Nexora.Application.Integrations.IApiKeyService,
            Nexora.Infrastructure.Integrations.SqlApiKeyService>();
        services.AddScoped<Nexora.Application.Integrations.IApiKeyAuthenticator,
            Nexora.Infrastructure.Integrations.SqlApiKeyAuthenticator>();

        // Continuité, sauvegarde & reprise (Phase 36)
        services.AddScoped<Nexora.Application.Continuity.IContinuityService,
            Nexora.Infrastructure.Continuity.SqlContinuityService>();

        // Qualité, exploitation & préparation production (Phase 37)
        services.AddScoped<Nexora.Application.Operations.IOperationsService,
            Nexora.Infrastructure.Operations.SqlOperationsService>();
        services.AddScoped<Nexora.Application.Operations.IPlatformMetrics,
            Nexora.Infrastructure.Operations.SqlPlatformMetrics>();

        // Administration opérationnelle & maintenance (Phase 41)
        services.AddScoped<Nexora.Application.Operations.IJobRegistry, Nexora.Infrastructure.Operations.SqlJobRegistry>();
        services.AddScoped<Nexora.Application.Operations.IQueueAdminService, Nexora.Infrastructure.Operations.SqlQueueAdminService>();
        services.AddScoped<Nexora.Application.Operations.IOpsDiagnosticsService, Nexora.Infrastructure.Operations.SqlOpsDiagnosticsService>();
        services.AddScoped<Nexora.Application.Operations.IFeatureFlagService, Nexora.Infrastructure.Operations.SqlFeatureFlagService>();
        services.AddScoped<Nexora.Application.Operations.IMaintenanceService, Nexora.Infrastructure.Operations.SqlMaintenanceService>();
        services.AddScoped<Nexora.Application.Operations.IRetentionPurgeService, Nexora.Infrastructure.Operations.SqlRetentionPurgeService>();
        services.AddScoped<Nexora.Application.Operations.IDataCorrectionService, Nexora.Infrastructure.Operations.SqlDataCorrectionService>();

        // Migration, importation & onboarding avancé (Phase 39)
        services.AddScoped<Nexora.Application.Migration.IEntityImporter, Nexora.Infrastructure.Migration.CustomerImporter>();
        services.AddScoped<Nexora.Application.Migration.IEntityImporter, Nexora.Infrastructure.Migration.SupplierImporter>();
        services.AddScoped<Nexora.Application.Migration.IEntityImporter, Nexora.Infrastructure.Migration.ProductImporter>();
        services.AddScoped<Nexora.Application.Migration.IEntityImporter, Nexora.Infrastructure.Migration.OpeningStockImporter>();
        services.AddScoped<Nexora.Application.Migration.IImportService, Nexora.Infrastructure.Migration.SqlImportService>();
        services.AddScoped<Nexora.Application.Migration.IOnboardingService, Nexora.Infrastructure.Migration.SqlOnboardingService>();

        // Administration SaaS & monétisation (Phase 38)
        services.AddScoped<Nexora.Application.Platform.IPlatformScope, Nexora.Infrastructure.Platform.PlatformScope>();
        services.AddScoped<Nexora.Application.Platform.IPlatformCatalogService, Nexora.Infrastructure.Platform.SqlPlatformCatalogService>();
        services.AddScoped<Nexora.Application.Platform.ICustomerAccountService, Nexora.Infrastructure.Platform.SqlCustomerAccountService>();
        services.AddScoped<Nexora.Application.Platform.ISubscriptionService, Nexora.Infrastructure.Platform.SqlSubscriptionService>();
        services.AddScoped<Nexora.Application.Platform.IPlatformBillingService, Nexora.Infrastructure.Platform.SqlPlatformBillingService>();
        services.AddScoped<Nexora.Application.Platform.IQuotaService, Nexora.Infrastructure.Platform.SqlQuotaService>();
        services.AddScoped<Nexora.Application.Platform.ILicenseService, Nexora.Infrastructure.Platform.LicenseService>();
        services.AddScoped<Nexora.Application.Platform.ISupportDeskService, Nexora.Infrastructure.Platform.SqlSupportDeskService>();
        services.AddScoped<Nexora.Application.Platform.ISaasMetricsService, Nexora.Infrastructure.Platform.SqlSaasMetricsService>();

        // Dashboard, Rapports & Pilotage (Phase 7)
        services.AddScoped<IAlertCenter, SqlAlertCenter>();
        services.AddScoped<IKpiService, SqlKpiService>();
        services.AddScoped<ISalesReports, SqlSalesReports>();
        services.AddScoped<IStockReports, SqlStockReports>();
        services.AddScoped<IAgingReports, SqlAgingReports>();
        services.AddScoped<IBudgetReports, SqlBudgetReports>();
        services.AddScoped<IDrillDown, SqlDrillDown>();
        services.AddScoped<IExportService, ReportExportService>();

        // Impression & export (reçus clients/fournisseurs, vrais .xlsx et PDF)
        services.AddSingleton<Nexora.Application.Printing.IDocumentRenderer, Nexora.Infrastructure.Printing.DocumentRenderer>();
        services.AddScoped<Nexora.Application.Printing.IPrintService, Nexora.Infrastructure.Printing.SqlPrintService>();
        services.AddScoped<Nexora.Application.Printing.IReceiptService, Nexora.Infrastructure.Printing.SqlReceiptService>();
        services.AddScoped<IAskNexora, RuleBasedAskNexora>();

        // Reporting & analytics — couche de confiance (Phase 31)
        services.AddScoped<IKpiReconciliation, SqlKpiReconciliation>();
        services.AddScoped<IDataQualityService, SqlDataQualityService>();
        services.AddScoped<IAnalyticsSnapshotService, SqlAnalyticsSnapshotService>();

        // Business Intelligence, Reporting & Pilotage (Phase 42)
        services.AddScoped<IKpiGovernanceService, SqlKpiGovernanceService>();
        services.AddScoped<IObjectiveService, SqlObjectiveService>();
        services.AddScoped<IReportCatalogService, SqlReportCatalogService>();
        services.AddScoped<IReportScheduleService, SqlReportScheduleService>();
        services.AddScoped<IForecastService, SqlForecastService>();
        services.AddScoped<IInsightService, SqlInsightService>();
        services.AddScoped<IBudgetService, SqlBudgetService>();

        // Intelligence métier & recommandations (Phase 43)
        services.AddScoped<IRecommendationService, SqlRecommendationService>();
        services.AddScoped<IIntelligenceRuleService, SqlIntelligenceRuleService>();
        services.AddScoped<IScoringService, SqlScoringService>();
        services.AddScoped<IAssistantService, SqlAssistantService>();
        services.AddScoped<ITaskService, SqlTaskService>();
        services.AddScoped<IIntelligenceAuditQuery, SqlIntelligenceAuditQuery>();

        // NEXORA ONE — recherche globale Ctrl+K & fil d'activité (docs/nexora-one-experience-intelligente.md)
        services.AddScoped<IGlobalSearchService, GlobalSearchService>();
        services.AddScoped<IActivityFeedService, ActivityFeedService>();

        // Mobile, terrain & multi-appareils (Phase 44)
        services.AddScoped<IAttachmentService, SqlAttachmentService>();
        services.AddScoped<IDeliveryService, SqlDeliveryService>();
        services.AddScoped<IFieldInterventionService, SqlFieldInterventionService>();
        services.AddScoped<ICrashReportService, SqlCrashReportService>();
        services.AddScoped<IMobileBootstrapService, SqlMobileBootstrapService>();

        // GED, documents, archivage & signature (Phase 45)
        services.AddScoped<IGedService, SqlGedService>();
        services.AddScoped<ISignatureService, SqlSignatureService>();
        services.AddScoped<IDocumentOcrService, SqlDocumentOcrService>();
        services.AddScoped<IDocumentRetentionService, SqlDocumentRetentionService>();
        services.AddScoped<IGedAuditQuery, SqlGedAuditQuery>();

        // API, intégrations & écosystème — marketplace partenaires (Phase 46)
        services.AddScoped<IMarketplaceCatalogService, SqlMarketplaceCatalogService>();
        services.AddScoped<IAppInstallationService, SqlAppInstallationService>();
        services.AddScoped<IDeveloperUsageQuery, SqlDeveloperUsageQuery>();
        services.AddScoped<IApiKeyUsageRecorder, SqlApiKeyUsageRecorder>();

        return services;
    }
}
