using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Mobile;
using Nexora.Application.Sales;
using Nexora.Application.Sync;
using Nexora.Contracts.Mobile;
using Nexora.Contracts.Sync;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Sync;

/// <summary>
/// Moteur de synchronisation offline (Phase 10 §11-16). Une opération offline est rejouée
/// via <c>SalesService.CheckoutAsync</c> — même règles, même idempotence (operation_uuid).
/// Aucune donnée n'est écrasée silencieusement : un désaccord devient un <c>sync_conflict</c>.
/// Depuis la Phase 44 (§25-29), le même moteur rejoue aussi les opérations terrain (livraison,
/// intervention) — aucune file offline séparée n'est créée pour le mobile.
/// </summary>
public sealed class SqlSyncService(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IDateTimeProvider clock,
    SalesService sales, IDeliveryService delivery, IFieldInterventionService interventions,
    Nexora.Application.Customers.ICustomerAccountService customerAccounts, IPermissionService permissions) : ISyncService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed record DeviceRow(Guid Id, string Status);
    private sealed record OpRow(Guid Id, string Status, Guid? ResultEntityId);
    private sealed record PolicyRow(bool AllowOfflineSales, bool AllowOfflineCredit, decimal OfflineCreditLimit, bool AllowOfflineCustomerPayment,
        bool AllowOfflineDiscount = true, int MaxOfflineMinutes = 0, string[]? AllowedPaymentMethods = null);
    private sealed record TenderTypeRow(Guid? AccountId, Guid? MethodId, string Type);
    private sealed record StoredOpRow(Guid Id, Guid DeviceId, Guid OperationUuid, string EntityType, string OperationType, string Payload,
        DateTimeOffset? ClientCreatedAt, short Priority);
    private sealed record CountRow(string Status, int Count);
    private sealed record GuidRow(Guid? Value);
    private sealed record InstantRow(DateTimeOffset? Value);
    private sealed record ConflictRow(Guid Id, Guid SyncOperationId, string ConflictType, string EntityType, Guid? EntityId, string Resolution, string? Detail, string? RecommendedAction);
    private sealed record DeviceListRow(Guid Id, string DeviceIdentifier, string? Name, string DeviceType, string Status, DateTimeOffset? LastSyncAt, DateTimeOffset? LastSeenAt, int Pending);

    public async Task<Result<PushBatchResult>> PushBatchAsync(PushBatchRequest request, CancellationToken ct = default)
    {
        if (request.Operations.Count == 0) return Error.Validation("sync.empty_batch", "Lot vide.");

        var device = await db.Database.SqlQuery<DeviceRow>($"""
            SELECT id AS "Id", status AS "Status" FROM nexora.sync_device
            WHERE id = {request.DeviceId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (device is null) return Error.NotFound("sync.device", "Appareil inconnu.");
        if (device.Status == "revoked") return Error.Forbidden("sync.device_revoked", "Appareil révoqué : synchronisation refusée (§31).");
        if (device.Status == "suspended") return Error.Forbidden("sync.device_suspended", "Appareil suspendu : synchronisation refusée.");

        var policy = await LoadPolicyAsync(ct);
        // Dernier contact de l'appareil AVANT ce lot : repère de la durée passée hors ligne.
        var lastSeen = (await db.Database.SqlQuery<InstantRow>($"""
            SELECT last_seen_at AS "Value" FROM nexora.sync_device WHERE id = {request.DeviceId}
            """).ToListAsync(ct)).FirstOrDefault()?.Value;

        var batchId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.sync_batch (id, company_id, device_id, operation_count)
            VALUES ({batchId}, {Company}, {request.DeviceId}, {request.Operations.Count})
            """, ct);

        var results = new List<SyncOperationResult>();
        int confirmed = 0, conflicts = 0, failed = 0;

        foreach (var op in request.Operations.OrderBy(o => o.Priority).ThenBy(o => o.ClientSequence ?? 0))
        {
            var r = await ProcessAsync(batchId, request.DeviceId, op, policy, lastSeen, ct);
            results.Add(r);
            if (r.Status == "SYNCED") confirmed++;
            else if (r.Status == "CONFLICT") conflicts++;
            else failed++;
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_batch SET confirmed_count = {confirmed}, conflict_count = {conflicts},
                failed_count = {failed}, updated_at = now() WHERE id = {batchId}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_device SET last_sync_at = now(), last_seen_at = now(), updated_at = now()
            WHERE id = {request.DeviceId}
            """, ct);
        await uow.SaveChangesAsync(ct);

        return Result.Success(new PushBatchResult(batchId, confirmed, conflicts, failed, results));
    }

    private async Task<PolicyRow> LoadPolicyAsync(CancellationToken ct)
        => await db.Database.SqlQuery<PolicyRow>($"""
            SELECT allow_offline_sales AS "AllowOfflineSales", allow_offline_credit AS "AllowOfflineCredit",
                   offline_credit_limit AS "OfflineCreditLimit", allow_offline_customer_payment AS "AllowOfflineCustomerPayment",
                   allow_offline_discount AS "AllowOfflineDiscount", max_offline_minutes AS "MaxOfflineMinutes",
                   ARRAY(SELECT jsonb_array_elements_text(allowed_payment_methods)) AS "AllowedPaymentMethods"
            FROM nexora.offline_policy WHERE company_id = {Company}
            """).FirstOrDefaultAsync(ct) ?? new PolicyRow(true, false, 0m, true);

    private async Task<SyncOperationResult> ProcessAsync(
        Guid batchId, Guid deviceId, SyncOperationInput op, PolicyRow policy, DateTimeOffset? lastSeen, CancellationToken ct)
    {
        // Idempotence : opération déjà connue (§13).
        var existing = await db.Database.SqlQuery<OpRow>($"""
            SELECT id AS "Id", status AS "Status", result_entity_id AS "ResultEntityId"
            FROM nexora.sync_operation WHERE operation_uuid = {op.OperationUuid}
            """).FirstOrDefaultAsync(ct);
        if (existing is { Status: "SYNCED" })
            return new SyncOperationResult(op.OperationUuid, "SYNCED", existing.ResultEntityId, null, "Déjà synchronisée.");
        if (existing is { Status: "CONFLICT" })
        {
            var cid = (await db.Database.SqlQuery<GuidRow>($"""
                SELECT id AS "Value" FROM nexora.sync_conflict WHERE sync_operation_id = {existing.Id} ORDER BY created_at DESC LIMIT 1
                """).ToListAsync(ct)).FirstOrDefault()?.Value;
            return new SyncOperationResult(op.OperationUuid, "CONFLICT", null, cid, "Conflit déjà enregistré.");
        }

        Guid opId;
        if (existing is null)
        {
            opId = Guid.NewGuid();
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.sync_operation
                    (id, company_id, device_id, operation_uuid, entity_type, entity_id, operation_type,
                     payload, client_sequence, client_created_at, status, priority, sync_batch_id, attempts, last_attempt_at)
                VALUES ({opId}, {Company}, {deviceId}, {op.OperationUuid}, {op.EntityType}, NULL, {op.OperationType},
                        {op.Payload.GetRawText()}::jsonb, {op.ClientSequence}, {op.ClientCreatedAt},
                        'SYNCING', {(short)op.Priority}, {batchId}, 1, now())
                """, ct);
        }
        else
        {
            opId = existing.Id;
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_operation SET status = 'SYNCING', attempts = attempts + 1,
                    last_attempt_at = now(), sync_batch_id = {batchId}, updated_at = now()
                WHERE id = {opId}
                """, ct);
        }

        // Finalisation phase 12 : le lot n'exige plus le droit de vendre (un livreur n'en a pas) — chaque opération est
        // contrôlée selon son type. Avant, un caissier pouvait conclure une livraison hors ligne sans droit terrain.
        var required = op.EntityType switch
        {
            "sale" => Nexora.Application.Security.Permissions.Pos.Sell,
            "delivery_stop" or "field_intervention" => Nexora.Application.Security.Permissions.FieldOps.Manage,
            _ => null,
        };
        if (required is not null && !(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(required))
        {
            await FailAsync(opId, "sync.forbidden", $"Permission requise pour cette opération : {required}.", ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, $"Permission requise : {required}.");
        }

        // Dispatch — Phase 10 : la vente est l'opération offline critique (§9).
        if (op is { EntityType: "sale", OperationType: "create" })
            return await ProcessSaleAsync(opId, op, policy, lastSeen, bypassPolicy: false, ct);
        // Phase 44 §25-29 : livraison et intervention terrain rejouent le même moteur.
        if (op is { EntityType: "delivery_stop", OperationType: "update" })
            return await ProcessDeliveryStopAsync(opId, op, ct);
        if (op is { EntityType: "field_intervention", OperationType: "update" })
            return await ProcessFieldInterventionAsync(opId, op, ct);
        // Module clients §24 : versement client saisi hors ligne, rejoué par le même moteur.
        if (op is { EntityType: "customer_payment", OperationType: "create" })
            return await ProcessCustomerPaymentAsync(opId, op, policy, ct);

        await FailAsync(opId, "sync.unsupported", $"Type d'opération « {op.EntityType}/{op.OperationType} » non pris en charge hors ligne.", ct);
        return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, "Type non pris en charge.");
    }

    // --- Module clients §24 : versement client hors ligne ---------------------

    private sealed record CustomerPaymentSyncPayload(
        Guid CustomerId, decimal Amount, Guid FinancialAccountId, Guid? PaymentMethodId, Guid? CashSessionId,
        string? Reference, string? Note, IReadOnlyList<Nexora.Contracts.Customers.PaymentAllocationInput>? Allocations);

    private async Task<SyncOperationResult> ProcessCustomerPaymentAsync(Guid opId, SyncOperationInput op, PolicyRow policy, CancellationToken ct)
    {
        if (!policy.AllowOfflineCustomerPayment)
            return await ConflictAsync(opId, op, "validation", "Les versements clients hors ligne sont interdits par la politique (§24).",
                "Saisir le versement en ligne, ou l'autoriser dans la politique hors ligne.", ct);
        if (!(await permissions.CurrentPermissionsAsync(tenant, ct)).Contains(Nexora.Application.Security.Permissions.Credit.ReceivePayment))
        {
            await FailAsync(opId, "sync.forbidden", "Enregistrer un versement exige la permission credit.receive_payment.", ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, "Permission credit.receive_payment requise.");
        }

        CustomerPaymentSyncPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<CustomerPaymentSyncPayload>(op.Payload.GetRawText(), Json)
                      ?? throw new JsonException("payload nul");
        }
        catch (JsonException ex)
        {
            await FailAsync(opId, "sync.bad_payload", $"Charge utile de versement illisible : {ex.Message}", ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, "Charge utile illisible.");
        }

        // L'identifiant d'opération du poste devient celui du versement : un rejeu ne paie jamais deux fois.
        var result = await customerAccounts.RecordPaymentAsync(payload.CustomerId, new Nexora.Contracts.Customers.RecordCustomerPaymentRequest(
            op.OperationUuid, payload.Amount, payload.FinancialAccountId, payload.PaymentMethodId, payload.CashSessionId,
            payload.Reference, payload.Note, payload.Allocations), ct);
        if (result.IsSuccess)
        {
            var summary = JsonSerializer.Serialize(new { payment = result.Value!.PaymentNumber, amount = result.Value.Amount });
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_operation SET status = 'SYNCED', entity_id = {result.Value.Id},
                    result_entity_id = {result.Value.Id}, result_summary = {summary}::jsonb,
                    error_code = NULL, error_detail = NULL, synced_at = now(), updated_at = now()
                WHERE id = {opId}
                """, ct);
            return new SyncOperationResult(op.OperationUuid, "SYNCED", result.Value.Id, null, result.Value.PaymentNumber);
        }

        var err = result.Error!;
        // Le crédit a été soldé ou modifié entre-temps : conflit à traiter, jamais un doublon ni un écrasement.
        if (err.Code is "credit.overpayment" or "credit.not_payable" or "credit.allocation_exceeds" or "credit.cash_session_closed")
            return await ConflictAsync(opId, op, "credit", err.Message,
                "Vérifier le compte du client au retour en ligne (versement déjà saisi ? crédit soldé ?) puis ressaisir ou rembourser.", ct);
        await FailAsync(opId, err.Code, err.Message, ct);
        return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, err.Message);
    }

    // --- Phase 44 §12, §52 : livraison ---------------------------------------

    private sealed record DeliveryStopSyncPayload(
        Guid StopId, decimal? Latitude, decimal? Longitude, Guid? SignatureAttachmentId,
        Guid? PhotoAttachmentId, decimal? AmountCollected, Guid? PaymentFinancialAccountId);

    private async Task<SyncOperationResult> ProcessDeliveryStopAsync(Guid opId, SyncOperationInput op, CancellationToken ct)
    {
        DeliveryStopSyncPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<DeliveryStopSyncPayload>(op.Payload.GetRawText(), Json)
                      ?? throw new JsonException("payload nul");
        }
        catch (JsonException ex)
        {
            await FailAsync(opId, "sync.bad_payload", $"Charge utile de livraison illisible : {ex.Message}", ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, "Charge utile illisible.");
        }

        var result = await delivery.CompleteStopAsync(payload.StopId, new CompleteDeliveryStopRequest(
            op.OperationUuid, payload.Latitude, payload.Longitude, payload.SignatureAttachmentId,
            payload.PhotoAttachmentId, payload.AmountCollected, payload.PaymentFinancialAccountId), ct);

        if (result.IsSuccess)
        {
            var summary = JsonSerializer.Serialize(new { stopId = payload.StopId, status = result.Value!.Status });
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_operation SET status = 'SYNCED', entity_id = {payload.StopId},
                    result_entity_id = {payload.StopId}, result_summary = {summary}::jsonb,
                    error_code = NULL, error_detail = NULL, synced_at = now(), updated_at = now()
                WHERE id = {opId}
                """, ct);
            return new SyncOperationResult(op.OperationUuid, "SYNCED", payload.StopId, null, "Livraison confirmée.");
        }

        var err = result.Error!;
        if (err.Code == "delivery_stop.already_closed")
            return await ConflictAsync(opId, op, "business", err.Message, "Vérifier l'état de la livraison au retour en ligne.", ct);
        await FailAsync(opId, err.Code, err.Message, ct);
        return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, err.Message);
    }

    // --- Phase 44 §13 : intervention technicien -------------------------------

    private sealed record FieldInterventionSyncPayload(
        Guid InterventionId, IReadOnlyList<ChecklistItemInput> Checklist, IReadOnlyList<PartUsedInput> PartsUsed,
        int TimeSpentMinutes, string ReportText, Guid? SignatureAttachmentId);

    private async Task<SyncOperationResult> ProcessFieldInterventionAsync(Guid opId, SyncOperationInput op, CancellationToken ct)
    {
        FieldInterventionSyncPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<FieldInterventionSyncPayload>(op.Payload.GetRawText(), Json)
                      ?? throw new JsonException("payload nul");
        }
        catch (JsonException ex)
        {
            await FailAsync(opId, "sync.bad_payload", $"Charge utile d'intervention illisible : {ex.Message}", ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, "Charge utile illisible.");
        }

        var result = await interventions.CompleteAsync(payload.InterventionId, new CompleteInterventionRequest(
            op.OperationUuid, payload.Checklist, payload.PartsUsed, payload.TimeSpentMinutes,
            payload.ReportText, payload.SignatureAttachmentId), ct);

        if (result.IsSuccess)
        {
            var summary = JsonSerializer.Serialize(new { interventionId = payload.InterventionId, status = result.Value!.Status });
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_operation SET status = 'SYNCED', entity_id = {payload.InterventionId},
                    result_entity_id = {payload.InterventionId}, result_summary = {summary}::jsonb,
                    error_code = NULL, error_detail = NULL, synced_at = now(), updated_at = now()
                WHERE id = {opId}
                """, ct);
            return new SyncOperationResult(op.OperationUuid, "SYNCED", payload.InterventionId, null, "Intervention confirmée.");
        }

        var err = result.Error!;
        if (err.Code == "field_intervention.already_closed")
            return await ConflictAsync(opId, op, "business", err.Message, "Vérifier l'état de l'intervention au retour en ligne.", ct);
        await FailAsync(opId, err.Code, err.Message, ct);
        return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, err.Message);
    }

    /// <param name="bypassPolicy">Rejeu décidé par un responsable (« le poste a raison ») : la politique hors ligne
    /// n'est pas réappliquée — seuls les contrôles métier de la vente (stock, prix, crédit client) le sont.</param>
    private async Task<SyncOperationResult> ProcessSaleAsync(
        Guid opId, SyncOperationInput op, PolicyRow policy, DateTimeOffset? lastSeen, bool bypassPolicy, CancellationToken ct)
    {
        if (!bypassPolicy && !policy.AllowOfflineSales)
            return await ConflictAsync(opId, op, "validation", "Les ventes hors ligne sont interdites par la politique (§10).",
                "Activer allow_offline_sales ou saisir la vente en ligne.", ct);

        CheckoutCommand cmd;
        try
        {
            cmd = JsonSerializer.Deserialize<CheckoutCommand>(op.Payload.GetRawText(), Json)
                  ?? throw new JsonException("payload nul");
        }
        catch (JsonException ex)
        {
            await FailAsync(opId, "sync.bad_payload", $"Charge utile de vente illisible : {ex.Message}", ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, "Charge utile illisible.");
        }

        cmd = cmd with { OperationUuid = op.OperationUuid };

        if (!bypassPolicy && await PolicyRefusalAsync(cmd, op, policy, lastSeen, ct) is { } refusal)
            return await ConflictAsync(opId, op, "validation", refusal,
                "Vérifier la vente puis l'accepter (« le poste a raison ») pour l'enregistrer, ou l'annuler.", ct);

        // Contrôle de crédit offline (§17) : le crédit projeté est bloqué si la politique l'interdit.
        var paid = cmd.Tenders.Sum(t => t.Amount);
        var estimatedTotal = EstimateTotal(cmd);
        var offlineCredit = Math.Max(0m, estimatedTotal - paid);
        if (!bypassPolicy && offlineCredit > 0m)
        {
            if (!policy.AllowOfflineCredit)
                return await ConflictAsync(opId, op, "credit",
                    "Vente à crédit hors ligne interdite par la politique (§17).",
                    "Encaisser comptant, ou autoriser le crédit offline dans la politique.", ct);
            if (policy.OfflineCreditLimit > 0m && offlineCredit > policy.OfflineCreditLimit)
                return await ConflictAsync(opId, op, "credit",
                    $"Crédit hors ligne {offlineCredit} > plafond {policy.OfflineCreditLimit} (§17).",
                    "Vérifier le crédit client au retour en ligne / créer une dérogation.", ct);
        }

        Nexora.Domain.Common.Result<CheckoutResult> result;
        try
        {
            result = await sales.CheckoutAsync(cmd, ct);
        }
        catch (Exception ex)
        {
            // Le moteur de synchro isole chaque opération : un échec ne confirme jamais une vente (§28).
            await FailAsync(opId, "sync.checkout_error", ex.Message, ct);
            return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, ex.Message);
        }

        if (result.IsSuccess)
        {
            var summary = JsonSerializer.Serialize(new { doc = result.Value!.DocumentNumber, total = result.Value.Total });
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_operation SET status = 'SYNCED', entity_id = {result.Value!.SaleId},
                    result_entity_id = {result.Value.SaleId}, result_summary = {summary}::jsonb,
                    error_code = NULL, error_detail = NULL, synced_at = now(), updated_at = now()
                WHERE id = {opId}
                """, ct);
            return new SyncOperationResult(op.OperationUuid, "SYNCED", result.Value.SaleId, null, result.Value.DocumentNumber);
        }

        var err = result.Error!;
        if (err.Code == "pos.insufficient_stock")
            return await ConflictAsync(opId, op, "stock", err.Message,
                "Réconcilier le stock central / créer un retour pour la vente en trop (§16).", ct);
        if (err.Code == "pos.credit_limit")
            return await ConflictAsync(opId, op, "credit", err.Message,
                "Vérifier le crédit client / créer une dérogation tracée (§17).", ct);

        await FailAsync(opId, err.Code, err.Message, ct);
        return new SyncOperationResult(op.OperationUuid, "FAILED", null, null, err.Message);
    }

    /// <summary>
    /// Politique hors ligne (phase 6) — ce que la vente hors ligne n'avait pas le droit de faire : remise interdite,
    /// moyen de paiement non autorisé, vente faite après la durée maximale sans contact avec le serveur.
    /// </summary>
    private async Task<string?> PolicyRefusalAsync(CheckoutCommand cmd, SyncOperationInput op, PolicyRow policy, DateTimeOffset? lastSeen, CancellationToken ct)
    {
        if (!policy.AllowOfflineDiscount && cmd.Lines.Any(l => l.LineDiscount > 0m))
            return "Remise accordée hors ligne alors que la politique l'interdit.";
        if (policy.AllowedPaymentMethods is { Length: > 0 } allowed && cmd.Tenders.Any(t => t.Amount > 0m))
        {
            var accounts = cmd.Tenders.Select(t => t.FinancialAccountId).Distinct().ToArray();
            var methods = cmd.Tenders.Where(t => t.PaymentMethodId is not null).Select(t => t.PaymentMethodId!.Value).Distinct().ToArray();
            var types = await db.Database.SqlQuery<TenderTypeRow>($"""
                SELECT fa.id AS "AccountId", NULL::uuid AS "MethodId", CASE fa.account_type WHEN 'cash' THEN 'cash' ELSE 'other' END AS "Type"
                FROM nexora.financial_account fa WHERE fa.company_id = {Company} AND fa.id = ANY({accounts})
                UNION ALL
                SELECT NULL::uuid, pm.id, pm.type FROM nexora.payment_method pm WHERE pm.company_id = {Company} AND pm.id = ANY({methods})
                """).ToListAsync(ct);
            foreach (var t in cmd.Tenders.Where(t => t.Amount > 0m))
            {
                var type = t.PaymentMethodId is { } mid
                    ? types.FirstOrDefault(x => x.MethodId == mid)?.Type
                    : types.FirstOrDefault(x => x.AccountId == t.FinancialAccountId)?.Type;
                if (type is null || !allowed.Contains(type, StringComparer.OrdinalIgnoreCase))
                    return $"Moyen de paiement « {type ?? "inconnu"} » non autorisé hors ligne (autorisés : {string.Join(", ", allowed)}).";
            }
        }
        if (policy.MaxOfflineMinutes > 0 && lastSeen is { } seen && op.ClientCreatedAt is { } created
            && (created - seen).TotalMinutes > policy.MaxOfflineMinutes)
            return $"Vente faite {(int)(created - seen).TotalMinutes} min après le dernier contact avec le serveur (maximum {policy.MaxOfflineMinutes} min).";
        return null;
    }

    private static decimal EstimateTotal(CheckoutCommand cmd)
        => cmd.Lines.Sum(l => (l.UnitPriceOverride ?? 0m) * l.Quantity - l.LineDiscount);

    private async Task<SyncOperationResult> ConflictAsync(
        Guid opId, SyncOperationInput op, string type, string detail, string recommended, CancellationToken ct)
    {
        var conflictId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_operation SET status = 'CONFLICT', error_code = {"sync.conflict." + type},
                error_detail = {detail}, updated_at = now() WHERE id = {opId}
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.sync_conflict
                (id, company_id, sync_operation_id, entity_type, entity_id, conflict_type, detail,
                 recommended_action, client_version, resolution)
            VALUES ({conflictId}, {Company}, {opId}, {op.EntityType}, NULL, {type}, {detail},
                    {recommended}, {op.Payload.GetRawText()}::jsonb, 'pending')
            """, ct);
        return new SyncOperationResult(op.OperationUuid, "CONFLICT", null, conflictId, detail);
    }

    private Task FailAsync(Guid opId, string code, string detail, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.sync_operation SET status = 'FAILED', error_code = {code}, error_detail = {detail}, updated_at = now()
            WHERE id = {opId}
            """, ct);

    public async Task<SyncCenterDto> SyncCenterAsync(CancellationToken ct = default)
    {
        var counts = await db.Database.SqlQuery<CountRow>($"""
            SELECT status AS "Status", COUNT(*)::int AS "Count"
            FROM nexora.sync_operation WHERE company_id = {Company} GROUP BY status
            """).ToListAsync(ct);
        int Count(string s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;

        var openConflicts = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.sync_conflict
            WHERE company_id = {Company} AND resolution = 'pending'
            """).FirstAsync(ct);

        var lastSync = (await db.Database.SqlQuery<InstantRow>($"""
            SELECT MAX(last_sync_at) AS "Value" FROM nexora.sync_device WHERE company_id = {Company}
            """).ToListAsync(ct)).FirstOrDefault()?.Value;

        var threshold = await db.Database.SqlQuery<int>($"""
            SELECT COALESCE((SELECT pending_alert_threshold FROM nexora.offline_policy WHERE company_id = {Company}), 50) AS "Value"
            """).FirstAsync(ct);

        var devices = await LoadDevicesAsync(ct);
        var conflicts = (await db.Database.SqlQuery<ConflictRow>($"""
            SELECT id AS "Id", sync_operation_id AS "SyncOperationId", conflict_type AS "ConflictType",
                   entity_type AS "EntityType", entity_id AS "EntityId", resolution AS "Resolution",
                   detail AS "Detail", recommended_action AS "RecommendedAction"
            FROM nexora.sync_conflict WHERE company_id = {Company} AND resolution = 'pending'
            ORDER BY created_at DESC LIMIT 100
            """).ToListAsync(ct))
            .Select(c => new SyncConflictDto(c.Id, c.SyncOperationId, c.ConflictType, c.EntityType, c.EntityId, c.Resolution, c.Detail, c.RecommendedAction))
            .ToList();

        var pending = Count("PENDING") + Count("SYNCING");
        var minutesSince = lastSync is { } ls ? (int)Math.Max(0, (clock.UtcNow - ls).TotalMinutes) : (int?)null;

        var actions = new List<string>();
        if (openConflicts > 0) actions.Add($"{openConflicts} conflit(s) à résoudre.");
        if (pending > threshold) actions.Add($"File de synchronisation élevée ({pending} > seuil {threshold}) — forcer une synchronisation.");
        if (Count("FAILED") > 0) actions.Add($"{Count("FAILED")} opération(s) en échec — inspecter le journal.");
        if (devices.Any(d => d.Status == "revoked" && d.PendingOperations > 0))
            actions.Add("Un appareil révoqué a des opérations en attente — traiter manuellement.");

        return new SyncCenterDto(clock.UtcNow, pending, Count("SYNCED"), Count("FAILED"), openConflicts,
            lastSync, minutesSince, pending > threshold, devices, conflicts, actions);
    }

    public async Task<Result> ResolveConflictAsync(Guid conflictId, ResolveConflictRequest request, CancellationToken ct = default)
    {
        var resolution = request.Resolution.Trim().ToLowerInvariant();
        string[] allowed = ["server_wins", "client_wins", "merged", "manual"];
        if (!allowed.Contains(resolution))
            return Error.Validation("sync.resolution", "Résolution invalide (server_wins/client_wins/merged/manual).");

        var conflict = (await db.Database.SqlQuery<GuidRow>($"""
            SELECT sync_operation_id AS "Value" FROM nexora.sync_conflict
            WHERE id = {conflictId} AND company_id = {Company} AND resolution = 'pending'
            """).ToListAsync(ct)).FirstOrDefault()?.Value;
        if (conflict is null) return Error.NotFound("sync.conflict", "Conflit introuvable ou déjà résolu.");

        // « Le poste a raison » sur une vente : le serveur la rejoue tout de suite (le poste ne renvoie jamais une
        // opération en conflit, et la même politique la remettrait en conflit). La politique hors ligne n'est pas
        // réappliquée ; si la vente reste impossible (stock, crédit client…), le conflit reste ouvert.
        if (resolution == "client_wins")
        {
            var stored = (await db.Database.SqlQuery<StoredOpRow>($"""
                SELECT id AS "Id", device_id AS "DeviceId", operation_uuid AS "OperationUuid", entity_type AS "EntityType",
                       operation_type AS "OperationType", payload::text AS "Payload", client_created_at AS "ClientCreatedAt",
                       priority AS "Priority"
                FROM nexora.sync_operation WHERE id = {conflict.Value}
                """).ToListAsync(ct)).FirstOrDefault();
            if (stored is { EntityType: "sale", OperationType: "create" })
            {
                using var payload = JsonDocument.Parse(stored.Payload);
                var op = new SyncOperationInput(stored.OperationUuid, stored.EntityType, stored.OperationType, payload.RootElement.Clone(),
                    stored.Priority, null, stored.ClientCreatedAt);
                var replay = await ProcessSaleAsync(stored.Id, op, await LoadPolicyAsync(ct), null, bypassPolicy: true, ct);
                if (replay.Status != "SYNCED")
                {
                    // Le rejeu a pu ouvrir un nouveau conflit (stock, crédit) : l'ancien est clos, le nouveau reste à traiter.
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.sync_conflict SET resolution = 'manual', resolution_note = {"Rejeu refusé : " + replay.Message},
                            resolved_by = {tenant.UserId}, resolved_at = now(), updated_at = now()
                        WHERE id = {conflictId} AND resolution = 'pending'
                        """, ct);
                    await uow.SaveChangesAsync(ct);
                    return Error.Conflict("sync.replay_refused", $"La vente n'a pas pu être enregistrée : {replay.Message}");
                }
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.sync_conflict SET resolution = 'client_wins', resolution_note = {request.Note},
                        resolved_by = {tenant.UserId}, resolved_at = now(), updated_at = now()
                    WHERE id = {conflictId}
                    """, ct);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.sync_operation SET conflict_resolution = 'client_wins', updated_at = now() WHERE id = {stored.Id}
                    """, ct);
                await uow.SaveChangesAsync(ct);
                return Result.Success();
            }
        }

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_conflict SET resolution = {resolution}, resolution_note = {request.Note},
                    resolved_by = {tenant.UserId}, resolved_at = now(), updated_at = now()
                WHERE id = {conflictId}
                """, token);
            // Journalise l'issue sur l'opération : abandonnée (server_wins) ou à rejouer (client_wins).
            var opStatus = resolution == "client_wins" ? "PENDING" : "FAILED";
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.sync_operation SET status = {opStatus}, conflict_resolution = {resolution}, updated_at = now()
                WHERE id = {conflict.Value}
                """, token);
            return 0;
        }, ct);
        return Result.Success();
    }

    private async Task<IReadOnlyList<SyncDeviceDto>> LoadDevicesAsync(CancellationToken ct)
    {
        var rows = await db.Database.SqlQuery<DeviceListRow>($"""
            SELECT d.id AS "Id", d.device_identifier AS "DeviceIdentifier", d.name AS "Name",
                   d.device_type AS "DeviceType", d.status AS "Status", d.last_sync_at AS "LastSyncAt",
                   d.last_seen_at AS "LastSeenAt",
                   COALESCE((SELECT COUNT(*) FROM nexora.sync_operation o
                             WHERE o.device_id = d.id AND o.status IN ('PENDING','SYNCING','FAILED','CONFLICT')), 0)::int AS "Pending"
            FROM nexora.sync_device d WHERE d.company_id = {Company} ORDER BY d.name NULLS LAST, d.device_identifier
            """).ToListAsync(ct);
        return rows.Select(r => new SyncDeviceDto(r.Id, r.DeviceIdentifier, r.Name ?? r.DeviceIdentifier,
            r.DeviceType, r.Status, r.LastSyncAt, r.LastSeenAt, r.Pending)).ToList();
    }
}
