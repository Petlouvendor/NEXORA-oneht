using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Intelligence;
using Nexora.Contracts.Intelligence;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Intelligence;

/// <summary>
/// Règles configurables (Phase 43 §34, §39) — <c>intelligence_rule</c>. Les valeurs système
/// (<c>company_id</c> NULL, amorcées par V056, comme <c>sector_profile</c>) ne sont jamais créées
/// ni supprimées via ce service : <see cref="SaveAsync"/> upsert toujours sur la clé de
/// l'entreprise active, <see cref="DeleteAsync"/> ne peut cibler qu'une règle de l'entreprise
/// (la clause <c>company_id = Company</c> exclut structurellement les lignes système).
/// </summary>
public sealed class SqlIntelligenceRuleService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IIntelligenceRuleService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, Guid? CompanyId, string Key, string Category, string Label, string ThresholdJson,
        int? PeriodDays, string DefaultPriority, string? RecipientRole, string ActionLabel, int? EscalateAfterDays,
        bool Enabled, DateOnly ValidFrom, DateOnly? ExpiresAt, int Version, DateTimeOffset? ApprovedAt);

    private const string Select = """
        SELECT id AS "Id", company_id AS "CompanyId", key AS "Key", category AS "Category", label AS "Label",
               threshold::text AS "ThresholdJson", period_days AS "PeriodDays", default_priority AS "DefaultPriority",
               recipient_role AS "RecipientRole", action_label AS "ActionLabel", escalate_after_days AS "EscalateAfterDays",
               enabled AS "Enabled", valid_from AS "ValidFrom", expires_at AS "ExpiresAt", version AS "Version",
               approved_at AS "ApprovedAt"
        FROM nexora.intelligence_rule
        """;

    public async Task<IReadOnlyList<IntelligenceRuleDto>> ListAsync(CancellationToken ct = default)
    {
        // Les deux niveaux restent visibles (système + surcharge entreprise) : utile pour voir ce qui s'applique réellement (§34).
        var rows = await db.Database.SqlQueryRaw<Row>(
            Select + " WHERE company_id = {0} OR company_id IS NULL ORDER BY key, company_id NULLS LAST", Company).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<IntelligenceRuleDto>> SaveAsync(SaveIntelligenceRuleRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Key)) return Error.Validation("intelligence_rule.key", "Clé requise.");
        if (string.IsNullOrWhiteSpace(request.Label)) return Error.Validation("intelligence_rule.label", "Libellé requis.");

        var key = request.Key.Trim().ToLowerInvariant();
        var thresholdJson = request.Threshold.GetRawText();

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.intelligence_rule
                (id, company_id, key, category, label, threshold, period_days, default_priority, recipient_role,
                 action_label, escalate_after_days, enabled, expires_at, owner_user_id)
            VALUES ({Guid.NewGuid()}, {Company}, {key}, {request.Category}, {request.Label}, {thresholdJson}::jsonb,
                    {request.PeriodDays}, {request.DefaultPriority}, {request.RecipientRole}, {request.ActionLabel},
                    {request.EscalateAfterDays}, {request.Enabled}, {request.ExpiresAt}, {tenant.UserId})
            ON CONFLICT ON CONSTRAINT intelligence_rule_key_uk DO UPDATE SET
                category = EXCLUDED.category, label = EXCLUDED.label, threshold = EXCLUDED.threshold,
                period_days = EXCLUDED.period_days, default_priority = EXCLUDED.default_priority,
                recipient_role = EXCLUDED.recipient_role, action_label = EXCLUDED.action_label,
                escalate_after_days = EXCLUDED.escalate_after_days, enabled = EXCLUDED.enabled,
                expires_at = EXCLUDED.expires_at, version = nexora.intelligence_rule.version + 1, updated_at = now()
            """, ct);
        await uow.SaveChangesAsync(ct);

        var finalId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.intelligence_rule WHERE company_id = {Company} AND key = {key}
            """).FirstAsync(ct);
        return await GetAsync(finalId, ct);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var rows = await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.intelligence_rule WHERE id = {id} AND company_id = {Company}", ct);
        if (rows == 0) return Error.NotFound("intelligence_rule.not_found", "Règle introuvable ou verrouillée (règle système).");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Result<IntelligenceRuleDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE id = {0}", id).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("intelligence_rule.not_found", "Règle introuvable.") : Result.Success(ToDto(row));
    }

    private static IntelligenceRuleDto ToDto(Row r) => new(r.Id, r.CompanyId is null, r.Key, r.Category, r.Label,
        JsonDocument.Parse(r.ThresholdJson).RootElement, r.PeriodDays, r.DefaultPriority, r.RecipientRole, r.ActionLabel,
        r.EscalateAfterDays, r.Enabled, r.ValidFrom, r.ExpiresAt, r.Version, r.ApprovedAt);
}
