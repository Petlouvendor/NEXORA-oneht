using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Operations;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

/// <summary>
/// Taux de change saisis (audit 2026-10, A-017). La table <c>exchange_rate</c> est commune à toute l'installation (pas
/// d'entreprise) : un taux saisi vaut pour toutes les entreprises. Sa saisie passe donc par la même garde que les autres
/// opérations de portée plateforme (<see cref="IPlatformOperationGuard"/>) : la direction sur une installation d'un seul
/// client, l'exploitant sur une plateforme partagée. Un taux n'est jamais modifié ni supprimé : les écritures passées
/// gardent le leur (pas de recalcul rétroactif, §22) ; on en saisit un nouveau, daté.
/// </summary>
public sealed class SqlExchangeRateService(NexoraDbContext db, IUnitOfWork uow, IPlatformOperationGuard guard, IDateTimeProvider clock)
    : IExchangeRateService
{
    private const string Select = """
        SELECT r.id AS "Id", b.code AS "BaseCurrency", q.code AS "QuoteCurrency", r.rate AS "Rate",
               r.effective_at AS "EffectiveAt", r.source AS "Source", r.created_at AS "CreatedAt"
        FROM nexora.exchange_rate r
        JOIN nexora.currency b ON b.id = r.base_currency_id
        JOIN nexora.currency q ON q.id = r.quote_currency_id
        """;

    public async Task<IReadOnlyList<ExchangeRateDto>> ListAsync(string? baseCurrency, string? quoteCurrency, CancellationToken ct = default)
        => await db.Database.SqlQueryRaw<ExchangeRateDto>(
            Select + " WHERE ({0} = '' OR b.code = {0}) AND ({1} = '' OR q.code = {1}) ORDER BY r.effective_at DESC, b.code, q.code LIMIT 200",
            (baseCurrency ?? "").Trim().ToUpperInvariant(), (quoteCurrency ?? "").Trim().ToUpperInvariant()).ToListAsync(ct);

    public async Task<Result<ExchangeRateDto>> RecordAsync(RecordExchangeRateRequest request, CancellationToken ct = default)
    {
        var from = request.BaseCurrency?.Trim().ToUpperInvariant() ?? "";
        var to = request.QuoteCurrency?.Trim().ToUpperInvariant() ?? "";
        if (from.Length == 0 || to.Length == 0) return Error.Validation("finance.rate_currency", "Les deux devises sont obligatoires.");
        if (from == to) return Error.Validation("finance.rate_currency", "Les deux devises doivent être différentes.");
        if (request.Rate <= 0m || request.Rate > 1_000_000m)
            return Error.Validation("finance.rate_value", "Le taux doit être strictement positif.");
        var effectiveAt = request.EffectiveAt ?? clock.UtcNow;
        if (effectiveAt > clock.UtcNow.AddDays(366))
            return Error.Validation("finance.rate_date", "Date d'effet trop lointaine.");

        if (await guard.RequirePlatformWideAsync(Nexora.Application.Security.Permissions.Finance.Update, ct) is { } refused)
            return refused;

        var ids = await db.Currencies.AsNoTracking().Where(c => c.Code == from || c.Code == to)
            .Select(c => new { c.Code, c.Id }).ToListAsync(ct);
        var baseId = ids.FirstOrDefault(c => c.Code == from)?.Id;
        var quoteId = ids.FirstOrDefault(c => c.Code == to)?.Id;
        if (baseId is null || quoteId is null) return Error.Validation("finance.rate_currency", "Devise inconnue.");

        var id = Guid.NewGuid();
        var inserted = await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.exchange_rate (id, base_currency_id, quote_currency_id, rate, effective_at, source)
            VALUES ({id}, {baseId}, {quoteId}, {request.Rate}, {effectiveAt}, 'manual')
            ON CONFLICT (base_currency_id, quote_currency_id, effective_at, source) DO NOTHING
            """, ct);
        if (inserted == 0)
            return Error.Conflict("finance.rate_exists", "Un taux existe déjà pour ces devises à cette date : saisissez une autre date d'effet.");
        await uow.SaveChangesAsync(ct);

        return Result.Success((await db.Database.SqlQueryRaw<ExchangeRateDto>(Select + " WHERE r.id = {0}", id).FirstAsync(ct)));
    }
}
