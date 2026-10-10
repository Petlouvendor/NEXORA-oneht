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
    Task SettlePromiseAsync(Guid promiseId, string status, decimal keptAmount, Guid? userId, CancellationToken ct = default);
    Task CancelPromiseAsync(Guid promiseId, string reason, Guid? userId, CancellationToken ct = default);
    /// <summary>Une promesse ouverte vise-t-elle déjà l'une de ces créances ?</summary>
    Task<bool> OpenPromiseCoversAsync(IReadOnlyList<Guid> receivableIds, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> OpenPromiseIdsAsync(CancellationToken ct = default);
    Task<bool> OpenDisputeOnReceivableAsync(Guid receivableId, CancellationToken ct = default);
    /// <summary>Sortie de litige : la créance retrouve le statut que ses règlements justifient (ouverte, partielle, payée).</summary>
    Task RestoreReceivableAfterDisputeAsync(Guid receivableId, CancellationToken ct = default);
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

public sealed record PromiseSettleData(Guid CustomerId, decimal Amount, decimal SnapshotRemaining, IReadOnlyList<Guid> ReceivableIds, string Status,
    DateOnly PromisedDate = default);
public sealed record DisputeCore(Guid CustomerId, Guid? ReceivableId, string Status);
public sealed record ReceivableCore(Guid CustomerId, string Currency, decimal OriginalAmount, decimal PaidAmount, decimal RemainingAmount, string Status);
public sealed record BlockCore(Guid CustomerId, string BlockType, bool IsActive);

/// <summary>
/// Recouvrement & crédit client (Phase 27 §21-27, §37) : promesses de paiement, litiges,
/// blocages, radiation contrôlée. Les créances conservent leur montant original (§12, §37).
/// </summary>
public sealed class CollectionsService(
    ICollectionsStore store, IFinancialEngine engine, IUnitOfWork uow,
    ITenantContext tenant, IDateTimeProvider clock, IBusinessCalendar calendar)
{
    private Guid Company => tenant.ActiveCompanyId
        ?? throw TenantErrors.ActiveCompanyRequired();

    // --- Promesse de paiement (§24) ---------------------------------
    private static readonly string[] Channels = ["phone", "email", "sms", "whatsapp", "visit", "internal"];
    private static readonly string[] OpenReceivable = ["OPEN", "PARTIALLY_PAID", "OVERDUE"];
    private DateOnly Today => calendar.Today;

    public async Task<Result<Guid>> CreatePromiseAsync(CreatePromiseRequest r, CancellationToken ct = default)
    {
        if (r.Amount <= 0m) return Error.Validation("credit.promise_amount", "Le montant promis doit être positif.");
        if (r.ReceivableIds is null || r.ReceivableIds.Count == 0)
            return Error.Validation("credit.promise_receivables", "Choisissez au moins une créance concernée par la promesse.");
        if (!Channels.Contains(r.Channel))
            return Error.Validation("credit.promise_channel", $"Canal « {r.Channel} » inconnu.");
        if (r.PromisedDate < Today)
            return Error.Validation("credit.promise_date", "La date promise ne peut pas être dans le passé.");
        if (r.PromisedDate > Today.AddDays(365))
            return Error.Validation("credit.promise_date", "La date promise ne peut pas dépasser un an : utilisez un échéancier.");
        if (r.Note is { Length: > 500 }) return Error.Validation("credit.promise_note", "Note limitée à 500 caractères.");
        if (await store.CustomerNameAsync(r.CustomerId, ct) is null)
            return Error.NotFound("credit.customer_not_found", "Client introuvable.");

        var ids = r.ReceivableIds.Distinct().ToList();
        decimal snapshot = 0m;
        string? currency = null;
        foreach (var rid in ids)
        {
            if (await store.GetReceivableAsync(rid, ct) is not { } rc)
                return Error.Validation("credit.promise_receivable_unknown", "Créance visée introuvable.");
            if (rc.CustomerId != r.CustomerId)
                return Error.Validation("credit.promise_receivable_customer", "Une créance visée n'appartient pas au client.");
            if (rc.Status == "IN_DISPUTE")
                return Error.Conflict("credit.promise_disputed", "Une créance visée est en litige : tranchez le litige d'abord.");
            if (!OpenReceivable.Contains(rc.Status) || rc.RemainingAmount <= 0m)
                return Error.Conflict("credit.promise_receivable_closed", "Une créance visée est déjà soldée, annulée ou radiée.");
            if (currency is not null && currency != rc.Currency)
                return Error.Validation("credit.promise_currency", "Les créances d'une promesse doivent être dans la même devise.");
            currency = rc.Currency;
            snapshot += rc.RemainingAmount;
        }
        if (r.Amount > snapshot)
            return Error.Validation("credit.promise_amount", $"Le montant promis dépasse le reste dû sur ces créances ({snapshot:0.00} {currency}).");
        if (await store.OpenPromiseCoversAsync(ids, ct))
            return Error.Conflict("credit.promise_overlap", "Une promesse en cours vise déjà l'une de ces créances.");

        var id = await store.CreatePromiseAsync(r.CustomerId, r.Amount, snapshot, r.PromisedDate, r.Channel,
            ids, string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim(), tenant.UserId, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    /// <summary>Solde la promesse : compare le solde des créances visées à celui figé à la création. Ce qui a
    /// baissé = tenu ; classe kept / partially_kept / broken (§24). Avant l'échéance, seule une promesse
    /// entièrement tenue peut être close : sinon le client a encore jusqu'à la date promise.</summary>
    public async Task<Result<string>> SettlePromiseAsync(Guid promiseId, CancellationToken ct = default)
    {
        var p = await store.GetPromiseAsync(promiseId, ct);
        if (p is null) return Error.NotFound("credit.promise_not_found", "Promesse introuvable.");
        if (p.Status is not ("open")) return Error.Conflict("credit.promise_closed", "Promesse déjà soldée ou annulée.");
        var (status, kept) = await EvaluateAsync(p, ct);
        if (status != "kept" && p.PromisedDate >= Today)
            return Error.Conflict("credit.promise_not_due",
                $"Échéance le {p.PromisedDate:dd/MM/yyyy} : la promesse ne peut pas être déclarée non tenue avant cette date ({kept:0.00} reçu sur {p.Amount:0.00}).");
        await store.SettlePromiseAsync(promiseId, status, kept, tenant.UserId, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(status);
    }

    private async Task<(string Status, decimal Kept)> EvaluateAsync(PromiseSettleData p, CancellationToken ct)
    {
        decimal currentRemaining = 0m;
        foreach (var rid in p.ReceivableIds)
            if (await store.GetReceivableAsync(rid, ct) is { } rc)
                currentRemaining += rc.RemainingAmount;
        var paidSince = Math.Max(0m, p.SnapshotRemaining - currentRemaining);
        var kept = Math.Min(p.Amount, paidSince);
        return (kept >= p.Amount ? "kept" : kept > 0m ? "partially_kept" : "broken", kept);
    }

    /// <summary>Planificateur (job <c>credit.settle_promises</c>) : classe les promesses échues, et celles déjà
    /// entièrement tenues. Renvoie le nombre de promesses closes.</summary>
    public async Task<int> SettleDuePromisesAsync(CancellationToken ct = default)
    {
        var closed = 0;
        foreach (var id in await store.OpenPromiseIdsAsync(ct))
        {
            if (await store.GetPromiseAsync(id, ct) is not { Status: "open" } p) continue;
            var (status, kept) = await EvaluateAsync(p, ct);
            if (status != "kept" && p.PromisedDate >= Today) continue;
            await store.SettlePromiseAsync(id, status, kept, null, ct);
            closed++;
        }
        if (closed > 0) await uow.SaveChangesAsync(ct);
        return closed;
    }

    public async Task<Result> CancelPromiseAsync(Guid promiseId, CancelPromiseRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 3)
            return Error.Validation("credit.promise_cancel_reason", "Un motif d'annulation est obligatoire.");
        var p = await store.GetPromiseAsync(promiseId, ct);
        if (p is null) return Error.NotFound("credit.promise_not_found", "Promesse introuvable.");
        if (p.Status != "open") return Error.Conflict("credit.promise_closed", "Promesse déjà soldée ou annulée.");
        await store.CancelPromiseAsync(promiseId, r.Reason.Trim(), tenant.UserId, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    // --- Litige (§25) --------------------------------------------
    public async Task<Result<Guid>> OpenDisputeAsync(OpenDisputeRequest r, CancellationToken ct = default)
    {
        if (r.DisputedAmount <= 0m) return Error.Validation("credit.dispute_amount", "Le montant contesté doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 3)
            return Error.Validation("credit.dispute_reason", "Un motif est obligatoire (§25).");
        if (r.Reason.Length > 500) return Error.Validation("credit.dispute_reason", "Motif limité à 500 caractères.");
        if (await store.CustomerNameAsync(r.CustomerId, ct) is null)
            return Error.NotFound("credit.customer_not_found", "Client introuvable.");
        if (r.ReceivableId is { } target)
        {
            if (await store.GetReceivableAsync(target, ct) is not { } rc)
                return Error.Validation("credit.dispute_receivable", "Créance introuvable.");
            if (rc.CustomerId != r.CustomerId)
                return Error.Validation("credit.dispute_receivable", "Cette créance n'appartient pas au client.");
            if (rc.Status is "PAID" or "WRITTEN_OFF" or "CANCELLED")
                return Error.Conflict("credit.dispute_receivable_closed", "Cette créance est déjà soldée, annulée ou radiée.");
            if (r.DisputedAmount > rc.RemainingAmount)
                return Error.Validation("credit.dispute_amount", $"Le montant contesté dépasse le reste dû ({rc.RemainingAmount:0.00} {rc.Currency}).");
            if (await store.OpenDisputeOnReceivableAsync(target, ct))
                return Error.Conflict("credit.dispute_exists", "Un litige est déjà ouvert sur cette créance.");
        }

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
        if (string.IsNullOrWhiteSpace(r.Resolution) || r.Resolution.Trim().Length < 3)
            return Error.Validation("credit.dispute_resolution", "Décrivez la résolution (elle reste au dossier).");
        var d = await store.GetDisputeAsync(disputeId, ct);
        if (d is null) return Error.NotFound("credit.dispute_not_found", "Litige introuvable.");
        if (d.Status is "resolved_customer" or "resolved_company" or "cancelled")
            return Error.Conflict("credit.dispute_closed", "Litige déjà clos.");

        await store.ResolveDisputeAsync(disputeId, r.Outcome, r.Resolution.Trim(), tenant.UserId, ct);
        // La créance sort du litige avec le statut que ses règlements justifient (une créance partiellement
        // payée redevenait « ouverte ») ; côté client, l'issue se traduit ensuite par un avoir ou une annulation.
        if (d.ReceivableId is { } rid && await store.GetReceivableAsync(rid, ct) is { Status: "IN_DISPUTE" })
            await store.RestoreReceivableAfterDisputeAsync(rid, ct);
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
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 3)
            return Error.Validation("credit.writeoff_reason", "Un motif est obligatoire (§37).");
        var rc = await store.GetReceivableAsync(receivableId, ct);
        if (rc is null) return Error.NotFound("credit.receivable_not_found", "Créance introuvable.");
        if (rc.Status is "PAID" or "WRITTEN_OFF" or "CANCELLED")
            return Error.Conflict("credit.receivable_closed", "Cette créance est déjà soldée ou radiée.");
        if (rc.Status == "IN_DISPUTE")
            return Error.Conflict("credit.receivable_in_dispute", "Cette créance est en litige : tranchez le litige avant de la passer en perte.");
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
