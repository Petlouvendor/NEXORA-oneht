using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Security;
using Nexora.Application.Notifications;
using Nexora.Application.Security;
using Nexora.Contracts.Notifications;

namespace Nexora.Api.Controllers;

/// <summary>
/// Gabarits de message versionnés par canal &amp; langue (Phase 34 §22-23, §45). Les gabarits
/// système sont en lecture seule ; l'édition crée / met à jour un gabarit propre à l'entreprise.
/// Le rendu applique la liste blanche des variables — aucun contenu non contrôlé n'est injecté.
/// </summary>
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/message-templates")]
public sealed class MessageTemplatesController(IMessageTemplateService templates) : ApiControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.Notifications.ManageTemplates)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await templates.ListAsync(ct));

    [HttpPost]
    [RequirePermission(Permissions.Notifications.ManageTemplates)]
    public async Task<IActionResult> Upsert([FromBody] UpsertMessageTemplateRequest r, CancellationToken ct)
        => FromResult(await templates.UpsertAsync(r, ct));

    [HttpPost("render")]
    [RequirePermission(Permissions.Notifications.ManageTemplates)]
    public async Task<IActionResult> Render([FromBody] RenderTemplateRequest r, CancellationToken ct)
        => FromResult(await templates.RenderAsync(r, ct));
}
