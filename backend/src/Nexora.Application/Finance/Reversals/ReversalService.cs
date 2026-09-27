using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Abstractions;
using Nexora.Domain.Accounting;
using Nexora.Domain.Common;

namespace Nexora.Application.Finance.Reversals;

public interface IReversalService
{
    /// <summary>Extourne d'une écriture validée (Phase 4 §28) : lien source → correction, motif exigé.</summary>
    Task<Result<Guid>> ReverseAsync(Guid journalEntryId, string reason, DateOnly? reversalDate = null, CancellationToken ct = default);
}

public sealed class ReversalService(
    IJournalEntryStore journalStore,
    IAccountingPeriods periods,
    IDocumentNumberGenerator numbers,
    IUnitOfWork uow,
    ITenantContext tenant,
    ILogger<ReversalService> logger) : IReversalService
{
    public async Task<Result<Guid>> ReverseAsync(
        Guid journalEntryId, string reason, DateOnly? reversalDate = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Error.Validation("accounting.reason_required", "Un motif d'extourne est obligatoire.");

        var source = await journalStore.GetWithLinesAsync(journalEntryId, ct);
        if (source is null) return Error.NotFound("accounting.entry_not_found", "Écriture introuvable.");
        if (source.Status != JournalEntryStatus.Posted)
            return Error.Conflict("accounting.entry_not_posted", "Seule une écriture validée peut être extournée.");

        var date = reversalDate ?? DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            var newId = await uow.ExecuteInTransactionAsync(async token =>
            {
                var period = await periods.ResolveOpenPeriodAsync(date, token);
                // Les extournes sont centralisées dans le journal des opérations diverses (§28).
                var odJournalId = await journalStore.ResolveJournalAsync("OD", token);
                var number = await numbers.NextAsync("journal:OD", null, token);

                var reversal = new JournalEntry
                {
                    Id = Guid.NewGuid(),
                    CompanyId = source.CompanyId,
                    JournalId = odJournalId,
                    AccountingPeriodId = period.Id,
                    EntryNumber = number,
                    EntryDate = date,
                    Origin = "reversal",
                    SourceType = "reversal",
                    SourceId = source.Id,
                    Description = $"Extourne de {source.EntryNumber} — {reason}",
                    Status = JournalEntryStatus.Posted,
                    PostedAt = DateTimeOffset.UtcNow,
                    PostedBy = tenant.UserId,
                    ReversalOfEntryId = source.Id,
                    CreatedBy = tenant.UserId,
                };

                var n = 1;
                foreach (var l in source.Lines)
                {
                    reversal.Lines.Add(new JournalEntryLine
                    {
                        Id = Guid.NewGuid(),
                        CompanyId = source.CompanyId,
                        AccountId = l.AccountId,
                        LineNumber = n++,
                        Description = "Extourne",
                        Debit = l.Credit,          // inversion débit ↔ crédit
                        Credit = l.Debit,
                        CurrencyId = l.CurrencyId,
                        ExchangeRateId = l.ExchangeRateId,
                        BaseDebit = l.BaseCredit,
                        BaseCredit = l.BaseDebit,
                        ThirdPartyType = l.ThirdPartyType,
                        ThirdPartyId = l.ThirdPartyId,
                        CostCenterId = l.CostCenterId,
                    });
                }

                var saved = await journalStore.AddAsync(reversal, token);
                await journalStore.MarkReversedAsync(source.Id, saved.Id, token);
                return saved.Id;
            }, ct);

            logger.LogInformation("Écriture {Source} extournée par {Reversal} — {Reason}", journalEntryId, newId, reason);
            return Result.Success(newId);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("accounting.reversal_failed", ex.Message);
        }
    }
}
