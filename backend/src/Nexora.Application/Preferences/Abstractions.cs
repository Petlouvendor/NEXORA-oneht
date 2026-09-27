using Nexora.Contracts.Preferences;
using Nexora.Domain.Common;

namespace Nexora.Application.Preferences;

/// <summary>
/// Préférences propres à l'utilisateur courant (Phase 13 §36) : thème, densité, langue,
/// mouvement réduit, mode dashboard, favoris. Aucune permission requise — chacun gère les
/// siennes ; l'isolation est portée par RLS (`user_id`).
/// </summary>
public interface IUserPreferenceService
{
    Task<UserPreferenceDto> GetAsync(CancellationToken ct = default);
    Task<Result<UserPreferenceDto>> UpdateAsync(UpdateUserPreferenceRequest request, CancellationToken ct = default);
}

/// <summary>
/// Personnalisation de l'entreprise active (Phase 13 §35) : nom affiché, couleur d'accent,
/// logo, format de reçu, formats de date / nombre. La personnalisation ne doit jamais
/// dégrader la lisibilité ni l'accessibilité — l'accent est validé (hex + luminance bornée).
/// Lecture : tout membre ; écriture : `admin.configure_company`.
/// </summary>
public interface ICompanyBrandingService
{
    Task<CompanyBrandingDto> GetAsync(CancellationToken ct = default);
    Task<Result<CompanyBrandingDto>> UpdateAsync(UpdateCompanyBrandingRequest request, CancellationToken ct = default);
}
