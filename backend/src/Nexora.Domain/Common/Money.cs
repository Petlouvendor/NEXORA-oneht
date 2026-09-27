namespace Nexora.Domain.Common;

/// <summary>
/// Montant monétaire : décimal + code devise (Phase 4 §32 — jamais de flottant binaire).
/// Immuable. Les opérations exigent une devise identique.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Zero(string currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;
    public bool IsPositive => Amount > 0m;
    public bool IsNegative => Amount < 0m;

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return this with { Amount = Amount + other.Amount };
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return this with { Amount = Amount - other.Amount };
    }

    public Money Negate() => this with { Amount = -Amount };

    public Money Abs() => this with { Amount = Math.Abs(Amount) };

    /// <summary>Arrondi bancaire à la précision de la devise (Phase 4 §32).</summary>
    public Money Round(int decimals) => this with { Amount = Math.Round(Amount, decimals, MidpointRounding.ToEven) };

    public static Money operator +(Money a, Money b) => a.Add(b);
    public static Money operator -(Money a, Money b) => a.Subtract(b);
    public static Money operator -(Money a) => a.Negate();

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Devises incompatibles : {Currency} vs {other.Currency}.");
    }

    public override string ToString() => $"{Amount:0.####} {Currency}";
}
