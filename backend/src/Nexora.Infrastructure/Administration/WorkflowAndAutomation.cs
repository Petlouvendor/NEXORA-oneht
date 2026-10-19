using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Administration;
using Nexora.Application.Security;
using Nexora.Contracts.Administration;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Administration;

/// <summary>Workflows d'approbation configurables + moteur de règles testable (Phase 14 §14-16).</summary>
public sealed class SqlWorkflowService(
    NexoraDbContext db, IUnitOfWork uow, ITenantContext tenant, IApprovalWorkflowService approvals) : IWorkflowService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] Ops = ["eq", "neq", "gt", "gte", "lt", "lte", "in", "contains"];

    private sealed record Row(Guid Id, string Code, string Name, string TriggerEvent, string Definition, string Status, bool IsTested, DateTimeOffset? LastTestedAt);

    public async Task<IReadOnlyList<WorkflowDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", trigger_event AS "TriggerEvent", definition::text AS "Definition",
                   status AS "Status", is_tested AS "IsTested", last_tested_at AS "LastTestedAt"
            FROM nexora.workflow WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    /// <summary>Types d'opération soumis à approbation. <c>Automatic</c> : une opération réelle consulte la règle d'elle-même.</summary>
    private static readonly WorkflowTriggerDto[] Triggers =
    [
        new("credit_sale", "Vente à crédit au-delà de la limite", true, "Dérogation de crédit demandée à la caisse ou en vente."),
        new("salary_advance", "Avance sur salaire", true, "Demande d'avance d'un salarié (module RH)."),
        new("employee_loan", "Prêt au salarié", true, "Demande de prêt d'un salarié (module RH)."),
        new("expense", "Dépense", false, "Demandes d'approbation de dépense déposées."),
        new("discount", "Remise", false, "Demandes d'approbation de remise déposées."),
        new("refund", "Remboursement", false, "Demandes d'approbation de remboursement déposées."),
        new("purchase", "Achat", false, "Demandes d'approbation d'achat déposées."),
        new("stock_adjustment", "Ajustement de stock", false, "Demandes d'approbation d'ajustement déposées."),
        new("price_change", "Changement de prix", false, "Demandes d'approbation de prix déposées."),
        new("void", "Annulation de vente", false, "Demandes d'approbation d'annulation déposées."),
        new("period_close", "Réouverture de période", false, "Demandes d'approbation de réouverture déposées."),
    ];

    public async Task<WorkflowCatalogDto> CatalogAsync(CancellationToken ct = default)
    {
        var roles = await db.Database.SqlQuery<WorkflowApproverRoleDto>($"""
            SELECT name AS "Name", description AS "Description" FROM nexora.role
            WHERE status = 'active' AND (company_id = {Company} OR (system_role AND name NOT LIKE 'platform\_%'))
            ORDER BY system_role, name
            """).ToListAsync(ct);
        return new WorkflowCatalogDto(Triggers, roles);
    }

    public async Task<Result<Guid>> UpsertAsync(UpsertWorkflowRequest request, CancellationToken ct = default)
    {
        var code = request.Code?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is < 2 or > 50) return Error.Validation("workflow.code", "Code invalide (2 à 50 caractères).");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("workflow.name", "Nom requis.");
        if (string.IsNullOrWhiteSpace(request.TriggerEvent)) return Error.Validation("workflow.trigger", "Déclencheur requis.");
        if (MapType(request.TriggerEvent) == "other")
            return Error.Validation("workflow.trigger", $"Déclencheur « {request.TriggerEvent} » inconnu.");

        var def = request.Definition;
        if (def.Conditions.Any(c => !Ops.Contains(c.Op.Trim().ToLowerInvariant())))
            return Error.Validation("workflow.condition_op", $"Opérateur invalide. Attendu : {string.Join(", ", Ops)}.");
        if (def.Logic is not ("all" or "any"))
            return Error.Validation("workflow.logic", "Logique invalide (all/any).");
        // Finalisation phase 11 : seule l'approbation a un effet réel. Les opérations réelles ne connaissent que le
        // montant : la condition est donc un seuil de montant (au plus un). Actions et notifications « automatiques »
        // ne sont exécutées nulle part — elles sont refusées plutôt que simulées.
        if (def.Approval is not { } approval)
            return Error.Validation("workflow.approval", "Un workflow exige une approbation (niveaux et rôle approbateur).");
        if (approval.Levels is < 1 or > 5) return Error.Validation("workflow.levels", "De 1 à 5 niveaux d'approbation.");
        if (!string.IsNullOrWhiteSpace(approval.ApproverRoleCode) && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.role WHERE name = {approval.ApproverRoleCode.Trim()} AND status = 'active'
                              AND (company_id = {Company} OR (system_role AND name NOT LIKE 'platform\_%'))) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("workflow.approver_role", $"Rôle approbateur « {approval.ApproverRoleCode} » introuvable ou inactif.");
        if ((def.Conditions?.Count ?? 0) > 1
            || (def.Conditions ?? []).Any(c => c.Field != "amount" || c.Op.Trim().ToLowerInvariant() is not ("gte" or "gt") || !TryDecimal(c.Value, out var v) || v < 0))
            return Error.Validation("workflow.condition",
                "Condition attendue : un seuil de montant (« montant ≥ » ou « montant > » une valeur positive), ou aucune condition.");
        if ((def.Actions?.Count ?? 0) > 0 || (def.Notifications?.Count ?? 0) > 0)
            return Error.Validation("workflow.actions",
                "Les actions et notifications automatiques ne sont pas exécutées par NEXORA : seule l'approbation s'applique.");
        def = def with { Logic = "all", Actions = [], Notifications = [] };
        var defJson = JsonSerializer.Serialize(def);

        var existing = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.workflow WHERE company_id = {Company} AND code = {code}
            """).FirstOrDefaultAsync(ct);

        if (existing != Guid.Empty)
        {
            if ((await LoadAsync(existing, ct))?.Status == "active")
                return Error.Conflict("workflow.active", "Workflow actif : désactivez-le avant de le modifier (il faudra le retester).");
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.workflow SET name = {request.Name.Trim()}, trigger_event = {request.TriggerEvent.Trim()},
                    definition = {defJson}::jsonb, is_tested = false, updated_by = {tenant.UserId}, updated_at = now()
                WHERE id = {existing}
                """, ct);
            return Result.Success(existing);
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.workflow (id, company_id, code, name, trigger_event, definition, status, created_by, updated_by)
            VALUES ({id}, {Company}, {code}, {request.Name.Trim()}, {request.TriggerEvent.Trim()}, {defJson}::jsonb, 'draft',
                    {tenant.UserId}, {tenant.UserId})
            """, ct);
        return Result.Success(id);
    }

    public async Task<Result<WorkflowTestResult>> TestAsync(Guid workflowId, WorkflowTestRequest request, CancellationToken ct = default)
    {
        var wf = await LoadAsync(workflowId, ct);
        if (wf is null) return Error.NotFound("workflow.workflow", "Workflow introuvable.");
        var def = ParseDefinition(wf.Definition);

        var (matched, evals) = Evaluate(def, request.Context);
        var result = new WorkflowTestResult(
            matched, def.Logic, evals,
            PlannedApprovals(def), def.Actions ?? [], def.Notifications ?? []);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.workflow_run (id, company_id, workflow_id, mode, context, matched, decision, created_by)
            VALUES ({Guid.NewGuid()}, {Company}, {workflowId}, 'test', {request.Context.GetRawText()}::jsonb, {matched},
                    {JsonSerializer.Serialize(result)}::jsonb, {tenant.UserId})
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.workflow SET is_tested = true, last_tested_at = now(), updated_at = now() WHERE id = {workflowId}
            """, ct);
        return Result.Success(result);
    }

    public async Task<Result> ActivateAsync(Guid workflowId, CancellationToken ct = default)
    {
        var wf = await LoadAsync(workflowId, ct);
        if (wf is null) return Error.NotFound("workflow.workflow", "Workflow introuvable.");
        if (!wf.IsTested) return Error.Validation("workflow.untested", "Testez le workflow avant de l'activer (§16).");
        var def = ParseDefinition(wf.Definition);
        if (def.Approval is null) return Error.Validation("workflow.approval", "Un workflow exige une approbation.");
        var minAmount = MinAmount(def);
        var conflict = await db.Database.SqlQuery<string?>($"""
            SELECT COALESCE(configuration->>'workflow', 'règle existante') AS "Value" FROM nexora.approval_policy
            WHERE company_id = {Company} AND request_type = {MapType(wf.TriggerEvent)} AND status = 'active' AND min_amount = {minAmount}
              AND COALESCE(configuration->>'workflow', '') <> {wf.Code}
            LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (conflict is not null)
            return Error.Conflict("workflow.conflict",
                $"Une autre règle active (« {conflict} ») couvre déjà ce type d'opération à partir du même montant : désactivez-la d'abord.");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.workflow SET status = 'active', updated_at = now() WHERE id = {workflowId}
                """, token);

            if (def.Approval is { } appr)
            {
                var requestType = MapType(wf.TriggerEvent);
                var roleId = string.IsNullOrWhiteSpace(appr.ApproverRoleCode) ? null : await db.Database.SqlQuery<Guid?>($"""
                    SELECT id AS "Value" FROM nexora.role
                    WHERE name = {appr.ApproverRoleCode} AND (company_id = {Company} OR system_role) ORDER BY system_role LIMIT 1
                    """).FirstOrDefaultAsync(token);

                var policyId = await db.Database.SqlQuery<Guid?>($"""
                    SELECT id AS "Value" FROM nexora.approval_policy
                    WHERE company_id = {Company} AND request_type = {requestType} AND configuration->>'workflow' = {wf.Code}
                    """).FirstOrDefaultAsync(token);

                if (policyId is { } pid)
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.approval_policy SET required_levels = {(short)Math.Clamp(appr.Levels, 1, 10)}, min_amount = {minAmount},
                            approver_role_id = {roleId}, status = 'active', updated_at = now() WHERE id = {pid}
                        """, token);
                else
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.approval_policy
                            (company_id, request_type, min_amount, required_levels, approver_role_id, configuration, status)
                        VALUES ({Company}, {requestType}, {minAmount}, {(short)Math.Clamp(appr.Levels, 1, 10)}, {roleId},
                                {$"{{\"workflow\":\"{wf.Code}\"}}"}::jsonb, 'active')
                        """, token);
            }
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<Result> SetStatusAsync(Guid workflowId, string status, CancellationToken ct = default)
    {
        var s = status?.Trim().ToLowerInvariant() ?? "";
        if (s is not ("draft" or "active" or "inactive")) return Error.Validation("workflow.status", "Statut invalide.");
        if (s == "active")
            return await ActivateAsync(workflowId, ct);
        var wf = await LoadAsync(workflowId, ct);
        if (wf is null) return Error.NotFound("workflow.workflow", "Workflow introuvable.");
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.workflow SET status = {s}, updated_at = now() WHERE id = {workflowId}
                """, token);
            // Sans cela, l'approbation restait exigée par les opérations réelles après la désactivation.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.approval_policy SET status = 'inactive', updated_at = now()
                WHERE company_id = {Company} AND configuration->>'workflow' = {wf.Code}
                """, token);
            return 0;
        }, ct);
        return Result.Success();
    }

    public async Task<Result<WorkflowEvaluationResult>> EvaluateAsync(WorkflowTriggerRequest request, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<Row>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", trigger_event AS "TriggerEvent", definition::text AS "Definition",
                   status AS "Status", is_tested AS "IsTested", last_tested_at AS "LastTestedAt"
            FROM nexora.workflow WHERE company_id = {Company} AND status = 'active' AND trigger_event = {request.TriggerEvent.Trim()}
            """).ToListAsync(ct);

        foreach (var wf in rows)
        {
            var def = ParseDefinition(wf.Definition);
            var (matched, _) = Evaluate(def, request.Context);
            if (!matched) continue;

            Guid? approvalId = null;
            var message = "Workflow déclenché.";
            if (def.Approval is not null)
            {
                decimal? amount = TryNumber(request.Context, "amount");
                var outcome = await approvals.SubmitAsync(new SubmitApprovalRequest(
                    MapType(wf.TriggerEvent), request.ReferenceType, request.ReferenceId, amount, $"workflow:{wf.Code}"), ct);
                if (outcome.IsSuccess)
                {
                    approvalId = outcome.Value!.RequestId;
                    message = outcome.Value.ApprovalRequired ? "Approbation requise — demande créée." : "Aucune approbation requise (politique absente).";
                }
                else
                    // Un échec de la demande d'approbation n'est plus présenté comme un déclenchement réussi.
                    return outcome.Error!;
            }

            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.workflow_run
                    (id, company_id, workflow_id, mode, trigger_ref_type, trigger_ref_id, context, matched, decision, approval_request_id, created_by)
                VALUES ({Guid.NewGuid()}, {Company}, {wf.Id}, 'live', {request.ReferenceType}, {request.ReferenceId},
                        {request.Context.GetRawText()}::jsonb, true,
                        {JsonSerializer.Serialize(new { actions = def.Actions, notifications = def.Notifications })}::jsonb,
                        {approvalId}, {tenant.UserId})
                """, ct);

            return Result.Success(new WorkflowEvaluationResult(true, wf.Id, approvalId, message));
        }

        return Result.Success(new WorkflowEvaluationResult(false, null, null, "Aucun workflow ne correspond."));
    }

    // --- Moteur de règles ---------------------------------------------------

    private static (bool Matched, IReadOnlyList<ConditionEvalDto> Evals) Evaluate(WorkflowDefinitionDto def, JsonElement ctx)
    {
        var evals = new List<ConditionEvalDto>();
        foreach (var c in def.Conditions ?? [])
        {
            JsonElement? actual = ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty(c.Field, out var a) ? a : null;
            var passed = Compare(actual, c.Op.Trim().ToLowerInvariant(), c.Value);
            evals.Add(new ConditionEvalDto(c.Field, c.Op, c.Value, actual, passed));
        }
        var matched = (def.Conditions?.Count ?? 0) == 0
            ? true
            : (def.Logic == "any" ? evals.Any(e => e.Passed) : evals.All(e => e.Passed));
        return (matched, evals);
    }

    private static bool Compare(JsonElement? actual, string op, JsonElement expected)
    {
        if (actual is null) return op == "neq";
        var a = actual.Value;

        if (op == "in")
            return expected.ValueKind == JsonValueKind.Array
                && expected.EnumerateArray().Any(e => JsonEquals(a, e));
        if (op == "contains")
            return a.ValueKind == JsonValueKind.String && expected.ValueKind == JsonValueKind.String
                && (a.GetString() ?? "").Contains(expected.GetString() ?? "", StringComparison.OrdinalIgnoreCase);
        if (op == "eq") return JsonEquals(a, expected);
        if (op == "neq") return !JsonEquals(a, expected);

        if (TryDecimal(a, out var av) && TryDecimal(expected, out var ev))
            return op switch { "gt" => av > ev, "gte" => av >= ev, "lt" => av < ev, "lte" => av <= ev, _ => false };
        return false;
    }

    private static bool JsonEquals(JsonElement a, JsonElement b)
    {
        if (TryDecimal(a, out var av) && TryDecimal(b, out var bv)) return av == bv;
        return a.ToString() == b.ToString();
    }

    private static bool TryDecimal(JsonElement e, out decimal value)
    {
        value = 0m;
        if (e.ValueKind == JsonValueKind.Number) { value = e.GetDecimal(); return true; }
        return e.ValueKind == JsonValueKind.String && decimal.TryParse(e.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private static decimal? TryNumber(JsonElement ctx, string field)
        => ctx.ValueKind == JsonValueKind.Object && ctx.TryGetProperty(field, out var v) && TryDecimal(v, out var d) ? d : null;

    /// <summary>Montant à partir duquel la règle s'applique (politique d'approbation : <c>min_amount ≤ montant</c>).</summary>
    private static decimal MinAmount(WorkflowDefinitionDto def)
    {
        var c = (def.Conditions ?? []).FirstOrDefault(x => x.Field == "amount");
        if (c is null || !TryDecimal(c.Value, out var v)) return 0m;
        return c.Op.Trim().ToLowerInvariant() == "gt" ? v + 0.01m : v;
    }

    private static IReadOnlyList<string> PlannedApprovals(WorkflowDefinitionDto def)
        => def.Approval is { } a
            ? [$"{a.Levels} niveau(x) d'approbation" + (string.IsNullOrWhiteSpace(a.ApproverRoleCode) ? "" : $" par « {a.ApproverRoleCode} »")]
            : [];

    private static string MapType(string triggerEvent) => triggerEvent.Trim().ToLowerInvariant() switch
    {
        "expense_recorded" or "expense" => "expense",
        "discount_applied" or "discount" => "discount",
        "refund_issued" or "refund" => "refund",
        "stock_adjustment" or "stock_adjustment_large" => "stock_adjustment",
        "price_change" or "price_changed" => "price_change",
        "period_reopen" or "period_reopened" => "period_close",
        "supplier_invoice" or "supplier_debt" or "purchase" => "purchase",
        "credit_over_limit" or "credit_sale" => "credit_sale",
        "salary_advance" => "salary_advance",
        "employee_loan" or "loan" => "employee_loan",
        "void" or "sale_void" => "void",
        _ => "other",
    };

    private Task<Row?> LoadAsync(Guid id, CancellationToken ct) => db.Database.SqlQuery<Row>($"""
        SELECT id AS "Id", code AS "Code", name AS "Name", trigger_event AS "TriggerEvent", definition::text AS "Definition",
               status AS "Status", is_tested AS "IsTested", last_tested_at AS "LastTestedAt"
        FROM nexora.workflow WHERE id = {id} AND company_id = {Company}
        """).FirstOrDefaultAsync(ct);

    private static WorkflowDefinitionDto ParseDefinition(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinitionDto>(json, Json)
                   ?? new WorkflowDefinitionDto("all", [], null, [], []);
        }
        catch (JsonException) { return new WorkflowDefinitionDto("all", [], null, [], []); }
    }

    private static WorkflowDto Map(Row r) => new(
        r.Id, r.Code, r.Name, r.TriggerEvent, ParseDefinition(r.Definition), r.Status, r.IsTested, r.LastTestedAt);
}

/// <summary>Automatisations planifiées : relances, alertes, rapports (Phase 14 §17-19).
/// Depuis la Phase 34, les notifications produites passent par <see cref="INotificationService"/>
/// (préférences + routage multi-canal + déduplication + file de remise fiable).</summary>
public sealed class SqlAutomationService(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock,
    Nexora.Application.Notifications.INotificationService notifications, IBusinessCalendar calendar)
    : IAutomationService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
    private static readonly string[] Kinds =
        ["dunning", "low_stock_alert", "expiry_alert", "scheduled_report", "supplier_reminder", "reconcile_assist", "archive", "scheduled_export"];
    private static readonly string[] Executable = ["dunning", "low_stock_alert", "expiry_alert"];

    private sealed record AutoRow(Guid Id, string Code, string Name, string Kind, string Schedule, string Parameters, string Status, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, Guid? CreatedBy);
    private sealed record RunRow(Guid Id, Guid AutomationId, string Mode, string Status, int ItemsProcessed, string Result, string? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);
    private sealed record OverdueRow(Guid Id, Guid CustomerId, string CustomerName, decimal Remaining, int DaysLate);
    private sealed record DunningRow(Guid Id, string Name, string Channel, string Levels, string Status);
    private sealed record LowStockRow(Guid ProductId, string Name, decimal OnHand, decimal ReorderPoint);
    private sealed record ExpiryRow(Guid Id, string LotNumber, DateOnly? Exp);

    public async Task<IReadOnlyList<AutomationDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<AutoRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", kind AS "Kind", schedule::text AS "Schedule",
                   parameters::text AS "Parameters", status AS "Status", last_run_at AS "LastRunAt", next_run_at AS "NextRunAt", created_by AS "CreatedBy"
            FROM nexora.automation WHERE company_id = {Company} ORDER BY name
            """).ToListAsync(ct);
        return rows.Select(Map).ToList();
    }

    public async Task<Result<Guid>> UpsertAsync(UpsertAutomationRequest request, CancellationToken ct = default)
    {
        var code = request.Code?.Trim().ToLowerInvariant() ?? "";
        if (code.Length is < 2 or > 50) return Error.Validation("automation.code", "Code invalide.");
        var kind = request.Kind?.Trim().ToLowerInvariant() ?? "";
        if (!Kinds.Contains(kind)) return Error.Validation("automation.kind", $"Type inconnu. Attendu : {string.Join(", ", Kinds)}.");
        // Finalisation phase 11 : seuls ces types ont un exécuteur réel ; les autres « s'exécutaient » sans aucun effet.
        if (!Executable.Contains(kind))
            return Error.Validation("automation.kind", $"Le type « {kind} » n'est pas encore exécuté par NEXORA : il ne peut pas être créé.");
        var frequency = request.Schedule.ValueKind == JsonValueKind.Object && request.Schedule.TryGetProperty("frequency", out var f) ? f.GetString() : null;
        if (frequency is not ("hourly" or "daily" or "weekly" or "monthly"))
            return Error.Validation("automation.schedule", "Fréquence requise : hourly, daily, weekly ou monthly.");
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("automation.name", "Nom requis.");

        var schedule = request.Schedule.ValueKind == JsonValueKind.Object ? request.Schedule.GetRawText() : "{}";
        var parameters = request.Parameters.ValueKind == JsonValueKind.Object ? request.Parameters.GetRawText() : "{}";
        var next = ComputeNextRun(request.Schedule, clock.UtcNow, await calendar.ZoneAsync(ct));

        var existing = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.automation WHERE company_id = {Company} AND code = {code}
            """).FirstOrDefaultAsync(ct);

        if (existing != Guid.Empty)
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.automation SET name = {request.Name.Trim()}, kind = {kind}, schedule = {schedule}::jsonb,
                    parameters = {parameters}::jsonb, next_run_at = {next}, updated_at = now() WHERE id = {existing}
                """, ct);
            return Result.Success(existing);
        }

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.automation (id, company_id, code, name, kind, schedule, parameters, status, next_run_at, created_by)
            VALUES ({id}, {Company}, {code}, {request.Name.Trim()}, {kind}, {schedule}::jsonb, {parameters}::jsonb, 'active', {next}, {tenant.UserId})
            """, ct);
        return Result.Success(id);
    }

    public async Task<Result> SetStatusAsync(Guid automationId, string status, CancellationToken ct = default)
    {
        var s = status?.Trim().ToLowerInvariant() ?? "";
        if (s is not ("active" or "paused")) return Error.Validation("automation.status", "Statut invalide (active/paused).");
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.automation SET status = {s}, updated_at = now() WHERE id = {automationId} AND company_id = {Company}
            """, ct);
        return n == 0 ? Error.NotFound("automation.automation", "Automatisation introuvable.") : Result.Success();
    }

    public async Task<Result<AutomationRunDto>> RunNowAsync(Guid automationId, bool preview, CancellationToken ct = default)
    {
        var auto = await db.Database.SqlQuery<AutoRow>($"""
            SELECT id AS "Id", code AS "Code", name AS "Name", kind AS "Kind", schedule::text AS "Schedule",
                   parameters::text AS "Parameters", status AS "Status", last_run_at AS "LastRunAt", next_run_at AS "NextRunAt", created_by AS "CreatedBy"
            FROM nexora.automation WHERE id = {automationId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (auto is null) return Error.NotFound("automation.automation", "Automatisation introuvable.");

        var mode = preview ? "test" : "live";
        var runId = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.automation_run (id, company_id, automation_id, mode, status) VALUES ({runId}, {Company}, {automationId}, {mode}, 'running')
            """, ct);

        var recipient = auto.CreatedBy ?? tenant.UserId;
        int items;
        object result;
        try
        {
            (items, result) = auto.Kind switch
            {
                "dunning" => await RunDunningAsync(preview, recipient, ct),
                "low_stock_alert" => await RunLowStockAsync(preview, recipient, ct),
                "expiry_alert" => await RunExpiryAsync(auto.Parameters, preview, recipient, ct),
                _ => (0, new { note = $"Exécuteur « {auto.Kind} » défini — aucun effet dans cette phase." }),
            };
        }
        catch (Exception ex)
        {
            // Le détail technique reste au journal d'exécution ; l'utilisateur reçoit un message lisible.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.automation_run SET status = 'failed', error = {ex.Message}, finished_at = now() WHERE id = {runId}
                """, ct);
            return Error.Unexpected("automation.run_failed",
                "L'automatisation a échoué ; le détail est consigné dans son historique d'exécution.");
        }

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.automation_run SET status = 'success', items_processed = {items}, result = {JsonSerializer.Serialize(result)}::jsonb, finished_at = now()
            WHERE id = {runId}
            """, ct);
        if (!preview)
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.automation SET last_run_at = now(), next_run_at = {ComputeNextRun(SafeJson(auto.Schedule), clock.UtcNow, await calendar.ZoneAsync(ct))}, updated_at = now()
                WHERE id = {automationId}
                """, ct);

        var row = await db.Database.SqlQuery<RunRow>($"""
            SELECT id AS "Id", automation_id AS "AutomationId", mode AS "Mode", status AS "Status", items_processed AS "ItemsProcessed",
                   result::text AS "Result", error AS "Error", started_at AS "StartedAt", finished_at AS "FinishedAt"
            FROM nexora.automation_run WHERE id = {runId}
            """).FirstAsync(ct);
        return Result.Success(MapRun(row));
    }

    public async Task<int> RunDueAsync(CancellationToken ct = default)
    {
        var due = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.automation
            WHERE company_id = {Company} AND status = 'active' AND next_run_at IS NOT NULL AND next_run_at <= now()
            """).ToListAsync(ct);
        foreach (var id in due) await RunNowAsync(id, preview: false, ct);
        return due.Count;
    }

    public async Task<IReadOnlyList<AutomationRunDto>> RunsAsync(Guid automationId, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<RunRow>($"""
            SELECT id AS "Id", automation_id AS "AutomationId", mode AS "Mode", status AS "Status", items_processed AS "ItemsProcessed",
                   result::text AS "Result", error AS "Error", started_at AS "StartedAt", finished_at AS "FinishedAt"
            FROM nexora.automation_run WHERE automation_id = {automationId} AND company_id = {Company}
            ORDER BY started_at DESC LIMIT 50
            """).ToListAsync(ct);
        return rows.Select(MapRun).ToList();
    }

    // --- Relances clients (§19) -------------------------------------------

    public async Task<DunningPolicyDto> GetDunningPolicyAsync(CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<DunningRow>($"""
            SELECT id AS "Id", name AS "Name", channel AS "Channel", levels::text AS "Levels", status AS "Status"
            FROM nexora.dunning_policy WHERE company_id = {Company} AND status = 'active' ORDER BY created_at LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (row is null)
            return new DunningPolicyDto(null, "Relances standard", "email",
            [
                new DunningLevelDto(1, 3, "Rappel amical", "Votre échéance est dépassée de quelques jours."),
                new DunningLevelDto(2, 15, "Relance", "Merci de régulariser votre solde sous 7 jours."),
                new DunningLevelDto(3, 30, "Mise en demeure", "Sans règlement, le dossier sera transmis."),
            ], "active");
        var levels = JsonSerializer.Deserialize<List<DunningLevelDto>>(row.Levels) ?? [];
        return new DunningPolicyDto(row.Id, row.Name, row.Channel, levels, row.Status);
    }

    public async Task<Result<DunningPolicyDto>> SetDunningPolicyAsync(DunningPolicyDto policy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(policy.Name)) return Error.Validation("dunning.name", "Nom requis.");
        if (policy.Levels.Count == 0) return Error.Validation("dunning.levels", "Au moins un niveau de relance.");
        if (policy.Levels.Any(l => l.OffsetDays < 0)) return Error.Validation("dunning.offset", "Les décalages doivent être positifs.");
        var levels = JsonSerializer.Serialize(policy.Levels.OrderBy(l => l.Level));

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.dunning_policy (id, company_id, name, channel, levels, status, created_by)
            VALUES ({policy.Id ?? Guid.NewGuid()}, {Company}, {policy.Name.Trim()}, {policy.Channel}, {levels}::jsonb, 'active', {tenant.UserId})
            ON CONFLICT (company_id, name) DO UPDATE SET channel = EXCLUDED.channel, levels = EXCLUDED.levels, updated_at = now()
            """, ct);
        return Result.Success(await GetDunningPolicyAsync(ct));
    }

    private async Task<(int, object)> RunDunningAsync(bool preview, Guid? recipient, CancellationToken ct)
    {
        var policy = await GetDunningPolicyAsync(ct);
        var overdue = await db.Database.SqlQuery<OverdueRow>($"""
            SELECT r.id AS "Id", r.customer_id AS "CustomerId", c.name AS "CustomerName", r.remaining_amount AS "Remaining",
                   (current_date - r.due_date)::int AS "DaysLate"
            FROM nexora.customer_receivable r
            JOIN nexora.customer c ON c.id = r.customer_id
            WHERE r.company_id = {Company} AND r.status IN ('OPEN','PARTIALLY_PAID','OVERDUE')
              AND r.due_date IS NOT NULL AND r.due_date < current_date AND c.dunning_opt_out = false
            ORDER BY r.due_date LIMIT 100
            """).ToListAsync(ct);

        var byLevel = new Dictionary<int, int>();
        var processed = 0;
        foreach (var o in overdue)
        {
            var level = policy.Levels.Where(l => o.DaysLate >= l.OffsetDays).OrderByDescending(l => l.OffsetDays).FirstOrDefault();
            if (level is null) continue;
            byLevel[level.Level] = byLevel.GetValueOrDefault(level.Level) + 1;
            processed++;

            if (!preview && recipient is { } dunningRecipient)
                await notifications.NotifyAsync(new Nexora.Contracts.Notifications.NotifyRequest(
                    Category: "dunning",
                    Recipients: [dunningRecipient],
                    TemplateKey: "dunning.reminder",
                    Variables: new Dictionary<string, string?>
                    {
                        ["level"] = level.Level.ToString(),
                        ["customer.name"] = o.CustomerName,
                        ["reminder.title"] = level.Title,
                        ["invoice.balance"] = o.Remaining.ToString("0.00"),
                        ["invoice.days_late"] = o.DaysLate.ToString(),
                        ["company.name"] = "",
                    },
                    Priority: level.Level >= 3 ? 2 : 3,
                    Severity: level.Level >= 3 ? "warning" : "info",
                    Source: "automation",
                    ReferenceType: "customer",
                    ReferenceId: o.CustomerId,
                    DedupKey: $"dunning:{o.Id}:L{level.Level}"), ct);
        }
        return (processed, new { overdue = overdue.Count, processed, byLevel, preview });
    }

    private async Task<(int, object)> RunLowStockAsync(bool preview, Guid? recipient, CancellationToken ct)
    {
        var low = await db.Database.SqlQuery<LowStockRow>($"""
            SELECT rr.product_id AS "ProductId", p.name AS "Name",
                   COALESCE(SUM(sb.quantity_available), 0) AS "OnHand", rr.reorder_point AS "ReorderPoint"
            FROM nexora.product_reorder_rule rr
            JOIN nexora.product p ON p.id = rr.product_id
            LEFT JOIN nexora.stock_balance sb ON sb.product_id = rr.product_id AND sb.company_id = rr.company_id
            WHERE rr.company_id = {Company} AND rr.status = 'active' AND rr.reorder_point > 0
            GROUP BY rr.product_id, p.name, rr.reorder_point
            HAVING COALESCE(SUM(sb.quantity_available), 0) <= rr.reorder_point
            """).ToListAsync(ct);

        if (!preview && recipient is { } stockRecipient)
            foreach (var l in low.Take(50))
                await notifications.NotifyAsync(new Nexora.Contracts.Notifications.NotifyRequest(
                    Category: "low_stock",
                    Recipients: [stockRecipient],
                    TemplateKey: "stock.low",
                    Variables: new Dictionary<string, string?>
                    {
                        ["product.name"] = l.Name,
                        ["stock.on_hand"] = l.OnHand.ToString("0.###"),
                        ["stock.reorder_point"] = l.ReorderPoint.ToString("0.###"),
                    },
                    Priority: 3, Severity: "warning", Source: "automation",
                    ReferenceType: "product", ReferenceId: l.ProductId,
                    DedupKey: $"low_stock:{l.ProductId}:{clock.UtcNow:yyyyMMdd}"), ct);
        return (low.Count, new { belowReorderPoint = low.Count, preview });
    }

    private async Task<(int, object)> RunExpiryAsync(string parameters, bool preview, Guid? recipient, CancellationToken ct)
    {
        var days = 30;
        try { if (SafeJson(parameters).TryGetProperty("withinDays", out var d) && d.TryGetInt32(out var v)) days = Math.Clamp(v, 1, 365); }
        catch (Exception) { /* défaut */ }

        var expiring = await db.Database.SqlQuery<ExpiryRow>($"""
            SELECT id AS "Id", lot_number AS "LotNumber", expiration_date AS "Exp" FROM nexora.lot
            WHERE company_id = {Company} AND status = 'active' AND expiration_date IS NOT NULL
              AND expiration_date <= current_date + {days}
            """).ToListAsync(ct);

        if (!preview && recipient is { } expiryRecipient)
            foreach (var e in expiring.Take(50))
                await notifications.NotifyAsync(new Nexora.Contracts.Notifications.NotifyRequest(
                    Category: "expiring_lot",
                    Recipients: [expiryRecipient],
                    TemplateKey: "stock.expiring",
                    Variables: new Dictionary<string, string?>
                    {
                        ["lot.number"] = e.LotNumber,
                        ["lot.expiration_date"] = e.Exp?.ToString("yyyy-MM-dd") ?? "",
                    },
                    Priority: 3, Severity: "warning", Source: "automation",
                    ReferenceType: "lot", ReferenceId: e.Id,
                    DedupKey: $"expiring_lot:{e.Id}"), ct);
        return (expiring.Count, new { expiringWithinDays = days, count = expiring.Count, preview });
    }

    /// <summary>
    /// Prochaine exécution, l'heure « atTime » étant celle de l'entreprise (audit 2026-10 éd. 2 : 08:00 était pris en
    /// UTC, soit 04:00 à Port-au-Prince).
    /// </summary>
    internal static DateTimeOffset? ComputeNextRun(JsonElement schedule, DateTimeOffset from, TimeZoneInfo zone)
    {
        if (schedule.ValueKind != JsonValueKind.Object) return null;
        var frequency = schedule.TryGetProperty("frequency", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "daily";
        var atTime = schedule.TryGetProperty("atTime", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "08:00" : "08:00";
        var parts = atTime.Split(':');
        var hour = parts.Length > 0 && int.TryParse(parts[0], out var h) ? Math.Clamp(h, 0, 23) : 8;
        var minute = parts.Length > 1 && int.TryParse(parts[1], out var m) ? Math.Clamp(m, 0, 59) : 0;

        var local = TimeZoneInfo.ConvertTime(from, zone);
        DateTimeOffset At(DateTime day)
        {
            var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified);
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();   // Npgsql : UTC seulement
        }
        var baseDay = At(local.Date);
        return frequency switch
        {
            "once" => null,
            "hourly" => from.AddHours(1),
            "daily" => baseDay > from ? baseDay : At(local.Date.AddDays(1)),
            "weekly" => At(local.Date.AddDays(7)),
            "monthly" => At(local.Date.AddMonths(1)),
            _ => At(local.Date.AddDays(1)),
        };
    }

    private static JsonElement SafeJson(string json)
    {
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone(); }
        catch (JsonException) { return JsonDocument.Parse("{}").RootElement.Clone(); }
    }

    private static AutomationDto Map(AutoRow r) => new(
        r.Id, r.Code, r.Name, r.Kind, SafeJson(r.Schedule), SafeJson(r.Parameters), r.Status, r.LastRunAt, r.NextRunAt);

    private static AutomationRunDto MapRun(RunRow r) => new(
        r.Id, r.AutomationId, r.Mode, r.Status, r.ItemsProcessed, SafeJson(r.Result), r.Error, r.StartedAt, r.FinishedAt);
}
