using Nexora.Domain.Common;

namespace Nexora.Domain.Identity;

public static class ScopeType
{
    public const string Platform = "PLATFORM";
    public const string Group = "GROUP";
    public const string Company = "COMPANY";
    public const string Establishment = "ESTABLISHMENT";
    public const string Store = "STORE";
    public const string Warehouse = "WAREHOUSE";
    public const string PosTerminal = "POS_TERMINAL";
}

/// <summary>Périmètre d'accès — table nexora.access_scope (Phase 2 §6, Phase 3 §10-12).</summary>
public sealed class AccessScope : Entity
{
    public string ScopeTypeValue { get; set; } = ScopeType.Company;
    public Guid? GroupId { get; set; }
    public Guid? CompanyId { get; set; }
    public Guid? EstablishmentId { get; set; }
    public Guid? StoreId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? PosTerminalId { get; set; }
}

/// <summary>
/// Affectation Utilisateur → Rôle → Périmètre — table nexora.user_role.
/// Peut être bornée dans le temps (accès temporaire, Phase 22 §9, §32) et révocable.
/// </summary>
public sealed class UserRole : Entity
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid ScopeId { get; set; }

    public DateTimeOffset GrantedAt { get; set; }
    public DateTimeOffset? ValidFrom { get; set; }
    public DateTimeOffset? ValidUntil { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }

    public AppUser? User { get; set; }
    public Role? Role { get; set; }
    public AccessScope? Scope { get; set; }

    /// <summary>L'affectation est-elle active à l'instant <paramref name="at"/> ?</summary>
    public bool IsActiveAt(DateTimeOffset at) =>
        RevokedAt is null
        && (ValidFrom is null || ValidFrom <= at)
        && (ValidUntil is null || ValidUntil > at);
}
