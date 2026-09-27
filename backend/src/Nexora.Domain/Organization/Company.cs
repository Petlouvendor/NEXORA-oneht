using Nexora.Domain.Common;

namespace Nexora.Domain.Organization;

/// <summary>Entreprise — unité principale d'isolation des données (Phase 2 §4, Phase 3 §11).</summary>
public sealed class Company : AuditableEntity
{
    public Guid? GroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LegalName { get; set; }
    public string? TaxIdentifier { get; set; }
    public string Status { get; set; } = "active";
    public string MainActivityType { get; set; } = "other";
    public string ComplexityLevel { get; set; } = "SIMPLE";
    public string DeploymentMode { get; set; } = "LOCAL";
    public Guid DefaultCurrencyId { get; set; }
    public string Timezone { get; set; } = "America/Port-au-Prince";

    public BusinessGroup? Group { get; set; }
    public ICollection<Establishment> Establishments { get; set; } = new List<Establishment>();
}
