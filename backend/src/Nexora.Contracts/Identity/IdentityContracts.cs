namespace Nexora.Contracts.Identity;

// --- Utilisateurs & affectations (Phase 22 §5-6, §9, §40) ------------

public sealed record UserSummaryDto(
    Guid Id, string Email, string? FullName, string Status, DateTimeOffset? LastLoginAt, bool MfaEnabled);

public sealed record AssignmentDto(
    Guid Id, Guid UserId, string RoleName, string ScopeType,
    Guid? GroupId, Guid? CompanyId, Guid? EstablishmentId,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, string? Reason,
    bool Active, DateTimeOffset GrantedAt, DateTimeOffset? RevokedAt);

public sealed record GrantAssignmentRequest(
    string RoleName, string ScopeType, Guid? CompanyId, Guid? EstablishmentId,
    DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, string? Reason);

public sealed record RevokeAssignmentRequest(string Reason);

public sealed record SetUserStatusRequest(string Status);

// --- Périmètre effectif de l'utilisateur courant (Phase 22 §14-16, §39) ---

public sealed record GroupScopeDto(Guid Id, string Name);

public sealed record CompanyScopeDto(
    Guid Id, string Name, Guid? GroupId, IReadOnlyList<string> Roles, bool ReadOnly);

public sealed record ScopeTreeDto(
    IReadOnlyList<GroupScopeDto> Groups, IReadOnlyList<CompanyScopeDto> Companies);

// --- Comptes & mots de passe (recette Phase 55 : AUTH-004/007/008, RBAC-001) -----------------

/// <summary>Création d'un utilisateur avec sa première affectation — jamais un compte orphelin,
/// et l'affectation passe par le même garde anti-élévation que <c>POST /users/{id}/assignments</c>.</summary>
public sealed record CreateUserRequest(string Email, string FullName, string Password, GrantAssignmentRequest InitialAssignment);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Token, string NewPassword);
