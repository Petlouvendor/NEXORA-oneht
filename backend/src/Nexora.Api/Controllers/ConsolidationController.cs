using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Abstractions;
using Nexora.Application.Group;
using Nexora.Application.Security;
using Nexora.Contracts.Group;
using Nexora.Domain.Common;

namespace Nexora.Api.Controllers;

/// <summary>
/// Processus de consolidation (Phase 9 §17-20). Brouillon → exécution → prêt → validé →
/// publié. La consolidation agrège des copies converties et mappées et n'écrit jamais
/// dans les comptabilités sources.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/groups/{groupId:guid}/consolidation")]
public sealed class ConsolidationController(IConsolidationService consolidation, ITenantContext tenant) : ApiControllerBase
{
    [HttpGet("runs")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> ListRuns(Guid groupId, CancellationToken ct)
        => Ok(await consolidation.ListRunsAsync(groupId, ct));

    // --- Phase 42 §55 : alias canoniques « à plat » (le groupe se résout via le périmètre) -----

    /// <summary>Résout le groupe unique du périmètre appelant ; ambigu (0 ou plusieurs) → erreur explicite.</summary>
    private Result<Guid> ResolveGroup() => tenant.GroupIds.Count switch
    {
        1 => Result.Success(tenant.GroupIds.Single()),
        0 => Error.Validation("consolidation.no_group", "Aucun groupe dans le périmètre — utilisez la route imbriquée /groups/{groupId}/consolidation."),
        _ => Error.Validation("consolidation.ambiguous_group", "Plusieurs groupes dans le périmètre — utilisez la route imbriquée /groups/{groupId}/consolidation."),
    };

    [HttpGet("~/api/v{version:apiVersion}/consolidations")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> ListRunsFlat(CancellationToken ct)
    {
        var group = ResolveGroup();
        if (!group.IsSuccess) throw new DomainException(group.Error!);
        return Ok(await consolidation.ListRunsAsync(group.Value, ct));
    }

    [HttpPost("~/api/v{version:apiVersion}/consolidations/{runId:guid}/run")]
    [RequirePermission(Permissions.Consolidation.Run)]
    public async Task<IActionResult> ExecuteFlat(Guid runId, CancellationToken ct)
    {
        var group = ResolveGroup();
        if (!group.IsSuccess) throw new DomainException(group.Error!);
        return FromResult(await consolidation.ExecuteAsync(group.Value, runId, ct));
    }

    [HttpGet("runs/{runId:guid}")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> GetRun(Guid groupId, Guid runId, CancellationToken ct)
        => FromResult(await consolidation.GetRunAsync(groupId, runId, ct));

    [HttpGet("runs/{runId:guid}/statements")]
    [RequirePermission(Permissions.Group.View)]
    public async Task<IActionResult> Statements(Guid groupId, Guid runId, CancellationToken ct)
        => FromResult(await consolidation.GetStatementsAsync(groupId, runId, ct));

    [HttpPost("runs")]
    [RequirePermission(Permissions.Consolidation.Run)]
    public async Task<IActionResult> Create(Guid groupId, [FromBody] CreateConsolidationRunRequest r, CancellationToken ct)
        => FromResult(await consolidation.CreateRunAsync(groupId, r, ct));

    [HttpPost("runs/{runId:guid}/execute")]
    [RequirePermission(Permissions.Consolidation.Run)]
    public async Task<IActionResult> Execute(Guid groupId, Guid runId, CancellationToken ct)
        => FromResult(await consolidation.ExecuteAsync(groupId, runId, ct));

    [HttpPost("runs/{runId:guid}/validate")]
    [RequirePermission(Permissions.Consolidation.Validate)]
    public async Task<IActionResult> Validate(Guid groupId, Guid runId, CancellationToken ct)
        => FromResult(await consolidation.ValidateAsync(groupId, runId, ct));

    [HttpPost("runs/{runId:guid}/publish")]
    [RequirePermission(Permissions.Consolidation.Publish)]
    public async Task<IActionResult> Publish(Guid groupId, Guid runId, CancellationToken ct)
        => FromResult(await consolidation.PublishAsync(groupId, runId, ct));

    [HttpPost("runs/{runId:guid}/reopen")]
    [RequirePermission(Permissions.Consolidation.Validate)]
    public async Task<IActionResult> Reopen(Guid groupId, Guid runId, [FromBody] ReopenRunRequest r, CancellationToken ct)
        => FromResult(await consolidation.ReopenAsync(groupId, runId, r, ct));
}
