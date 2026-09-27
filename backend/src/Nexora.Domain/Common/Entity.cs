namespace Nexora.Domain.Common;

/// <summary>Base pour toute entité persistée (clé primaire uuid, cf. Phase 2 §3).</summary>
public abstract class Entity
{
    public Guid Id { get; set; }
}

/// <summary>Entité horodatée (created_at / updated_at maintenus par la base).</summary>
public abstract class AuditableEntity : Entity
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
