using Nexora.Domain.Common;

namespace Nexora.Domain.Identity;

public sealed class Role : AuditableEntity
{
    public Guid? CompanyId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool SystemRole { get; set; }
    public string Status { get; set; } = "active";

    public ICollection<Permission> Permissions { get; set; } = new List<Permission>();
}

public sealed class Permission : Entity
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Module { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
}
