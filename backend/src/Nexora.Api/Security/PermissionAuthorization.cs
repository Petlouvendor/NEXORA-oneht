using Microsoft.AspNetCore.Authorization;
using Nexora.Application.Abstractions;

namespace Nexora.Api.Security;

/// <summary>Exige une permission dans le périmètre de l'entreprise active (Phase 3 §10, deny-by-default).</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequirePermissionAttribute(string permission) : AuthorizeAttribute(PolicyPrefix + permission)
{
    public const string PolicyPrefix = "perm:";
    public string Permission => permission;
}

public sealed class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission => permission;
}

/// <summary>Fabrique les politiques « perm:&lt;code&gt; » à la volée.</summary>
public sealed class PermissionPolicyProvider(
    Microsoft.Extensions.Options.IOptions<AuthorizationOptions> options) : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(RequirePermissionAttribute.PolicyPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[RequirePermissionAttribute.PolicyPrefix.Length..];
            return new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission))
                .Build();
        }
        return await base.GetPolicyAsync(policyName);
    }
}

public sealed class PermissionAuthorizationHandler(
    IPermissionService permissions,
    ITenantContext tenant,
    ILogger<PermissionAuthorizationHandler> logger)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (tenant.UserId is not { } userId)
            return;   // non authentifié → échec (deny-by-default)

        var profile = await permissions.LoadProfileAsync(userId);

        // Périmètre : entreprise active si sélectionnée, sinon n'importe laquelle autorisée.
        var granted = profile.HasPermission(requirement.Permission, tenant.ActiveCompanyId);

        // Clé d'API (Phase 35 §13) : la requête est en outre bornée aux scopes de la clé —
        // une clé n'accorde jamais plus que ce que son propriétaire détient, ni au-delà de son propre périmètre.
        if (granted && tenant.ApiScopes is { } scopes && !scopes.Contains(requirement.Permission))
        {
            granted = false;
            logger.LogWarning("Refus : clé d'API sans scope {Permission}", requirement.Permission);
        }

        if (granted)
            context.Succeed(requirement);
        else
            logger.LogWarning(
                "Refus : utilisateur {UserId} sans permission {Permission} (entreprise {Company})",
                userId, requirement.Permission, tenant.ActiveCompanyId);
    }
}
