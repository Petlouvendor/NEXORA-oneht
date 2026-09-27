using Nexora.Domain.Common;

namespace Nexora.Domain.Organization;

/// <summary>Groupe d'entreprises — table nexora.business_group (Phase 2 §4).</summary>
public sealed class BusinessGroup : AuditableEntity
{
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? TaxIdentifier { get; set; }
    public string Status { get; set; } = "active";
    public Guid DefaultCurrencyId { get; set; }

    public ICollection<Company> Companies { get; set; } = new List<Company>();
}
