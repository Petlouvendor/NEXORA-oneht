using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Application.Setup;
using Nexora.Contracts.Setup;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Setup;

namespace Nexora.Api.Controllers;

/// <summary>
/// Assistant d'installation (résumé fonctionnel §20). Ouvert seulement en mode installation et
/// protégé par le jeton affiché dans le journal de démarrage (<c>X-Setup-Token</c>) ; désactivé dès
/// que l'application est configurée. Seul <c>GET status</c> reste public, pour que l'interface sache
/// s'il faut afficher l'assistant.
/// </summary>
[ApiVersion("1.0")]
[AllowAnonymous]
public sealed class SetupController(
    SetupState state,
    ISetupInstaller installer,
    IHostApplicationLifetime lifetime,
    IConfiguration configuration,
    ILogger<SetupController> logger) : ApiControllerBase
{
    [HttpGet("status")]
    [ProducesResponseType<SetupStatusDto>(StatusCodes.Status200OK)]
    public IActionResult Status()
        => Ok(new SetupStatusDto(
            SetupRequired: state.Required && !state.Installed,
            Installed: !state.Required || state.Installed,
            RestartRequired: state.Installed));

    [HttpGet("options")]
    [ProducesResponseType<SetupOptionsDto>(StatusCodes.Status200OK)]
    public IActionResult Options()
    {
        EnsureOpen();
        return Ok(new SetupOptionsDto(SetupInstaller.ActivityChoices, SetupInstaller.CurrencyChoices, SetupInstaller.SslModes));
    }

    /// <summary>Première page de l'assistant : vérifie le jeton d'installation.</summary>
    [HttpPost("verify-token")]
    [EnableRateLimiting("auth")]
    public IActionResult VerifyToken()
    {
        Guard();
        return NoContent();
    }

    /// <summary>Page PostgreSQL : « Tester la connexion avant initialisation ».</summary>
    [HttpPost("database/test")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<DatabaseTestResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> TestDatabase([FromBody] DatabaseSetupRequest request, CancellationToken ct)
    {
        Guard();
        return FromResult(await installer.TestDatabaseAsync(request, ct));
    }

    /// <summary>Migrations → entreprise → administrateur → paramètres, puis redémarrage du service.</summary>
    [HttpPost("install")]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<InstallResultDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Install([FromBody] InstallRequest request, CancellationToken ct)
    {
        Guard();
        // L'installation ne doit pas être interrompue si le navigateur coupe la requête.
        var result = await installer.InstallAsync(request, CancellationToken.None);
        if (result.IsSuccess && configuration.GetValue("Install:AutoRestart", true))
        {
            // Arrêt après l'envoi de la réponse : le superviseur (service Windows, systemd, Docker)
            // relance l'API, qui lit alors la configuration écrite.
            Response.OnCompleted(() =>
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                    logger.LogWarning("Installation terminée : arrêt du service pour redémarrage.");
                    lifetime.StopApplication();
                });
                return Task.CompletedTask;
            });
        }
        return FromResult(result);
    }

    private void EnsureOpen()
    {
        if (!state.Required)
            throw new DomainException(Error.NotFound("setup.disabled",
                "L'assistant d'installation est désactivé : l'application est déjà configurée."));
        if (state.Installed)
            throw new DomainException(Error.Conflict("setup.completed",
                "L'installation est terminée : redémarrez le service."));
    }

    private void Guard()
    {
        EnsureOpen();
        if (!state.TokenMatches(Request.Headers[SetupState.TokenHeader].ToString()))
            throw new DomainException(Error.Forbidden("setup.invalid_token",
                "Jeton d'installation invalide : il est affiché dans le journal de démarrage du serveur."));
    }
}
