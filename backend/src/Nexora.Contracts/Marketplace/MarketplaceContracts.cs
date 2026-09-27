namespace Nexora.Contracts.Marketplace;

// =============================================================================
// Marketplace & écosystème partenaires (Phase 46 §41-48). Le catalogue est curé par NEXORA
// (`platform.manage_marketplace`) ; une entreprise installe une app avec un périmètre de scopes
// qu'elle accorde explicitement, jamais hérité de ce que l'app a demandé (§44, §56).
// =============================================================================

public sealed record MarketplaceAppDto(
    Guid Id,
    string Slug,
    string Name,
    string PublisherName,
    string? PublisherContact,
    string Category,
    string Certification,   // official | certified | community
    string Status,          // draft | pending_review | approved | rejected | suspended
    string Version,
    string? Description,
    IReadOnlyList<string> RequestedScopes,
    string PricingModel,    // free | paid | subscription | usage
    decimal? PriceAmount,
    string PriceCurrencyCode,
    string? HomepageUrl,
    string? PrivacyPolicyUrl,
    string? SupportContact,
    Guid? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string? ReviewNotes,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt);

public sealed record SubmitAppRequest(
    string Slug,
    string Name,
    string PublisherName,
    string? PublisherContact,
    string Category,
    string Version,
    string? Description,
    IReadOnlyList<string> RequestedScopes,
    string PricingModel = "free",
    decimal? PriceAmount = null,
    string PriceCurrencyCode = "XOF",
    string? HomepageUrl = null,
    string? PrivacyPolicyUrl = null,
    string? SupportContact = null);

/// <summary>App review (§45) : identité éditeur, sécurité des scopes, documentation — décision tracée.</summary>
public sealed record ReviewAppRequest(string Decision, string? Notes);   // approve | reject | suspend

public sealed record InstallAppRequest(IReadOnlyList<string> GrantedScopes, Dictionary<string, string>? Config);

public sealed record AppInstallationDto(
    Guid Id,
    Guid MarketplaceAppId,
    string AppSlug,
    string AppName,
    string AppCategory,
    string AppCertification,
    string Status,   // installed | disabled | uninstalled
    IReadOnlyList<string> GrantedScopes,
    Dictionary<string, string> Config,
    DateTimeOffset InstalledAt,
    DateTimeOffset? DisabledAt,
    DateTimeOffset? UninstalledAt);

/// <summary>Usage quotidien d'une clé (portail développeur §47-48).</summary>
public sealed record ApiKeyDailyUsageDto(DateOnly Date, long RequestCount, long RateLimitedCount);

public sealed record DeveloperKeyUsageDto(Guid ApiKeyId, string KeyName, IReadOnlyList<ApiKeyDailyUsageDto> Daily);
