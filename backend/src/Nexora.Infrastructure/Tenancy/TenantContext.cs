using Nexora.Application.Abstractions;

namespace Nexora.Infrastructure.Tenancy;

/// <summary>
/// Détenteur scoped du contexte de requête. Immuable côté consommateurs métier ;
/// seul le middleware API (ou une portée système) le renseigne.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    private readonly HashSet<Guid> _companyIds = [];
    private readonly HashSet<Guid> _groupIds = [];
    private int _systemDepth;

    public Guid? UserId { get; private set; }
    public IReadOnlyCollection<Guid> CompanyIds => _companyIds;
    public IReadOnlyCollection<Guid> GroupIds => _groupIds;
    public Guid? ActiveCompanyId { get; private set; }
    public bool BypassRls => _systemDepth > 0;
    public IReadOnlyCollection<string>? ApiScopes { get; private set; }
    public IReadOnlyCollection<Guid>? EstablishmentIds { get; private set; }

    public void SetEstablishments(IEnumerable<Guid>? establishmentIds)
        => EstablishmentIds = establishmentIds is null ? null : establishmentIds.Distinct().ToArray();

    public void SetUser(Guid userId) => UserId = userId;

    public void SetApiScopes(IEnumerable<string>? scopes)
        => ApiScopes = scopes is null ? null : new HashSet<string>(scopes, StringComparer.Ordinal);

    public void SetScope(IEnumerable<Guid> companyIds, IEnumerable<Guid> groupIds, Guid? activeCompanyId)
    {
        _companyIds.Clear();
        _groupIds.Clear();
        foreach (var c in companyIds) _companyIds.Add(c);
        foreach (var g in groupIds) _groupIds.Add(g);
        ActiveCompanyId = activeCompanyId;
    }

    public IDisposable BeginSystemScope()
    {
        _systemDepth++;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            owner._systemDepth--;
        }
    }
}
