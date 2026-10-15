using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Présences, congés et absences, heures supplémentaires, éléments variables (§18-§20). Chaque demande est
/// validée par une autre personne (§39), la preuve de l'approbation est conservée (§19) ; une donnée tombant
/// dans une paie validée est verrouillée par la base (§18 « verrouiller après clôture »).
/// </summary>
public sealed class SqlHrTimeService(
    NexoraDbContext db,
    ITenantContext tenant,
    PayrollAccess access,
    PayrollConfigurationReader config,
    PayrollNotifier notifier) : IHrTimeService
{
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    private static readonly string[] ReadWide = [Permissions.Hr.ManageTime, Permissions.Payroll.ViewSalaries, Permissions.Payroll.Calculate];
    private static readonly string[] ReadTeam = [Permissions.Hr.ApproveRequests];

    private Task<EmployeeScope> ScopeAsync(CancellationToken ct) => access.ScopeAsync(ReadWide, ReadTeam, allowSelf: true, ct);

    private static Error Locked(string message) => Error.Conflict("payroll.period_locked", message);

    /// <summary>Erreur de la base (verrou de période, 4-yeux, unicité) traduite en refus métier.</summary>
    private static Error? FromDatabase(Exception ex)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException;
        if (pg is null) return null;
        return pg.SqlState switch
        {
            "23505" => Error.Conflict("hr.duplicate", "Une saisie existe déjà pour cet employé à cette date."),
            "23514" when pg.MessageText.Contains("verrouill", StringComparison.OrdinalIgnoreCase) => Locked(pg.MessageText),
            "23514" => Error.Validation("hr.invalid", Persistence.DatabaseErrors.ClientMessage(pg)),
            "23503" => Error.Validation("hr.invalid", "Employé ou période introuvable dans l'entreprise."),
            _ => null,
        };
    }

    private sealed record EmployeeRef(Guid Id, string Name, DateOnly HireDate, DateOnly? TerminationDate, string Status);

    private async Task<EmployeeRef?> EmployeeAsync(Guid employeeId, CancellationToken ct)
        => await db.Database.SqlQuery<EmployeeRef>($"""
            SELECT id AS "Id", first_name || ' ' || last_name AS "Name", hire_date AS "HireDate", termination_date AS "TerminationDate",
                   status AS "Status"
            FROM nexora.employee WHERE id = {employeeId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    // ================================================================== Présences (§18)

    private sealed record AttendanceRow(
        Guid Id, Guid EmployeeId, string EmployeeName, DateOnly WorkDate, string Status, decimal? HoursWorked, bool Justified,
        string Source, string? Note, string ValidationStatus, string? ValidatedByName, DateTimeOffset? ValidatedAt, Guid? CreatedBy);

    private IQueryable<AttendanceRow> AttendanceQuery() => db.Database.SqlQuery<AttendanceRow>($"""
        SELECT a.id AS "Id", a.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", a.work_date AS "WorkDate",
               a.status AS "Status", a.hours_worked AS "HoursWorked", a.justified AS "Justified", a.source AS "Source", a.note AS "Note",
               a.validation_status AS "ValidationStatus", v.full_name AS "ValidatedByName", a.validated_at AS "ValidatedAt",
               a.created_by AS "CreatedBy"
        FROM nexora.attendance_record a
        JOIN nexora.employee e ON e.id = a.employee_id
        LEFT JOIN nexora.app_user v ON v.id = a.validated_by
        WHERE a.company_id = {Company}
        """);

    private static AttendanceDto ToDto(AttendanceRow r) => new(r.Id, r.EmployeeId, r.EmployeeName, r.WorkDate, r.Status, r.HoursWorked,
        r.Justified, r.Source, r.Note, r.ValidationStatus, r.ValidatedByName, r.ValidatedAt);

    public async Task<IReadOnlyList<AttendanceDto>> AttendanceAsync(Guid? employeeId, DateOnly? from, DateOnly? to, string? validationStatus,
        CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var q = AttendanceQuery();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (from is { } f) q = q.Where(x => x.WorkDate >= f);
        if (to is { } t) q = q.Where(x => x.WorkDate <= t);
        if (!string.IsNullOrWhiteSpace(validationStatus)) q = q.Where(x => x.ValidationStatus == validationStatus);
        return (await q.OrderByDescending(x => x.WorkDate).ThenBy(x => x.EmployeeName).Take(2000).ToListAsync(ct))
            .Where(x => scope.Allows(x.EmployeeId)).Select(ToDto).ToList();
    }

    /// <summary>Saisie ou import par lot (§18 « importer ou saisir ») : une ligne par employé et par jour, mise à jour
    /// tant qu'elle n'est pas validée ; chaque ligne en erreur est signalée sans bloquer les autres.</summary>
    public async Task<Result<AttendanceImportResultDto>> RecordAttendanceAsync(RecordAttendanceRequest r, CancellationToken ct = default)
    {
        if (r.Records is not { Count: > 0 }) return Error.Validation("hr.attendance_empty", "Aucune présence à enregistrer.");
        if (r.Records.Count > 5000) return Error.Validation("hr.attendance_too_many", "5 000 lignes au plus par import.");
        var source = r.Source is "import" or "device" ? r.Source : "manual";
        int created = 0, updated = 0;
        var errors = new List<string>();
        var employees = new Dictionary<Guid, EmployeeRef?>();
        foreach (var (line, i) in r.Records.Select((x, i) => (x, i + 1)))
        {
            if (!employees.TryGetValue(line.EmployeeId, out var emp))
                employees[line.EmployeeId] = emp = await EmployeeAsync(line.EmployeeId, ct);
            if (emp is null) { errors.Add($"Ligne {i} : employé introuvable."); continue; }
            if (!PayrollRules.AttendanceStatuses.Contains(line.Status)) { errors.Add($"Ligne {i} : statut « {line.Status} » inconnu."); continue; }
            if (line.WorkDate < emp.HireDate || (emp.TerminationDate is { } td && line.WorkDate > td))
            { errors.Add($"Ligne {i} ({emp.Name}) : date hors de la période d'emploi."); continue; }
            if (line.HoursWorked is < 0 or > 24) { errors.Add($"Ligne {i} ({emp.Name}) : nombre d'heures invalide."); continue; }
            try
            {
                var existing = await db.Database.SqlQuery<string?>($"""
                    SELECT validation_status AS "Value" FROM nexora.attendance_record
                    WHERE employee_id = {line.EmployeeId} AND work_date = {line.WorkDate}
                    """).FirstOrDefaultAsync(ct);
                if (existing == "validated") { errors.Add($"Ligne {i} ({emp.Name}, {line.WorkDate:dd/MM/yyyy}) : présence déjà validée."); continue; }
                if (existing is null)
                {
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.attendance_record
                            (company_id, employee_id, work_date, status, hours_worked, justified, source, note, created_by)
                        VALUES ({Company}, {line.EmployeeId}, {line.WorkDate}, {line.Status}, {line.HoursWorked}, {line.Justified},
                                {source}, {line.Note}, {UserId})
                        """, ct);
                    created++;
                }
                else
                {
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.attendance_record
                           SET status = {line.Status}, hours_worked = {line.HoursWorked}, justified = {line.Justified}, note = {line.Note},
                               source = {source}, validation_status = 'pending', validated_by = NULL, validated_at = NULL, created_by = {UserId}
                         WHERE employee_id = {line.EmployeeId} AND work_date = {line.WorkDate}
                        """, ct);
                    updated++;
                }
            }
            catch (Exception ex) when (FromDatabase(ex) is { } error)
            {
                errors.Add($"Ligne {i} ({emp.Name}, {line.WorkDate:dd/MM/yyyy}) : {error.Message}");
            }
        }
        return Result.Success(new AttendanceImportResultDto(created, updated, errors));
    }

    public async Task<Result<AttendanceDto>> DecideAttendanceAsync(Guid id, bool validate, DecisionRequest r, CancellationToken ct = default)
    {
        var row = await AttendanceQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("hr.attendance_not_found", "Présence introuvable.");
        if (await access.CanDecideAsync(Permissions.Hr.ApproveRequests, row.EmployeeId, row.CreatedBy, ct) is { } refused) return refused;
        if (row.ValidationStatus != "pending") return Error.Conflict("hr.already_decided", "Cette présence a déjà été traitée.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.attendance_record
                   SET validation_status = {(validate ? "validated" : "rejected")}, validated_by = {UserId}, validated_at = now(),
                       note = COALESCE(NULLIF({r.Note ?? ""}, ''), note)
                 WHERE id = {id} AND company_id = {Company} AND validation_status = 'pending'
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        return Result.Success(ToDto((await AttendanceQuery().Where(x => x.Id == id).FirstAsync(ct))));
    }

    // ================================================================== Congés et absences (§18)

    private sealed record LeaveRow(
        Guid Id, Guid EmployeeId, string EmployeeName, string LeaveType, DateOnly StartDate, DateOnly EndDate, decimal Days, bool Paid,
        string? Reason, string Status, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName,
        DateTimeOffset? DecidedAt, string? DecisionNote);

    private IQueryable<LeaveRow> LeaveQuery() => db.Database.SqlQuery<LeaveRow>($"""
        SELECT l.id AS "Id", l.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", l.leave_type AS "LeaveType",
               l.start_date AS "StartDate", l.end_date AS "EndDate", l.days AS "Days", l.paid AS "Paid", l.reason AS "Reason",
               l.status AS "Status", l.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName", l.requested_at AS "RequestedAt",
               dc.full_name AS "DecidedByName", l.decided_at AS "DecidedAt", l.decision_note AS "DecisionNote"
        FROM nexora.leave_record l
        JOIN nexora.employee e ON e.id = l.employee_id
        LEFT JOIN nexora.app_user rq ON rq.id = l.requested_by
        LEFT JOIN nexora.app_user dc ON dc.id = l.decided_by
        WHERE l.company_id = {Company}
        """);

    private static LeaveDto ToDto(LeaveRow r) => new(r.Id, r.EmployeeId, r.EmployeeName, r.LeaveType, r.StartDate, r.EndDate, r.Days, r.Paid,
        r.Reason, r.Status, r.RequestedByName, r.RequestedAt, r.DecidedByName, r.DecidedAt, r.DecisionNote);

    public async Task<IReadOnlyList<LeaveDto>> LeavesAsync(Guid? employeeId, string? status, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var q = LeaveQuery();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        return (await q.OrderByDescending(x => x.StartDate).Take(1000).ToListAsync(ct)).Where(x => scope.Allows(x.EmployeeId)).Select(ToDto).ToList();
    }

    public async Task<Result<LeaveDto>> RequestLeaveAsync(RequestLeaveRequest r, CancellationToken ct = default)
    {
        if (await access.CanActForAsync(Permissions.Hr.ManageTime, r.EmployeeId, ct) is { } refused) return refused;
        var emp = await EmployeeAsync(r.EmployeeId, ct);
        if (emp is null) return Error.NotFound("hr.employee_not_found", "Employé introuvable.");
        if (!PayrollRules.LeaveTypes.Contains(r.LeaveType)) return Error.Validation("hr.leave_type", $"Type d'absence « {r.LeaveType} » inconnu.");
        if (r.EndDate < r.StartDate) return Error.Validation("hr.leave_dates", "La fin précède le début.");
        if (r.StartDate < emp.HireDate) return Error.Validation("hr.leave_dates", "L'absence précède la date d'entrée.");
        var settings = (await config.ReadAsync(ct)).Calc;
        var span = settings.DayCountBasis == "working"
            ? PayrollCalculator.WorkingDays(r.StartDate, r.EndDate, settings.WorkingWeekDays)
            : r.EndDate.DayNumber - r.StartDate.DayNumber + 1;
        var days = r.Days ?? span;
        if (days <= 0 || days > r.EndDate.DayNumber - r.StartDate.DayNumber + 1)
            return Error.Validation("hr.leave_days", "Nombre de jours incohérent avec les dates (demi-journées permises).");
        if (r.LeaveType == "unjustified_absence" && r.Paid == true)
            return Error.Validation("hr.leave_paid", "Une absence injustifiée ne peut pas être rémunérée.");
        var paid = r.Paid ?? PayrollRules.PaidLeaveTypes.Contains(r.LeaveType);

        var overlap = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.leave_record WHERE employee_id = {r.EmployeeId} AND status IN ('requested','approved')
                          AND daterange(start_date, end_date, '[]') && daterange({r.StartDate}, {r.EndDate}, '[]')) AS "Value"
            """).FirstAsync(ct);
        if (overlap) return Error.Conflict("hr.leave_overlap", "Une absence est déjà demandée ou approuvée sur ces dates.");

        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.leave_record (id, company_id, employee_id, leave_type, start_date, end_date, days, paid, reason, status, requested_by)
                VALUES ({id}, {Company}, {r.EmployeeId}, {r.LeaveType}, {r.StartDate}, {r.EndDate}, {Math.Round(days, 2)}, {paid},
                        {r.Reason}, 'requested', {UserId})
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        await notifier.ToPermissionAsync(Permissions.Hr.ApproveRequests, "Absence à valider",
            $"{emp.Name} — {PayrollCalculator.LeaveLabel(r.LeaveType)} du {r.StartDate:dd/MM/yyyy} au {r.EndDate:dd/MM/yyyy} ({days:0.##} j).",
            "/hr?tab=approvals", "leave_record", id, $"leave:{id}:requested", ct);
        return Result.Success(ToDto(await LeaveQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<LeaveDto>> DecideLeaveAsync(Guid id, bool approve, DecisionRequest r, CancellationToken ct = default)
    {
        var row = await LeaveQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("hr.leave_not_found", "Absence introuvable.");
        if (await access.CanDecideAsync(Permissions.Hr.ApproveRequests, row.EmployeeId, row.RequestedBy, ct) is { } refused) return refused;
        if (row.Status != "requested") return Error.Conflict("hr.already_decided", "Cette demande a déjà été traitée.");
        if (!approve && string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("hr.decision_note", "Indiquez le motif du refus.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.leave_record SET status = {(approve ? "approved" : "rejected")}, decided_by = {UserId}, decided_at = now(),
                       decision_note = {r.Note?.Trim()}
                WHERE id = {id} AND company_id = {Company} AND status = 'requested'
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        await notifier.ToUsersAsync([row.RequestedBy, await notifier.EmployeeUserAsync(row.EmployeeId, ct)],
            approve ? "Absence approuvée" : "Absence refusée",
            $"{PayrollCalculator.LeaveLabel(row.LeaveType)} du {row.StartDate:dd/MM/yyyy} au {row.EndDate:dd/MM/yyyy}{(r.Note is { Length: > 0 } n ? " — " + n : "")}",
            "/me/hr?tab=leaves", "leave_record", id, $"leave:{id}:decided", ct);
        return Result.Success(ToDto(await LeaveQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<LeaveDto>> CancelLeaveAsync(Guid id, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("hr.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await LeaveQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("hr.leave_not_found", "Absence introuvable.");
        if (row.RequestedBy != UserId && !await access.HasAnyAsync([Permissions.Hr.ManageTime, Permissions.Hr.ApproveRequests], ct))
            return Error.Forbidden("payroll.forbidden", "Seul le demandeur ou un gestionnaire RH peut annuler cette absence.");
        if (row.Status is not ("requested" or "approved")) return Error.Conflict("hr.already_decided", "Cette absence ne peut plus être annulée.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.leave_record SET status = 'cancelled', decision_note = {"Annulée : " + r.Reason.Trim()}
                WHERE id = {id} AND company_id = {Company} AND status IN ('requested','approved')
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        return Result.Success(ToDto(await LeaveQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    // ================================================================== Heures supplémentaires (§19)

    private sealed record OvertimeRow(
        Guid Id, Guid EmployeeId, string EmployeeName, DateOnly WorkDate, decimal Hours, string OvertimeType, decimal Coefficient,
        string? Reason, string Status, Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, string? DecidedByName,
        DateTimeOffset? DecidedAt, string? DecisionNote, Guid? PayrollItemId);

    private IQueryable<OvertimeRow> OvertimeQuery() => db.Database.SqlQuery<OvertimeRow>($"""
        SELECT o.id AS "Id", o.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", o.work_date AS "WorkDate",
               o.hours AS "Hours", o.overtime_type AS "OvertimeType", o.coefficient AS "Coefficient", o.reason AS "Reason",
               o.status AS "Status", o.requested_by AS "RequestedBy", rq.full_name AS "RequestedByName", o.requested_at AS "RequestedAt",
               dc.full_name AS "DecidedByName", o.decided_at AS "DecidedAt", o.decision_note AS "DecisionNote",
               o.payroll_item_id AS "PayrollItemId"
        FROM nexora.overtime_record o
        JOIN nexora.employee e ON e.id = o.employee_id
        LEFT JOIN nexora.app_user rq ON rq.id = o.requested_by
        LEFT JOIN nexora.app_user dc ON dc.id = o.decided_by
        WHERE o.company_id = {Company}
        """);

    private static OvertimeDto ToDto(OvertimeRow r) => new(r.Id, r.EmployeeId, r.EmployeeName, r.WorkDate, r.Hours, r.OvertimeType, r.Coefficient,
        r.Reason, r.Status, r.RequestedByName, r.RequestedAt, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.PayrollItemId);

    public async Task<IReadOnlyList<OvertimeDto>> OvertimeAsync(Guid? employeeId, string? status, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var q = OvertimeQuery();
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        return (await q.OrderByDescending(x => x.WorkDate).Take(1000).ToListAsync(ct)).Where(x => scope.Allows(x.EmployeeId)).Select(ToDto).ToList();
    }

    public async Task<Result<OvertimeDto>> RecordOvertimeAsync(RecordOvertimeRequest r, CancellationToken ct = default)
    {
        if (await access.CanActForAsync(Permissions.Hr.ManageTime, r.EmployeeId, ct) is { } refused) return refused;
        var emp = await EmployeeAsync(r.EmployeeId, ct);
        if (emp is null) return Error.NotFound("hr.employee_not_found", "Employé introuvable.");
        if (!PayrollRules.OvertimeTypes.Contains(r.OvertimeType)) return Error.Validation("hr.overtime_type", $"Type « {r.OvertimeType} » inconnu.");
        if (r.Hours is <= 0 or > 24) return Error.Validation("hr.overtime_hours", "Nombre d'heures compris entre 0 et 24.");
        if (r.WorkDate < emp.HireDate || (emp.TerminationDate is { } td && r.WorkDate > td))
            return Error.Validation("hr.overtime_date", "Date hors de la période d'emploi.");
        // §19 « appliquer coefficient / règle configurée » : figé à la saisie, preuve de la règle en vigueur ce jour-là.
        var coefficient = (await config.ReadAsync(ct)).OvertimeCoefficients.TryGetValue(r.OvertimeType, out var c) ? c : 1.5m;
        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.overtime_record (id, company_id, employee_id, work_date, hours, overtime_type, coefficient, reason, status, requested_by)
                VALUES ({id}, {Company}, {r.EmployeeId}, {r.WorkDate}, {Math.Round(r.Hours, 2)}, {r.OvertimeType}, {coefficient}, {r.Reason},
                        'requested', {UserId})
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        await notifier.ToPermissionAsync(Permissions.Hr.ApproveRequests, "Heures supplémentaires à valider",
            $"{emp.Name} — {r.Hours:0.##} h le {r.WorkDate:dd/MM/yyyy} (coefficient {coefficient:0.##}).",
            "/hr?tab=approvals", "overtime_record", id, $"overtime:{id}:requested", ct);
        return Result.Success(ToDto(await OvertimeQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<OvertimeDto>> DecideOvertimeAsync(Guid id, bool approve, DecisionRequest r, CancellationToken ct = default)
    {
        var row = await OvertimeQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("hr.overtime_not_found", "Saisie introuvable.");
        if (await access.CanDecideAsync(Permissions.Hr.ApproveRequests, row.EmployeeId, row.RequestedBy, ct) is { } refused) return refused;
        if (row.Status != "requested") return Error.Conflict("hr.already_decided", "Cette saisie a déjà été traitée.");
        if (!approve && string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("hr.decision_note", "Indiquez le motif du refus.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.overtime_record SET status = {(approve ? "approved" : "rejected")}, decided_by = {UserId}, decided_at = now(),
                       decision_note = {r.Note?.Trim()}
                WHERE id = {id} AND company_id = {Company} AND status = 'requested'
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        await notifier.ToUsersAsync([row.RequestedBy, await notifier.EmployeeUserAsync(row.EmployeeId, ct)],
            approve ? "Heures supplémentaires approuvées" : "Heures supplémentaires refusées",
            $"{row.Hours:0.##} h le {row.WorkDate:dd/MM/yyyy}{(r.Note is { Length: > 0 } n ? " — " + n : "")}",
            "/me/hr", "overtime_record", id, $"overtime:{id}:decided", ct);
        return Result.Success(ToDto(await OvertimeQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<OvertimeDto>> CancelOvertimeAsync(Guid id, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("hr.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await OvertimeQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("hr.overtime_not_found", "Saisie introuvable.");
        if (row.RequestedBy != UserId && !await access.HasAnyAsync([Permissions.Hr.ManageTime, Permissions.Hr.ApproveRequests], ct))
            return Error.Forbidden("payroll.forbidden", "Seul le demandeur ou un gestionnaire RH peut annuler cette saisie.");
        if (row.Status is not ("requested" or "approved") || row.PayrollItemId is not null)
            return Error.Conflict("hr.already_decided", "Cette saisie ne peut plus être annulée.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.overtime_record SET status = 'cancelled', decision_note = {"Annulée : " + r.Reason.Trim()}
                WHERE id = {id} AND company_id = {Company} AND status IN ('requested','approved')
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        return Result.Success(ToDto(await OvertimeQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    // ================================================================== Éléments variables (§20, §21)

    private sealed record VariableRow(
        Guid Id, Guid EmployeeId, string EmployeeName, Guid PeriodId, string PeriodLabel, string ComponentCode, string ComponentName,
        string Kind, decimal Amount, string Description, string? LegalBasis, string Status, Guid SubmittedBy, string? SubmittedByName,
        DateTimeOffset SubmittedAt, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionNote, Guid? PayrollItemId);

    private IQueryable<VariableRow> VariableQuery() => db.Database.SqlQuery<VariableRow>($"""
        SELECT v.id AS "Id", v.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName", v.period_id AS "PeriodId",
               p.label AS "PeriodLabel", pc.code AS "ComponentCode", pc.name AS "ComponentName", pc.kind AS "Kind", v.amount AS "Amount",
               v.description AS "Description", v.legal_basis AS "LegalBasis", v.status AS "Status", v.submitted_by AS "SubmittedBy",
               sb.full_name AS "SubmittedByName", v.submitted_at AS "SubmittedAt", dc.full_name AS "DecidedByName",
               v.decided_at AS "DecidedAt", v.decision_note AS "DecisionNote", v.payroll_item_id AS "PayrollItemId"
        FROM nexora.payroll_variable v
        JOIN nexora.employee e ON e.id = v.employee_id
        JOIN nexora.payroll_period p ON p.id = v.period_id
        JOIN nexora.pay_component pc ON pc.id = v.component_id
        LEFT JOIN nexora.app_user sb ON sb.id = v.submitted_by
        LEFT JOIN nexora.app_user dc ON dc.id = v.decided_by
        WHERE v.company_id = {Company}
        """);

    private static PayrollVariableDto ToDto(VariableRow r) => new(r.Id, r.EmployeeId, r.EmployeeName, r.PeriodId, r.PeriodLabel, r.ComponentCode,
        r.ComponentName, r.Kind, r.Amount, r.Description, r.LegalBasis, r.Status, r.SubmittedByName, r.SubmittedAt, r.DecidedByName,
        r.DecidedAt, r.DecisionNote, r.PayrollItemId);

    public async Task<IReadOnlyList<PayrollVariableDto>> VariablesAsync(Guid? periodId, Guid? employeeId, string? status, CancellationToken ct = default)
    {
        var scope = await ScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var q = VariableQuery();
        if (periodId is { } p) q = q.Where(x => x.PeriodId == p);
        if (employeeId is { } e) q = q.Where(x => x.EmployeeId == e);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(x => x.Status == status);
        return (await q.OrderByDescending(x => x.SubmittedAt).Take(1000).ToListAsync(ct)).Where(x => scope.Allows(x.EmployeeId)).Select(ToDto).ToList();
    }

    private sealed record VariableComponentRow(Guid Id, string Kind, string Category, bool RequiresLegalBasis, string Name);
    private sealed record PeriodStateRow(string Status, DateOnly CutoffDate, string Label);

    public async Task<Result<PayrollVariableDto>> SubmitVariableAsync(SubmitVariableRequest r, CancellationToken ct = default)
    {
        // Saisie : RH pour tous ; Manager pour son équipe (une prime de performance est proposée par le Manager, §20).
        if (!await access.HasAnyAsync([Permissions.Hr.ManageTime, Permissions.Hr.ApproveRequests], ct))
            return Error.Forbidden("payroll.forbidden", "Saisir un élément variable exige hr.manage_time ou hr.approve_requests.");
        if (!await access.HasAsync(Permissions.Hr.ManageTime, ct) && await access.IsTeamRestrictedAsync(ct)
            && !(await access.TeamAsync(ct)).Contains(r.EmployeeId))
            return Error.Forbidden("payroll.not_in_team", "Cet employé n'appartient pas à votre équipe.");
        if (await access.OwnEmployeeIdAsync(ct) == r.EmployeeId)
            return Error.Forbidden("payroll.self_approval", "Vous ne pouvez pas saisir un élément de paie vous concernant.");
        var emp = await EmployeeAsync(r.EmployeeId, ct);
        if (emp is null) return Error.NotFound("hr.employee_not_found", "Employé introuvable.");
        if (r.Amount <= 0m) return Error.Validation("payroll.variable_amount", "Le montant doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Description)) return Error.Validation("payroll.variable_description", "La description est obligatoire (§21 : source documentée).");

        var component = await db.Database.SqlQuery<VariableComponentRow>($"""
            SELECT id AS "Id", kind AS "Kind", category AS "Category", requires_legal_basis AS "RequiresLegalBasis", name AS "Name"
            FROM nexora.pay_component
            WHERE code = {r.ComponentCode.Trim().ToUpperInvariant()} AND status = 'active' AND (company_id = {Company} OR company_id IS NULL)
            ORDER BY company_id NULLS LAST LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (component is null) return Error.Validation("payroll.component", $"Composant « {r.ComponentCode} » inconnu ou inactif.");
        if (component.Category is "base" or "overtime" or "absence" or "advance" or "loan" or "reimbursement" or "tax" or "social" or "adjustment")
            return Error.Validation("payroll.component",
                "Ce composant est produit par le moteur (salaire, heures sup., absences, avances, frais, régularisations) : il ne se saisit pas en variable.");
        if (component.RequiresLegalBasis && string.IsNullOrWhiteSpace(r.LegalBasis))
            return Error.Validation("payroll.legal_basis", "Retenue disciplinaire : indiquer la base légale et contractuelle (§21).");

        var period = await db.Database.SqlQuery<PeriodStateRow>($"""
            SELECT status AS "Status", cutoff_date AS "CutoffDate", label AS "Label"
            FROM nexora.payroll_period WHERE id = {r.PeriodId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (period is null) return Error.NotFound("payroll.period_not_found", "Période de paie introuvable.");
        if (period.Status is not ("draft" or "calculated" or "in_validation"))
            return Locked($"Période {period.Label} au statut « {period.Status} » : correction par régularisation uniquement.");
        if (await PayrollRules.TodayAsync(db, ct) > period.CutoffDate)
            return Locked($"Date de clôture des variables dépassée ({period.CutoffDate:dd/MM/yyyy}) : correction par régularisation.");

        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.payroll_variable
                    (id, company_id, employee_id, period_id, component_id, amount, description, legal_basis, status, submitted_by)
                VALUES ({id}, {Company}, {r.EmployeeId}, {r.PeriodId}, {component.Id}, {PayrollRules.Money(r.Amount)}, {r.Description.Trim()},
                        {r.LegalBasis}, 'submitted', {UserId})
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        await notifier.ToPermissionAsync(Permissions.Hr.ApproveRequests, "Élément variable à valider",
            $"{emp.Name} — {component.Name} : {r.Amount:N2} ({r.Description.Trim()}), période {period.Label}.",
            "/hr?tab=approvals", "payroll_variable", id, $"variable:{id}:submitted", ct);
        return Result.Success(ToDto(await VariableQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<PayrollVariableDto>> DecideVariableAsync(Guid id, bool approve, DecisionRequest r, CancellationToken ct = default)
    {
        var row = await VariableQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("payroll.variable_not_found", "Élément introuvable.");
        if (await access.CanDecideAsync(Permissions.Hr.ApproveRequests, row.EmployeeId, row.SubmittedBy, ct) is { } refused) return refused;
        if (row.Status != "submitted") return Error.Conflict("hr.already_decided", "Cet élément a déjà été traité.");
        if (!approve && string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("hr.decision_note", "Indiquez le motif du refus.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_variable SET status = {(approve ? "approved" : "rejected")}, decided_by = {UserId}, decided_at = now(),
                       decision_note = {r.Note?.Trim()}
                WHERE id = {id} AND company_id = {Company} AND status = 'submitted'
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        await notifier.ToUsersAsync([row.SubmittedBy], approve ? "Élément variable approuvé" : "Élément variable refusé",
            $"{row.EmployeeName} — {row.ComponentName} : {row.Amount:N2}{(r.Note is { Length: > 0 } n ? " — " + n : "")}",
            "/hr?tab=variables", "payroll_variable", id, $"variable:{id}:decided", ct);
        return Result.Success(ToDto(await VariableQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<PayrollVariableDto>> CancelVariableAsync(Guid id, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("hr.cancel_reason", "Le motif d'annulation est obligatoire.");
        var row = await VariableQuery().Where(x => x.Id == id).FirstOrDefaultAsync(ct);
        if (row is null || !(await ScopeAsync(ct)).Allows(row.EmployeeId)) return Error.NotFound("payroll.variable_not_found", "Élément introuvable.");
        if (row.SubmittedBy != UserId && !await access.HasAsync(Permissions.Hr.ManageTime, ct))
            return Error.Forbidden("payroll.forbidden", "Seul l'auteur ou un gestionnaire RH peut annuler cet élément.");
        if (row.Status is not ("submitted" or "approved") || row.PayrollItemId is not null)
            return Error.Conflict("hr.already_decided", "Cet élément ne peut plus être annulé.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_variable SET status = 'cancelled', decision_note = {"Annulé : " + r.Reason.Trim()}
                WHERE id = {id} AND company_id = {Company} AND status IN ('submitted','approved')
                """, ct);
        }
        catch (Exception ex) when (FromDatabase(ex) is { } error)
        {
            return error;
        }
        return Result.Success(ToDto(await VariableQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    // ================================================================== File de validation

    private sealed record PendingRow(
        string Kind, Guid Id, Guid EmployeeId, string EmployeeName, string Summary, decimal? Amount, string? Currency,
        DateTimeOffset RequestedAt, Guid? RequestedBy, string? RequestedByName);

    public async Task<IReadOnlyList<HrApprovalItemDto>> PendingApprovalsAsync(CancellationToken ct = default)
    {
        var scope = await access.ScopeAsync(
            [Permissions.Hr.ManageTime, Permissions.Payroll.ViewSalaries, Permissions.Payroll.AdvanceApprove, Permissions.Payroll.ExpenseApprove,
             Permissions.Payroll.ApproveAdjustment],
            [Permissions.Hr.ApproveRequests, Permissions.Payroll.AdvanceApprove], allowSelf: false, ct);
        if (scope.IsEmpty) return [];
        var rows = await db.Database.SqlQuery<PendingRow>($"""
            SELECT 'leave' AS "Kind", l.id AS "Id", l.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName",
                   'Absence ' || l.leave_type || ' du ' || to_char(l.start_date, 'DD/MM/YYYY') || ' au ' || to_char(l.end_date, 'DD/MM/YYYY')
                       || ' (' || trim(to_char(l.days, 'FM990.99')) || ' j)' AS "Summary",
                   NULL::numeric AS "Amount", NULL::text AS "Currency", l.requested_at AS "RequestedAt", l.requested_by AS "RequestedBy",
                   u.full_name AS "RequestedByName"
            FROM nexora.leave_record l JOIN nexora.employee e ON e.id = l.employee_id LEFT JOIN nexora.app_user u ON u.id = l.requested_by
            WHERE l.company_id = {Company} AND l.status = 'requested'
            UNION ALL
            SELECT 'overtime', o.id, o.employee_id, e.first_name || ' ' || e.last_name,
                   'Heures supplémentaires : ' || trim(to_char(o.hours, 'FM990.99')) || ' h le ' || to_char(o.work_date, 'DD/MM/YYYY'),
                   NULL, NULL, o.requested_at, o.requested_by, u.full_name
            FROM nexora.overtime_record o JOIN nexora.employee e ON e.id = o.employee_id LEFT JOIN nexora.app_user u ON u.id = o.requested_by
            WHERE o.company_id = {Company} AND o.status = 'requested'
            UNION ALL
            SELECT 'variable', v.id, v.employee_id, e.first_name || ' ' || e.last_name, pc.name || ' — ' || v.description || ' (' || p.label || ')',
                   v.amount, NULL, v.submitted_at, v.submitted_by, u.full_name
            FROM nexora.payroll_variable v JOIN nexora.employee e ON e.id = v.employee_id JOIN nexora.pay_component pc ON pc.id = v.component_id
            JOIN nexora.payroll_period p ON p.id = v.period_id LEFT JOIN nexora.app_user u ON u.id = v.submitted_by
            WHERE v.company_id = {Company} AND v.status = 'submitted'
            UNION ALL
            SELECT 'attendance', a.id, a.employee_id, e.first_name || ' ' || e.last_name,
                   'Présence ' || a.status || ' le ' || to_char(a.work_date, 'DD/MM/YYYY') || CASE WHEN a.justified THEN ' (justifiée)' ELSE '' END,
                   NULL, NULL, a.created_at, a.created_by, u.full_name
            FROM nexora.attendance_record a JOIN nexora.employee e ON e.id = a.employee_id LEFT JOIN nexora.app_user u ON u.id = a.created_by
            WHERE a.company_id = {Company} AND a.validation_status = 'pending' AND a.status IN ('absent', 'half_day', 'late')
            UNION ALL
            SELECT 'advance', x.id, x.employee_id, e.first_name || ' ' || e.last_name,
                   CASE x.kind WHEN 'loan' THEN 'Prêt ' WHEN 'acompte' THEN 'Acompte ' ELSE 'Avance ' END || x.advance_number
                       || COALESCE(' — ' || x.reason, ''),
                   x.amount_requested, cur.code, x.requested_at, x.requested_by, u.full_name
            FROM nexora.employee_advance x JOIN nexora.employee e ON e.id = x.employee_id JOIN nexora.currency cur ON cur.id = x.currency_id
            LEFT JOIN nexora.app_user u ON u.id = x.requested_by
            WHERE x.company_id = {Company} AND x.status = 'requested'
            UNION ALL
            SELECT 'expense_claim', c.id, c.employee_id, e.first_name || ' ' || e.last_name,
                   'Note de frais ' || c.claim_number || ' — ' || c.description, c.amount, cur.code, c.submitted_at, c.submitted_by, u.full_name
            FROM nexora.employee_expense_claim c JOIN nexora.employee e ON e.id = c.employee_id JOIN nexora.currency cur ON cur.id = c.currency_id
            LEFT JOIN nexora.app_user u ON u.id = c.submitted_by
            WHERE c.company_id = {Company} AND c.status = 'submitted'
            UNION ALL
            SELECT 'adjustment', j.id, j.employee_id, e.first_name || ' ' || e.last_name,
                   'Régularisation ' || j.adjustment_number || ' — ' || j.reason, j.amount, cur.code, j.requested_at, j.requested_by, u.full_name
            FROM nexora.payroll_adjustment j JOIN nexora.employee e ON e.id = j.employee_id
            JOIN nexora.payroll_item i ON i.id = j.original_item_id JOIN nexora.currency cur ON cur.id = i.currency_id
            LEFT JOIN nexora.app_user u ON u.id = j.requested_by
            WHERE j.company_id = {Company} AND j.status = 'requested'
            ORDER BY 8
            LIMIT 500
            """).ToListAsync(ct);

        var result = new List<HrApprovalItemDto>();
        foreach (var r in rows.Where(r => scope.Allows(r.EmployeeId)))
        {
            var permission = r.Kind switch
            {
                "advance" => Permissions.Payroll.AdvanceApprove,
                "expense_claim" => Permissions.Payroll.ExpenseApprove,
                "adjustment" => Permissions.Payroll.ApproveAdjustment,
                _ => Permissions.Hr.ApproveRequests,
            };
            var refusal = await access.CanDecideAsync(permission, r.EmployeeId, r.RequestedBy, ct);
            // Les montants d'une note de frais ou d'une régularisation restent réservés aux droits paie.
            var showAmount = r.Kind is "advance" or "variable" || await access.CanViewSalariesAsync(ct) || refusal is null;
            result.Add(new HrApprovalItemDto(r.Kind, r.Id, r.EmployeeId, r.EmployeeName, r.Summary, showAmount ? r.Amount : null,
                showAmount ? r.Currency : null, r.RequestedAt, r.RequestedByName, refusal is null, refusal?.Message));
        }
        return result;
    }
}
