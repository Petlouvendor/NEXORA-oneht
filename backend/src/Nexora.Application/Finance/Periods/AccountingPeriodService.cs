using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Periods;

public interface IAccountingPeriodService
{
    Task<Result> CloseAsync(Guid periodId, CancellationToken ct = default);
    Task<Result> ReopenAsync(Guid periodId, string reason, CancellationToken ct = default);

    /// <summary>Ouvre l'exercice qui suit le dernier (A-042). Refusé si un exercice qui n'a pas encore commencé existe déjà.</summary>
    Task<Result<AccountingPeriod>> OpenNextYearAsync(CancellationToken ct = default);

    /// <summary>
    /// Planificateur (job <c>accounting.open_next_year</c>) : ouvre l'exercice suivant quand le dernier se termine dans
    /// moins de <see cref="AccountingPeriodService.OpenAheadDays"/> jours. Renvoie le nombre d'exercices ouverts (0 ou 1).
    /// </summary>
    Task<int> OpenNextYearIfDueAsync(CancellationToken ct = default);
}

/// <summary>Clôture / réouverture de période (Phase 4 §27). La réouverture est tracée.</summary>
public sealed class AccountingPeriodService(
    IAccountingPeriods periods,
    ITenantContext tenant,
    IBusinessCalendar calendar,
    ILogger<AccountingPeriodService> logger,
    Ledger.IFiscalYearService fiscalYear) : IAccountingPeriodService
{
    /// <summary>Fenêtre d'ouverture anticipée de l'exercice suivant par le planificateur.</summary>
    public const int OpenAheadDays = 31;

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

        // Exercice clôturé (écriture de clôture passée) : la clôture est annulée avec lui (A-044). Avant, seule la
        // période rouvrait ; les écritures passées ensuite n'étaient jamais soldées et la reclôture était refusée.
        var year = await fiscalYear.ReopenClosedYearAsync(periodId, reason, ct);
        if (!year.IsSuccess) return year.Error!;
        if (year.Value)
        {
            logger.LogWarning("Exercice {Period} rouvert (clôture annulée) par {User} — motif : {Reason}", periodId, tenant.UserId, reason);
            return Result.Success();
        }

        await periods.ReopenAsync(periodId, tenant.UserId, ct);
        logger.LogWarning("Période {Period} rouverte par {User} — motif : {Reason}", periodId, tenant.UserId, reason);
        return Result.Success();
    }

    public async Task<Result<AccountingPeriod>> OpenNextYearAsync(CancellationToken ct = default)
    {
        var last = await periods.LastAsync(ct);
        if (last is null)
            return Error.Conflict("accounting.no_period", "Aucun exercice comptable : l'entreprise n'a pas été initialisée.");
        var today = await calendar.TodayAsync(ct);
        if (last.StartDate > today)
            return Error.Conflict("accounting.next_year_exists",
                $"L'exercice « {last.Name} » est déjà ouvert et n'a pas encore commencé.");

        var created = await periods.OpenNextYearAsync(ct);
        logger.LogInformation("Exercice {Name} ({Start} → {End}) ouvert par {User}", created.Name, created.StartDate, created.EndDate, tenant.UserId);
        return Result.Success(created);
    }

    public async Task<int> OpenNextYearIfDueAsync(CancellationToken ct = default)
    {
        var last = await periods.LastAsync(ct);
        if (last is null) return 0;
        var today = await calendar.TodayAsync(ct);
        if (last.StartDate > today || last.EndDate > today.AddDays(OpenAheadDays)) return 0;

        var created = await periods.OpenNextYearAsync(ct);
        logger.LogInformation("Exercice {Name} ({Start} → {End}) ouvert par le planificateur", created.Name, created.StartDate, created.EndDate);
        return 1;
    }
}
