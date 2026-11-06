using Nexora.Domain.Accounting;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;

namespace Nexora.Application.Finance.Abstractions;

/// <summary>Résout un compte du plan comptable à partir d'une clé de mapping (Phase 4 §5).</summary>
public interface IAccountResolver
{
    Task<Guid> ResolveAsync(string mappingKey, CancellationToken ct = default);
    Task<Guid> ResolveByCodeAsync(string accountCode, CancellationToken ct = default);
    Task<Account> GetAsync(Guid accountId, CancellationToken ct = default);

    /// <summary>Compte GL d'un compte financier (tender) — sinon repli sur le mapping cash/bank (§9).</summary>
    Task<Guid> ResolveTenderAccountAsync(Guid financialAccountId, CancellationToken ct = default);
}

/// <summary>Taux de change historisés (Phase 4 §22 — pas de recalcul rétroactif).</summary>
public interface IExchangeRateProvider
{
    /// <summary>Taux pour convertir 1 <paramref name="fromCurrency"/> en <paramref name="toCurrency"/> à la date donnée.</summary>
    Task<(decimal Rate, Guid? RateId)> GetRateAsync(
        string fromCurrency, string toCurrency, DateTimeOffset at, CancellationToken ct = default);

    Task<(Money Converted, Guid? RateId)> ConvertAsync(
        Money amount, string toCurrency, DateTimeOffset at, CancellationToken ct = default);
}

/// <summary>Périodes comptables : résolution, clôture, réouverture (§4, §27).</summary>
public interface IAccountingPeriods
{
    /// <summary>Période ouverte couvrant <paramref name="date"/> ; ouvre au besoin les exercices suivants pour une
    /// opération du jour postérieure au dernier exercice (A-042).</summary>
    Task<AccountingPeriod> ResolveOpenPeriodAsync(DateOnly date, CancellationToken ct = default);
    Task<AccountingPeriod?> GetAsync(Guid periodId, CancellationToken ct = default);
    /// <summary>Exercice le plus récent (fin la plus tardive), ou null si l'entreprise n'en a aucun.</summary>
    Task<AccountingPeriod?> LastAsync(CancellationToken ct = default);
    /// <summary>Crée l'exercice d'un an qui suit immédiatement le dernier.</summary>
    Task<AccountingPeriod> OpenNextYearAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AccountingPeriod>> ListAsync(CancellationToken ct = default);
    Task CloseAsync(Guid periodId, Guid? closedBy, CancellationToken ct = default);
    Task ReopenAsync(Guid periodId, Guid? reopenedBy, CancellationToken ct = default);
}

public interface IDocumentNumberGenerator
{
    Task<string> NextAsync(string documentType, Guid? establishmentId = null, CancellationToken ct = default);
}

/// <summary>Devise fonctionnelle de l'entreprise active + précision par devise (§22, §32).</summary>
public interface ICompanyCurrencyContext
{
    Task<string> FunctionalCurrencyAsync(CancellationToken ct = default);
    Task<int> DecimalPlacesAsync(string currency, CancellationToken ct = default);
    Task<Guid> ResolveCurrencyIdAsync(string currency, CancellationToken ct = default);
}

// --- Écritures / sous-livres : persistance dans la transaction du moteur ---

public interface IJournalEntryStore
{
    Task<Guid?> FindByOperationAsync(Guid operationUuid, CancellationToken ct = default);
    Task<Guid> ResolveJournalAsync(string journalCode, CancellationToken ct = default);
    Task<JournalEntry> AddAsync(JournalEntry entry, CancellationToken ct = default);
    Task<JournalEntry?> GetWithLinesAsync(Guid entryId, CancellationToken ct = default);
    Task MarkReversedAsync(Guid entryId, Guid reversalEntryId, CancellationToken ct = default);
}

public interface IReceivablePayableStore
{
    Task<CustomerReceivable> AddReceivableAsync(CustomerReceivable receivable, CancellationToken ct = default);
    Task<SupplierPayable> AddPayableAsync(SupplierPayable payable, CancellationToken ct = default);
    Task<CustomerReceivable?> GetReceivableAsync(Guid id, CancellationToken ct = default);
    Task<SupplierPayable?> GetPayableAsync(Guid id, CancellationToken ct = default);
}

public interface IPaymentStore
{
    Task<Guid?> FindByOperationAsync(Guid operationUuid, CancellationToken ct = default);
    Task<Payment> AddAsync(Payment payment, CancellationToken ct = default);
    Task<Payment?> GetWithAllocationsAsync(Guid paymentId, CancellationToken ct = default);
    Task AddAllocationAsync(PaymentAllocation allocation, CancellationToken ct = default);
}

public interface IFinancialAccountStore
{
    Task<FinancialAccount?> GetAsync(Guid id, CancellationToken ct = default);
    Task<FinancialTransaction> AddTransactionAsync(FinancialTransaction transaction, CancellationToken ct = default);
    Task AdjustBalanceAsync(Guid financialAccountId, decimal delta, CancellationToken ct = default);
}
