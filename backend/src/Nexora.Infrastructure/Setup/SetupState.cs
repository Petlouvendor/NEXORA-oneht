using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Nexora.Infrastructure.Setup;

/// <summary>
/// Mode installation (résumé fonctionnel §20). Actif seulement quand AUCUNE base n'est configurée
/// (<c>Database:ConnectionString</c> vide) : l'API ne sert alors que l'assistant, protégé par un
/// jeton à usage unique affiché dans le journal de démarrage du serveur (header <see cref="TokenHeader"/>).
/// Toutes les autres routes répondent 503. Dès qu'une base est configurée — par l'assistant ou par
/// l'exploitant — l'assistant est désactivé.
/// </summary>
public sealed class SetupState
{
    public const string TokenHeader = "X-Setup-Token";
    public const string DefaultConfigFileName = "nexora.install.json";

    private readonly byte[] _token;
    private int _installing;
    private volatile bool _installed;

    public SetupState(bool required, string token, string configPath)
    {
        Required = required;
        Token = token;
        _token = Encoding.UTF8.GetBytes(token);
        ConfigPath = configPath;
    }

    /// <summary>L'API a démarré sans base : l'assistant est ouvert (jusqu'au redémarrage).</summary>
    public bool Required { get; }

    /// <summary>L'installation a abouti pendant ce processus : il reste à redémarrer le service.</summary>
    public bool Installed => _installed;

    public string Token { get; }

    /// <summary>Fichier de configuration écrit par l'assistant (chaîne de connexion, clés).</summary>
    public string ConfigPath { get; }

    public bool TokenMatches(string? candidate)
        => !string.IsNullOrEmpty(candidate)
           && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _token);

    /// <summary>Une seule installation à la fois.</summary>
    public bool TryBeginInstall() => Interlocked.CompareExchange(ref _installing, 1, 0) == 0;

    public void EndInstall(bool succeeded)
    {
        if (succeeded) _installed = true;
        Interlocked.Exchange(ref _installing, 0);
    }

    public static bool IsSetupMode(IConfiguration configuration)
        => string.IsNullOrWhiteSpace(configuration["Database:ConnectionString"]);

    public static string ConfigPathFrom(IConfiguration configuration, string contentRoot)
        => configuration["Install:ConfigPath"] is { Length: > 0 } p
            ? Path.GetFullPath(p)
            : Path.Combine(contentRoot, DefaultConfigFileName);

    public static SetupState FromConfiguration(IConfiguration configuration, string contentRoot)
    {
        // Jeton imposé par l'exploitant (déploiement automatisé) ou tiré au hasard à chaque démarrage.
        var token = configuration["Install:SetupToken"] is { Length: >= 16 } t
            ? t
            : Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return new SetupState(IsSetupMode(configuration), token, ConfigPathFrom(configuration, contentRoot));
    }
}
