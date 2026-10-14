using Microsoft.EntityFrameworkCore;
using Nexora.Application.Finance.Abstractions;
using Nexora.Application.Abstractions;
using Nexora.Domain.Accounting;
using Nexora.Domain.Finance;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Finance;

public sealed class EfJournalEntryStore(NexoraDbContext db, ITenantContext tenant) : IJournalEntryStore
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

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

    /// <summary>
    /// Ajout atomique au solde (audit 2026-10, A-018). Avant : lecture du compte, ajout en mémoire puis écriture de
    /// la valeur calculée — deux opérations simultanées sur le même compte (ventes et transferts) écrasaient l'une
    /// l'autre (525 HTG perdus sur 20 opérations). Désormais <c>current_balance = current_balance + delta</c> en base :
    /// la ligne reste verrouillée jusqu'à la fin de la transaction de l'appelant et aucune mise à jour n'est perdue.
    /// </summary>
    public async Task AdjustBalanceAsync(Guid financialAccountId, decimal delta, CancellationToken ct = default)
    {
        var updated = await db.FinancialAccounts.Where(a => a.Id == financialAccountId)
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.CurrentBalance, a => a.CurrentBalance + delta), ct);
        if (updated == 0)
            throw new InvalidOperationException($"Compte financier {financialAccountId} introuvable.");

        // Entité déjà suivie par le contexte : l'aligner (valeur courante ET originale) pour qu'EF ne réécrive pas
        // l'ancien solde à l'enregistrement.
        if (db.FinancialAccounts.Local.FirstOrDefault(a => a.Id == financialAccountId) is { } tracked)
        {
            var p = db.Entry(tracked).Property(a => a.CurrentBalance);
            p.CurrentValue += delta;
            p.OriginalValue = p.CurrentValue;
        }
    }
}
