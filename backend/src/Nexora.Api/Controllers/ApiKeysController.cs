using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Integrations;
using Nexora.Application.Security;
using Nexora.Contracts.Integrations;

namespace Nexora.Api.Controllers;

/// <summary>
/// Clés d'API pour les intégrations serveur (Phase 35 §13-14, §52). Le secret n'est renvoyé
/// qu'à la création ou la rotation — jamais ensuite. Le périmètre d'une clé ne peut pas dépasser
/// les permissions de l'utilisateur qui la crée.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/api-keys")]
[RequirePermission(Permissions.Integrations.ManageApiKeys)]
public sealed class ApiKeysController(IApiKeyService keys) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await keys.ListAsync(ct));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest r, CancellationToken ct)
        => FromResult(await keys.CreateAsync(r, ct));

    [HttpPost("{id:guid}/rotate")]
    public async Task<IActionResult> Rotate(Guid id, CancellationToken ct)
        => FromResult(await keys.RotateAsync(id, ct));

    [HttpPost("{id:guid}/revoke")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
        => FromResult(await keys.RevokeAsync(id, ct));
}
