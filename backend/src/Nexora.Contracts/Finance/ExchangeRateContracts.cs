namespace Nexora.Contracts.Finance;

/// <summary>Taux de change : 1 <c>BaseCurrency</c> = <c>Rate</c> <c>QuoteCurrency</c>, en vigueur à partir de <c>EffectiveAt</c>.</summary>
public sealed record ExchangeRateDto(
    Guid Id, string BaseCurrency, string QuoteCurrency, decimal Rate, DateTimeOffset EffectiveAt, string Source, DateTimeOffset CreatedAt);

/// <summary>Saisie d'un taux. Sans date d'effet : maintenant. Un taux n'est jamais modifié : on en saisit un nouveau.</summary>
public sealed record RecordExchangeRateRequest(string BaseCurrency, string QuoteCurrency, decimal Rate, DateTimeOffset? EffectiveAt = null);
