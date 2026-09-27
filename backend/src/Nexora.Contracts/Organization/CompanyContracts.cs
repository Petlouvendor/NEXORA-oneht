namespace Nexora.Contracts.Organization;

public sealed record CompanyResponse(
    Guid Id,
    Guid? GroupId,
    string Name,
    string? LegalName,
    string Status,
    string MainActivityType,
    string ComplexityLevel,
    string DeploymentMode,
    string Timezone,
    Guid DefaultCurrencyId);
