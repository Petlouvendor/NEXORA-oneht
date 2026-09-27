using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Sectors;
using Nexora.Application.Security;
using Nexora.Contracts.Sectors;

namespace Nexora.Api.Controllers;

/// <summary>
/// Configuration sectorielle (Phase 8 §3-4, §16, §18). Le noyau reste commun ;
/// on active/désactive des modules et on ajoute des champs personnalisés sans
/// jamais supprimer de données existantes (§29).
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/sectors")]
public sealed class SectorsController(
    ISectorConfigService config,
    ICustomFieldService customFields) : ApiControllerBase
{
    [HttpGet("config")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> GetConfig(CancellationToken ct)
        => Ok(await config.GetAsync(ct));

    [HttpPut("activities")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> SetActivities([FromBody] SetActivitiesRequest r, CancellationToken ct)
        => FromResult(await config.SetActivitiesAsync(r, ct));

    [HttpPut("modules")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> SetModule([FromBody] SetModuleRequest r, CancellationToken ct)
        => FromResult(await config.SetModuleAsync(r, ct));

    [HttpPut("terminology")]
    [RequirePermission(Permissions.Sectors.Terminology)]
    public async Task<IActionResult> SetTerminology([FromBody] SetTerminologyRequest r, CancellationToken ct)
        => FromResult(await config.SetTerminologyAsync(r, ct));

    // --- Champs personnalisés (§16) -----------------------------------

    [HttpPost("custom-fields")]
    [RequirePermission(Permissions.Sectors.CustomFields)]
    public async Task<IActionResult> Define([FromBody] DefineCustomFieldRequest r, CancellationToken ct)
        => FromResult(await customFields.DefineAsync(r, ct));

    [HttpGet("custom-fields/{entityType}")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> List(string entityType, CancellationToken ct)
        => Ok(await customFields.ListAsync(entityType, ct));

    [HttpPut("custom-fields/values")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> SetValues([FromBody] SetCustomFieldValuesRequest r, CancellationToken ct)
        => FromResult(await customFields.SetValuesAsync(r, ct));

    [HttpGet("custom-fields/values/{entityType}/{entityId:guid}")]
    [RequirePermission(Permissions.Sectors.Configure)]
    public async Task<IActionResult> GetValues(string entityType, Guid entityId, CancellationToken ct)
        => Ok(await customFields.GetValuesAsync(entityType, entityId, ct));
}
