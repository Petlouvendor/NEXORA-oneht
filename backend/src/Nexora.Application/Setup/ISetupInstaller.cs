using Nexora.Contracts.Setup;
using Nexora.Domain.Common;

namespace Nexora.Application.Setup;

/// <summary>
/// Installation initiale (résumé fonctionnel §20) :
/// PostgreSQL → test → migrations → entreprise → administrateur → paramètres → application.
/// </summary>
public interface ISetupInstaller
{
    /// <summary>Teste la connexion et dit si l'installation peut se faire sur cette base
    /// (version, droits, base vide). Un échec de connexion n'est pas une erreur : il est décrit.</summary>
    Task<Result<DatabaseTestResultDto>> TestDatabaseAsync(DatabaseSetupRequest request, CancellationToken ct = default);

    /// <summary>Crée la base si besoin, applique les migrations, crée l'entreprise, son référentiel,
    /// le premier administrateur et le rôle applicatif, puis écrit la configuration.</summary>
    Task<Result<InstallResultDto>> InstallAsync(InstallRequest request, CancellationToken ct = default);
}
