using Nexora.Contracts.Identity;
using Nexora.Domain.Common;

namespace Nexora.Application.Identity;

/// <summary>
/// Administration des utilisateurs et de leurs affectations (Phase 22 §9, §12-13, §32-33, §40).
/// Une affectation = le droit d'exercer un rôle dans un périmètre, éventuellement borné dans le
/// temps (accès temporaire) et révocable. Réservé à `admin.manage_users`, borné au périmètre
/// de l'appelant.
/// </summary>
public interface IUserAssignmentService
{
    Task<IReadOnlyList<UserSummaryDto>> ListUsersAsync(CancellationToken ct = default);
    Task<Result<IReadOnlyList<AssignmentDto>>> ListAssignmentsAsync(Guid userId, CancellationToken ct = default);
    Task<Result<Guid>> GrantAsync(Guid userId, GrantAssignmentRequest request, CancellationToken ct = default);
    Task<Result> RevokeAsync(Guid assignmentId, RevokeAssignmentRequest request, CancellationToken ct = default);
    Task<Result> SetStatusAsync(Guid userId, SetUserStatusRequest request, CancellationToken ct = default);
    /// <summary>Recette Phase 55 RBAC-001 : création d'un compte AVEC sa première affectation, atomiquement.</summary>
    Task<Result<Guid>> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default);
}

/// <summary>Périmètre effectif de l'utilisateur courant — alimente le sélecteur d'entreprise (§14-16, §39).</summary>
public interface IScopeQueryService
{
    Task<ScopeTreeDto> MyScopesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> MyPermissionsAsync(CancellationToken ct = default);
}
