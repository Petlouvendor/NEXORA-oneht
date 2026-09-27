using Nexora.Domain.Common;

namespace Nexora.Domain.Organization;

/// <summary>Établissement — table nexora.establishment (Phase 2 §5).</summary>
public sealed class Establishment : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string Status { get; set; } = "active";

    public Company? Company { get; set; }
}
