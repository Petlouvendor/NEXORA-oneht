using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Contracts.Credit;
using Nexora.Domain.Common;

namespace Nexora.Application.Credit;

public interface ICollectionsStore
{
    Task<string?> CustomerNameAsync(Guid customerId, CancellationToken ct = default);
    Task<string> CustomerCurrencyAsync(Guid customerId, CancellationToken ct = default);
    Task<Guid> CreatePromiseAsync(Guid customerId, decimal amount, decimal snapshotRemaining, DateOnly date, string channel, IReadOnlyList<Guid> receivableIds, string? note, Guid? userId, CancellationToken ct = default);
    Task<PromiseSettleData?> GetPromiseAsync(Guid promiseId, CancellationToken ct = default);
    Task SettlePromiseAsync(Guid promiseId, string status, decimal keptAmount, CancellationToken ct = default);
    Task<Guid> OpenDisputeAsync(Guid customerId, Guid? receivableId, decimal amount, string reason, Guid? userId, CancellationToken ct = default);
    Task<DisputeCore?> GetDisputeAsync(Guid disputeId, CancellationToken ct = default);
    Task ResolveDisputeAsync(Guid disputeId, string status, string resolution, Guid? userId, CancellationToken ct = default);
    Task SetReceivableStatusAsync(Guid receivableId, string status, CancellationToken ct = default);
    Task<ReceivableCore?> GetReceivableAsync(Guid receivableId, CancellationToken ct = default);
    Task<bool> ActiveBlockExistsAsync(Guid customerId, string blockType, CancellationToken ct = default);
    Task<Guid> CreateBlockAsync(Guid customerId, string blockType, string reason, Guid? userId, CancellationToken ct = default);
    Task<BlockCore?> GetBlockAsync(Guid blockId, CancellationToken ct = default);
    Task ReleaseBlockAsync(Guid blockId, string reason, Guid? userId, CancellationToken ct = default);
    Task<int> ActiveBlockCountAsync(Guid customerId, CancellationToken ct = default);
    Task SetCustomerStatusAsync(Guid customerId, string status, CancellationToken ct = default);
    Task<Guid> CreateWriteOffAsync(Guid receivableId, Guid customerId, decimal amount, string reason, Guid journalEntryId, Guid? userId, CancellationToken ct = default);
    Task ApplyWriteOffToReceivableAsync(Guid receivableId, decimal amount, CancellationToken ct = default);
}

public sealed record PromiseSettleData(Guid CustomerId, decimal Amount, decimal SnapshotRemaining, IReadOnlyList<Guid> ReceivableIds, string Status);
public sealed record DisputeCore(Guid CustomerId, Guid? ReceivableId, string Status);
public sealed record ReceivableCore(Guid CustomerId, string Currency, decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount, string Status);
public sealed record BlockCore(Guid CustomerId, string BlockType, bool IsActive);

/// <summary>
/// Recouvrement & crédit client (Phase 27 §21-27, §37) : promesses de paiement, litiges,
/// blocages, radiation contrôlée. Les créances conservent leur montant original (§12, §37).
/// </summary>
public sealed class CollectionsService(
    ICollectionsStore store, IFinancialEngine engine, IUnitOfWork uow,
    ITenantContext tenant, IDateTimeProvider clock)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw new InvalidOperationException("Aucune entreprise active.");

    // --- Promesse de paiement (§24) ---------------------------------
    public async Task<Result<Guid>> CreatePromiseAsync(CreatePromiseRequest r, CancellationToken ct = default)
    {
        if (r.Amount <= 0m) return Error.Validation("credit.promise_amount", "Le montant promis doit être positif.");
        if (r.ReceivableIds is null || r.ReceivableIds.Count == 0)
            return Error.Validation("credit.promise_receivables", "Une promesse doit viser au moins une créance (§24).");
        if (await store.CustomerNameAsync(r.CustomerId, ct) is null)
            return Error.NotFound("credit.customer_not_found", "Client introuvable.");
        var channels = new[] { "phone", "email", "sms", "whatsapp", "visit", "internal" };
        var channel = channels.Contains(r.Channel) ? r.Channel : "phone";

        decimal snapshot = 0m;
        foreach (var rid in r.ReceivableIds)
        {
            if (await store.GetReceivableAsync(rid, ct) is not { } rc)
                return Error.Validation("credit.promise_receivable_unknown", "Créance visée introuvable.");
            if (rc.CustomerId != r.CustomerId)
                return Error.Validation("credit.promise_receivable_customer", "Une créance visée n'appartient pas au client.");
            snapshot += rc.RemainingAmount;
        }

        var id = await store.CreatePromiseAsync(r.CustomerId, r.Amount, snapshot, r.PromisedDate, channel,
            r.ReceivableIds, r.Note, tenant.UserId, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    /// <summary>Solde la promesse : compare le solde des créances visées à celui figé à la
    /// création. Ce qui a baissé = tenu ; classe kept / partially_kept / broken (§24).</summary>
    public async Task<Result<string>> SettlePromiseAsync(Guid promiseId, CancellationToken ct = default)
    {
        var p = await store.GetPromiseAsync(promiseId, ct);
        if (p is null) return Error.NotFound("credit.promise_not_found", "Promesse introuvable.");
        if (p.Status is not ("open")) return Error.Conflict("credit.promise_closed", "Promesse déjà soldée.");

        decimal currentRemaining = 0m;
        foreach (var rid in p.ReceivableIds)
            if (await store.GetReceivableAsync(rid, ct) is { } rc)
                currentRemaining += rc.RemainingAmount;

        var paidSince = Math.Max(0m, p.SnapshotRemaining - currentRemaining);
        var kept = Math.Min(p.Amount, paidSince);
        var status = kept >= p.Amount ? "kept" : kept > 0m ? "partially_kept" : "broken";
        await store.SettlePromiseAsync(promiseId, status, kept, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(status);
    }

    // --- Litige (§25) --------------------------------------------
    public async Task<Result<Guid>> OpenDisputeAsync(OpenDisputeRequest r, CancellationToken ct = default)
    {
        if (r.DisputedAmount <= 0m) return Error.Validation("credit.dispute_amount", "Le montant contesté doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("credit.dispute_reason", "Un motif est obligatoire (§25).");
        if (await store.CustomerNameAsync(r.CustomerId, ct) is null)
            return Error.NotFound("credit.customer_not_found", "Client introuvable.");

        var id = await store.OpenDisputeAsync(r.CustomerId, r.ReceivableId, r.DisputedAmount, r.Reason.Trim(), tenant.UserId, ct);
        if (r.ReceivableId is { } rid && await store.GetReceivableAsync(rid, ct) is { Status: "OPEN" or "OVERDUE" or "PARTIALLY_PAID" })
            await store.SetReceivableStatusAsync(rid, "IN_DISPUTE", ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> ResolveDisputeAsync(Guid disputeId, ResolveDisputeRequest r, CancellationToken ct = default)
    {
        var outcomes = new[] { "resolved_customer", "resolved_company", "cancelled" };
        if (!outcomes.Contains(r.Outcome)) return Error.Validation("credit.dispute_outcome", "Issue de litige inconnue.");
        var d = await store.GetDisputeAsync(disputeId, ct);
        if (d is null) return Error.NotFound("credit.dispute_not_found", "Litige introuvable.");
        if (d.Status is "resolved_customer" or "resolved_company" or "cancelled")
            return Error.Conflict("credit.dispute_closed", "Litige déjà clos.");

        await store.ResolveDisputeAsync(disputeId, r.Outcome, r.Resolution ?? "", tenant.UserId, ct);
        // La créance sort du litige : rétablie côté entreprise, à traiter par avoir/radiation côté client.
        if (d.ReceivableId is { } rid && await store.GetReceivableAsync(rid, ct) is { Status: "IN_DISPUTE" })
            await store.SetReceivableStatusAsync(rid, "OPEN", ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Blocage / déblocage (§26-27) ---------------------------
    public async Task<Result<Guid>> BlockCustomerAsync(BlockCustomerRequest r, CancellationToken ct = default)
    {
        var types = new[] { "credit", "sales", "delivery", "account" };
        if (!types.Contains(r.BlockType)) return Error.Validation("credit.block_type", "Type de blocage inconnu.");
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("credit.block_reason", "Un motif est obligatoire (§26).");
        if (await store.CustomerNameAsync(r.CustomerId, ct) is null)
            return Error.NotFound("credit.customer_not_found", "Client introuvable.");
        if (await store.ActiveBlockExistsAsync(r.CustomerId, r.BlockType, ct))
            return Error.Conflict("credit.already_blocked", $"Ce client a déjà un blocage « {r.BlockType} » actif.");

        var id = await store.CreateBlockAsync(r.CustomerId, r.BlockType, r.Reason.Trim(), tenant.UserId, ct);
        if (r.BlockType is "credit" or "sales" or "account")
            await store.SetCustomerStatusAsync(r.CustomerId, "blocked", ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> ReleaseBlockAsync(Guid blockId, ReleaseBlockRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("credit.release_reason", "Un motif de déblocage est obligatoire (§27).");
        var b = await store.GetBlockAsync(blockId, ct);
        if (b is null) return Error.NotFound("credit.block_not_found", "Blocage introuvable.");
        if (!b.IsActive) return Error.Conflict("credit.block_released", "Blocage déjà levé.");

        await store.ReleaseBlockAsync(blockId, r.Reason.Trim(), tenant.UserId, ct);
        if (await store.ActiveBlockCountAsync(b.CustomerId, ct) == 0)
            await store.SetCustomerStatusAsync(b.CustomerId, "active", ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Radiation de créance (§37) ----------------------------
    public async Task<Result<WriteOffResultDto>> WriteOffReceivableAsync(
        Guid receivableId, WriteOffReceivableRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("credit.writeoff_reason", "Un motif est obligatoire (§37).");
        var rc = await store.GetReceivableAsync(receivableId, ct);
        if (rc is null) return Error.NotFound("credit.receivable_not_found", "Créance introuvable.");
        if (rc.Status is "PAID" or "WRITTEN_OFF" or "CANCELLED")
            return Error.Conflict("credit.receivable_closed", "Cette créance est déjà soldée ou radiée.");
        if (r.Amount <= 0m || r.Amount != rc.RemainingAmount)
            return Error.Validation("credit.writeoff_amount",
                $"La radiation doit porter sur le solde exact restant ({rc.RemainingAmount}).");

        return await uow.ExecuteInTransactionAsync<Result<WriteOffResultDto>>(async token =>
        {
            var now = clock.UtcNow;
            var posted = await engine.HandleAsync(new ReceivableWrittenOff(
                Company, r.OperationUuid, now, receivableId, rc.CustomerId,
                new Money(r.Amount, rc.Currency), r.Reason.Trim()) { UserId = tenant.UserId }, token);
            if (!posted.IsSuccess) throw new InvalidOperationException(posted.Error!.Message);

            await store.ApplyWriteOffToReceivableAsync(receivableId, r.Amount, token);
            var woId = await store.CreateWriteOffAsync(receivableId, rc.CustomerId, r.Amount, r.Reason.Trim(),
                posted.Value!.JournalEntryId, tenant.UserId, token);
            await uow.SaveChangesAsync(token);
            return Result.Success(new WriteOffResultDto(woId, receivableId, r.Amount, posted.Value.JournalEntryId));
        }, ct);
    }
}
