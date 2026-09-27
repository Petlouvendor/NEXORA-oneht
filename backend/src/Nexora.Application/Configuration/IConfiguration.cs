using Nexora.Contracts.Configuration;
using Nexora.Domain.Common;

namespace Nexora.Application.Configuration;

/// <summary>
/// Profils sectoriels (Phase 32 §6, §49) : bundles réutilisables de configuration (modules,
/// champs personnalisés, terminologie, réglages, numérotation) appliqués à une entreprise
/// sans changer de version logicielle (§1-2). Chaque application prend un snapshot versionné
/// (§44) et passe les garde-fous (§52).
/// </summary>
public interface ISectorProfileService
{
    Task<IReadOnlyList<SectorProfileDto>> ListAsync(CancellationToken ct = default);
    Task<Result<SectorProfileDetailDto>> GetAsync(string key, CancellationToken ct = default);
    Task<Result<ProfilePreviewDto>> PreviewAsync(string key, CancellationToken ct = default);
    Task<Result<ApplyProfileResultDto>> ApplyAsync(string key, ApplyProfileRequest r, CancellationToken ct = default);
}

/// <summary>
/// Transfert de configuration (Phase 32 §46-48) : export d'un bundle (jamais de secrets, soldes
/// ni historique), import, clonage vers une autre entreprise autorisée.
/// </summary>
public interface IConfigTransferService
{
    Task<ConfigBundleDto> ExportAsync(CancellationToken ct = default);
    Task<Result<ApplyProfileResultDto>> ImportAsync(ImportConfigRequest r, CancellationToken ct = default);
    Task<Result<ApplyProfileResultDto>> CloneAsync(CloneConfigRequest r, CancellationToken ct = default);
    Task<IReadOnlyList<ConfigTransferDto>> HistoryAsync(CancellationToken ct = default);
}

/// <summary>
/// Garde-fous de configuration (Phase 32 §52) : cohérence avant publication — devise valide,
/// mappings comptables requis, séquences uniques, modules cohérents, champs valides, approbateurs réels.
/// </summary>
public interface IConfigGuardrails
{
    Task<GuardrailReportDto> ValidateAsync(CancellationToken ct = default);
}
