using Microsoft.EntityFrameworkCore;
using Nexora.Domain.Accounting;
using Nexora.Domain.Catalog;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;
using Nexora.Domain.Identity;
using Nexora.Domain.Inventory;
using Nexora.Domain.Organization;
using Nexora.Domain.Purchasing;
using Nexora.Domain.Sales;

namespace Nexora.Infrastructure.Persistence;

/// <summary>
/// Contexte EF Core en mode « base d'abord » : le schéma est la propriété des migrations SQL
/// de <c>database/migrations</c> (Phase 2). Aucune migration EF n'est générée ici (Phase 3 §14 :
/// séparation migration / runtime).
/// </summary>
public sealed partial class NexoraDbContext(DbContextOptions<NexoraDbContext> options) : DbContext(options)
{
    public DbSet<BusinessGroup> BusinessGroups => Set<BusinessGroup>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Establishment> Establishments => Set<Establishment>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<AccessScope> AccessScopes => Set<AccessScope>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();

    // --- Moteur financier & comptable (Phase 4) ---
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountMapping> AccountMappings => Set<AccountMapping>();
    public DbSet<Journal> Journals => Set<Journal>();
    public DbSet<AccountingPeriod> AccountingPeriods => Set<AccountingPeriod>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalEntryLine> JournalEntryLines => Set<JournalEntryLine>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    public DbSet<CustomerReceivable> Receivables => Set<CustomerReceivable>();
    public DbSet<SupplierPayable> Payables => Set<SupplierPayable>();
    public DbSet<FinancialAccount> FinancialAccounts => Set<FinancialAccount>();
    public DbSet<FinancialTransaction> FinancialTransactions => Set<FinancialTransaction>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    // --- Catalogue / Stocks / Achats (Phase 5) ---
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductPrice> ProductPrices => Set<ProductPrice>();
    public DbSet<PriceList> PriceLists => Set<PriceList>();
    public DbSet<ProductSupplier> ProductSuppliers => Set<ProductSupplier>();
    public DbSet<ProductBarcode> ProductBarcodes => Set<ProductBarcode>();
    public DbSet<UnitConversion> UnitConversions => Set<UnitConversion>();
    public DbSet<ProductReorderRule> ReorderRules => Set<ProductReorderRule>();
    public DbSet<ProductCostHistory> CostHistory => Set<ProductCostHistory>();
    public DbSet<InventoryLocation> InventoryLocations => Set<InventoryLocation>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<Lot> Lots => Set<Lot>();
    public DbSet<LotStock> LotStocks => Set<LotStock>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<InventoryCount> InventoryCounts => Set<InventoryCount>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<PurchaseRequest> PurchaseRequests => Set<PurchaseRequest>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<SupplierReturn> SupplierReturns => Set<SupplierReturn>();

    // --- Ventes & POS (Phase 6) ---
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<SaleLine> SaleLines => Set<SaleLine>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderLine> SalesOrderLines => Set<SalesOrderLine>();
    public DbSet<SalePayment> SalePayments => Set<SalePayment>();
    public DbSet<SalePromotion> SalePromotions => Set<SalePromotion>();
    public DbSet<SaleReturn> SaleReturns => Set<SaleReturn>();
    public DbSet<CashRegister> CashRegisters => Set<CashRegister>();
    public DbSet<CashSession> CashSessions => Set<CashSession>();
    public DbSet<CashMovement> CashMovements => Set<CashMovement>();

    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteLine> QuoteLines => Set<QuoteLine>();

    partial void ConfigureFinance(ModelBuilder b);
    partial void ConfigureCatalog(ModelBuilder b);
    partial void ConfigureSales(ModelBuilder b);
    partial void ConfigureDocuments(ModelBuilder b);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("nexora");

        b.Entity<BusinessGroup>(e =>
        {
            e.ToTable("business_group");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.LegalName).HasColumnName("legal_name");
            e.Property(x => x.TaxIdentifier).HasColumnName("tax_identifier");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.DefaultCurrencyId).HasColumnName("default_currency_id");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.HasMany(x => x.Companies).WithOne(x => x.Group!).HasForeignKey(x => x.GroupId);
        });

        b.Entity<Company>(e =>
        {
            e.ToTable("company");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.LegalName).HasColumnName("legal_name");
            e.Property(x => x.TaxIdentifier).HasColumnName("tax_identifier");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.MainActivityType).HasColumnName("main_activity_type");
            e.Property(x => x.ComplexityLevel).HasColumnName("complexity_level");
            e.Property(x => x.DeploymentMode).HasColumnName("deployment_mode");
            e.Property(x => x.DefaultCurrencyId).HasColumnName("default_currency_id");
            e.Property(x => x.Timezone).HasColumnName("timezone");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.HasMany(x => x.Establishments).WithOne(x => x.Company!).HasForeignKey(x => x.CompanyId);
        });

        b.Entity<Establishment>(e =>
        {
            e.ToTable("establishment");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Address).HasColumnName("address");
            e.Property(x => x.Phone).HasColumnName("phone");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
        });

        b.Entity<AppUser>(e =>
        {
            e.ToTable("app_user");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Email).HasColumnName("email");
            e.Property(x => x.Username).HasColumnName("username");
            e.Property(x => x.PasswordHash).HasColumnName("password_hash");
            e.Property(x => x.ExternalIdentity).HasColumnName("external_identity");
            e.Property(x => x.FullName).HasColumnName("full_name");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.MfaEnabled).HasColumnName("mfa_enabled");
            e.Property(x => x.MfaSecret).HasColumnName("mfa_secret");
            e.Property(x => x.MfaPendingSecret).HasColumnName("mfa_pending_secret");
            e.Property(x => x.MfaEnrolledAt).HasColumnName("mfa_enrolled_at");
            e.Property(x => x.FailedLoginCount).HasColumnName("failed_login_count");
            e.Property(x => x.LockedUntil).HasColumnName("locked_until");
            e.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.HasMany(x => x.UserRoles).WithOne(x => x.User!).HasForeignKey(x => x.UserId);
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("role");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.SystemRole).HasColumnName("system_role");
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAddOrUpdate();
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at").ValueGeneratedOnAddOrUpdate();
            e.HasMany(x => x.Permissions).WithMany()
                .UsingEntity(
                    "role_permission",
                    l => l.HasOne(typeof(Permission)).WithMany().HasForeignKey("permission_id").HasPrincipalKey(nameof(Permission.Id)),
                    r => r.HasOne(typeof(Role)).WithMany().HasForeignKey("role_id").HasPrincipalKey(nameof(Role.Id)),
                    j => j.ToTable("role_permission"));
        });

        b.Entity<Permission>(e =>
        {
            e.ToTable("permission");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Code).HasColumnName("code");
            e.Property(x => x.Name).HasColumnName("name");
            e.Property(x => x.Description).HasColumnName("description");
            e.Property(x => x.Module).HasColumnName("module");
            e.Property(x => x.Action).HasColumnName("action");
        });

        b.Entity<AccessScope>(e =>
        {
            e.ToTable("access_scope");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.ScopeTypeValue).HasColumnName("scope_type");
            e.Property(x => x.GroupId).HasColumnName("group_id");
            e.Property(x => x.CompanyId).HasColumnName("company_id");
            e.Property(x => x.EstablishmentId).HasColumnName("establishment_id");
            e.Property(x => x.StoreId).HasColumnName("store_id");
            e.Property(x => x.WarehouseId).HasColumnName("warehouse_id");
            e.Property(x => x.PosTerminalId).HasColumnName("pos_terminal_id");
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("user_role");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.RoleId).HasColumnName("role_id");
            e.Property(x => x.ScopeId).HasColumnName("scope_id");
            e.Property(x => x.GrantedAt).HasColumnName("granted_at");
            e.Property(x => x.ValidFrom).HasColumnName("valid_from");
            e.Property(x => x.ValidUntil).HasColumnName("valid_until");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            e.Property(x => x.RevokedBy).HasColumnName("revoked_by");
            e.HasOne(x => x.Role!).WithMany().HasForeignKey(x => x.RoleId);
            e.HasOne(x => x.Scope!).WithMany().HasForeignKey(x => x.ScopeId);
        });

        ConfigureFinance(b);
        ConfigureCatalog(b);
        ConfigureSales(b);
        ConfigureDocuments(b);
    }
}
