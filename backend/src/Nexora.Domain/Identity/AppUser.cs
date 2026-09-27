using Nexora.Domain.Common;

namespace Nexora.Domain.Identity;

/// <summary>Utilisateur plateforme — table nexora.app_user (Phase 2 §6, Phase 3 §9).</summary>
public sealed class AppUser : AuditableEntity
{
    public string Email { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? PasswordHash { get; set; }
    public string? ExternalIdentity { get; set; }
    public string? FullName { get; set; }
    public string Status { get; set; } = "active";
    public bool MfaEnabled { get; set; }
    public string? MfaSecret { get; set; }
    public string? MfaPendingSecret { get; set; }
    public DateTimeOffset? MfaEnrolledAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    public bool IsActive => Status == "active";
}
