using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Sales;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sales;

/// <summary>
/// Validation sur place par un responsable — voir <see cref="IManagerOverrideService"/>.
/// Les refus d'identification sont des 422 / 403, jamais des 401 : un 401 déconnecterait le caissier.
/// </summary>
public sealed class ManagerOverrideService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUserRepository users,
    IPasswordHasher hasher,
    IMfaService mfa,
    IPermissionService permissions,
    ISecurityEventLog securityEvents,
    IDateTimeProvider clock,
    IReturnRequestService returns,
    IDiscountRequestService discounts,
    CashSessionService sessions,
    Nexora.Application.Customers.ICreditOverrideService creditOverrides,
    ILogger<ManagerOverrideService> logger) : IManagerOverrideService
{
    /// <summary>Permission exigée du responsable pour chaque action.</summary>
    private static readonly Dictionary<string, string> RequiredPermission = new(StringComparer.Ordinal)
    {
        ["approve_return"] = Permissions.Pos.Refund,
        ["reject_return"] = Permissions.Pos.Refund,
        ["approve_discount"] = Permissions.Pos.ApproveDiscount,
        ["reject_discount"] = Permissions.Pos.ApproveDiscount,
        ["validate_close"] = Permissions.Pos.ManageSessions,
        // Module clients §12 : dépassement de limite de crédit, par le workflow d'approbation configuré.
        ["approve_credit_override"] = Permissions.Pos.CreditOverride,
        ["reject_credit_override"] = Permissions.Pos.CreditOverride,
    };

    public async Task<Result<ManagerOverrideResultDto>> ExecuteAsync(
        ManagerOverrideRequest request, string? ipAddress, CancellationToken ct = default)
    {
        var action = (request.Action ?? "").Trim().ToLowerInvariant();
        if (!RequiredPermission.TryGetValue(action, out var permission))
            return Error.Validation("pos.override_action", $"Action « {request.Action} » inconnue.");
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            return Error.Validation("pos.override_credentials_required", "Saisissez l'e-mail et le mot de passe du responsable.");
        var company = tenant.ActiveCompanyId;
        var cashier = tenant.UserId ?? throw new InvalidOperationException("Utilisateur requis.");
        var context = new { action, targetId = request.TargetId, requestedFrom = cashier };

        // --- 1. Identification du responsable : mêmes règles que la connexion (Phase 33) --------
        var invalid = Error.Validation("pos.override_invalid_credentials", "Identifiants du responsable invalides.");
        var manager = await users.FindByEmailAsync(request.Email.Trim(), ct);
        if (manager is null || !manager.IsActive || string.IsNullOrEmpty(manager.PasswordHash))
        {
            await securityEvents.RecordAsync("pos.override_failed", null, company, "compte inconnu ou inactif", ipAddress, context, ct);
            return invalid;
        }
        if (manager.LockedUntil is { } until && until > clock.UtcNow)
        {
            await securityEvents.RecordAsync("pos.override_blocked", manager.Id, company, "compte verrouillé", ipAddress, context, ct);
            return Error.Validation("pos.override_locked",
                "Compte du responsable temporairement verrouillé après plusieurs tentatives.");
        }
        if (!hasher.Verify(request.Password, manager.PasswordHash))
        {
            var locked = await users.RegisterFailedLoginAsync(manager.Id, clock.UtcNow, ct);
            await securityEvents.RecordAsync(locked is null ? "pos.override_failed" : "pos.override_locked",
                manager.Id, company, "mot de passe incorrect", ipAddress, context, ct);
            return invalid;
        }
        if (manager.MfaEnabled && !string.IsNullOrEmpty(manager.MfaSecret))
        {
            if (string.IsNullOrWhiteSpace(request.MfaCode))
                return Error.Validation("pos.override_mfa_required",
                    "Ce responsable a activé la double authentification : saisissez son code.");
            var (challenge, _) = await mfa.IssueChallengeAsync(manager.Id, company, ct);
            if (!(await mfa.VerifyChallengeAsync(challenge, request.MfaCode.Trim(), ct)).IsSuccess)
            {
                await users.RegisterFailedLoginAsync(manager.Id, clock.UtcNow, ct);
                await securityEvents.RecordAsync("pos.override_failed", manager.Id, company, "code MFA invalide", ipAddress, context, ct);
                return Error.Validation("pos.override_mfa_invalid", "Code de double authentification invalide.");
            }
        }
        await users.ResetFailedLoginsAsync(manager.Id, ct);

        // --- 2. Habilitation dans l'entreprise du poste ; jamais le caissier lui-même -------------
        if (manager.Id == cashier)
            return Error.Forbidden("pos.self_approval",
                "La validation doit être faite par un autre utilisateur que celui connecté au poste.");
        var granted = (await permissions.LoadProfileAsync(manager.Id, ct)).PermissionsFor(company);
        if (!granted.Contains(permission))
        {
            await securityEvents.RecordAsync("pos.override_denied", manager.Id, company, $"permission {permission} absente", ipAddress, context, ct);
            return Error.Forbidden("pos.override_forbidden",
                $"{manager.FullName ?? manager.Email} n'a pas le droit de valider cette action ({permission}).");
        }

        // --- 3. Exécution AU NOM du responsable : contrôles métier, limites et audit sont les siens --
        var outcome = await ActAsAsync(manager.Id, cashier, () => RunAsync(action, request, ct), ct);
        await securityEvents.RecordAsync(outcome.IsSuccess ? "pos.override_succeeded" : "pos.override_refused",
            manager.Id, company, outcome.IsSuccess ? action : outcome.Error!.Code, ipAddress, context, ct);
        if (!outcome.IsSuccess) return outcome.Error!;

        logger.LogInformation("Validation sur place {Action} de {Target} par {Manager} au poste de {Cashier}",
            action, request.TargetId, manager.Id, cashier);
        return Result.Success(new ManagerOverrideResultDto(action, request.TargetId, manager.Id,
            manager.FullName ?? manager.Email, outcome.Value!));
    }

    private async Task<Result<string>> RunAsync(string action, ManagerOverrideRequest request, CancellationToken ct)
    {
        var decision = new DecideRequest(request.Note);
        return action switch
        {
            "approve_return" => Status(await returns.ApproveAsync(request.TargetId, decision, ct), r => r.Status),
            "reject_return" => Status(await returns.RejectAsync(request.TargetId, decision, ct), r => r.Status),
            "approve_discount" => Status(await discounts.ApproveAsync(request.TargetId, decision, ct), r => r.Status),
            "reject_discount" => Status(await discounts.RejectAsync(request.TargetId, decision, ct), r => r.Status),
            "validate_close" => Status(await sessions.ValidateCloseAsync(new ValidateCloseCommand(request.TargetId, request.Note), ct), r => r.Status),
            "approve_credit_override" => Status(await creditOverrides.ApproveAsync(request.TargetId, request.Note, ct), r => r.Status),
            "reject_credit_override" => Status(await creditOverrides.RejectAsync(request.TargetId, request.Note, ct), r => r.Status),
            _ => Error.Validation("pos.override_action", "Action inconnue."),
        };
    }

    private static Result<string> Status<T>(Result<T> r, Func<T, string> status)
        => r.IsSuccess ? Result.Success(status(r.Value!)) : Result.Failure<string>(r.Error!);

    /// <summary>
    /// Bascule l'identité de la requête sur le responsable le temps de l'action : permissions, limites,
    /// règle « demandeur ≠ validateur », colonnes <c>decided_by</c> et journal d'audit (variable de session
    /// <c>nexora.user_id</c>) portent son nom. L'identité du caissier est toujours restaurée.
    /// </summary>
    private async Task<T> ActAsAsync<T>(Guid manager, Guid cashier, Func<Task<T>> work, CancellationToken ct)
    {
        tenant.SetUser(manager);
        try
        {
            await SetSessionUserAsync(manager, ct);
            return await work();
        }
        finally
        {
            tenant.SetUser(cashier);
            await SetSessionUserAsync(cashier, CancellationToken.None);
        }
    }

    /// <summary>Une connexion déjà ouverte garde la variable posée à son ouverture : on la réaligne.</summary>
    private Task SetSessionUserAsync(Guid userId, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"SELECT set_config('nexora.user_id', {userId.ToString("D")}, false)", ct);
}
