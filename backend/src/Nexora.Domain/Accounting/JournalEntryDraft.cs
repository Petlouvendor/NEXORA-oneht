using Nexora.Domain.Common;

namespace Nexora.Domain.Accounting;

/// <summary>Ligne de brouillon d'écriture (montant devise + montant converti devise entreprise).</summary>
public sealed record DraftLine(
    Guid AccountId,
    decimal Debit,
    decimal Credit,
    string Currency,
    decimal BaseDebit,
    decimal BaseCredit,
    Guid? ExchangeRateId = null,
    string? Description = null,
    string? ThirdPartyType = null,
    Guid? ThirdPartyId = null,
    Guid? CostCenterId = null);

/// <summary>
/// Brouillon d'écriture équilibré construit par une règle de génération (Phase 4 §4, §7).
/// Garantit débit = crédit (en devise et en devise de référence) avant validation.
/// </summary>
public sealed class JournalEntryDraft
{
    private readonly List<DraftLine> _lines = [];

    public JournalEntryDraft(
        string journalCode, DateOnly date, string baseCurrency,
        string? sourceType = null, Guid? sourceId = null, string? description = null)
    {
        JournalCode = journalCode;
        Date = date;
        BaseCurrency = baseCurrency;
        SourceType = sourceType;
        SourceId = sourceId;
        Description = description;
    }

    public string JournalCode { get; }
    public DateOnly Date { get; }
    public string BaseCurrency { get; }
    public string? SourceType { get; }
    public Guid? SourceId { get; }
    public string? Description { get; }
    public IReadOnlyList<DraftLine> Lines => _lines;

    public JournalEntryDraft Debit(
        Guid accountId, Money amount, Money baseAmount, Guid? exchangeRateId = null,
        string? description = null, string? thirdPartyType = null, Guid? thirdPartyId = null, Guid? costCenterId = null)
    {
        if (amount.IsNegative) return Credit(accountId, amount.Abs(), baseAmount.Abs(), exchangeRateId, description, thirdPartyType, thirdPartyId, costCenterId);
        _lines.Add(new DraftLine(accountId, amount.Amount, 0m, amount.Currency, baseAmount.Amount, 0m,
            exchangeRateId, description, thirdPartyType, thirdPartyId, costCenterId));
        return this;
    }

    public JournalEntryDraft Credit(
        Guid accountId, Money amount, Money baseAmount, Guid? exchangeRateId = null,
        string? description = null, string? thirdPartyType = null, Guid? thirdPartyId = null, Guid? costCenterId = null)
    {
        if (amount.IsNegative) return Debit(accountId, amount.Abs(), baseAmount.Abs(), exchangeRateId, description, thirdPartyType, thirdPartyId, costCenterId);
        _lines.Add(new DraftLine(accountId, 0m, amount.Amount, amount.Currency, 0m, baseAmount.Amount,
            exchangeRateId, description, thirdPartyType, thirdPartyId, costCenterId));
        return this;
    }

    /// <summary>Porte l'arrondi de conversion d'une devise étrangère sur une ligne (A-041, voir <see cref="ConversionRounding"/>).</summary>
    public void AbsorbConversionRounding(int functionalDecimals = 2)
    {
        var adapters = _lines.Select(l => new LineAdapter(l)).ToList();
        ConversionRounding.Absorb(adapters, BaseCurrency, functionalDecimals);
        for (var i = 0; i < _lines.Count; i++)
            _lines[i] = _lines[i] with { BaseDebit = adapters[i].BaseDebit, BaseCredit = adapters[i].BaseCredit };
    }

    private sealed class LineAdapter(DraftLine line) : ConversionRounding.ILine
    {
        public string Currency => line.Currency;
        public decimal Debit => line.Debit;
        public decimal Credit => line.Credit;
        public decimal BaseDebit { get; set; } = line.BaseDebit;
        public decimal BaseCredit { get; set; } = line.BaseCredit;
    }

    public decimal TotalBaseDebit => _lines.Sum(l => l.BaseDebit);
    public decimal TotalBaseCredit => _lines.Sum(l => l.BaseCredit);

    public bool IsBalanced => _lines.Count >= 2 && TotalBaseDebit == TotalBaseCredit;

    /// <summary>Vérifie l'équilibre ; jette si déséquilibré (Phase 4 §4 : débit = crédit).</summary>
    public void EnsureBalanced()
    {
        if (_lines.Count < 2)
            throw new InvalidOperationException("Une écriture requiert au moins 2 lignes.");
        if (TotalBaseDebit != TotalBaseCredit)
            throw new InvalidOperationException(
                $"Écriture déséquilibrée : débit {TotalBaseDebit} ≠ crédit {TotalBaseCredit}.");
    }
}
