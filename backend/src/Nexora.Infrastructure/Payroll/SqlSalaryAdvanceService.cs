using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Engine;
using Nexora.Application.Finance.Events;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Contracts.Security;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Avances sur salaire, acomptes et prêts au personnel (§12-§17). Chaque avance est une créance distincte sur
/// l'employé (§12), avec sa référence, son solde et son échéancier (§16) ; la demande passe les contrôles de
/// politique (§13.2), le workflow d'approbation configuré (Phase 11, jamais par soi-même), puis le versement relié
/// à la trésorerie (§13.5). Les récupérations (paie ou remboursement direct) sont immuables et ne dépassent jamais
/// le solde (garde de la base, §15).
/// </summary>
public sealed class SqlSalaryAdvanceService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    PayrollAccess access,
    PayrollConfigurationReader config,
    PayrollNotifier notifier,
    IApprovalWorkflowService approvals,
    IFinancialEngine engine,
    IDateTimeProvider clock,
    ILogger<SqlSalaryAdvanceService> logger, IBusinessCalendar calendar) : ISalaryAdvanceService
{
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    private static readonly string[] ReadWide =
    [
        Permissions.Payroll.ViewSalaries, Permissions.Payroll.AdvanceRequest, Permissions.Payroll.AdvancePay,
        Permissions.Payroll.AdvanceSchedule, Permissions.Payroll.AdvanceApprove,
    ];
    private static readonly string[] ReadTeam = [Permissions.Payroll.AdvanceApprove, Permissions.Hr.ApproveRequests];

    private Task<EmployeeScope> ScopeAsync(CancellationToken ct) => access.ScopeAsync(ReadWide, ReadTeam, allowSelf: true, ct);

    private static Error NotFound() => Error.NotFound("payroll.advance_not_found", "Avance introuvable.");

    private static string KindLabel(string kind) => kind switch { "loan" => "Prêt", "acompte" => "Acompte", _ => "Avance" };

    // ================================================================== Lecture

    private sealed record AdvanceRow(
        Guid Id, string AdvanceNumber, Guid EmployeeId, string EmployeeName, string EmployeeNumber, string Kind, string Currency,
        DateOnly RequestDate, decimal AmountRequested, decimal? AmountApproved, decimal AmountPaid, decimal RecoveredAmount, decimal Balance,
        string? Reason, string? Conditions, string RecoveryMethod, int InstallmentsCount, decimal? RecoveryPercent, DateOnly? FirstRecoveryDate,
        string Status, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, string? ApprovedByName, DateTimeOffset? ApprovedAt,
        string? DecisionNote, string? PaidByName, DateTimeOffset? PaidAt, string? FinancialAccountName, string? PaymentMethod,
        string? PaymentReference, Guid? JournalEntryId, DateTimeOffset? SettledAt, string? CancelReason, Guid? ApprovalRequestId,
        int ApprovalLevel, int RequiredLevels, Guid? PaymentOperationUuid);

    private IQueryable<AdvanceRow> Query() => db.Database.SqlQuery<AdvanceRow>($"""
        SELECT a.id AS "Id", a.advance_number AS "AdvanceNumber", a.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName",
               e.employee_number AS "EmployeeNumber", a.kind AS "Kind", cur.code AS "Currency", a.request_date AS "RequestDate",
               a.amount_requested AS "AmountRequested", a.amount_approved AS "AmountApproved", a.amount_paid AS "AmountPaid",
               a.recovered_amount AS "RecoveredAmount", a.balance AS "Balance", a.reason AS "Reason", a.conditions AS "Conditions",
               a.recovery_method AS "RecoveryMethod", a.installments_count AS "InstallmentsCount", a.recovery_percent AS "RecoveryPercent",
               a.first_recovery_date AS "FirstRecoveryDate", a.status AS "Status", a.requested_by AS "RequestedBy",
               rq.full_name AS "RequestedByName", a.requested_at AS "RequestedAt", ap.full_name AS "ApprovedByName",
               a.approved_at AS "ApprovedAt", a.decision_note AS "DecisionNote", pb.full_name AS "PaidByName", a.paid_at AS "PaidAt",
               fa.name AS "FinancialAccountName", a.payment_method AS "PaymentMethod", a.payment_reference AS "PaymentReference",
               a.journal_entry_id AS "JournalEntryId", a.settled_at AS "SettledAt", a.cancel_reason AS "CancelReason",
               a.approval_request_id AS "ApprovalRequestId", COALESCE(ar.current_level, 0) AS "ApprovalLevel",
               COALESCE(ar.required_levels, 1)::int AS "RequiredLevels", a.payment_operation_uuid AS "PaymentOperationUuid"
        FROM nexora.employee_advance a
        JOIN nexora.employee e ON e.id = a.employee_id
        JOIN nexora.currency cur ON cur.id = a.currency_id
        LEFT JOIN nexora.app_user rq ON rq.id = a.requested_by
        LEFT JOIN nexora.app_user ap ON ap.id = a.approved_by
        LEFT JOIN nexora.app_user pb ON pb.id = a.paid_by
        LEFT JOIN nexora.financial_account fa ON fa.id = a.financial_account_id
        LEFT JOIN nexora.approval_request ar ON ar.id = a.approval_request_id
        WHERE a.company_id = {Company}
        """);

    private sealed record InstallmentRow(Guid Id, Guid AdvanceId, int SeqNo, int ScheduleVersion, DateOnly DueDate, decimal Amount,
        decimal RecoveredAmount, string Status);

    private async Task<AdvanceDto> ToDtoAsync(AdvanceRow r, CancellationToken ct)
    {
        var schedule = await db.Database.SqlQuery<InstallmentRow>($"""
            SELECT id AS "Id", advance_id AS "AdvanceId", seq_no AS "SeqNo", schedule_version AS "ScheduleVersion", due_date AS "DueDate",
                   amount AS "Amount", recovered_amount AS "RecoveredAmount", status AS "Status"
            FROM nexora.advance_installment WHERE advance_id = {r.Id}
            ORDER BY schedule_version, seq_no
            """).ToListAsync(ct);
        IReadOnlyList<AdvanceCheckDto> checks = [];
        if (r.Status == "requested")
            checks = (await ChecksAsync(r.EmployeeId, r.Kind, r.AmountApproved ?? r.AmountRequested, r.Id, ct))
                .Select(c => new AdvanceCheckDto(c.Code, c.Passed, c.Blocking, c.Message)).ToList();
        return new AdvanceDto(r.Id, r.AdvanceNumber, r.EmployeeId, r.EmployeeName, r.EmployeeNumber, r.Kind, r.Currency, r.RequestDate,
            r.AmountRequested, r.AmountApproved, r.AmountPaid, r.RecoveredAmount, r.Balance, r.Reason, r.Conditions, r.RecoveryMethod,
            r.InstallmentsCount, r.RecoveryPercent, r.FirstRecoveryDate, r.Status, r.RequestedByName, r.RequestedAt, r.ApprovedByName,
            r.ApprovedAt, r.DecisionNote, r.PaidByName, r.PaidAt, r.FinancialAccountName, r.PaymentMethod, r.PaymentReference,
            r.JournalEntryId, r.SettledAt, r.CancelReason, r.ApprovalLevel, r.RequiredLevels, checks,
            schedule.Select(s => new InstallmentDto(s.Id, s.SeqNo, s.ScheduleVersion, s.DueDate, s.Amount, s.RecoveredAmount, s.Status)).ToList());
    }

    public async Task<IReadOnlyList<AdvanceDto>> ListAsync(Guid? employeeId, string? status, string? kind, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var q = Query();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status))
            q = status == "open" ? q.Where(x => x.Status == "paid" || x.Status == "in_repayment") : q.Where(x => x.Status == status);
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(x => x.Kind == kind);
        // Périmètre appliqué AVANT la limite (audit 2026-10 éd. 2) : tronquée d'abord, la liste pouvait
        // cacher une partie de l'équipe d'un Manager.
        if (!scope.All) { var inScope = scope.Ids.ToArray(); q = q.Where(x => inScope.Contains(x.EmployeeId)); }
        var rows = (await q.OrderByDescending(x => x.RequestedAt).Take(500).ToListAsync(ct)).Where(x => scope.Allows(x.EmployeeId)).ToList();
        var result = new List<AdvanceDto>();
        foreach (var r in rows) result.Add(await ToDtoAsync(r, ct));
        return result;
    }

    private async Task<AdvanceRow?> VisibleAsync(Guid advanceId, CancellationToken ct)
    {
        var row = await Query().Where(x => x.Id == advanceId).FirstOrDefaultAsync(ct);
        return row is not null && (await ScopeAsync(ct)).Allows(row.EmployeeId) ? row : null;
    }

    public async Task<Result<AdvanceDto>> GetAsync(Guid advanceId, CancellationToken ct = default)
        => await VisibleAsync(advanceId, ct) is { } row ? Result.Success(await ToDtoAsync(row, ct)) : NotFound();

    // ================================================================== Contrôles de politique (§13.2, §16)

    private sealed record PositionRow(
        string Status, bool ActiveContract, decimal? SalaryAmount, string? PayBasis, decimal? WeeklyHours, string? Currency, string? Frequency,
        int OpenAdvances, decimal AdvanceOutstanding, decimal LoanOutstanding, int OpenLoans);

    private async Task<PositionRow?> PositionAsync(Guid employeeId, Guid? excluding, CancellationToken ct)
        => await db.Database.SqlQuery<PositionRow>($"""
            SELECT e.status AS "Status", (c.id IS NOT NULL) AS "ActiveContract", s.amount AS "SalaryAmount", s.pay_basis AS "PayBasis",
                   c.weekly_hours AS "WeeklyHours", cur.code AS "Currency", c.pay_frequency AS "Frequency",
                   (SELECT count(*)::int FROM nexora.employee_advance a WHERE a.employee_id = e.id AND a.kind <> 'loan'
                      AND a.status IN ('requested','approved','paid','in_repayment') AND a.id IS DISTINCT FROM {excluding}) AS "OpenAdvances",
                   COALESCE((SELECT sum(CASE WHEN a.status IN ('paid','in_repayment') THEN a.balance
                                             ELSE COALESCE(a.amount_approved, a.amount_requested) END)
                             FROM nexora.employee_advance a WHERE a.employee_id = e.id AND a.kind <> 'loan'
                               AND a.status IN ('requested','approved','paid','in_repayment') AND a.id IS DISTINCT FROM {excluding}), 0)
                       AS "AdvanceOutstanding",
                   COALESCE((SELECT sum(CASE WHEN a.status IN ('paid','in_repayment') THEN a.balance
                                             ELSE COALESCE(a.amount_approved, a.amount_requested) END)
                             FROM nexora.employee_advance a WHERE a.employee_id = e.id AND a.kind = 'loan'
                               AND a.status IN ('requested','approved','paid','in_repayment') AND a.id IS DISTINCT FROM {excluding}), 0)
                       AS "LoanOutstanding",
                   (SELECT count(*)::int FROM nexora.employee_advance a WHERE a.employee_id = e.id AND a.kind = 'loan'
                      AND a.status IN ('requested','approved','paid','in_repayment') AND a.id IS DISTINCT FROM {excluding}) AS "OpenLoans"
            FROM nexora.employee e
            LEFT JOIN LATERAL (
                SELECT * FROM nexora.employee_contract x WHERE x.employee_id = e.id AND x.status = 'active'
                  AND x.start_date <= current_date AND (x.end_date IS NULL OR x.end_date >= current_date)
                ORDER BY x.start_date DESC LIMIT 1) c ON true
            LEFT JOIN nexora.currency cur ON cur.id = c.currency_id
            LEFT JOIN LATERAL (
                SELECT h.* FROM nexora.salary_history h WHERE h.contract_id = c.id AND h.effective_date <= current_date
                ORDER BY h.effective_date DESC LIMIT 1) s ON true
            WHERE e.id = {employeeId} AND e.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    private async Task<IReadOnlyList<AdvanceCheck>> ChecksAsync(Guid employeeId, string kind, decimal amount, Guid? excluding, CancellationToken ct)
    {
        var cfg = await config.ReadAsync(ct);
        var p = await PositionAsync(employeeId, excluding, ct);
        if (p is null) return [new AdvanceCheck("employee_active", false, true, "Employé introuvable.")];
        // Salaire ramené au mois pour appliquer les plafonds exprimés en % du salaire mensuel.
        decimal monthly = p.SalaryAmount is { } amt ? p.PayBasis switch
        {
            "daily" => amt * cfg.Calc.DaysPerMonth,
            "hourly" => amt * (p.WeeklyHours ?? cfg.Calc.DefaultWeeklyHours) * 52m / 12m,
            _ => amt,
        } : 0m;
        var position = new EmployeeDebtPosition(Math.Round(monthly, 2), p.OpenAdvances, p.AdvanceOutstanding, p.LoanOutstanding, p.OpenLoans);
        return AdvanceRules.Evaluate(kind, amount, position, cfg.Advances,
            p.ActiveContract && p.Status is "active" or "on_leave", p.SalaryAmount is not null);
    }

    public async Task<Result<IReadOnlyList<AdvanceCheckDto>>> PreviewAsync(Guid employeeId, RequestAdvanceRequest r, CancellationToken ct = default)
    {
        if (await access.CanActForAsync(Permissions.Payroll.AdvanceRequest, employeeId, ct) is { } refused) return refused;
        if (!PayrollRules.AdvanceKinds.Contains(r.Kind)) return Error.Validation("payroll.advance_kind", $"Type « {r.Kind} » inconnu.");
        return Result.Success<IReadOnlyList<AdvanceCheckDto>>((await ChecksAsync(employeeId, r.Kind, r.Amount, null, ct))
            .Select(c => new AdvanceCheckDto(c.Code, c.Passed, c.Blocking, c.Message)).ToList());
    }

    // ================================================================== Demande (§13.1-§13.2)

    private sealed record PolicyRow(short RequiredLevels);

    public async Task<Result<AdvanceDto>> RequestAsync(Guid employeeId, RequestAdvanceRequest r, CancellationToken ct = default)
    {
        if (await access.CanActForAsync(Permissions.Payroll.AdvanceRequest, employeeId, ct) is { } refused) return refused;
        if (!PayrollRules.AdvanceKinds.Contains(r.Kind)) return Error.Validation("payroll.advance_kind", $"Type « {r.Kind} » inconnu.");
        var amount = PayrollRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("payroll.advance_amount", "Le montant demandé doit être positif.");
        // Acompte : paiement anticipé d'une rémunération déjà due, récupéré en une fois sur la paie suivante (§17).
        var method = r.Kind == "acompte" ? "next_payroll" : r.RecoveryMethod ?? (r.Kind == "loan" ? "installments" : "next_payroll");
        if (!PayrollRules.RecoveryMethods.Contains(method)) return Error.Validation("payroll.recovery_method", $"Mode de récupération « {method} » inconnu.");
        var installments = method == "installments" ? r.Installments ?? 1 : 1;
        if (installments is < 1 or > 120) return Error.Validation("payroll.installments", "Nombre d'échéances compris entre 1 et 120.");
        if (method == "percent_of_net" && r.RecoveryPercent is not (> 0m and <= 100m))
            return Error.Validation("payroll.recovery_percent", "Pourcentage du net compris entre 0 et 100.");
        if (r.Kind == "loan" && string.IsNullOrWhiteSpace(r.Reason))
            return Error.Validation("payroll.advance_reason", "Le motif d'un prêt est obligatoire.");

        var checks = await ChecksAsync(employeeId, r.Kind, amount, null, ct);
        var failed = checks.Where(c => c.Blocking && !c.Passed).ToList();
        if (failed.Count > 0)
            return Error.Validation("payroll.advance_policy", string.Join(" ", failed.Select(c => c.Message)));
        var position = (await PositionAsync(employeeId, null, ct))!;
        var currency = position.Currency ?? await db.Database.SqlQuery<string>($"""
            SELECT cur.code AS "Value" FROM nexora.company c JOIN nexora.currency cur ON cur.id = c.default_currency_id WHERE c.id = {Company}
            """).FirstAsync(ct);

        var requestType = r.Kind == "loan" ? "employee_loan" : "salary_advance";
        var policy = await db.Database.SqlQuery<PolicyRow>($"""
            SELECT required_levels AS "RequiredLevels" FROM nexora.approval_policy
            WHERE company_id = {Company} AND request_type = {requestType} AND status = 'active' AND min_amount <= {amount}
            ORDER BY min_amount DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        var levels = Math.Max((short)1, policy?.RequiredLevels ?? (short)1);
        var today = await PayrollRules.TodayAsync(db, ct);
        var id = Guid.NewGuid();
        var approvalId = Guid.NewGuid();
        string number = "";
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                // Contrôles de politique rejoués sous verrou du dossier (audit 2026-10 éd. 2) : deux demandes simultanées
                // passaient chacune le plafond du nombre d'avances en cours.
                await db.Database.ExecuteSqlAsync($"SELECT 1 FROM nexora.employee WHERE id = {employeeId} AND company_id = {Company} FOR UPDATE", token);
                var locked = (await ChecksAsync(employeeId, r.Kind, amount, null, token)).Where(c => c.Blocking && !c.Passed).ToList();
                if (locked.Count > 0)
                    throw new PayrollRefusal(Error.Validation("payroll.advance_policy", string.Join(" ", locked.Select(c => c.Message))));
                var prefix = r.Kind switch { "loan" => "PRT-", "acompte" => "ACP-", _ => "AVS-" };
                number = await PayrollRules.NextNumberAsync(db, Company, "employee_" + r.Kind, prefix, today.Year, token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.employee_advance
                        (id, company_id, advance_number, employee_id, kind, currency_id, request_date, amount_requested, reason, conditions,
                         recovery_method, installments_count, recovery_percent, first_recovery_date, status, approval_request_id, requested_by)
                    VALUES ({id}, {Company}, {number}, {employeeId}, {r.Kind}, (SELECT id FROM nexora.currency WHERE code = {currency}), {today},
                            {amount}, {r.Reason?.Trim()}, {r.Conditions?.Trim()}, {method}, {installments},
                            {(method == "percent_of_net" ? r.RecoveryPercent : null)}, {r.FirstRecoveryDate}, 'requested', {approvalId}, {UserId})
                    """, token);
                // Workflow d'approbation configuré (Phase 11) : niveaux lus dans approval_policy, au moins un approbateur.
                var context = JsonSerializer.Serialize(new { advanceId = id, advanceNumber = number, kind = r.Kind, employeeId });
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.approval_request
                        (id, company_id, request_type, reference_type, reference_id, requested_by, amount, currency_id, status, current_level,
                         required_levels, context)
                    VALUES ({approvalId}, {Company}, {requestType}, 'employee_advance', {id}, {UserId}, {amount},
                            (SELECT id FROM nexora.currency WHERE code = {currency}), 'pending', 1, {levels}, {context}::jsonb)
                    """, token);
                await EventAsync(id, "requested", amount, null, number, r.Reason?.Trim(), token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("payroll.advance_invalid", ex.Message);
        }

        var dto = (await GetAsync(id, ct)).Value!;
        await notifier.ToPermissionAsync(Permissions.Payroll.AdvanceApprove, $"{KindLabel(r.Kind)} à valider",
            $"{dto.EmployeeName} — {number} : {amount:N2} {currency}{(r.Reason is { Length: > 0 } reason ? " (" + reason.Trim() + ")" : "")}.",
            "/hr?tab=approvals", "employee_advance", id, $"advance:{id}:requested", ct);
        logger.LogInformation("Demande {Number} de {Amount} {Currency} créée", number, amount, currency);
        return Result.Success(dto);
    }

    // Horodatage réel (clock_timestamp) : plusieurs étapes d'une même transaction gardent leur ordre dans l'historique.
    private Task EventAsync(Guid advanceId, string type, decimal? amount, decimal? balanceAfter, string? reference, string? note, CancellationToken ct)
        => db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.employee_advance_event (company_id, advance_id, event_type, amount, balance_after, reference, note, actor_id, occurred_at)
            VALUES ({Company}, {advanceId}, {type}, {amount}, {balanceAfter}, {reference}, {note}, {tenant.UserId}, clock_timestamp())
            """, ct);

    // ================================================================== Décision (§13.3-§13.4)

    public async Task<Result<AdvanceDto>> ApproveAsync(Guid advanceId, ApproveAdvanceRequest r, CancellationToken ct = default)
    {
        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        if (await access.CanDecideAsync(Permissions.Payroll.AdvanceApprove, row.EmployeeId, row.RequestedBy, ct) is { } refused) return refused;
        if (row.Status != "requested") return Error.Conflict("payroll.advance_state", "Cette demande a déjà été traitée.");
        var approved = PayrollRules.Money(r.AmountApproved ?? row.AmountApproved ?? row.AmountRequested);
        if (approved <= 0m || approved > row.AmountRequested)
            return Error.Validation("payroll.advance_amount", "Le montant approuvé doit être positif et ne pas dépasser le montant demandé.");
        var method = row.Kind == "acompte" ? "next_payroll" : r.RecoveryMethod ?? row.RecoveryMethod;
        if (!PayrollRules.RecoveryMethods.Contains(method)) return Error.Validation("payroll.recovery_method", $"Mode « {method} » inconnu.");
        var installments = method == "installments" ? r.Installments ?? row.InstallmentsCount : 1;
        if (installments is < 1 or > 120) return Error.Validation("payroll.installments", "Nombre d'échéances compris entre 1 et 120.");
        var percent = method == "percent_of_net" ? r.RecoveryPercent ?? row.RecoveryPercent : null;
        if (method == "percent_of_net" && percent is not (> 0m and <= 100m))
            return Error.Validation("payroll.recovery_percent", "Pourcentage du net compris entre 0 et 100.");
        var failed = (await ChecksAsync(row.EmployeeId, row.Kind, approved, row.Id, ct)).Where(c => c.Blocking && !c.Passed).ToList();
        if (failed.Count > 0) return Error.Validation("payroll.advance_policy", string.Join(" ", failed.Select(c => c.Message)));
        if (row.ApprovalRequestId is not { } approvalId) return Error.Conflict("payroll.advance_state", "Demande sans circuit d'approbation.");

        var final = false;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                // Le moteur d'approbation applique demandeur ≠ approbateur et deux approbateurs distincts par niveau ;
                // sa décision et la mise à jour de l'avance forment une seule transaction.
                var decided = await approvals.DecideAsync(approvalId, new ApprovalDecisionRequest(true, r.Note?.Trim()), token);
                if (!decided.IsSuccess) throw new PayrollRefusal(decided.Error!);
                final = decided.Value!.Status == "approved";
                Guid? approver = final ? UserId : null;
                DateTimeOffset? approvedAt = final ? clock.UtcNow : null;
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.employee_advance
                       SET amount_approved = {approved}, recovery_method = {method}, installments_count = {installments},
                           recovery_percent = {percent}, first_recovery_date = COALESCE({r.FirstRecoveryDate}, first_recovery_date),
                           decision_note = COALESCE({r.Note?.Trim()}, decision_note),
                           status = {(final ? "approved" : "requested")},
                           approved_by = {approver}, approved_at = {approvedAt}
                     WHERE id = {advanceId} AND company_id = {Company} AND status = 'requested'
                    """, token);
                await EventAsync(advanceId, final ? "approved" : "approval_step", approved, null, row.AdvanceNumber,
                    final ? r.Note?.Trim() : $"Niveau {decided.Value.CurrentLevel - 1}/{decided.Value.RequiredLevels} approuvé. {r.Note}".Trim(), token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("payroll.advance_invalid", ex.Message);
        }
        if (final)
        {
            await notifier.ToUsersAsync([row.RequestedBy, await notifier.EmployeeUserAsync(row.EmployeeId, ct)],
                $"{KindLabel(row.Kind)} approuvé(e)", $"{row.AdvanceNumber} : {approved:N2} {row.Currency} approuvé(s).",
                "/me/hr?tab=advances", "employee_advance", advanceId, $"advance:{advanceId}:approved", ct);
            await notifier.ToPermissionAsync(Permissions.Payroll.AdvancePay, $"{KindLabel(row.Kind)} à verser",
                $"{row.EmployeeName} — {row.AdvanceNumber} : {approved:N2} {row.Currency}.",
                "/hr?tab=advances", "employee_advance", advanceId, $"advance:{advanceId}:to_pay", ct);
        }
        return await GetAsync(advanceId, ct);
    }

    public async Task<Result<AdvanceDto>> RejectAsync(Guid advanceId, DecisionRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("payroll.decision_note", "Indiquez le motif du refus.");
        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        if (await access.CanDecideAsync(Permissions.Payroll.AdvanceApprove, row.EmployeeId, row.RequestedBy, ct) is { } refused) return refused;
        if (row.Status != "requested") return Error.Conflict("payroll.advance_state", "Cette demande a déjà été traitée.");
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                if (row.ApprovalRequestId is { } approvalId)
                {
                    var decided = await approvals.DecideAsync(approvalId, new ApprovalDecisionRequest(false, r.Note.Trim()), token);
                    if (!decided.IsSuccess) throw new PayrollRefusal(decided.Error!);
                }
                await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee_advance SET status = 'rejected', decision_note = {r.Note.Trim()}
                WHERE id = {advanceId} AND company_id = {Company} AND status = 'requested'
                """, token);
                await EventAsync(advanceId, "rejected", null, null, row.AdvanceNumber, r.Note.Trim(), token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        await notifier.ToUsersAsync([row.RequestedBy, await notifier.EmployeeUserAsync(row.EmployeeId, ct)],
            $"{KindLabel(row.Kind)} refusé(e)", $"{row.AdvanceNumber} : {r.Note.Trim()}",
            "/me/hr?tab=advances", "employee_advance", advanceId, $"advance:{advanceId}:rejected", ct);
        return await GetAsync(advanceId, ct);
    }

    public async Task<Result<AdvanceDto>> CancelAsync(Guid advanceId, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("payroll.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        var own = await access.OwnEmployeeIdAsync(ct) == row.EmployeeId;
        if (row.RequestedBy != UserId && !own && !await access.HasAnyAsync([Permissions.Payroll.AdvanceApprove, Permissions.Payroll.AdvanceRequest], ct))
            return Error.Forbidden("payroll.forbidden", "Seuls le demandeur, l'employé ou un gestionnaire des avances peuvent annuler cette demande.");
        if (row.Status is not ("requested" or "approved"))
            return Error.Conflict("payroll.advance_state", "Une avance versée ne s'annule pas : elle se rembourse (§15).");
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee_advance SET status = 'cancelled', cancelled_by = {UserId}, cancelled_at = now(), cancel_reason = {r.Reason.Trim()}
                WHERE id = {advanceId} AND company_id = {Company} AND status IN ('requested','approved')
                """, token);
            if (row.ApprovalRequestId is { } approvalId)
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.approval_request SET status = 'cancelled', completed_at = now(), updated_at = now()
                    WHERE id = {approvalId} AND status = 'pending'
                    """, token);
            await EventAsync(advanceId, "cancelled", null, null, row.AdvanceNumber, r.Reason.Trim(), token);
            return 0;
        }, ct);
        return await GetAsync(advanceId, ct);
    }

    // ================================================================== Versement (§13.5-§13.7)

    private sealed record AccountRow(Guid Id, string Status, string Currency);

    public async Task<Result<AdvanceDto>> PayAsync(Guid advanceId, PayAdvanceRequest r, CancellationToken ct = default)
    {
        if (r.OperationUuid == Guid.Empty) return Error.Validation("payroll.operation_uuid", "Identifiant d'opération obligatoire (anti double versement).");
        var replay = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.employee_advance WHERE payment_operation_uuid = {r.OperationUuid} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (replay is { } done) return await GetAsync(done, ct);

        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        if (row.Status != "approved")
            return Error.Conflict("payroll.advance_state", row.Status is "paid" or "in_repayment" or "settled"
                ? "Cette avance a déjà été versée." : "L'avance doit d'abord être approuvée.");
        if (!PayrollRules.PaymentMethods.Contains(r.PaymentMethod)) return Error.Validation("payroll.payment_method", $"Moyen « {r.PaymentMethod} » inconnu.");
        var account = await db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "Id", fa.status AS "Status", cur.code AS "Currency"
            FROM nexora.financial_account fa JOIN nexora.currency cur ON cur.id = fa.currency_id
            WHERE fa.id = {r.FinancialAccountId} AND fa.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (account is null || account.Status != "active") return Error.Validation("payroll.financial_account", "Compte de versement introuvable ou inactif.");
        if (account.Currency != row.Currency)
            return Error.Validation("payroll.financial_account", $"Le compte est en {account.Currency}, l'avance en {row.Currency}.");
        // Date de versement : jamais dans le futur (audit 2026-10 éd. 2 : elle était libre).
        if (r.PaidOn is { } paidOn && paidOn > await PayrollRules.TodayAsync(db, ct))
            return Error.Validation("payroll.paid_on", "La date de versement ne peut pas être dans le futur.");
        // Versement en espèces depuis une caisse dont une session est ouverte : rattaché à cette session, sinon la sortie
        // n'apparaissait pas dans l'attendu de la clôture (écart de caisse inexpliqué).
        var cashSessionId = r.CashSessionId ?? await db.Database.SqlQuery<Guid?>($"""
            SELECT cs.id AS "Value" FROM nexora.cash_session cs JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
            WHERE cr.financial_account_id = {account.Id} AND cs.company_id = {Company} AND cs.status = 'open'
            ORDER BY cs.opened_at DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        Guid? sessionAccount = null;
        if (cashSessionId is { } sid)
        {
            sessionAccount = await db.Database.SqlQuery<Guid?>($"""
                SELECT cr.financial_account_id AS "Value" FROM nexora.cash_session cs JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
                WHERE cs.id = {sid} AND cs.company_id = {Company} AND cs.status = 'open'
                """).FirstOrDefaultAsync(ct);
            if (sessionAccount is null) return Error.Conflict("payroll.cash_session_closed", "La session de caisse n'est pas ouverte.");
        }

        var paidAt = r.PaidOn is { } day ? new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero) : clock.UtcNow;
        var amount = row.AmountApproved!.Value;
        var frequency = (await PositionAsync(row.EmployeeId, row.Id, ct))?.Frequency ?? "monthly";
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                if (await PayrollRules.CashShortfallAsync(db, Company, account.Id, amount, token) is { } shortfall)
                    throw new PayrollRefusal(shortfall);
                // Créance sur l'employé, jamais une charge salariale (§14, §39) : Dr avances/prêts au personnel / Cr trésorerie.
                var posted = await engine.HandleAsync(new EmployeeAdvancePaid(Company, r.OperationUuid, paidAt, row.Id, row.EmployeeId,
                    new Money(amount, row.Currency), row.Kind == "loan", account.Id, row.AdvanceNumber) { UserId = tenant.UserId }, token);
                if (!posted.IsSuccess) throw new PayrollRefusal(posted.Error!);
                var n = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.employee_advance
                       SET status = 'paid', amount_paid = {amount}, paid_by = {UserId}, paid_at = {paidAt}, financial_account_id = {account.Id},
                           payment_method = {r.PaymentMethod}, payment_reference = {r.Reference}, cash_session_id = {cashSessionId},
                           journal_entry_id = {posted.Value!.JournalEntryId}, payment_operation_uuid = {r.OperationUuid}
                     WHERE id = {row.Id} AND company_id = {Company} AND status = 'approved'
                    """, token);
                if (n != 1) throw new PayrollRefusal(Error.Conflict("payroll.advance_state", "Avance versée entre-temps."));

                var first = row.FirstRecoveryDate ?? calendar.DateOf(paidAt);
                var schedule = AdvanceRules.BuildSchedule(amount, row.RecoveryMethod, row.InstallmentsCount, first, frequency);
                foreach (var (seq, due, value) in schedule)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.advance_installment (company_id, advance_id, seq_no, schedule_version, due_date, amount, created_by)
                        VALUES ({Company}, {row.Id}, {seq}, 1, {due}, {value}, {UserId})
                        """, token);
                if (cashSessionId is { } session && sessionAccount == account.Id)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.cash_movement
                            (company_id, cash_session_id, movement_type, amount, currency_id, reference_type, reference_id, note, occurred_at, user_id)
                        VALUES ({Company}, {session}, 'payout', {-amount}, (SELECT currency_id FROM nexora.employee_advance WHERE id = {row.Id}),
                                'employee_advance', {row.Id}, {KindLabel(row.Kind) + " " + row.AdvanceNumber + " — " + row.EmployeeName}, {paidAt}, {UserId})
                        """, token);
                await EventAsync(row.Id, "paid", amount, amount, r.Reference ?? row.AdvanceNumber,
                    $"{PayslipDocuments.MethodLabel(r.PaymentMethod)} — écriture {posted.Value.EntryNumber}", token);
                await EventAsync(row.Id, "schedule_set", amount, amount, row.AdvanceNumber, schedule.Count == 0
                    ? (row.RecoveryMethod == "manual" ? "Récupération manuelle (remboursements directs)" : $"{row.RecoveryPercent:0.##} % du net à chaque paie")
                    : string.Join(" ; ", schedule.Select(s => $"{s.DueDate:dd/MM/yyyy} : {s.Amount:N2}")), token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Versement refusé pour {Advance}", row.AdvanceNumber);
            return Error.Conflict("payroll.advance_payment_failed", ex.Message);
        }
        await notifier.ToUsersAsync([await notifier.EmployeeUserAsync(row.EmployeeId, ct)], $"{KindLabel(row.Kind)} versé(e)",
            $"{row.AdvanceNumber} : {amount:N2} {row.Currency} versé(s). Récupération : {RecoveryLabel(row.RecoveryMethod, row.InstallmentsCount, row.RecoveryPercent)}.",
            "/me/hr?tab=advances", "employee_advance", row.Id, $"advance:{row.Id}:paid", ct);
        return await GetAsync(row.Id, ct);
    }

    private static string RecoveryLabel(string method, int installments, decimal? percent) => method switch
    {
        "installments" => $"{installments} échéance(s) sur les paies",
        "percent_of_net" => $"{percent:0.##} % du net à chaque paie",
        "manual" => "remboursements directs",
        _ => "retenue sur la prochaine paie",
    };

    // ================================================================== Remboursement direct (§15, §29 « repay »)

    public async Task<Result<AdvanceDto>> RepayAsync(Guid advanceId, RepayAdvanceRequest r, CancellationToken ct = default)
    {
        if (r.OperationUuid == Guid.Empty) return Error.Validation("payroll.operation_uuid", "Identifiant d'opération obligatoire.");
        var replay = await db.Database.SqlQuery<Guid?>($"""
            SELECT advance_id AS "Value" FROM nexora.advance_repayment WHERE operation_uuid = {r.OperationUuid} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (replay is { } done) return await GetAsync(done, ct);
        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        if (row.Status is not ("paid" or "in_repayment"))
            return Error.Conflict("payroll.advance_state", row.Status == "settled" ? "Cette avance est déjà soldée." : "L'avance n'a pas été versée.");
        var amount = PayrollRules.Money(r.Amount);
        if (amount <= 0m) return Error.Validation("payroll.repayment_amount", "Le montant remboursé doit être positif.");
        // §15, §37 test 22 : jamais de récupération supérieure au solde (également garanti par la base).
        if (amount > row.Balance)
            return Error.Validation("payroll.repayment_exceeds_balance",
                $"Récupération de {amount:N2} supérieure au solde restant de {row.Balance:N2} {row.Currency}.");
        if (!PayrollRules.PaymentMethods.Contains(r.PaymentMethod)) return Error.Validation("payroll.payment_method", $"Moyen « {r.PaymentMethod} » inconnu.");
        var account = await db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "Id", fa.status AS "Status", cur.code AS "Currency"
            FROM nexora.financial_account fa JOIN nexora.currency cur ON cur.id = fa.currency_id
            WHERE fa.id = {r.FinancialAccountId} AND fa.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (account is null || account.Status != "active") return Error.Validation("payroll.financial_account", "Compte d'encaissement introuvable ou inactif.");
        if (account.Currency != row.Currency) return Error.Validation("payroll.financial_account", $"Le compte est en {account.Currency}, l'avance en {row.Currency}.");

        var now = clock.UtcNow;
        var repaymentId = Guid.NewGuid();
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var posted = await engine.HandleAsync(new EmployeeAdvanceRepaid(Company, r.OperationUuid, now, repaymentId, row.Id, row.EmployeeId,
                    new Money(amount, row.Currency), row.Kind == "loan", account.Id, row.AdvanceNumber) { UserId = tenant.UserId }, token);
                if (!posted.IsSuccess) throw new PayrollRefusal(posted.Error!);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.advance_repayment
                        (id, company_id, advance_id, source, amount, currency_id, repaid_at, financial_account_id, payment_method, reference,
                         journal_entry_id, operation_uuid, note, created_by)
                    VALUES ({repaymentId}, {Company}, {row.Id}, 'manual', {amount}, (SELECT currency_id FROM nexora.employee_advance WHERE id = {row.Id}),
                            {now}, {account.Id}, {r.PaymentMethod}, {r.Reference}, {posted.Value!.JournalEntryId}, {r.OperationUuid}, {r.Note}, {UserId})
                    """, token);
                var balance = await db.Database.SqlQuery<decimal>($"""
                    SELECT balance AS "Value" FROM nexora.employee_advance WHERE id = {row.Id}
                    """).FirstAsync(token);
                await EventAsync(row.Id, "recovered_manual", amount, balance, r.Reference ?? row.AdvanceNumber, r.Note, token);
                if (balance == 0m) await EventAsync(row.Id, "settled", null, 0m, row.AdvanceNumber, "Avance soldée", token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("payroll.repayment_failed", ex.Message);
        }
        return await GetAsync(row.Id, ct);
    }

    // ================================================================== Échéancier (§15 « modification soumise à permission »)

    public async Task<Result<AdvanceDto>> RescheduleAsync(Guid advanceId, RescheduleAdvanceRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            return Error.Validation("payroll.schedule_reason", "Le motif de modification de l'échéancier est obligatoire.");
        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        if (row.Status is not ("paid" or "in_repayment")) return Error.Conflict("payroll.advance_state", "Seule une avance en cours de récupération se rééchelonne.");
        var installments = (r.Installments ?? []).Select(i => (DueDate: i.DueDate, Amount: PayrollRules.Money(i.Amount)))
            .OrderBy(i => i.DueDate).ToList();
        if (AdvanceRules.ValidateReschedule(row.Balance, installments) is { } invalid) return Error.Validation("payroll.schedule_invalid", invalid);
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var version = await db.Database.SqlQuery<int>($"""
                    SELECT COALESCE(max(schedule_version), 0) + 1 AS "Value" FROM nexora.advance_installment WHERE advance_id = {row.Id}
                    """).FirstAsync(token);
                // Une échéance partiellement récupérée est close à hauteur de ce qui a été retenu ; les autres sont remplacées.
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.advance_installment SET amount = recovered_amount, status = 'recovered'
                    WHERE advance_id = {row.Id} AND status = 'partial'
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.advance_installment SET status = 'replaced' WHERE advance_id = {row.Id} AND status = 'pending'
                    """, token);
                var seq = 1;
                foreach (var (due, amount) in installments)
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.advance_installment (company_id, advance_id, seq_no, schedule_version, due_date, amount, created_by)
                        VALUES ({Company}, {row.Id}, {seq++}, {version}, {due}, {amount}, {UserId})
                        """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.employee_advance SET recovery_method = 'installments', installments_count = {installments.Count}, recovery_percent = NULL
                    WHERE id = {row.Id}
                    """, token);
                await EventAsync(row.Id, "schedule_changed", row.Balance, row.Balance, $"v{version}",
                    $"{r.Reason.Trim()} — " + string.Join(" ; ", installments.Select(i => $"{i.DueDate:dd/MM/yyyy} : {i.Amount:N2}")), token);
                return 0;
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("payroll.schedule_invalid", ex.Message);
        }
        return await GetAsync(row.Id, ct);
    }

    // ================================================================== Historique (§13.10, §29)

    private sealed record EventRow(Guid Id, string EventType, decimal? Amount, decimal? BalanceAfter, string? Reference, string? Note,
        string? ActorName, DateTimeOffset OccurredAt);
    private sealed record RepaymentRow(Guid Id, string Source, decimal Amount, string Currency, DateTimeOffset RepaidAt, string? RunNumber,
        string? PeriodLabel, string? Reference, Guid? JournalEntryId, string? CreatedByName);

    public async Task<Result<AdvanceHistoryDto>> HistoryAsync(Guid advanceId, CancellationToken ct = default)
    {
        var row = await VisibleAsync(advanceId, ct);
        if (row is null) return NotFound();
        var events = await db.Database.SqlQuery<EventRow>($"""
            SELECT ev.id AS "Id", ev.event_type AS "EventType", ev.amount AS "Amount", ev.balance_after AS "BalanceAfter",
                   ev.reference AS "Reference", ev.note AS "Note", u.full_name AS "ActorName", ev.occurred_at AS "OccurredAt"
            FROM nexora.employee_advance_event ev LEFT JOIN nexora.app_user u ON u.id = ev.actor_id
            WHERE ev.advance_id = {advanceId} ORDER BY ev.occurred_at, ev.id
            """).ToListAsync(ct);
        var repayments = await db.Database.SqlQuery<RepaymentRow>($"""
            SELECT rp.id AS "Id", rp.source AS "Source", rp.amount AS "Amount", cur.code AS "Currency", rp.repaid_at AS "RepaidAt",
                   r.run_number AS "RunNumber", p.label AS "PeriodLabel", rp.reference AS "Reference", rp.journal_entry_id AS "JournalEntryId",
                   u.full_name AS "CreatedByName"
            FROM nexora.advance_repayment rp
            JOIN nexora.currency cur ON cur.id = rp.currency_id
            LEFT JOIN nexora.payroll_item i ON i.id = rp.payroll_item_id
            LEFT JOIN nexora.payroll_run r ON r.id = i.run_id
            LEFT JOIN nexora.payroll_period p ON p.id = i.period_id
            LEFT JOIN nexora.app_user u ON u.id = rp.created_by
            WHERE rp.advance_id = {advanceId} ORDER BY rp.repaid_at
            """).ToListAsync(ct);
        return Result.Success(new AdvanceHistoryDto(await ToDtoAsync(row, ct),
            events.Select(e => new AdvanceEventDto(e.Id, e.EventType, e.Amount, e.BalanceAfter, e.Reference, e.Note, e.ActorName, e.OccurredAt)).ToList(),
            repayments.Select(x => new AdvanceRepaymentDto(x.Id, x.Source, x.Amount, x.Currency, x.RepaidAt, x.RunNumber, x.PeriodLabel,
                x.Reference, x.JournalEntryId, x.CreatedByName)).ToList()));
    }
}
