using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Identity;
using Nexora.Application.Security;
using Nexora.Contracts.Identity;

namespace Nexora.Api.Controllers;

/// <summary>
/// Administration des utilisateurs et de leurs affectations (Phase 22 §40). Une affectation lie
/// un rôle à un périmètre ; elle peut être bornée dans le temps (accès temporaire) et révoquée.
/// L'appelant ne peut agir que dans son propre périmètre et n'accorde jamais plus que ce qu'il
/// détient (moindre privilège, §12, §18).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/users")]
public sealed class UsersController(IUserAssignmentService assignments, IMfaService mfa) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await assignments.ListUsersAsync(ct));

    /// <summary>Création d'un utilisateur avec sa première affectation (recette Phase 55 RBAC-001).
    /// Un compte n'existe jamais sans rôle, et ne peut pas être plus puissant que son créateur.</summary>
    [HttpPost]
    [RequirePermission(Permissions.Admin.ManageUsers)]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest r, CancellationToken ct)
        => FromResult(await assignments.CreateUserAsync(r, ct));

    [HttpGet("{id:guid}/assignments")]
    [RequirePermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> Assignments(Guid id, CancellationToken ct)
        => FromResult(await assignments.ListAssignmentsAsync(id, ct));

    [HttpPost("{id:guid}/assignments")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> Grant(Guid id, [FromBody] GrantAssignmentRequest r, CancellationToken ct)
        => FromResult(await assignments.GrantAsync(id, r, ct));

    [HttpPost("assignments/{assignmentId:guid}/revoke")]
    [RequireStepUp(StepUpPurposes.PermissionChange)]
    [RequirePermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> Revoke(Guid assignmentId, [FromBody] RevokeAssignmentRequest r, CancellationToken ct)
        => FromResult(await assignments.RevokeAsync(assignmentId, r, ct));

    [HttpPost("{id:guid}/status")]
    [RequirePermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetUserStatusRequest r, CancellationToken ct)
        => FromResult(await assignments.SetStatusAsync(id, r, ct));

    /// <summary>Réinitialise la MFA d'un utilisateur — récupération de compte (Phase 33 §9, §39).</summary>
    [HttpPost("{id:guid}/mfa/reset")]
    [RequirePermission(Permissions.Security.ResetMfa)]
    public async Task<IActionResult> ResetMfa(Guid id, CancellationToken ct)
        => FromResult(await mfa.ResetForUserAsync(id, ct));
}
