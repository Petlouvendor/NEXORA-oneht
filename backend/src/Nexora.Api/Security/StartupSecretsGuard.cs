using Nexora.Infrastructure.Operations;

namespace Nexora.Api.Security;

/// <summary>
/// Résumé fonctionnel §18, §21 (« ne jamais stocker les secrets dans le code ») : hors environnement
/// de développement, l'API refuse de démarrer si un secret manque ou retombe sur une valeur de repli
/// connue — sinon un oubli de configuration permettrait de forger des jetons ou de déchiffrer les
/// secrets MFA avec une clé publique (celle écrite dans le code). Aucun secret n'est affiché.
/// </summary>
public static class StartupSecretsGuard
{
    /// <summary>Même lecture que la checklist de mise en production et la vue « Production » (finalisation phase 18).</summary>
    public static IReadOnlyList<string> Problems(IConfiguration configuration)
        => ProductionConfiguration.SecretProblems(configuration);

    /// <summary>Lève si un secret de production manque (sauf en Development).</summary>
    public static void EnsureConfigured(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;
        var problems = Problems(configuration);
        if (problems.Count > 0)
            throw new InvalidOperationException(
                $"Démarrage refusé ({environment.EnvironmentName}) — secrets de production manquants ou faibles : " +
                string.Join(" ; ", problems) + ". Fournissez-les par variables d'environnement ou gestionnaire de secrets.");
    }
}
