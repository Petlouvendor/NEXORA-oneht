using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Customers;
using Nexora.Application.Sales;
using Nexora.Application.Security;
using Nexora.Contracts.Customers;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Customers;

/// <summary>
/// Dépassement de limite de crédit (module clients §12) par le workflow d'approbation configuré :
/// une demande <c>approval_request</c> de type <c>credit_sale</c> (Phase 11), niveaux et montant plancher
/// lus dans <c>approval_policy</c>, demandeur ≠ approbateur, deux approbateurs distincts en double
/// approbation. L'approbation vaut pour CE panier, CE client, au plus CE montant, une seule vente.
/// </summary>
public sealed class SqlCreditOverrideService(
    NexoraDbContext db,
    ITenantContext tenant,
    ICreditControl credit,
    IApprovalWorkflowService approvals,
    IPosApprovalNotifier notifier,
    ILogger<SqlCreditOverrideService> logger) : ICreditOverrideService
{
    private const string Kind = "Dépassement de crédit";
    private const string ReferenceType = "pos_credit_override";
    private Guid Company => CustomerAccountRules.Company(tenant);
    private Guid UserId => CustomerAccountRules.User(tenant);

    private sealed record Row(
        Guid Id, Guid CartOperationUuid, string Context, decimal Amount, string Currency, string Status, int CurrentLevel,
        int RequiredLevels, Guid? RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName,
        string? DecisionNote, bool Consumed);

    private IQueryable<Row> Query() => db.Database.SqlQuery<Row>($"""
        SELECT a.id AS "Id", a.reference_id AS "CartOperationUuid", a.context::text AS "Context", a.amount AS "Amount",
               cur.code AS "Currency", a.status AS "Status", a.current_level AS "CurrentLevel", a.required_levels AS "RequiredLevels",
               a.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName", a.requested_at AS "RequestedAt",
               (SELECT u.full_name FROM nexora.approval_action x JOIN nexora.app_user u ON u.id = x.approver_id
                 WHERE x.approval_request_id = a.id AND x.action IN ('approve','reject') ORDER BY x.created_at DESC LIMIT 1) AS "DecidedByName",
               (SELECT x.comment FROM nexora.approval_action x
                 WHERE x.approval_request_id = a.id AND x.action IN ('approve','reject') ORDER BY x.created_at DESC LIMIT 1) AS "DecisionNote",
               (a.consumed_at IS NOT NULL) AS "Consumed"
        FROM nexora.approval_request a
        JOIN nexora.currency cur ON cur.id = a.currency_id
        LEFT JOIN nexora.app_user rq ON rq.id = a.requested_by
        WHERE a.company_id = {Company} AND a.reference_type = {ReferenceType}
        """);

    private sealed record Ctx(Guid CustomerId, string? CustomerName, string? Reason, string? LimitMessage);

    private static CreditOverrideDto ToDto(Row r)
    {
        var c = JsonSerializer.Deserialize<Ctx>(r.Context, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new Ctx(Guid.Empty, null, null, null);
        return new CreditOverrideDto(r.Id, r.CartOperationUuid, c.CustomerId, c.CustomerName ?? "", r.Amount, r.Currency,
            c.Reason ?? "", c.LimitMessage, r.Status, r.CurrentLevel, r.RequiredLevels, r.RequestedByName, r.RequestedAt,
            r.DecidedByName, r.DecisionNote, r.Consumed);
    }

    public async Task<Result<CreditOverrideDto>> GetAsync(Guid id, CancellationToken ct = default)
        => await Query().Where(r => r.Id == id).FirstOrDefaultAsync(ct) is { } row
            ? Result.Success(ToDto(row))
            : Error.NotFound("credit.override_not_found", "Demande de dépassement introuvable.");

    public async Task<IReadOnlyList<CreditOverrideDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var q = Query();
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(r => r.Status == status);
        return (await q.OrderByDescending(r => r.RequestedAt).Take(200).ToListAsync(ct)).Select(ToDto).ToList();
    }

    private sealed record CustomerRow(string Name);
    private sealed record PolicyRow(short RequiredLevels);

    public async Task<Result<CreditOverrideDto>> RequestAsync(CreditOverrideRequestInput r, CancellationToken ct = default)
    {
        var reason = r.Reason?.Trim() ?? "";
        if (reason.Length < 5) return Error.Validation("credit.override_reason", "Le motif du dépassement est obligatoire.");
        var amount = CustomerAccountRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("credit.override_amount", "Montant de crédit invalide.");
        var customer = await db.Database.SqlQuery<CustomerRow>($"""
            SELECT name AS "Name" FROM nexora.customer WHERE id = {r.CustomerId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (customer is null) return Error.NotFound("customer.not_found", "Client introuvable.");

        // Une demande encore valable pour ce panier et ce client, d'un montant suffisant, est réutilisée.
        var existing = await Query()
            .Where(x => x.CartOperationUuid == r.CartOperationUuid && (x.Status == "pending" || x.Status == "approved") && !x.Consumed)
            .OrderByDescending(x => x.RequestedAt).ToListAsync(ct);
        var reusable = existing.Select(ToDto).FirstOrDefault(x => x.CustomerId == r.CustomerId && x.Amount >= amount);
        if (reusable is not null) return Result.Success(reusable);

        var decision = await credit.EvaluateAsync(r.CustomerId, r.Currency, amount, ct);
        if (decision.Allowed)
            return Error.Conflict("credit.override_not_needed", "Le crédit reste dans la limite : aucune approbation n'est nécessaire.");
        if (!decision.RequiresOverride)
            return Error.Conflict("credit.override_impossible", decision.Message ?? "Aucune dérogation possible pour ce client.");

        // Nombre de niveaux : politique configurée pour credit_sale (au moins un approbateur, toujours).
        var policy = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT required_levels AS "RequiredLevels" FROM nexora.approval_policy
            WHERE company_id = {Company} AND request_type = 'credit_sale' AND status = 'active' AND min_amount <= {amount}
            ORDER BY min_amount DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        var levels = Math.Max((short)1, policy?.RequiredLevels ?? (short)1);

        // Les demandes plus petites encore ouvertes pour ce panier sont remplacées.
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.approval_request SET status = 'cancelled', completed_at = now(), updated_at = now()
            WHERE company_id = {Company} AND reference_type = {ReferenceType} AND reference_id = {r.CartOperationUuid}
              AND status = 'pending'
            """, ct);

        var id = Guid.NewGuid();
        var context = JsonSerializer.Serialize(new
        {
            customerId = r.CustomerId, customerName = customer.Name, reason, limitMessage = decision.Message,
            projected = decision.ProjectedExposure, available = decision.AvailableCredit,
        });
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.approval_request
                (id, company_id, request_type, reference_type, reference_id, requested_by, amount, currency_id, status,
                 current_level, required_levels, context)
            VALUES ({id}, {Company}, 'credit_sale', {ReferenceType}, {r.CartOperationUuid}, {UserId}, {amount},
                    (SELECT id FROM nexora.currency WHERE code = {r.Currency}), 'pending', 1, {levels}, {context}::jsonb)
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.approval_action (company_id, approval_request_id, approver_id, level, action, comment)
            VALUES ({Company}, {id}, {UserId}, 0, 'comment', {reason})
            """, ct);

        await notifier.NotifyApproversAsync(Permissions.Pos.CreditOverride, Kind, $"DEP-{id.ToString("N")[..8].ToUpperInvariant()}",
            $"{customer.Name} — crédit de {amount:N2} {r.Currency} au-delà du permis ({decision.Message}). Motif : {reason}",
            $"/pos?approvals=credit&id={id}", "approval_request", id, ct);
        logger.LogInformation("Demande de dépassement de crédit {Request} pour {Customer} : {Amount} {Currency}", id, r.CustomerId, amount, r.Currency);
        return await GetAsync(id, ct);
    }

    public Task<Result<CreditOverrideDto>> ApproveAsync(Guid id, string? note, CancellationToken ct = default)
        => DecideAsync(id, approve: true, note, ct);

    public async Task<Result<CreditOverrideDto>> RejectAsync(Guid id, string? note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note)) return Error.Validation("credit.decision_note_required", "Indiquez le motif du refus.");
        return await DecideAsync(id, approve: false, note, ct);
    }

    private async Task<Result<CreditOverrideDto>> DecideAsync(Guid id, bool approve, string? note, CancellationToken ct)
    {
        var row = await Query().Where(r => r.Id == id).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("credit.override_not_found", "Demande de dépassement introuvable.");
        // Le moteur d'approbation applique demandeur ≠ approbateur et les niveaux configurés.
        var decided = await approvals.DecideAsync(id, new ApprovalDecisionRequest(approve, note?.Trim()), ct);
        if (!decided.IsSuccess) return decided.Error!;
        var dto = (await GetAsync(id, ct)).Value!;
        if (dto.Status is "approved" or "rejected" && row.RequestedBy is { } requester)
            await notifier.NotifyRequesterAsync(requester, Kind, $"DEP-{id.ToString("N")[..8].ToUpperInvariant()}",
                dto.Status == "approved", note, $"/pos?approvals=credit&id={id}", "approval_request", id, ct);
        return Result.Success(dto);
    }
}
