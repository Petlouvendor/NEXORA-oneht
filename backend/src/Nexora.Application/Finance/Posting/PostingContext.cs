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
    int functionalDecimals)
{
    public string FunctionalCurrency { get; } = functionalCurrency;
    public int FunctionalDecimals { get; } = functionalDecimals;

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
