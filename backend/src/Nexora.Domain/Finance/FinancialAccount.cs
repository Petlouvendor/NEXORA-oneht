using Nexora.Domain.Common;

namespace Nexora.Domain.Finance;

/// <summary>Compte financier : caisse / banque / Mobile Money — table nexora.financial_account (§18).</summary>
public sealed class FinancialAccount : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public string AccountType { get; set; } = "cash";
    public string Name { get; set; } = string.Empty;
    public string? AccountNumberMasked { get; set; }
    public Guid CurrencyId { get; set; }
    public decimal OpeningBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public Guid? LedgerAccountId { get; set; }
    public string Status { get; set; } = "active";
}

/// <summary>Mouvement de trésorerie — table nexora.financial_transaction (§17-18).</summary>
public sealed class FinancialTransaction : AuditableEntity
{
    public Guid CompanyId { get; set; }
    public Guid FinancialAccountId { get; set; }
    public string TransactionType { get; set; } = "deposit";
    public decimal Amount { get; set; }            // signé : + entrée, - sortie
    public Guid CurrencyId { get; set; }
    public Guid? ExchangeRateId { get; set; }
    public Guid? CounterpartyAccountId { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Status { get; set; } = "cleared";
    public Guid? SourceOperationUuid { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>Taux de change historisé — table nexora.exchange_rate (§22).</summary>
public sealed class ExchangeRate : Entity
{
    public Guid BaseCurrencyId { get; set; }
    public Guid QuoteCurrencyId { get; set; }
    public decimal Rate { get; set; }
    public DateTimeOffset EffectiveAt { get; set; }
    public string Source { get; set; } = "manual";
}
