using Microsoft.EntityFrameworkCore;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Abstractions;
using Nexora.Domain.Accounting;
using Nexora.Domain.Finance;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

public sealed class EfJournalEntryStore(NexoraDbContext db, ITenantContext tenant) : IJournalEntryStore
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    public Task<Guid?> FindByOperationAsync(Guid operationUuid, CancellationToken ct = default)
        => db.JournalEntries.AsNoTracking()
            .Where(e => e.SourceOperationUuid == operationUuid)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(ct);

    public async Task<Guid> ResolveJournalAsync(string journalCode, CancellationToken ct = default)
    {
        var id = await db.Journals.AsNoTracking()
            .Where(j => j.CompanyId == Company && j.Code == journalCode && j.Status == "active")
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);
        return id ?? throw new InvalidOperationException($"Journal « {journalCode} » introuvable pour l'entreprise.");
    }

    public async Task<JournalEntry> AddAsync(JournalEntry entry, CancellationToken ct = default)
    {
        await db.JournalEntries.AddAsync(entry, ct);
        return entry;
    }

    public Task<JournalEntry?> GetWithLinesAsync(Guid entryId, CancellationToken ct = default)
        => db.JournalEntries.AsNoTracking().Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.Id == entryId, ct);

    public async Task MarkReversedAsync(Guid entryId, Guid reversalEntryId, CancellationToken ct = default)
    {
        _ = reversalEntryId;   // le lien inverse est porté par l'écriture d'extourne (ReversalOfEntryId)
        var entry = await db.JournalEntries.FirstAsync(e => e.Id == entryId, ct);
        entry.Status = JournalEntryStatus.Reversed;
    }
}

public sealed class EfReceivablePayableStore(NexoraDbContext db) : IReceivablePayableStore
{
    public async Task<CustomerReceivable> AddReceivableAsync(CustomerReceivable receivable, CancellationToken ct = default)
    {
        await db.Receivables.AddAsync(receivable, ct);
        return receivable;
    }

    public async Task<SupplierPayable> AddPayableAsync(SupplierPayable payable, CancellationToken ct = default)
    {
        await db.Payables.AddAsync(payable, ct);
        return payable;
    }

    public Task<CustomerReceivable?> GetReceivableAsync(Guid id, CancellationToken ct = default)
        => db.Receivables.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<SupplierPayable?> GetPayableAsync(Guid id, CancellationToken ct = default)
        => db.Payables.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
}

public sealed class EfPaymentStore(NexoraDbContext db) : IPaymentStore
{
    public Task<Guid?> FindByOperationAsync(Guid operationUuid, CancellationToken ct = default)
        => db.Payments.AsNoTracking()
            .Where(p => p.SourceOperationUuid == operationUuid)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);

    public async Task<Payment> AddAsync(Payment payment, CancellationToken ct = default)
    {
        await db.Payments.AddAsync(payment, ct);
        return payment;
    }

    public Task<Payment?> GetWithAllocationsAsync(Guid paymentId, CancellationToken ct = default)
        => db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct);

    public async Task AddAllocationAsync(PaymentAllocation allocation, CancellationToken ct = default)
        => await db.PaymentAllocations.AddAsync(allocation, ct);
}

public sealed class EfFinancialAccountStore(NexoraDbContext db) : IFinancialAccountStore
{
    public Task<FinancialAccount?> GetAsync(Guid id, CancellationToken ct = default)
        => db.FinancialAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<FinancialTransaction> AddTransactionAsync(FinancialTransaction transaction, CancellationToken ct = default)
    {
        await db.FinancialTransactions.AddAsync(transaction, ct);
        return transaction;
    }

    public async Task AdjustBalanceAsync(Guid financialAccountId, decimal delta, CancellationToken ct = default)
    {
        var account = await db.FinancialAccounts.FirstAsync(a => a.Id == financialAccountId, ct);
        account.CurrentBalance += delta;
    }
}
