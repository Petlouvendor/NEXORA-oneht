using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Posting;

/// <summary>
/// Services de contexte fournis aux règles de génération : résolution de comptes,
/// conversion de devise historique, devise fonctionnelle et précision.
/// </summary>
public sealed class PostingContext(
    IAccountResolver accounts,
    IExchangeRateProvider fx,
    string functionalCurrency,
    int functionalDecimals,
    TimeZoneInfo? zone = null)
{
    public string FunctionalCurrency { get; } = functionalCurrency;
    public int FunctionalDecimals { get; } = functionalDecimals;

    /// <summary>Fuseau de l'entreprise : la date comptable d'un événement est son jour civil local, pas le jour UTC.</summary>
    public TimeZoneInfo Zone { get; } = zone ?? TimeZoneInfo.Utc;

    /// <summary>Date comptable d'un instant (jour civil dans le fuseau de l'entreprise).</summary>
    public DateOnly DateOf(DateTimeOffset instant) => BusinessDate.Of(instant, Zone);

    public Task<Guid> Account(string mappingKey, CancellationToken ct = default) => accounts.ResolveAsync(mappingKey, ct);

    public Task<Guid> TenderAccount(Guid financialAccountId, CancellationToken ct = default)
        => accounts.ResolveTenderAccountAsync(financialAccountId, ct);

    /// <summary>Convertit un montant transaction en devise fonctionnelle au taux de la date (§22).</summary>
    public async Task<(Money Base, Guid? RateId)> ToFunctional(Money amount, DateTimeOffset at, CancellationToken ct = default)
    {
        if (string.Equals(amount.Currency, FunctionalCurrency, StringComparison.OrdinalIgnoreCase))
            return (amount.Round(FunctionalDecimals), null);
        var (converted, rateId) = await fx.ConvertAsync(amount, FunctionalCurrency, at, ct);
        return (converted.Round(FunctionalDecimals), rateId);
    }
}
