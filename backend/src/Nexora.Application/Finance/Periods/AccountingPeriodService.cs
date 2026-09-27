using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Periods;

public interface IAccountingPeriodService
{
    Task<Result> CloseAsync(Guid periodId, CancellationToken ct = default);
    Task<Result> ReopenAsync(Guid periodId, string reason, CancellationToken ct = default);
}

/// <summary>Clôture / réouverture de période (Phase 4 §27). La réouverture est tracée.</summary>
public sealed class AccountingPeriodService(
    IAccountingPeriods periods,
    ITenantContext tenant,
    ILogger<AccountingPeriodService> logger) : IAccountingPeriodService
{
    public async Task<Result> CloseAsync(Guid periodId, CancellationToken ct = default)
    {
        var period = await periods.GetAsync(periodId, ct);
        if (period is null) return Error.NotFound("accounting.period_not_found", "Période introuvable.");
        if (!period.IsOpen) return Error.Conflict("accounting.period_not_open", "La période n'est pas ouverte.");

        await periods.CloseAsync(periodId, tenant.UserId, ct);
        logger.LogInformation("Période {Period} clôturée par {User}", periodId, tenant.UserId);
        return Result.Success();
    }

    public async Task<Result> ReopenAsync(Guid periodId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("accounting.reason_required", "Un motif de réouverture est obligatoire.");

        var period = await periods.GetAsync(periodId, ct);
        if (period is null) return Error.NotFound("accounting.period_not_found", "Période introuvable.");
        if (period.IsOpen) return Result.Success();

        await periods.ReopenAsync(periodId, tenant.UserId, ct);
        logger.LogWarning("Période {Period} rouverte par {User} — motif : {Reason}", periodId, tenant.UserId, reason);
        return Result.Success();
    }
}
