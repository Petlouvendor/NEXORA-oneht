using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Catalog;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Periods;
using Nexora.Application.Finance.Posting;
using Nexora.Application.Finance.Reversals;
using Nexora.Application.Inventory;
using Nexora.Application.Purchasing;
using Nexora.Application.Sales;
using Nexora.Application.UseCases.Auth;
using Nexora.Application.UseCases.Organization;

namespace Nexora.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<LoginHandler>();
        services.AddScoped<MfaVerifyHandler>();
        services.AddScoped<AccessTokenIssuer>();
        services.AddScoped<ListCompaniesHandler>();

        // Moteur financier & comptable (Phase 4)
        services.AddSingleton<IPostingRuleSet, PostingRuleSet>();
        services.AddScoped<IFinancialEngine, FinancialEngine>();
        services.AddScoped<IFinanceOperations, FinanceOperations>();
        services.AddScoped<IAccountingPeriodService, AccountingPeriodService>();
        services.AddScoped<IReversalService, ReversalService>();

        // Catalogue, stocks & achats (Phase 5)
        services.AddScoped<CatalogService>();
        services.AddScoped<PurchasingService>();
        services.AddScoped<InventoryOperationsService>();

        // Ventes & POS (Phase 6) — commandes clients (Phase 25)
        services.AddScoped<SalesService>();
        services.AddScoped<SalesReturnService>();
        services.AddScoped<CashSessionService>();
        services.AddScoped<OrderService>();
        services.AddScoped<QuoteService>();

        // Recouvrement & crédit client (Phase 27)
        services.AddScoped<Credit.CollectionsService>();

        return services;
    }
}
