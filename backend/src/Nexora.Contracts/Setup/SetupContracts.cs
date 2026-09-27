namespace Nexora.Contracts.Setup;

/// <summary>
/// État de l'assistant d'installation (résumé fonctionnel §20). <c>SetupRequired</c> : aucune base
/// configurée, l'assistant est ouvert. <c>RestartRequired</c> : installation faite, le service doit
/// redémarrer pour charger la configuration écrite.
/// </summary>
public sealed record SetupStatusDto(bool SetupRequired, bool Installed, bool RestartRequired);

public sealed record SetupChoiceDto(string Code, string Label);

public sealed record SetupOptionsDto(
    IReadOnlyList<SetupChoiceDto> Activities,
    IReadOnlyList<SetupChoiceDto> Currencies,
    IReadOnlyList<string> SslModes);

/// <summary>Page PostgreSQL de l'assistant : hôte, port, base, utilisateur, mot de passe, SSL.</summary>
public sealed record DatabaseSetupRequest(
    string Host, int Port, string Database, string Username, string Password, string? SslMode = "Prefer");

public sealed record DatabaseTestResultDto(
    bool Connected,
    string? ServerVersion,
    bool DatabaseExists,
    bool AlreadyInitialized,
    bool IsSuperuser,
    bool CanCreateRole,
    bool CanCreateDatabase,
    bool Ready,
    IReadOnlyList<string> Messages);

public sealed record SetupCompanyInput(string Name, string ActivityType, string Currency);

public sealed record SetupAdminInput(string FullName, string Email, string Password);

/// <summary>Paramètres initiaux (modifiables ensuite dans Administration → Paramètres).</summary>
public sealed record SetupSettingsInput(
    bool RequireCashSession = true,
    decimal CashVarianceThreshold = 500,
    decimal MaxDiscountPercent = 15,
    bool AllowNegativeStock = false);

public sealed record InstallRequest(
    DatabaseSetupRequest Database,
    SetupCompanyInput Company,
    SetupAdminInput Admin,
    SetupSettingsInput? Settings = null);

public sealed record InstallResultDto(
    Guid CompanyId, string CompanyName, string AdminEmail, int MigrationsApplied, bool RestartRequired);
