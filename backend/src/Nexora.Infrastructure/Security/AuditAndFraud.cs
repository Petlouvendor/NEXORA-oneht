using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Security;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Security;

/// <summary>Consultation du journal d'audit, bornée au périmètre entreprise (Phase 11 §10-12, §39).</summary>
public sealed class SqlAuditQueryService(NexoraDbContext db, ITenantContext tenant, IPermissionService permissions) : IAuditQueryService
{
    private sealed record Row(
        Guid Id, DateTimeOffset OccurredAt, Guid? UserId, Guid? CompanyId, string Action,
        string EntityType, Guid? EntityId, string? OldValues, string? NewValues, string? IpAddress, string? DeviceId);

    private (int Page, int Size) Paging(AuditQueryFilter f) => (Math.Max(1, f.Page), Math.Clamp(f.PageSize, 1, 200));

    /// <summary>
    /// Entreprises lisibles (audit 2026-10 éd. 2, A-034) : celles du jeton où l'appelant détient la permission. Avant,
    /// <c>security.audit_view</c> dans A suffisait à lire l'audit de B — anciennes et nouvelles valeurs comprises — dès
    /// lors que l'utilisateur appartenait aussi à B, même comme simple caissier.
    /// </summary>
    private async Task<Guid[]> CompaniesAsync(string permission, CancellationToken ct)
    {
        if (tenant.UserId is not { } userId) return tenant.CompanyIds.ToArray();   // traitement système (planificateur)
        var profile = await permissions.LoadProfileAsync(userId, ct);
        return tenant.CompanyIds.Where(c => profile.HasPermission(permission, c)).ToArray();
    }

    public async Task<AuditPageDto> QueryAsync(AuditQueryFilter filter, CancellationToken ct = default, string? requiredPermission = null)
    {
        var Companies = await CompaniesAsync(requiredPermission ?? Nexora.Application.Security.Permissions.Security.AuditView, ct);
        var (page, size) = Paging(filter);
        DateTimeOffset? from = filter.From is { } d ? new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DateTimeOffset? to = filter.To is { } d2 ? new DateTimeOffset(d2.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.Zero) : null;

        // Périmètre serveur : uniquement les entreprises autorisées (§9, §39).
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", occurred_at AS "OccurredAt", user_id AS "UserId", company_id AS "CompanyId",
                   action AS "Action", entity_type AS "EntityType", entity_id AS "EntityId",
                   old_values::text AS "OldValues", new_values::text AS "NewValues",
                   host(ip_address) AS "IpAddress", device_id AS "DeviceId"
            FROM nexora.audit_log
            WHERE (company_id = ANY({Companies}) OR ({tenant.BypassRls} AND company_id IS NOT NULL))
              AND ({from}::timestamptz IS NULL OR occurred_at >= {from}::timestamptz)
              AND ({to}::timestamptz IS NULL OR occurred_at <= {to}::timestamptz)
              AND ({filter.Action}::text IS NULL OR action = {filter.Action}::text)
              AND ({filter.EntityType}::text IS NULL OR entity_type = {filter.EntityType}::text)
              AND ({filter.EntityId}::uuid IS NULL OR entity_id = {filter.EntityId}::uuid)
              AND ({filter.UserId}::uuid IS NULL OR user_id = {filter.UserId}::uuid)
            ORDER BY occurred_at DESC
            OFFSET {(page - 1) * size} LIMIT {size}
            """).ToListAsync(ct);

        var total = await db.Database.SqlQuery<long>($"""
            SELECT COUNT(*)::bigint AS "Value" FROM nexora.audit_log
            WHERE (company_id = ANY({Companies}) OR ({tenant.BypassRls} AND company_id IS NOT NULL))
              AND ({from}::timestamptz IS NULL OR occurred_at >= {from}::timestamptz)
              AND ({to}::timestamptz IS NULL OR occurred_at <= {to}::timestamptz)
              AND ({filter.Action}::text IS NULL OR action = {filter.Action}::text)
              AND ({filter.EntityType}::text IS NULL OR entity_type = {filter.EntityType}::text)
              AND ({filter.EntityId}::uuid IS NULL OR entity_id = {filter.EntityId}::uuid)
              AND ({filter.UserId}::uuid IS NULL OR user_id = {filter.UserId}::uuid)
            """).FirstAsync(ct);

        return new AuditPageDto(rows.Select(ToDto).ToList(), total, page, size);
    }

    public async Task<Result<(string FileName, string Content)>> ExportAsync(AuditQueryFilter filter, CancellationToken ct = default)
    {
        var page = await QueryAsync(filter with { Page = 1, PageSize = 200 }, ct);
        var sb = new StringBuilder("occurred_at;user_id;company_id;action;entity_type;entity_id;changes\n");
        foreach (var e in page.Events)
            sb.Append(e.OccurredAt.ToString("o")).Append(';').Append(e.UserId).Append(';').Append(e.CompanyId)
              .Append(';').Append(e.Action).Append(';').Append(e.EntityType).Append(';').Append(e.EntityId)
              .Append(';').Append(e.Changes is null ? "" : string.Join("|", e.Changes.Select(kv => $"{kv.Key}={kv.Value}"))).Append('\n');

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.export_log (company_id, user_id, report_key, format, row_count, sensitivity)
            VALUES ({tenant.ActiveCompanyId}, {tenant.UserId}, 'audit_log', 'csv', {page.Events.Count}, 'restricted')
            """, ct);

        return Result.Success(($"audit_{DateTime.UtcNow:yyyyMMdd_HHmmss}.csv", sb.ToString()));
    }

    private static AuditEventDto ToDto(Row r)
    {
        Dictionary<string, string>? changes = null;
        if (r.Action == "UPDATE" && r.OldValues is not null && r.NewValues is not null)
        {
            var oldV = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.OldValues) ?? [];
            var newV = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(r.NewValues) ?? [];
            changes = new Dictionary<string, string>();
            foreach (var (k, nv) in newV)
            {
                if (k is "updated_at" or "created_at") continue;
                var os = oldV.TryGetValue(k, out var ov) ? ov.ToString() : "";
                if (os != nv.ToString()) changes[k] = $"{os} → {nv}";
            }
            if (changes.Count == 0) changes = null;
        }
        return new AuditEventDto(r.Id, r.OccurredAt, r.UserId, r.CompanyId, r.Action, r.EntityType, r.EntityId,
            changes, r.IpAddress, r.DeviceId);
    }
}

/// <summary>
/// Moteur de détection anti-fraude (Phase 11 §14-18). Balaie l'activité récente selon des
/// règles configurables, produit des signaux notés (0-100). Ne conclut jamais à une fraude
/// à la place de l'humain (§1) ; aucun signal n'est supprimé sans trace (§17).
/// </summary>
public sealed class SqlFraudDetectionService(NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock) : IFraudDetectionService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record RuleRow(string Code, string Name, string Category, string SubjectType, int WindowMinutes, decimal Threshold, string SeverityBase);
    private sealed record Hit(Guid? SubjectId, string? SubjectLabel, Guid? ActorUserId, decimal Value);
    private sealed record SignalRow(
        Guid Id, string RuleCode, string RuleName, string Category, string Severity, int RiskScore,
        string SubjectType, Guid? SubjectId, string? SubjectLabel, Guid? ActorUserId,
        DateTimeOffset WindowFrom, DateTimeOffset WindowTo, decimal ObservedValue, decimal Threshold,
        string Detail, string Status, DateTimeOffset DetectedAt, string? ReviewNote);

    public async Task<FraudScanResult> ScanAsync(CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var rules = await db.Database.SqlQuery<RuleRow>($"""
            SELECT fr.code AS "Code", fr.name AS "Name", fr.category AS "Category", fr.subject_type AS "SubjectType",
                   COALESCE(s.window_minutes, fr.window_minutes) AS "WindowMinutes",
                   COALESCE(s.threshold, fr.threshold) AS "Threshold", fr.severity_base AS "SeverityBase"
            FROM nexora.fraud_rule fr
            JOIN nexora.fraud_rule_setting s ON s.rule_code = fr.code AND s.company_id = {Company}
            WHERE s.enabled
            """).ToListAsync(ct);

        // Clé de dédup = jour de scan : re-balayer le même jour met à jour, ne duplique pas.
        var dedupKey = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        int evaluated = 0, created = 0, updated = 0;
        foreach (var rule in rules)
        {
            var windowFrom = now.AddMinutes(-rule.WindowMinutes);
            var hits = await EvaluateAsync(rule, windowFrom, now, ct);
            if (hits is null) continue;   // règle non implémentée
            evaluated++;

            foreach (var hit in hits.Where(h => (rule.Threshold > 0m && h.Value >= rule.Threshold)
                                                || (rule.Threshold == 0m && h.Value > 0m)))
            {
                var score = RiskScore(rule.SeverityBase, hit.Value, rule.Threshold);
                var severity = Severity(score);
                var detail = $"{rule.Name} : valeur observée {hit.Value:0.##} (seuil {rule.Threshold:0.##}).";
                var n = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.fraud_signal
                        (company_id, rule_code, category, severity, risk_score, subject_type, subject_id, subject_label,
                         actor_user_id, window_from, window_to, observed_value, threshold, detail, status, detected_at)
                    VALUES ({Company}, {rule.Code}, {rule.Category}, {severity}, {(short)score}, {rule.SubjectType},
                            {hit.SubjectId}, {hit.SubjectLabel}, {hit.ActorUserId},
                            {dedupKey}, {now}, {hit.Value}, {rule.Threshold},
                            {detail}, 'open', now())
                    ON CONFLICT (company_id, rule_code, subject_type, subject_id, window_from) DO UPDATE
                    SET observed_value = EXCLUDED.observed_value, risk_score = EXCLUDED.risk_score,
                        severity = EXCLUDED.severity, detail = EXCLUDED.detail, window_to = EXCLUDED.window_to, updated_at = now()
                    WHERE nexora.fraud_signal.status = 'open'
                    """, ct);
                if (n > 0) created++; else updated++;
            }
        }
        return new FraudScanResult(evaluated, created, updated);
    }

    private async Task<List<Hit>?> EvaluateAsync(RuleRow rule, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var f = from.UtcDateTime;
        var fd = DateOnly.FromDateTime(from.UtcDateTime);
        return rule.Code switch
        {
            "pos_frequent_voids" => await CountAsync($"""
                SELECT s.salesperson_user_id AS "SubjectId", u.full_name AS "SubjectLabel",
                       s.salesperson_user_id AS "ActorUserId", COUNT(*)::numeric AS "Value"
                FROM nexora.sale s LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id
                WHERE s.company_id = {Company} AND s.status = 'void' AND s.voided_at >= {f} AND s.salesperson_user_id IS NOT NULL
                GROUP BY s.salesperson_user_id, u.full_name
                """, ct),

            "pos_post_payment_void" => await CountAsync($"""
                SELECT s.salesperson_user_id AS "SubjectId", u.full_name AS "SubjectLabel",
                       s.salesperson_user_id AS "ActorUserId", COUNT(*)::numeric AS "Value"
                FROM nexora.sale s LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id
                WHERE s.company_id = {Company} AND s.status = 'void' AND s.amount_paid > 0 AND s.voided_at >= {f}
                GROUP BY s.salesperson_user_id, u.full_name
                """, ct),

            "pos_abnormal_refunds" => await CountAsync($"""
                SELECT sr.created_by AS "SubjectId", u.full_name AS "SubjectLabel", sr.created_by AS "ActorUserId",
                       COUNT(*)::numeric AS "Value"
                FROM nexora.sale_return sr LEFT JOIN nexora.app_user u ON u.id = sr.created_by
                WHERE sr.company_id = {Company} AND sr.status = 'completed' AND sr.returned_at >= {f} AND sr.created_by IS NOT NULL
                GROUP BY sr.created_by, u.full_name
                """, ct),

            "pos_repeated_price_change" => await CountAsync($"""
                SELECT s.salesperson_user_id AS "SubjectId", u.full_name AS "SubjectLabel",
                       s.salesperson_user_id AS "ActorUserId", COUNT(*)::numeric AS "Value"
                FROM nexora.sale_line sl JOIN nexora.sale s ON s.id = sl.sale_id
                LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id
                WHERE s.company_id = {Company} AND s.sold_at >= {f}
                  AND sl.original_unit_price IS NOT NULL AND sl.original_unit_price <> sl.unit_price
                GROUP BY s.salesperson_user_id, u.full_name
                """, ct),

            "pos_cash_variance" => await CountAsync($"""
                SELECT cs.user_id AS "SubjectId", u.full_name AS "SubjectLabel", cs.user_id AS "ActorUserId",
                       COUNT(*)::numeric AS "Value"
                FROM nexora.cash_session cs LEFT JOIN nexora.app_user u ON u.id = cs.user_id
                WHERE cs.company_id = {Company} AND cs.status IN ('closed','reconciled')
                  AND cs.closed_at >= {f} AND cs.difference IS NOT NULL AND cs.difference <> 0
                GROUP BY cs.user_id, u.full_name
                """, ct),

            "pos_unusual_credit_sales" => await CountAsync($"""
                SELECT s.salesperson_user_id AS "SubjectId", u.full_name AS "SubjectLabel",
                       s.salesperson_user_id AS "ActorUserId", COUNT(*)::numeric AS "Value"
                FROM nexora.sale s LEFT JOIN nexora.app_user u ON u.id = s.salesperson_user_id
                WHERE s.company_id = {Company} AND s.sale_type IN ('credit','mixed') AND s.sold_at >= {f}
                GROUP BY s.salesperson_user_id, u.full_name
                """, ct),

            "stock_frequent_adjustments" => await CountAsync($"""
                SELECT m.user_id AS "SubjectId", u.full_name AS "SubjectLabel", m.user_id AS "ActorUserId",
                       COUNT(*)::numeric AS "Value"
                FROM nexora.stock_movement m LEFT JOIN nexora.app_user u ON u.id = m.user_id
                WHERE m.company_id = {Company} AND m.occurred_at >= {f}
                  AND m.movement_type IN ('adjustment','loss','breakage','expiry') AND m.user_id IS NOT NULL
                GROUP BY m.user_id, u.full_name
                """, ct),

            "stock_repeated_losses" => await CountAsync($"""
                SELECT m.product_id AS "SubjectId", p.name AS "SubjectLabel", NULL::uuid AS "ActorUserId",
                       COUNT(*)::numeric AS "Value"
                FROM nexora.stock_movement m JOIN nexora.product p ON p.id = m.product_id
                WHERE m.company_id = {Company} AND m.occurred_at >= {f}
                  AND m.movement_type IN ('loss','breakage','expiry')
                GROUP BY m.product_id, p.name
                """, ct),

            "finance_reversals" => await CountAsync($"""
                SELECT e.created_by AS "SubjectId", u.full_name AS "SubjectLabel", e.created_by AS "ActorUserId",
                       COUNT(*)::numeric AS "Value"
                FROM nexora.journal_entry e LEFT JOIN nexora.app_user u ON u.id = e.created_by
                WHERE e.company_id = {Company} AND e.status = 'reversed' AND e.entry_date >= {fd} AND e.created_by IS NOT NULL
                GROUP BY e.created_by, u.full_name
                """, ct),

            "finance_supplier_change" => await CountAsync($"""
                SELECT a.user_id AS "SubjectId", u.full_name AS "SubjectLabel", a.user_id AS "ActorUserId",
                       COUNT(*)::numeric AS "Value"
                FROM nexora.audit_log a LEFT JOIN nexora.app_user u ON u.id = a.user_id
                WHERE a.company_id = {Company} AND a.entity_type = 'supplier' AND a.action = 'UPDATE' AND a.occurred_at >= {f}
                GROUP BY a.user_id, u.full_name
                """, ct),

            _ => null,   // pos_unusual_discounts, finance_expense_under_threshold : définis, non implémentés
        };
    }

    private async Task<List<Hit>> CountAsync(FormattableString sql, CancellationToken ct)
        => await db.Database.SqlQuery<Hit>(sql).ToListAsync(ct);

    private static int RiskScore(string severityBase, decimal observed, decimal threshold)
    {
        var baseScore = severityBase switch { "critical" => 90, "high" => 70, "medium" => 50, _ => 25 };
        var overshoot = threshold > 0m ? (int)Math.Min(20m, Math.Round((observed / threshold - 1m) * 20m)) : 10;
        return Math.Clamp(baseScore + Math.Max(0, overshoot), 0, 100);
    }

    private static string Severity(int score) => score switch { >= 85 => "critical", >= 65 => "high", >= 40 => "medium", _ => "low" };

    public async Task<IReadOnlyList<FraudSignalDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<SignalRow>($"""
            SELECT fs.id AS "Id", fs.rule_code AS "RuleCode", fr.name AS "RuleName", fs.category AS "Category",
                   fs.severity AS "Severity", fs.risk_score AS "RiskScore", fs.subject_type AS "SubjectType",
                   fs.subject_id AS "SubjectId", fs.subject_label AS "SubjectLabel", fs.actor_user_id AS "ActorUserId",
                   fs.window_from AS "WindowFrom", fs.window_to AS "WindowTo", fs.observed_value AS "ObservedValue",
                   fs.threshold AS "Threshold", fs.detail AS "Detail", fs.status AS "Status",
                   fs.detected_at AS "DetectedAt", fs.review_note AS "ReviewNote"
            FROM nexora.fraud_signal fs JOIN nexora.fraud_rule fr ON fr.code = fs.rule_code
            WHERE fs.company_id = {Company} AND ({status}::text IS NULL OR fs.status = {status}::text)
            ORDER BY fs.risk_score DESC, fs.detected_at DESC LIMIT 200
            """).ToListAsync(ct);
        return rows.Select(r => new FraudSignalDto(r.Id, r.RuleCode, r.RuleName, r.Category, r.Severity, r.RiskScore,
            r.SubjectType, r.SubjectId, r.SubjectLabel, r.ActorUserId, r.WindowFrom, r.WindowTo, r.ObservedValue,
            r.Threshold, r.Detail, r.Status, r.DetectedAt, r.ReviewNote)).ToList();
    }

    public async Task<Result> ReviewAsync(Guid signalId, ReviewSignalRequest request, CancellationToken ct = default)
    {
        var status = request.Status.Trim().ToLowerInvariant();
        string[] allowed = ["reviewing", "confirmed", "false_positive", "dismissed"];
        if (!allowed.Contains(status)) return Error.Validation("security.signal_status", "Statut de revue invalide.");
        if (status is "confirmed" or "dismissed" && string.IsNullOrWhiteSpace(request.Note))
            return Error.Validation("security.review_note", "Une note de revue est requise pour confirmer ou écarter (§17).");

        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.fraud_signal SET status = {status}, review_note = {request.Note},
                reviewed_by = {tenant.UserId}, reviewed_at = now(), updated_at = now()
            WHERE id = {signalId} AND company_id = {Company}
            """, ct);
        return n == 0 ? Error.NotFound("security.signal", "Signal introuvable.") : Result.Success();
    }
}
