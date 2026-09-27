using Microsoft.Extensions.Configuration.Json;
using Nexora.Infrastructure.Setup;

namespace Nexora.Api.Setup;

/// <summary>
/// Résumé fonctionnel §20 : la configuration écrite par l'assistant d'installation est chargée au
/// démarrage, juste au-dessus des fichiers appsettings — les variables d'environnement, les secrets
/// utilisateur et la ligne de commande gardent la priorité.
/// </summary>
public static class InstallConfiguration
{
    public static void AddInstallFile(ConfigurationManager configuration, string contentRoot)
    {
        var path = SetupState.ConfigPathFrom(configuration, contentRoot);
        var source = new JsonConfigurationSource { Path = path, Optional = true, ReloadOnChange = false };
        source.ResolveFileProvider();

        var sources = configuration.Sources;
        var anchor = -1;
        for (var i = 0; i < sources.Count; i++)
            if (sources[i] is JsonConfigurationSource { Path: { } p } && p.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
                anchor = i;
        sources.Insert(anchor + 1, source);
    }
}
