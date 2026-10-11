namespace Nexora.Domain.Common;

/// <summary>
/// Erreurs de périmètre de requête (finalisation phase 18). Une opération qui écrit ou lit pour une entreprise
/// précise exige l'en-tête <c>X-Company-Id</c> : son absence est une requête mal formée (400), jamais une
/// erreur interne (500) journalisée avec sa pile d'appels.
/// </summary>
public static class TenantErrors
{
    public const string CompanyRequiredCode = "tenant.company_required";

    public static DomainException ActiveCompanyRequired() => new(Error.BadRequest(CompanyRequiredCode,
        "Sélectionnez une entreprise : cette opération exige une entreprise active (en-tête X-Company-Id)."));
}
