using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Migration;
using Nexora.Application.Security;
using Nexora.Contracts.Migration;

namespace Nexora.Api.Controllers;

/// <summary>Onboarding avancé (Phase 39 §31-34, §45) : checklist dérivée + score de migration.</summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/onboarding")]
public sealed class OnboardingController(IOnboardingService onboarding) : ApiControllerBase
{
    [HttpGet("checklist")]
    [RequirePermission(Permissions.Migration.View)]
    public async Task<IActionResult> Checklist(CancellationToken ct) => Ok(await onboarding.GetChecklistAsync(ct));

    [HttpPost("tasks")]
    [RequirePermission(Permissions.Migration.Manage)]
    public async Task<IActionResult> CompleteTask([FromBody] CompleteOnboardingTaskRequest r, CancellationToken ct)
        => FromResult(await onboarding.CompleteTaskAsync(r, ct));
}
