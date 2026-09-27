using Nexora.Application.Abstractions;
using Nexora.Contracts.Organization;

namespace Nexora.Application.UseCases.Organization;

/// <summary>
/// Liste les entreprises visibles dans le périmètre courant.
/// L'isolation est appliquée par PostgreSQL (RLS) via le contexte de session,
/// pas par un filtre applicatif seul (Phase 3 §11).
/// </summary>
public sealed class ListCompaniesHandler(ICompanyRepository companies)
{
    public async Task<IReadOnlyList<CompanyResponse>> HandleAsync(CancellationToken ct = default)
    {
        var rows = await companies.ListForCurrentScopeAsync(ct);
        return rows
            .Select(c => new CompanyResponse(
                c.Id, c.GroupId, c.Name, c.LegalName, c.Status,
                c.MainActivityType, c.ComplexityLevel, c.DeploymentMode, c.Timezone, c.DefaultCurrencyId))
            .ToArray();
    }
}
