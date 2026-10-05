using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Finance;
using Nexora.Application.Integrations;
using Nexora.Contracts.Finance;
using Nexora.Contracts.Integrations;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Integrations;

/// <summary>
/// Passerelle de paiement indépendante du fournisseur (§7-11 ; finalisation phase 15). L'intention est
/// créée chez le fournisseur du connecteur (<see cref="IPaymentProvider"/>) et porte son mode :
/// « simulation » (aucun argent réel, confirmée à la main en environnement de test) ou « réel »
/// (confirmé seulement par le fournisseur, interrogé par <see cref="VerifyAsync"/>). Ce n'est qu'à la
/// confirmation qu'un <c>payment</c> est passé par le moteur financier ; le montant est toujours revérifié (§12).
/// </summary>
public sealed class SqlPaymentGateway(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock,
    IFinanceOperations finance, IOutboundWebhookService outbound, IPaymentProviderRegistry providers,
    Payroll.PersonalDataProtector protector, ICustomerAccountService customerAccounts) : IPaymentGateway
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");
    private static readonly string[] Methods = ["card", "mobile_money", "bank_transfer", "wallet", "link", "qr"];
    private static readonly string[] Purposes = ["sale", "receivable", "link"];

    private sealed record IntentRow(
        Guid Id, Guid ConnectorId, string? ProviderReference, decimal Amount, Guid CurrencyId, string Currency,
        string Method, string Purpose, string Status, Guid? PaymentId, decimal RefundedAmount, Guid? CustomerId,
        Guid? FinancialAccountId, string ProviderResponse, DateTimeOffset? ExpiresAt, DateTimeOffset? ConfirmedAt,
        string? FailureReason, DateTimeOffset CreatedAt, string Mode, string? RedirectUrl, string? ProviderOrderId);

    private sealed record ConnectorRow(Guid Id, string ConnectorType, string Status, string Provider, string Environment);

    public async Task<Result<PaymentIntentDto>> CreateIntentAsync(CreatePaymentIntentRequest request, CancellationToken ct = default)
    {
        if (request.Amount <= 0m) return Error.Validation("integrations.amount", "Le montant doit être positif.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return Error.Validation("integrations.idempotency", "Clé d'idempotence requise.");
        var method = (request.Method?.Trim().ToLowerInvariant()) is { Length: > 0 } m && Methods.Contains(m) ? m : "card";
        var purpose = (request.Purpose?.Trim().ToLowerInvariant()) is { Length: > 0 } p && Purposes.Contains(p) ? p : "link";

        // Rejeu idempotent : même clé → même intention (§13).
        var dup = await LoadAsync(existingByKey: request.IdempotencyKey, ct: ct);
        if (dup is not null) return Result.Success(ToDto(dup));

        var connector = await db.Database.SqlQuery<ConnectorRow>($"""
            SELECT id AS "Id", connector_type AS "ConnectorType", status AS "Status", provider AS "Provider", environment AS "Environment"
            FROM nexora.integration_connector WHERE id = {request.ConnectorId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (connector is null) return Error.NotFound("integrations.connector", "Connecteur introuvable.");
        if (connector.ConnectorType != "payment") return Error.Validation("integrations.connector_type", "Le connecteur n'est pas de type paiement.");
        if (connector.Status != "active") return Error.Validation("integrations.connector_inactive", "Connecteur désactivé : paiement refusé (§40).");
        var provider = providers.Resolve(connector.Provider);
        if (provider is null) return Error.Validation("integrations.provider_unknown", "Aucun fournisseur de paiement n'est disponible pour ce connecteur.");
        var currencyCode = request.Currency.Trim().ToUpperInvariant();
        if (provider.Currencies.Count > 0 && !provider.Currencies.Contains(currencyCode))
            return Error.Validation("integrations.currency_unsupported",
                $"{provider.DisplayName} n'accepte que : {string.Join(", ", provider.Currencies)}.");

        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.currency WHERE code = {currencyCode}
            """).FirstOrDefaultAsync(ct);
        if (currencyId == Guid.Empty) return Error.Validation("integrations.currency", $"Devise « {request.Currency} » inconnue.");

        var customerExists = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.customer WHERE id = {request.CustomerId} AND company_id = {Company}
            """).FirstAsync(ct);
        if (customerExists == 0) return Error.NotFound("integrations.customer", "Client introuvable.");

        var accountExists = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM nexora.financial_account WHERE id = {request.SettlementAccountId} AND company_id = {Company}
            """).FirstAsync(ct);
        if (accountExists == 0) return Error.NotFound("integrations.settlement_account", "Compte de règlement introuvable.");

        var expiresAt = clock.UtcNow.AddMinutes(Math.Clamp(request.ExpiresInMinutes ?? 30, 1, 1440));
        // Numéro de commande transmis au fournisseur : chiffres seulement (forme des exemples MonCash), unique.
        var orderId = $"{clock.UtcNow:yyMMddHHmmss}{Random.Shared.Next(100000, 999999)}";
        var context = await ConnectorContexts.LoadAsync(db, protector, connector.Id, Company, ct);
        var providerResult = await provider.CreatePaymentAsync(context!,
            new ProviderPaymentRequest(orderId, request.Amount, currencyCode, request.Reference), ct);
        if (!providerResult.Success)
            return Error.Conflict("integrations.provider_refused",
                $"{provider.DisplayName} : {providerResult.Message}{(providerResult.ErrorStatus is { } es ? $" ({es})" : "")}");
        var providerRef = providerResult.ProviderReference;
        var mode = provider.IsSimulation ? "simulation" : "real";

        var allocations = (request.Allocations ?? [])
            .Where(a => a.Amount > 0m)
            .Select(a => new { targetId = a.TargetId, amount = a.Amount, isReceivable = a.IsReceivable })
            .ToArray();
        var providerResponse = JsonSerializer.Serialize(new
        {
            adapter = provider.Key,
            mode,
            provider = connector.Provider,
            state = "pending",
            nexora = new { allocations, reference = request.Reference },
        });

        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.payment_intent
                    (id, company_id, connector_id, idempotency_key, provider_reference, amount, currency_id,
                     method, purpose, sale_id, customer_id, financial_account_id, status, provider_response,
                     expires_at, requested_by, mode, redirect_url, provider_order_id)
                VALUES ({id}, {Company}, {request.ConnectorId}, {request.IdempotencyKey.Trim()}, {providerRef},
                        {request.Amount}, {currencyId}, {method}, {purpose}, {request.SaleId}, {request.CustomerId},
                        {request.SettlementAccountId}, 'pending', {providerResponse}::jsonb, {expiresAt}, {tenant.UserId},
                        {mode}, {providerResult.RedirectUrl}, {orderId})
                """, ct);
        }
        catch (Npgsql.PostgresException pg) when (pg.SqlState == "23505")
        {
            var again = await LoadAsync(existingByKey: request.IdempotencyKey, ct: ct);
            if (again is not null) return Result.Success(ToDto(again));
            throw;
        }

        var created = await LoadAsync(id: id, ct: ct);
        return Result.Success(ToDto(created!));
    }

    public async Task<Result<PaymentIntentDto>> GetIntentAsync(Guid intentId, CancellationToken ct = default)
    {
        var row = await LoadAsync(id: intentId, ct: ct);
        return row is null ? Error.NotFound("integrations.intent", "Intention de paiement introuvable.") : Result.Success(ToDto(row));
    }

    public async Task<IReadOnlyList<PaymentIntentDto>> ListIntentsAsync(string? status, CancellationToken ct = default)
    {
        var s = status?.Trim().ToLowerInvariant();
        var rows = await db.Database.SqlQuery<IntentRow>($"""
            SELECT pi.id AS "Id", pi.connector_id AS "ConnectorId", pi.provider_reference AS "ProviderReference",
                   pi.amount AS "Amount", pi.currency_id AS "CurrencyId", cur.code AS "Currency", pi.method AS "Method",
                   pi.purpose AS "Purpose", pi.status AS "Status", pi.payment_id AS "PaymentId",
                   pi.refunded_amount AS "RefundedAmount", pi.customer_id AS "CustomerId",
                   pi.financial_account_id AS "FinancialAccountId", pi.provider_response::text AS "ProviderResponse",
                   pi.expires_at AS "ExpiresAt", pi.confirmed_at AS "ConfirmedAt", pi.failure_reason AS "FailureReason",
                   pi.created_at AS "CreatedAt", pi.mode AS "Mode", pi.redirect_url AS "RedirectUrl",
                   pi.provider_order_id AS "ProviderOrderId"
            FROM nexora.payment_intent pi JOIN nexora.currency cur ON cur.id = pi.currency_id
            WHERE pi.company_id = {Company} AND ({s}::text IS NULL OR pi.status = {s})
            ORDER BY pi.created_at DESC LIMIT 200
            """).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<PaymentIntentDto>> ProviderCallbackAsync(Guid intentId, ProviderCallbackRequest request, CancellationToken ct = default)
    {
        var row = await LoadAsync(id: intentId, ct: ct);
        if (row is null) return Error.NotFound("integrations.intent", "Intention de paiement introuvable.");
        // Une réponse « fournisseur » saisie à la main n'existe qu'en simulation : un paiement réel n'est
        // confirmé que par le fournisseur lui-même (sinon on encaisserait de l'argent qui n'est jamais arrivé).
        if (row.Mode != "simulation")
            return Error.Conflict("integrations.not_simulation",
                "Paiement réel : seule la vérification auprès du fournisseur peut le confirmer.");
        if (await ConnectorEnvironmentAsync(row.ConnectorId, ct) == "production")
            return Error.Conflict("integrations.not_simulation", "Simulation interdite sur un connecteur de production.");

        return (request.Outcome?.Trim().ToLowerInvariant()) switch
        {
            "confirmed" or "confirm" or "success" or "succeeded"
                => await ConfirmAsync(row, request.ProviderAmount, request.ProviderReference ?? row.ProviderReference, ct),
            "failed" or "failure" or "declined"
                => await TerminateAsync(row, "failed", "SIMULATION : paiement refusé.", ct),
            "cancelled" or "canceled" or "expired"
                => await TerminateAsync(row, "cancelled", "SIMULATION : paiement annulé.", ct),
            _ => Error.Validation("integrations.callback_outcome", "Issue inconnue (confirmed / failed / cancelled)."),
        };
    }

    /// <summary>Confirme l'intention et crée le paiement réel. Partagé par le rappel simulé et l'ingestion webhook.</summary>
    private async Task<Result<PaymentIntentDto>> ConfirmAsync(
        IntentRow row, decimal? providerAmount, string? providerReference, CancellationToken ct)
    {
        if (row.Status == "confirmed") return Result.Success(ToDto(row));   // idempotent (§13)
        if (row.Status is "failed" or "cancelled" or "expired" or "amount_mismatch" or "refunded" or "partially_refunded")
            return Error.Conflict("integrations.intent_terminal",
                $"Intention déjà « {row.Status} » — confirmation tardive ignorée, à investiguer au rapprochement (§14).");

        // Ne jamais faire confiance au montant sans validation (§12).
        if (providerAmount is decimal pa && decimal.Round(pa, 4) != decimal.Round(row.Amount, 4))
        {
            var mismatchResponse = PatchProviderResponse(row.ProviderResponse, "amount_mismatch", providerAmount, providerReference);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payment_intent
                SET status = 'amount_mismatch',
                    failure_reason = {$"Montant fournisseur {pa} ≠ attendu {row.Amount}"},
                    provider_response = {mismatchResponse}::jsonb, updated_at = now()
                WHERE id = {row.Id}
                """, ct);
            return Result.Success(ToDto(row with { Status = "amount_mismatch", FailureReason = $"Montant fournisseur {pa} ≠ attendu {row.Amount}" }));
        }

        if (row.CustomerId is null || row.FinancialAccountId is null)
            return Error.Validation("integrations.intent_incomplete", "Intention sans client ou compte de règlement : confirmation impossible.");

        var allocations = ParseAllocations(row.ProviderResponse);
        var confirmedAt = clock.UtcNow;
        var reference = providerReference ?? row.ProviderReference;

        // Le moteur financier est atomique par lui-même ; l'operation_uuid = id de l'intention
        // garantit qu'une double confirmation ne crée pas deux paiements (§13).
        Result<PostingResultResponse> pay;
        try
        {
            pay = await finance.RecordCustomerPaymentAsync(new RecordPaymentRequest(
                row.Id, confirmedAt, row.CustomerId.Value, row.Amount, row.Currency,
                row.FinancialAccountId.Value, allocations, reference), ct);
        }
        catch (InvalidOperationException ex)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payment_intent SET status = 'failed', failure_reason = {ex.Message}, updated_at = now()
                WHERE id = {row.Id}
                """, ct);
            return Error.Unexpected("integrations.confirm_failed", $"Confirmation impossible : {ex.Message}");
        }

        if (!pay.IsSuccess)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payment_intent SET status = 'failed', failure_reason = {pay.Error!.Message}, updated_at = now()
                WHERE id = {row.Id}
                """, ct);
            return pay.Error!;
        }

        var pr = PatchProviderResponse(row.ProviderResponse, "confirmed", providerAmount, reference);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payment_intent
            SET status = 'confirmed', payment_id = {pay.Value!.PaymentId}, provider_reference = {reference},
                confirmed_at = {confirmedAt}, failure_reason = NULL, provider_response = {pr}::jsonb, updated_at = now()
            WHERE id = {row.Id}
            """, ct);

        await outbound.EmitAsync("payment.confirmed", row.Id, new
        {
            intentId = row.Id,
            paymentId = pay.Value.PaymentId,
            amount = row.Amount,
            currency = row.Currency,
            customerId = row.CustomerId,
            providerReference = reference,
        }, ct);

        return Result.Success(ToDto(row with
        {
            Status = "confirmed",
            PaymentId = pay.Value.PaymentId,
            ProviderReference = reference,
            ConfirmedAt = confirmedAt,
        }));
    }

    private async Task<Result<PaymentIntentDto>> TerminateAsync(IntentRow row, string status, string reason, CancellationToken ct)
    {
        if (row.Status == status) return Result.Success(ToDto(row));
        if (row.Status is "confirmed" or "refunded" or "partially_refunded")
            return Error.Conflict("integrations.intent_terminal", $"Intention déjà « {row.Status} » — issue « {status} » ignorée.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payment_intent SET status = {status}, failure_reason = {reason}, updated_at = now()
            WHERE id = {row.Id}
            """, ct);
        return Result.Success(ToDto(row with { Status = status, FailureReason = reason }));
    }

    /// <summary>
    /// Remboursement (finalisation phase 15). Il changeait seulement le statut : ni l'argent ni la comptabilité ne
    /// bougeaient. Désormais : simulation → remboursement total qui annule réellement le versement client
    /// (contre-passation) ; fournisseur réel sans remboursement par API (MonCash) → refus explicite.
    /// </summary>
    public async Task<Result<PaymentIntentDto>> RefundAsync(Guid intentId, RefundPaymentRequest request, CancellationToken ct = default)
    {
        var row = await LoadAsync(id: intentId, ct: ct);
        if (row is null) return Error.NotFound("integrations.intent", "Intention de paiement introuvable.");
        if (row.Status != "confirmed")
            return Error.Validation("integrations.refund_state", "Seul un paiement confirmé peut être remboursé.");
        if (request.Amount <= 0m) return Error.Validation("integrations.refund_amount", "Le montant du remboursement doit être positif.");
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Error.Validation("integrations.refund_reason", "Le motif du remboursement est obligatoire (5 caractères au moins).");

        var provider = providers.Resolve(await ConnectorProviderAsync(row.ConnectorId, ct));
        if (provider is null || !provider.SupportsRefund || (row.Mode == "real" && !provider.IsSimulation))
            return Error.Conflict("integrations.refund_unsupported",
                $"{provider?.DisplayName ?? "Ce fournisseur"} ne propose pas de remboursement par API : remboursez le client depuis le portail du " +
                "fournisseur, puis annulez le versement dans son compte client (Clients → versements).");
        if (request.Amount != row.Amount)
            return Error.Validation("integrations.refund_partial",
                "Seul le remboursement total est pris en charge : il annule le versement et ses écritures.");
        if (row.PaymentId is not { } paymentId)
            return Error.Conflict("integrations.refund_no_payment", "Aucun versement n'est lié à ce paiement.");

        var voided = await customerAccounts.VoidPaymentAsync(paymentId, new Nexora.Contracts.Customers.VoidPaymentRequest(
            $"Remboursement {(row.Mode == "simulation" ? "(SIMULATION) " : "")}{row.ProviderReference} : {reason}"), ct);
        if (!voided.IsSuccess) return voided.Error!;

        var newRefunded = row.Amount;
        var status = "refunded";
        var pr = PatchProviderResponse(row.ProviderResponse, "refund", request.Amount, row.ProviderReference);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payment_intent
            SET status = {status}, refunded_amount = {newRefunded},
                failure_reason = {"Remboursement : " + reason},
                provider_response = {pr}::jsonb, updated_at = now()
            WHERE id = {row.Id}
            """, ct);

        await outbound.EmitAsync("payment.refunded", Guid.NewGuid(), new
        {
            intentId = row.Id, amount = request.Amount, totalRefunded = newRefunded,
            currency = row.Currency, reason = request.Reason,
        }, ct);

        var updated = await LoadAsync(id: row.Id, ct: ct);
        return Result.Success(ToDto(updated!));
    }

    public async Task<Result<PaymentIntentDto>> VerifyAsync(Guid intentId, CancellationToken ct = default)
    {
        var row = await LoadAsync(id: intentId, ct: ct);
        if (row is null) return Error.NotFound("integrations.intent", "Intention de paiement introuvable.");
        if (row.Status == "confirmed") return Result.Success(ToDto(row));
        if (row.Mode != "real")
            return Error.Conflict("integrations.simulation_no_provider",
                "SIMULATION : aucun fournisseur à interroger ; utilisez « Simuler la réponse du fournisseur ».");
        if (row.Status is not ("pending" or "authorized"))
            return Error.Conflict("integrations.intent_terminal", $"Paiement déjà « {row.Status} ».");
        var provider = providers.Resolve(await ConnectorProviderAsync(row.ConnectorId, ct));
        if (provider is null || row.ProviderOrderId is null)
            return Error.Validation("integrations.provider_unknown", "Fournisseur ou numéro de commande inconnu : vérification impossible.");

        var context = await ConnectorContexts.LoadAsync(db, protector, row.ConnectorId, Company, ct);
        var lookup = await provider.RetrievePaymentAsync(context!, row.ProviderOrderId, ct);
        return lookup.State switch
        {
            // Le montant du fournisseur passe par le contrôle d'écart habituel (§12).
            "paid" => await ConfirmAsync(row, lookup.Amount ?? -1m, lookup.TransactionId ?? row.ProviderReference, ct),
            "not_found" => Result.Success(ToDto(row with { FailureReason = lookup.Message })),
            _ => Error.Unexpected("integrations.provider_unreachable",
                $"{provider.DisplayName} : {lookup.Message}{(lookup.ErrorStatus is { } es ? $" ({es})" : "")}"),
        };
    }

    public async Task<Result<PaymentIntentDto>> ConfirmFromProviderEventAsync(
        Guid intentId, decimal? providerAmount, string? providerReference, CancellationToken ct = default)
    {
        var row = await LoadAsync(id: intentId, ct: ct);
        if (row is null) return Error.NotFound("integrations.intent", "Intention de paiement introuvable.");
        // Un événement reçu ne suffit pas pour un paiement réel : on interroge le fournisseur.
        return row.Mode == "real"
            ? await VerifyAsync(intentId, ct)
            : await ProviderCallbackAsync(intentId, new ProviderCallbackRequest("confirmed", providerAmount, providerReference), ct);
    }

    private Task<string> ConnectorProviderAsync(Guid connectorId, CancellationToken ct)
        => db.Database.SqlQuery<string>($"""
            SELECT provider AS "Value" FROM nexora.integration_connector WHERE id = {connectorId} AND company_id = {Company}
            """).FirstAsync(ct);

    private Task<string> ConnectorEnvironmentAsync(Guid connectorId, CancellationToken ct)
        => db.Database.SqlQuery<string>($"""
            SELECT environment AS "Value" FROM nexora.integration_connector WHERE id = {connectorId} AND company_id = {Company}
            """).FirstAsync(ct);

    public async Task<int> ExpireStaleAsync(CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payment_intent
            SET status = 'expired', failure_reason = 'Aucune confirmation du fournisseur avant expiration (§14).', updated_at = now()
            WHERE company_id = {Company} AND status IN ('pending','authorized') AND expires_at IS NOT NULL AND expires_at < now()
            """, ct);

    // --- Helpers ---------------------------------------------------------

    private async Task<IntentRow?> LoadAsync(Guid? id = null, string? existingByKey = null, CancellationToken ct = default)
        => await db.Database.SqlQuery<IntentRow>($"""
            SELECT pi.id AS "Id", pi.connector_id AS "ConnectorId", pi.provider_reference AS "ProviderReference",
                   pi.amount AS "Amount", pi.currency_id AS "CurrencyId", cur.code AS "Currency", pi.method AS "Method",
                   pi.purpose AS "Purpose", pi.status AS "Status", pi.payment_id AS "PaymentId",
                   pi.refunded_amount AS "RefundedAmount", pi.customer_id AS "CustomerId",
                   pi.financial_account_id AS "FinancialAccountId", pi.provider_response::text AS "ProviderResponse",
                   pi.expires_at AS "ExpiresAt", pi.confirmed_at AS "ConfirmedAt", pi.failure_reason AS "FailureReason",
                   pi.created_at AS "CreatedAt", pi.mode AS "Mode", pi.redirect_url AS "RedirectUrl",
                   pi.provider_order_id AS "ProviderOrderId"
            FROM nexora.payment_intent pi JOIN nexora.currency cur ON cur.id = pi.currency_id
            WHERE pi.company_id = {Company}
              AND (({id}::uuid IS NULL) OR pi.id = {id})
              AND (({existingByKey}::text IS NULL) OR pi.idempotency_key = {existingByKey})
            LIMIT 1
            """).FirstOrDefaultAsync(ct);

    private static IReadOnlyList<AllocationInput> ParseAllocations(string providerResponse)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(providerResponse) ? "{}" : providerResponse);
            if (!doc.RootElement.TryGetProperty("nexora", out var nx) || !nx.TryGetProperty("allocations", out var arr)
                || arr.ValueKind != JsonValueKind.Array)
                return [];
            var list = new List<AllocationInput>();
            foreach (var e in arr.EnumerateArray())
                list.Add(new AllocationInput(
                    e.GetProperty("targetId").GetGuid(),
                    e.GetProperty("amount").GetDecimal(),
                    e.TryGetProperty("isReceivable", out var ir) && ir.GetBoolean()));
            return list;
        }
        catch (JsonException) { return []; }
    }

    private static string PatchProviderResponse(string current, string state, decimal? observedAmount, string? reference)
    {
        Dictionary<string, JsonElement> map;
        try
        {
            map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                string.IsNullOrWhiteSpace(current) ? "{}" : current) ?? new();
        }
        catch (JsonException) { map = new(); }

        var merged = new Dictionary<string, object?>();
        foreach (var (k, v) in map) merged[k] = v;
        merged["state"] = state;
        if (observedAmount is not null) merged["observed_amount"] = observedAmount;
        if (reference is not null) merged["reference"] = reference;
        merged["callback_at"] = DateTimeOffset.UtcNow;
        return JsonSerializer.Serialize(merged);
    }

    private static PaymentIntentDto ToDto(IntentRow r) => new(
        r.Id, r.ConnectorId, r.ProviderReference, r.Amount, r.Currency, r.Method, r.Purpose, r.Status,
        r.PaymentId, r.RefundedAmount, r.ExpiresAt, r.ConfirmedAt, r.FailureReason, r.CreatedAt,
        r.Mode, r.RedirectUrl, r.ProviderOrderId, r.CustomerId);
}

/// <summary>Rapprochement des paiements : intentions fournisseur ↔ paiements internes (§15).</summary>
public sealed class SqlPaymentReconciliationService(NexoraDbContext db, ITenantContext tenant) : IPaymentReconciliationService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record IntentLite(
        Guid Id, string? ProviderReference, decimal Amount, string Status, Guid? PaymentId,
        decimal? ObservedAmount, DateTimeOffset? ExpiresAt);

    public async Task<PaymentReconciliationReport> ReconcileAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var fromTs = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var toTs = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero);

        var rows = await db.Database.SqlQuery<IntentLite>($"""
            SELECT id AS "Id", provider_reference AS "ProviderReference", amount AS "Amount", status AS "Status",
                   payment_id AS "PaymentId",
                   NULLIF(provider_response->>'observed_amount','')::numeric AS "ObservedAmount",
                   expires_at AS "ExpiresAt"
            FROM nexora.payment_intent
            WHERE company_id = {Company} AND created_at >= {fromTs} AND created_at <= {toTs}
            """).ToListAsync(ct);

        var findings = new List<ReconciliationFinding>();

        foreach (var r in rows.Where(r => r.Status == "amount_mismatch"))
            findings.Add(new ReconciliationFinding("amount_mismatch", r.Id, r.ProviderReference,
                r.Amount, r.ObservedAmount, "Montant fournisseur différent du montant attendu — aucun paiement créé (§12)."));

        foreach (var r in rows.Where(r => r.Status == "confirmed" && r.PaymentId is null))
            findings.Add(new ReconciliationFinding("missing_payment", r.Id, r.ProviderReference,
                r.Amount, null, "Intention confirmée sans paiement lié — à régulariser."));

        foreach (var r in rows.Where(r => r.Status is "pending" or "authorized"
                     && r.ExpiresAt is { } e && e < DateTimeOffset.UtcNow))
            findings.Add(new ReconciliationFinding("stale_pending", r.Id, r.ProviderReference,
                r.Amount, null, "Intention en attente au-delà de son expiration — lancer l'expiration (§14)."));

        foreach (var grp in rows.Where(r => !string.IsNullOrEmpty(r.ProviderReference))
                     .GroupBy(r => r.ProviderReference).Where(g => g.Count() > 1))
            foreach (var r in grp)
                findings.Add(new ReconciliationFinding("duplicate_reference", r.Id, r.ProviderReference,
                    r.Amount, null, $"Référence fournisseur « {grp.Key} » partagée par {grp.Count()} intentions."));

        return new PaymentReconciliationReport(from, to,
            rows.Count(r => r.Status == "confirmed"),
            rows.Count(r => r.PaymentId is not null),
            findings);
    }
}
