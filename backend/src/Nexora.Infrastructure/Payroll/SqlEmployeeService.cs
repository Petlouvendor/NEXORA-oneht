using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Abstractions;
using Nexora.Application.Payroll;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Dossier employé (§7), contrats et historique de rémunération immuable (§8), éléments récurrents (§20),
/// documents RH confidentiels. Les salaires ne sortent que vers <c>payroll.view_salaries</c> ou l'employé
/// lui-même ; un dossier hors périmètre répond « introuvable » (§5).
/// </summary>
public sealed class SqlEmployeeService(
    NexoraDbContext db,
    ITenantContext tenant,
    IUnitOfWork uow,
    PayrollAccess access,
    PersonalDataProtector protector,
    ILogger<SqlEmployeeService> logger) : IEmployeeService
{
    private Guid Company => PayrollRules.Company(tenant);
    private Guid UserId => PayrollRules.User(tenant);

    private static readonly string[] ViewWide = [Permissions.Hr.ViewEmployees, Permissions.Hr.ManageEmployees, Permissions.Payroll.ViewSalaries];
    private static readonly string[] ViewTeam = [Permissions.Hr.ApproveRequests];

    private Task<EmployeeScope> ViewScopeAsync(CancellationToken ct) => access.ScopeAsync(ViewWide, ViewTeam, allowSelf: true, ct);

    /// <summary>Salaires visibles : droit paie global, ou l'employé pour son propre dossier.</summary>
    private async Task<bool> CanSeeSalaryOfAsync(Guid employeeId, CancellationToken ct)
        => await access.CanViewSalariesAsync(ct)
           || (await access.HasAsync(Permissions.Payroll.SelfService, ct) && await access.OwnEmployeeIdAsync(ct) == employeeId);

    private async Task<bool> VisibleAsync(Guid employeeId, CancellationToken ct)
        => (await ViewScopeAsync(ct)).Allows(employeeId) && await ExistsAsync(employeeId, ct);

    private async Task<bool> ExistsAsync(Guid employeeId, CancellationToken ct)
        => await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.employee WHERE id = {employeeId} AND company_id = {Company}) AS "Value"
            """).FirstAsync(ct);

    private static Error NotFound() => Error.NotFound("hr.employee_not_found", "Employé introuvable.");

    // ================================================================== Départements

    private sealed record DepartmentRow(Guid Id, string Code, string Name, Guid? ManagerEmployeeId, string? ManagerName, string Status, int EmployeeCount);

    public async Task<IReadOnlyList<DepartmentDto>> DepartmentsAsync(CancellationToken ct = default)
        => (await db.Database.SqlQuery<DepartmentRow>($"""
            SELECT d.id AS "Id", d.code AS "Code", d.name AS "Name", d.manager_employee_id AS "ManagerEmployeeId",
                   m.first_name || ' ' || m.last_name AS "ManagerName", d.status AS "Status",
                   (SELECT count(*)::int FROM nexora.employee e WHERE e.department_id = d.id AND e.status <> 'terminated') AS "EmployeeCount"
            FROM nexora.department d
            LEFT JOIN nexora.employee m ON m.id = d.manager_employee_id
            WHERE d.company_id = {Company}
            ORDER BY d.name
            """).ToListAsync(ct))
            .Select(r => new DepartmentDto(r.Id, r.Code, r.Name, r.ManagerEmployeeId, r.ManagerName, r.Status, r.EmployeeCount)).ToList();

    public async Task<Result<DepartmentDto>> SaveDepartmentAsync(Guid? id, SaveDepartmentRequest r, CancellationToken ct = default)
    {
        var code = r.Code?.Trim().ToUpperInvariant() ?? "";
        var name = r.Name?.Trim() ?? "";
        if (code.Length == 0 || name.Length == 0) return Error.Validation("hr.department_invalid", "Code et nom du département obligatoires.");
        var status = r.Status is "inactive" ? "inactive" : "active";
        try
        {
            if (id is { } existing)
            {
                var n = await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.department SET code = {code}, name = {name}, manager_employee_id = {r.ManagerEmployeeId}, status = {status}
                    WHERE id = {existing} AND company_id = {Company}
                    """, ct);
                if (n == 0) return Error.NotFound("hr.department_not_found", "Département introuvable.");
            }
            else
            {
                id = Guid.NewGuid();
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.department (id, company_id, code, name, manager_employee_id, status)
                    VALUES ({id}, {Company}, {code}, {name}, {r.ManagerEmployeeId}, {status})
                    """, ct);
            }
        }
        catch (PostgresException ex) when (ex.SqlState is "23505" or "23503")
        {
            return Error.Conflict("hr.department_invalid", ex.SqlState == "23505"
                ? $"Le code de département « {code} » existe déjà." : "Responsable introuvable dans l'entreprise.");
        }
        return Result.Success((await DepartmentsAsync(ct)).First(d => d.Id == id));
    }

    // ================================================================== Employés

    private sealed record ListRow(
        Guid Id, string EmployeeNumber, string FirstName, string LastName, string? JobTitle, Guid? DepartmentId, string? DepartmentName,
        string Status, DateOnly HireDate, string? EstablishmentName, bool HasUser, string? ContractType, decimal? CurrentSalary,
        string? SalaryBasis, string? Currency);

    public async Task<IReadOnlyList<EmployeeListItemDto>> ListAsync(string? search, string? status, Guid? departmentId, CancellationToken ct = default)
    {
        var scope = await ViewScopeAsync(ct);
        if (scope.IsEmpty) return [];
        var q = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        var rows = await db.Database.SqlQuery<ListRow>($"""
            SELECT e.id AS "Id", e.employee_number AS "EmployeeNumber", e.first_name AS "FirstName", e.last_name AS "LastName",
                   e.job_title AS "JobTitle", e.department_id AS "DepartmentId", d.name AS "DepartmentName", e.status AS "Status",
                   e.hire_date AS "HireDate", est.name AS "EstablishmentName", (e.user_id IS NOT NULL) AS "HasUser",
                   c.contract_type AS "ContractType", s.amount AS "CurrentSalary", s.pay_basis AS "SalaryBasis", cur.code AS "Currency"
            FROM nexora.employee e
            LEFT JOIN nexora.department d ON d.id = e.department_id
            LEFT JOIN nexora.establishment est ON est.id = e.establishment_id
            LEFT JOIN LATERAL (
                SELECT x.* FROM nexora.employee_contract x
                WHERE x.employee_id = e.id AND x.status IN ('active', 'ended', 'terminated')
                ORDER BY (x.status = 'active') DESC, x.start_date DESC LIMIT 1) c ON true
            LEFT JOIN nexora.currency cur ON cur.id = c.currency_id
            LEFT JOIN LATERAL (
                SELECT h.amount, h.pay_basis FROM nexora.salary_history h
                WHERE h.contract_id = c.id AND h.effective_date <= current_date
                ORDER BY h.effective_date DESC LIMIT 1) s ON true
            WHERE e.company_id = {Company}
              AND ({status}::text IS NULL OR e.status = {status}::text)
              AND ({departmentId}::uuid IS NULL OR e.department_id = {departmentId}::uuid)
              AND ({q}::text IS NULL OR nexora.search_norm(e.first_name || ' ' || e.last_name || ' ' || e.employee_number)
                                        LIKE '%' || nexora.search_norm({q}::text) || '%')
            ORDER BY e.last_name, e.first_name
            LIMIT 1000
            """).ToListAsync(ct);
        var salaries = await access.CanViewSalariesAsync(ct);
        var own = await access.OwnEmployeeIdAsync(ct);
        return rows.Where(r => scope.Allows(r.Id)).Select(r =>
        {
            var show = salaries || own == r.Id;
            return new EmployeeListItemDto(r.Id, r.EmployeeNumber, r.FirstName, r.LastName, r.JobTitle, r.DepartmentId, r.DepartmentName,
                r.Status, r.HireDate, r.EstablishmentName, r.HasUser, r.ContractType, show ? r.CurrentSalary : null,
                show ? r.SalaryBasis : null, show ? r.Currency : null);
        }).ToList();
    }

    private sealed record EmployeeRow(
        Guid Id, string EmployeeNumber, string FirstName, string LastName, DateOnly? BirthDate, string? Phone, string? Email, string? Address,
        string? City, string? EmergencyContactName, string? EmergencyContactPhone, string? JobTitle, Guid? DepartmentId, string? DepartmentName,
        Guid? ManagerEmployeeId, string? ManagerName, Guid? EstablishmentId, string? EstablishmentName, DateOnly HireDate,
        DateOnly? TerminationDate, string? TerminationReason, string Status, Guid? UserId, string? UserEmail, string PaymentMethod,
        string? BankName, string? BankAccountLast4, string? MobileMoneyLast4, string? TaxIdLast4, string? SocialIdLast4, string? Notes,
        DateTimeOffset CreatedAt, string? CreatedByName, DateTimeOffset UpdatedAt);

    private sealed record CompensationRow(
        Guid? ContractId, string? ContractNumber, string? ContractType, string? PayFrequency, string? PayBasis, string? Currency,
        decimal? BaseSalary, decimal? HourlyRate, DateOnly? EffectiveDate, decimal AdvanceBalance, decimal LoanBalance, int OpenAdvances);

    public async Task<EmployeeDto?> GetAsync(Guid employeeId, CancellationToken ct = default)
    {
        if (!(await ViewScopeAsync(ct)).Allows(employeeId)) return null;
        var r = await db.Database.SqlQuery<EmployeeRow>($"""
            SELECT e.id AS "Id", e.employee_number AS "EmployeeNumber", e.first_name AS "FirstName", e.last_name AS "LastName",
                   e.birth_date AS "BirthDate", e.phone AS "Phone", e.email AS "Email", e.address AS "Address", e.city AS "City",
                   e.emergency_contact_name AS "EmergencyContactName", e.emergency_contact_phone AS "EmergencyContactPhone",
                   e.job_title AS "JobTitle", e.department_id AS "DepartmentId", d.name AS "DepartmentName",
                   e.manager_employee_id AS "ManagerEmployeeId", m.first_name || ' ' || m.last_name AS "ManagerName",
                   e.establishment_id AS "EstablishmentId", est.name AS "EstablishmentName", e.hire_date AS "HireDate",
                   e.termination_date AS "TerminationDate", e.termination_reason AS "TerminationReason", e.status AS "Status",
                   e.user_id AS "UserId", u.email::text AS "UserEmail", e.payment_method AS "PaymentMethod", e.bank_name AS "BankName",
                   e.bank_account_last4 AS "BankAccountLast4", e.mobile_money_last4 AS "MobileMoneyLast4", e.tax_id_last4 AS "TaxIdLast4",
                   e.social_id_last4 AS "SocialIdLast4", e.notes AS "Notes", e.created_at AS "CreatedAt", cb.full_name AS "CreatedByName",
                   e.updated_at AS "UpdatedAt"
            FROM nexora.employee e
            LEFT JOIN nexora.department d ON d.id = e.department_id
            LEFT JOIN nexora.employee m ON m.id = e.manager_employee_id
            LEFT JOIN nexora.establishment est ON est.id = e.establishment_id
            LEFT JOIN nexora.app_user u ON u.id = e.user_id
            LEFT JOIN nexora.app_user cb ON cb.id = e.created_by
            WHERE e.id = {employeeId} AND e.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (r is null) return null;

        EmployeeCompensationDto? compensation = null;
        if (await CanSeeSalaryOfAsync(employeeId, ct))
        {
            var c = await db.Database.SqlQuery<CompensationRow>($"""
                SELECT c.id AS "ContractId", c.contract_number AS "ContractNumber", c.contract_type AS "ContractType",
                       c.pay_frequency AS "PayFrequency", s.pay_basis AS "PayBasis", cur.code AS "Currency", s.amount AS "BaseSalary",
                       s.hourly_rate AS "HourlyRate", s.effective_date AS "EffectiveDate",
                       COALESCE((SELECT sum(a.balance) FROM nexora.employee_advance a WHERE a.employee_id = {employeeId}
                                   AND a.kind <> 'loan' AND a.status IN ('paid','in_repayment')), 0) AS "AdvanceBalance",
                       COALESCE((SELECT sum(a.balance) FROM nexora.employee_advance a WHERE a.employee_id = {employeeId}
                                   AND a.kind = 'loan' AND a.status IN ('paid','in_repayment')), 0) AS "LoanBalance",
                       (SELECT count(*)::int FROM nexora.employee_advance a WHERE a.employee_id = {employeeId}
                          AND a.status IN ('requested','approved','paid','in_repayment')) AS "OpenAdvances"
                FROM (SELECT 1) one
                LEFT JOIN LATERAL (
                    SELECT x.* FROM nexora.employee_contract x
                    WHERE x.employee_id = {employeeId} AND x.status IN ('active','ended','terminated')
                    ORDER BY (x.status = 'active') DESC, x.start_date DESC LIMIT 1) c ON true
                LEFT JOIN nexora.currency cur ON cur.id = c.currency_id
                LEFT JOIN LATERAL (
                    SELECT h.* FROM nexora.salary_history h WHERE h.contract_id = c.id AND h.effective_date <= current_date
                    ORDER BY h.effective_date DESC LIMIT 1) s ON true
                """).FirstAsync(ct);
            compensation = new EmployeeCompensationDto(c.ContractId, c.ContractNumber, c.ContractType, c.PayFrequency, c.PayBasis, c.Currency,
                c.BaseSalary, c.HourlyRate, c.EffectiveDate, c.AdvanceBalance, c.LoanBalance, c.OpenAdvances);
        }

        return new EmployeeDto(r.Id, r.EmployeeNumber, r.FirstName, r.LastName, r.BirthDate, r.Phone, r.Email, r.Address, r.City,
            r.EmergencyContactName, r.EmergencyContactPhone, r.JobTitle, r.DepartmentId, r.DepartmentName, r.ManagerEmployeeId, r.ManagerName,
            r.EstablishmentId, r.EstablishmentName, r.HireDate, r.TerminationDate, r.TerminationReason, r.Status, r.UserId, r.UserEmail,
            r.PaymentMethod, r.BankName, PayrollRules.Masked(r.BankAccountLast4), PayrollRules.Masked(r.MobileMoneyLast4),
            PayrollRules.Masked(r.TaxIdLast4), PayrollRules.Masked(r.SocialIdLast4), r.Notes, r.CreatedAt, r.CreatedByName, r.UpdatedAt,
            compensation, await access.CanViewSalariesAsync(ct), await access.HasAsync(Permissions.Payroll.ViewSensitive, ct),
            await access.HasAsync(Permissions.Hr.ManageEmployees, ct));
    }

    private static Error? ValidateEmployee(SaveEmployeeRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.FirstName) || string.IsNullOrWhiteSpace(r.LastName))
            return Error.Validation("hr.employee_name", "Nom et prénom obligatoires.");
        if (r.PaymentMethod is { } m && !PayrollRules.PaymentMethods.Contains(m))
            return Error.Validation("hr.payment_method", $"Moyen de paiement « {m} » inconnu.");
        if (r.BirthDate is { } b && b >= r.HireDate)
            return Error.Validation("hr.birth_date", "La date de naissance doit précéder la date d'entrée.");
        return null;
    }

    public async Task<Result<EmployeeDto>> CreateAsync(SaveEmployeeRequest r, CancellationToken ct = default)
    {
        if (ValidateEmployee(r) is { } invalid) return invalid;
        if (r.Contract?.BaseSalary is not null && !await access.HasAsync(Permissions.Payroll.ManageCompensation, ct))
            return Error.Forbidden("payroll.forbidden", "Fixer un salaire exige le droit payroll.manage_compensation.");
        var id = Guid.NewGuid();
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var number = string.IsNullOrWhiteSpace(r.EmployeeNumber)
                    ? await PayrollRules.NextNumberAsync(db, Company, "employee", "EMP-", r.HireDate.Year, token)
                    : r.EmployeeNumber.Trim().ToUpperInvariant();
                var tax = r.TaxParameters is { ValueKind: JsonValueKind.Object } tp ? tp.GetRawText() : "{}";
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.employee
                        (id, company_id, employee_number, first_name, last_name, birth_date, phone, email, address, city,
                         emergency_contact_name, emergency_contact_phone, job_title, department_id, manager_employee_id, establishment_id,
                         hire_date, status, payment_method, bank_name, bank_account_encrypted, bank_account_last4,
                         mobile_money_encrypted, mobile_money_last4, tax_id_encrypted, tax_id_last4, social_id_encrypted, social_id_last4,
                         tax_parameters, notes, created_by, updated_by)
                    VALUES ({id}, {Company}, {number}, {r.FirstName.Trim()}, {r.LastName.Trim()}, {r.BirthDate}, {r.Phone}, {r.Email},
                            {r.Address}, {r.City}, {r.EmergencyContactName}, {r.EmergencyContactPhone}, {r.JobTitle}, {r.DepartmentId},
                            {r.ManagerEmployeeId}, {r.EstablishmentId}, {r.HireDate}, 'active', {r.PaymentMethod ?? "bank_transfer"},
                            {r.BankName}, {protector.Protect(r.BankAccount)}, {PayrollRules.Last4(r.BankAccount)},
                            {protector.Protect(r.MobileMoney)}, {PayrollRules.Last4(r.MobileMoney)},
                            {protector.Protect(r.TaxId)}, {PayrollRules.Last4(r.TaxId)},
                            {protector.Protect(r.SocialId)}, {PayrollRules.Last4(r.SocialId)},
                            {tax}::jsonb, {r.Notes}, {UserId}, {UserId})
                    """, token);
                if (r.Contract is { } contract)
                {
                    var created = await CreateContractCoreAsync(id, contract, token);
                    if (!created.IsSuccess) throw new PayrollRefusal(created.Error!);
                }
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex) when (Describe(ex) is { } message)
        {
            return Error.Conflict("hr.employee_invalid", message);
        }
        logger.LogInformation("Employé {Employee} créé", id);
        return Result.Success((await GetAsync(id, ct))!);
    }

    /// <summary>Contraintes de la base traduites en message métier (unicité du matricule, rattachements).</summary>
    private static string? Describe(Exception ex)
    {
        var pg = ex as PostgresException ?? ex.InnerException as PostgresException ?? (ex.InnerException?.InnerException as PostgresException);
        var text = pg?.MessageText ?? ex.Message;
        if (pg?.ConstraintName is { } c)
        {
            if (c == "employee_number_uk") return "Ce matricule existe déjà dans l'entreprise.";
            if (c == "employee_user_uk") return "Cet utilisateur est déjà rattaché à un autre dossier employé.";
            if (c.EndsWith("_fk", StringComparison.Ordinal)) return "Département, responsable ou établissement introuvable dans l'entreprise.";
            if (c == "employee_contract_no_overlap") return "Ce contrat chevauche un autre contrat de l'employé.";
        }
        if (text.Contains("employee_number_uk", StringComparison.Ordinal)) return "Ce matricule existe déjà dans l'entreprise.";
        if (text.Contains("employee_contract_no_overlap", StringComparison.Ordinal)) return "Ce contrat chevauche un autre contrat de l'employé.";
        if (text.Contains("employee_user_uk", StringComparison.Ordinal)) return "Cet utilisateur est déjà rattaché à un autre dossier employé.";
        if (text.Contains("_fk", StringComparison.Ordinal)) return "Département, responsable ou établissement introuvable dans l'entreprise.";
        return pg is null ? ex.Message : Persistence.DatabaseErrors.ClientMessage(pg);
    }

    public async Task<Result<EmployeeDto>> UpdateAsync(Guid employeeId, SaveEmployeeRequest r, CancellationToken ct = default)
    {
        if (ValidateEmployee(r) is { } invalid) return invalid;
        if (!await ExistsAsync(employeeId, ct)) return NotFound();
        if (r.ManagerEmployeeId == employeeId) return Error.Validation("hr.manager_self", "Un employé ne peut pas être son propre responsable.");
        var tax = r.TaxParameters is { ValueKind: JsonValueKind.Object } tp ? tp.GetRawText() : null;
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee SET
                    first_name = {r.FirstName.Trim()}, last_name = {r.LastName.Trim()}, birth_date = {r.BirthDate}, phone = {r.Phone},
                    email = {r.Email}, address = {r.Address}, city = {r.City}, emergency_contact_name = {r.EmergencyContactName},
                    emergency_contact_phone = {r.EmergencyContactPhone}, job_title = {r.JobTitle}, department_id = {r.DepartmentId},
                    manager_employee_id = {r.ManagerEmployeeId}, establishment_id = {r.EstablishmentId}, hire_date = {r.HireDate},
                    payment_method = {r.PaymentMethod ?? "bank_transfer"}, bank_name = {r.BankName},
                    employee_number = COALESCE(NULLIF(btrim({r.EmployeeNumber ?? ""}), ''), employee_number),
                    bank_account_encrypted = CASE WHEN {r.BankAccount}::text IS NULL THEN bank_account_encrypted ELSE {protector.Protect(r.BankAccount)} END,
                    bank_account_last4 = CASE WHEN {r.BankAccount}::text IS NULL THEN bank_account_last4 ELSE {PayrollRules.Last4(r.BankAccount)} END,
                    mobile_money_encrypted = CASE WHEN {r.MobileMoney}::text IS NULL THEN mobile_money_encrypted ELSE {protector.Protect(r.MobileMoney)} END,
                    mobile_money_last4 = CASE WHEN {r.MobileMoney}::text IS NULL THEN mobile_money_last4 ELSE {PayrollRules.Last4(r.MobileMoney)} END,
                    tax_id_encrypted = CASE WHEN {r.TaxId}::text IS NULL THEN tax_id_encrypted ELSE {protector.Protect(r.TaxId)} END,
                    tax_id_last4 = CASE WHEN {r.TaxId}::text IS NULL THEN tax_id_last4 ELSE {PayrollRules.Last4(r.TaxId)} END,
                    social_id_encrypted = CASE WHEN {r.SocialId}::text IS NULL THEN social_id_encrypted ELSE {protector.Protect(r.SocialId)} END,
                    social_id_last4 = CASE WHEN {r.SocialId}::text IS NULL THEN social_id_last4 ELSE {PayrollRules.Last4(r.SocialId)} END,
                    tax_parameters = COALESCE({tax}::jsonb, tax_parameters),
                    notes = {r.Notes}, updated_by = {UserId}
                WHERE id = {employeeId} AND company_id = {Company}
                """, ct);
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException or DbUpdateException)
        {
            return Error.Conflict("hr.employee_invalid", Describe(ex) ?? ex.Message);
        }
        return Result.Success((await GetAsync(employeeId, ct))!);
    }

    public async Task<Result<EmployeeDto>> SetStatusAsync(Guid employeeId, EmployeeStatusRequest r, CancellationToken ct = default)
    {
        if (r.Status is not ("active" or "suspended" or "on_leave" or "terminated"))
            return Error.Validation("hr.employee_status", $"Statut « {r.Status} » inconnu.");
        var current = await db.Database.SqlQuery<string?>($"""
            SELECT status AS "Value" FROM nexora.employee WHERE id = {employeeId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (current is null) return NotFound();
        if (current == "terminated")
            return Error.Conflict("hr.employee_terminated", "Employé sorti : le dossier est conservé mais ne peut pas être réactivé (réembauche = nouveau dossier).");
        if (r.Status == "terminated")
        {
            if (r.Date is not { } date) return Error.Validation("hr.termination_date", "La date de sortie est obligatoire.");
            if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("hr.termination_reason", "Le motif de sortie est obligatoire.");
            try
            {
                await uow.ExecuteInTransactionAsync(async token =>
                {
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.employee SET status = 'terminated', termination_date = {date}, termination_reason = {r.Reason.Trim()},
                               updated_by = {UserId}
                        WHERE id = {employeeId} AND company_id = {Company}
                        """, token);
                    // Contrats en cours terminés à la date de sortie (jamais supprimés, §8).
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.employee_contract
                           SET status = 'terminated', end_date = LEAST(COALESCE(end_date, {date}), {date}),
                               end_reason = {"Sortie de l'employé — " + r.Reason.Trim()}, ended_at = now(), ended_by = {UserId}
                         WHERE employee_id = {employeeId} AND company_id = {Company} AND status = 'active'
                        """, token);
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.employee_pay_element SET status = 'ended', end_date = LEAST(COALESCE(end_date, {date}), {date}), ended_by = {UserId}
                         WHERE employee_id = {employeeId} AND company_id = {Company} AND status = 'active'
                        """, token);
                    return 0;
                }, ct);
            }
            catch (InvalidOperationException ex)
            {
                return Error.Conflict("hr.employee_invalid", Describe(ex) ?? ex.Message);
            }
        }
        else
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee SET status = {r.Status}, updated_by = {UserId} WHERE id = {employeeId} AND company_id = {Company}
                """, ct);
        }
        return Result.Success((await GetAsync(employeeId, ct))!);
    }

    public async Task<Result<EmployeeDto>> LinkUserAsync(Guid employeeId, LinkEmployeeUserRequest r, CancellationToken ct = default)
    {
        if (!await ExistsAsync(employeeId, ct)) return NotFound();
        if (r.UserId is { } user)
        {
            // L'utilisateur doit appartenir à l'entreprise (affectation active) : on ne rattache jamais un compte étranger.
            bool member;
            using (tenant.BeginSystemScope())
                member = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS(
                        SELECT 1 FROM nexora.user_role ur JOIN nexora.access_scope sc ON sc.id = ur.scope_id
                        WHERE ur.user_id = {user} AND ur.revoked_at IS NULL
                          AND (sc.company_id = {Company}
                               OR (sc.group_id IS NOT NULL AND sc.group_id = (SELECT group_id FROM nexora.company WHERE id = {Company})))) AS "Value"
                    """).FirstAsync(ct);
            if (!member) return Error.Validation("hr.user_not_member", "Cet utilisateur n'a aucun accès à l'entreprise.");
        }
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee SET user_id = {r.UserId}, updated_by = {UserId} WHERE id = {employeeId} AND company_id = {Company}
                """, ct);
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException or DbUpdateException)
        {
            return Error.Conflict("hr.employee_invalid", Describe(ex) ?? ex.Message);
        }
        return Result.Success((await GetAsync(employeeId, ct))!);
    }

    private sealed record SensitiveRow(
        string? BankName, string? BankAccount, string? MobileMoney, string? TaxId, string? SocialId, string TaxParameters);

    public async Task<Result<EmployeeSensitiveDto>> SensitiveAsync(Guid employeeId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<SensitiveRow>($"""
            SELECT bank_name AS "BankName", bank_account_encrypted AS "BankAccount", mobile_money_encrypted AS "MobileMoney",
                   tax_id_encrypted AS "TaxId", social_id_encrypted AS "SocialId", tax_parameters::text AS "TaxParameters"
            FROM nexora.employee WHERE id = {employeeId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return NotFound();
        await access.LogSensitiveAccessAsync("employee", employeeId, "bank_and_tax_identifiers", ct);
        using var doc = JsonDocument.Parse(row.TaxParameters);
        return Result.Success(new EmployeeSensitiveDto(employeeId, row.BankName, protector.Unprotect(row.BankAccount),
            protector.Unprotect(row.MobileMoney), protector.Unprotect(row.TaxId), protector.Unprotect(row.SocialId), doc.RootElement.Clone()));
    }

    // ================================================================== Contrats (§8)

    private sealed record ContractRow(
        Guid Id, string ContractNumber, string ContractType, DateOnly StartDate, DateOnly? EndDate, DateOnly? TrialEndDate, string PayFrequency,
        string PayBasis, string Currency, decimal WeeklyHours, string? JobTitle, string? Benefits, string? Conditions, string Status,
        string? EndReason, DateTimeOffset CreatedAt, string? CreatedByName);

    private IQueryable<ContractRow> ContractQuery(Guid employeeId) => db.Database.SqlQuery<ContractRow>($"""
        SELECT c.id AS "Id", c.contract_number AS "ContractNumber", c.contract_type AS "ContractType", c.start_date AS "StartDate",
               c.end_date AS "EndDate", c.trial_end_date AS "TrialEndDate", c.pay_frequency AS "PayFrequency", c.pay_basis AS "PayBasis",
               cur.code AS "Currency", c.weekly_hours AS "WeeklyHours", c.job_title AS "JobTitle", c.benefits AS "Benefits",
               c.conditions AS "Conditions", c.status AS "Status", c.end_reason AS "EndReason", c.created_at AS "CreatedAt",
               u.full_name AS "CreatedByName"
        FROM nexora.employee_contract c
        JOIN nexora.currency cur ON cur.id = c.currency_id
        LEFT JOIN nexora.app_user u ON u.id = c.created_by
        WHERE c.employee_id = {employeeId} AND c.company_id = {Company}
        """);

    private static ContractDto ToDto(ContractRow r) => new(r.Id, r.ContractNumber, r.ContractType, r.StartDate, r.EndDate, r.TrialEndDate,
        r.PayFrequency, r.PayBasis, r.Currency, r.WeeklyHours, r.JobTitle, r.Benefits, r.Conditions, r.Status, r.EndReason, r.CreatedAt, r.CreatedByName);

    public async Task<Result<IReadOnlyList<ContractDto>>> ContractsAsync(Guid employeeId, CancellationToken ct = default)
    {
        if (!await VisibleAsync(employeeId, ct)) return NotFound();
        return Result.Success<IReadOnlyList<ContractDto>>((await ContractQuery(employeeId).OrderByDescending(c => c.StartDate).ToListAsync(ct)).Select(ToDto).ToList());
    }

    public async Task<Result<ContractDto>> CreateContractAsync(Guid employeeId, CreateContractRequest r, CancellationToken ct = default)
    {
        if (!await ExistsAsync(employeeId, ct)) return NotFound();
        if (r.BaseSalary is not null && !await access.HasAsync(Permissions.Payroll.ManageCompensation, ct))
            return Error.Forbidden("payroll.forbidden", "Fixer un salaire exige le droit payroll.manage_compensation.");
        try
        {
            return await uow.ExecuteInTransactionAsync(async token =>
            {
                var created = await CreateContractCoreAsync(employeeId, r, token);
                if (!created.IsSuccess) throw new PayrollRefusal(created.Error!);
                return created;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Error.Conflict("hr.contract_invalid", Describe(ex) ?? ex.Message);
        }
    }

    private sealed record EmployeeStateRow(string Status, DateOnly HireDate);
    private sealed record OpenContractRow(Guid Id, string ContractNumber, DateOnly StartDate);

    /// <summary>Crée le contrat (et le salaire initial) dans la transaction courante. Le contrat actif à durée
    /// indéterminée qui le précède est clos la veille : jamais deux contrats actifs qui se chevauchent.</summary>
    private async Task<Result<ContractDto>> CreateContractCoreAsync(Guid employeeId, CreateContractRequest r, CancellationToken ct)
    {
        if (!PayrollRules.ContractTypes.Contains(r.ContractType)) return Error.Validation("hr.contract_type", $"Type de contrat « {r.ContractType} » inconnu.");
        if (!PayrollRules.Frequencies.Contains(r.PayFrequency)) return Error.Validation("hr.pay_frequency", $"Fréquence « {r.PayFrequency} » inconnue.");
        if (!PayrollRules.PayBases.Contains(r.PayBasis)) return Error.Validation("hr.pay_basis", $"Base de rémunération « {r.PayBasis} » inconnue.");
        if (r.EndDate is { } end && end < r.StartDate) return Error.Validation("hr.contract_dates", "La fin du contrat précède son début.");
        if (r.ContractType == "fixed_term" && r.EndDate is null) return Error.Validation("hr.contract_dates", "Un contrat à durée déterminée exige une date de fin.");
        if (r.BaseSalary is { } salary && salary <= 0m) return Error.Validation("payroll.salary_amount", "Le salaire doit être positif.");
        var weekly = r.WeeklyHours ?? 48m;
        if (weekly is <= 0 or > 84) return Error.Validation("hr.weekly_hours", "Durée hebdomadaire invalide.");

        var employee = await db.Database.SqlQuery<EmployeeStateRow>($"""
            SELECT status AS "Status", hire_date AS "HireDate" FROM nexora.employee WHERE id = {employeeId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (employee is null) return NotFound();
        if (employee.Status == "terminated") return Error.Conflict("hr.employee_terminated", "Employé sorti : aucun nouveau contrat.");
        if (r.StartDate < employee.HireDate) return Error.Validation("hr.contract_dates", "Le contrat ne peut pas commencer avant la date d'entrée.");

        var currencyId = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.currency WHERE code = {r.Currency.Trim().ToUpperInvariant()}
            """).FirstOrDefaultAsync(ct);
        if (currencyId is null) return Error.Validation("hr.currency", $"Devise « {r.Currency} » inconnue.");

        var previous = await db.Database.SqlQuery<OpenContractRow>($"""
            SELECT id AS "Id", contract_number AS "ContractNumber", start_date AS "StartDate" FROM nexora.employee_contract
            WHERE employee_id = {employeeId} AND company_id = {Company} AND status = 'active' AND end_date IS NULL AND start_date < {r.StartDate}
            FOR UPDATE
            """).ToListAsync(ct);
        var id = Guid.NewGuid();
        var number = await PayrollRules.NextNumberAsync(db, Company, "employee_contract", "CTR-", r.StartDate.Year, ct);
        foreach (var p in previous)
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee_contract SET status = 'ended', end_date = {r.StartDate.AddDays(-1)},
                       end_reason = {"Remplacé par le contrat " + number}, ended_at = now(), ended_by = {UserId}
                WHERE id = {p.Id}
                """, ct);

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.employee_contract
                (id, company_id, employee_id, contract_number, contract_type, start_date, end_date, trial_end_date, pay_frequency,
                 pay_basis, currency_id, weekly_hours, job_title, benefits, conditions, status, created_by)
            VALUES ({id}, {Company}, {employeeId}, {number}, {r.ContractType}, {r.StartDate}, {r.EndDate}, {r.TrialEndDate}, {r.PayFrequency},
                    {r.PayBasis}, {currencyId}, {weekly}, {r.JobTitle}, {r.Benefits}, {r.Conditions}, 'active', {UserId})
            """, ct);
        if (r.JobTitle is { Length: > 0 } title)
            await db.Database.ExecuteSqlAsync($"UPDATE nexora.employee SET job_title = {title} WHERE id = {employeeId}", ct);
        if (r.BaseSalary is { } amount)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.salary_history
                    (company_id, employee_id, contract_id, effective_date, pay_basis, amount, hourly_rate, currency_id, reason, created_by)
                VALUES ({Company}, {employeeId}, {id}, {r.StartDate}, {r.PayBasis}, {PayrollRules.Money(amount)},
                        {r.HourlyRate}, {currencyId}, {r.SalaryReason ?? ("Salaire initial — contrat " + number)}, {UserId})
                """, ct);
        return Result.Success(ToDto((await ContractQuery(employeeId).Where(c => c.Id == id).FirstAsync(ct))));
    }

    public async Task<Result<ContractDto>> EndContractAsync(Guid contractId, EndContractRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("hr.contract_end_reason", "Le motif de fin de contrat est obligatoire.");
        var row = await db.Database.SqlQuery<ContractEndRow>($"""
            SELECT employee_id AS "EmployeeId", start_date AS "StartDate", status AS "Status"
            FROM nexora.employee_contract WHERE id = {contractId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("hr.contract_not_found", "Contrat introuvable.");
        if (row.Status != "active") return Error.Conflict("hr.contract_closed", "Seul un contrat actif peut être terminé.");
        if (r.EndDate < row.StartDate) return Error.Validation("hr.contract_dates", "La fin du contrat précède son début.");
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee_contract
                   SET status = {(r.Terminated ? "terminated" : "ended")}, end_date = {r.EndDate}, end_reason = {r.Reason.Trim()},
                       ended_at = now(), ended_by = {UserId}
                 WHERE id = {contractId} AND company_id = {Company}
                """, ct);
        }
        catch (Exception ex) when (ex is PostgresException or InvalidOperationException or DbUpdateException)
        {
            return Error.Conflict("hr.contract_invalid", Describe(ex) ?? ex.Message);
        }
        return Result.Success(ToDto(await ContractQuery(row.EmployeeId).Where(c => c.Id == contractId).FirstAsync(ct)));
    }

    private sealed record ContractEndRow(Guid EmployeeId, DateOnly StartDate, string Status);

    // ================================================================== Rémunération (§8)

    private sealed record SalaryRow(
        Guid Id, Guid ContractId, string ContractNumber, DateOnly EffectiveDate, string PayBasis, decimal Amount, decimal? HourlyRate,
        string Currency, decimal? PreviousAmount, DateOnly? PreviousEffectiveDate, string Reason, string? CreatedByName, DateTimeOffset CreatedAt);

    private IQueryable<SalaryRow> SalaryQuery(Guid employeeId) => db.Database.SqlQuery<SalaryRow>($"""
        SELECT h.id AS "Id", h.contract_id AS "ContractId", c.contract_number AS "ContractNumber", h.effective_date AS "EffectiveDate",
               h.pay_basis AS "PayBasis", h.amount AS "Amount", h.hourly_rate AS "HourlyRate", cur.code AS "Currency",
               h.previous_amount AS "PreviousAmount", h.previous_effective_date AS "PreviousEffectiveDate", h.reason AS "Reason",
               u.full_name AS "CreatedByName", h.created_at AS "CreatedAt"
        FROM nexora.salary_history h
        JOIN nexora.employee_contract c ON c.id = h.contract_id
        JOIN nexora.currency cur ON cur.id = h.currency_id
        LEFT JOIN nexora.app_user u ON u.id = h.created_by
        WHERE h.employee_id = {employeeId} AND h.company_id = {Company}
        """);

    private static SalaryHistoryDto ToDto(SalaryRow r) => new(r.Id, r.ContractId, r.ContractNumber, r.EffectiveDate, r.PayBasis, r.Amount,
        r.HourlyRate, r.Currency, r.PreviousAmount, r.PreviousEffectiveDate, r.Reason, r.CreatedByName, r.CreatedAt);

    public async Task<Result<IReadOnlyList<SalaryHistoryDto>>> SalaryHistoryAsync(Guid employeeId, CancellationToken ct = default)
    {
        if (!await CanSeeSalaryOfAsync(employeeId, ct) || !await ExistsAsync(employeeId, ct)) return NotFound();
        return Result.Success<IReadOnlyList<SalaryHistoryDto>>((await SalaryQuery(employeeId).OrderByDescending(s => s.EffectiveDate)
            .ThenByDescending(s => s.CreatedAt).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private sealed record ContractPickRow(Guid Id, string PayBasis, Guid CurrencyId, DateOnly StartDate, DateOnly? EndDate);

    public async Task<Result<SalaryHistoryDto>> ChangeSalaryAsync(Guid employeeId, ChangeSalaryRequest r, CancellationToken ct = default)
    {
        if (!await ExistsAsync(employeeId, ct)) return NotFound();
        if (r.Amount <= 0m) return Error.Validation("payroll.salary_amount", "Le salaire doit être positif.");
        if (r.HourlyRate is <= 0m) return Error.Validation("payroll.salary_amount", "Le taux horaire doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 3)
            return Error.Validation("payroll.salary_reason", "Le motif du changement de salaire est obligatoire (§8).");
        var contract = await db.Database.SqlQuery<ContractPickRow>($"""
            SELECT id AS "Id", pay_basis AS "PayBasis", currency_id AS "CurrencyId", start_date AS "StartDate", end_date AS "EndDate"
            FROM nexora.employee_contract
            WHERE employee_id = {employeeId} AND company_id = {Company} AND status IN ('active','ended','terminated')
              AND ({r.ContractId}::uuid IS NULL OR id = {r.ContractId}::uuid)
              AND start_date <= {r.EffectiveDate} AND (end_date IS NULL OR end_date >= {r.EffectiveDate})
            ORDER BY start_date DESC LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (contract is null)
            return Error.Validation("payroll.no_contract", "Aucun contrat de l'employé ne couvre la date d'effet.");
        var id = Guid.NewGuid();
        try
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.salary_history
                    (id, company_id, employee_id, contract_id, effective_date, pay_basis, amount, hourly_rate, currency_id, reason, created_by)
                VALUES ({id}, {Company}, {employeeId}, {contract.Id}, {r.EffectiveDate}, {contract.PayBasis}, {PayrollRules.Money(r.Amount)},
                        {r.HourlyRate}, {contract.CurrencyId}, {r.Reason.Trim()}, {UserId})
                """, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            return Error.Conflict("payroll.salary_duplicate", "Un salaire existe déjà à cette date d'effet pour ce contrat : choisir une autre date.");
        }
        catch (PostgresException ex) when (ex.SqlState == "23514")
        {
            return Error.Conflict("payroll.period_locked", Persistence.DatabaseErrors.ClientMessage(ex));
        }
        logger.LogInformation("Salaire de {Employee} modifié au {Date}", employeeId, r.EffectiveDate);
        return Result.Success(ToDto(await SalaryQuery(employeeId).Where(s => s.Id == id).FirstAsync(ct)));
    }

    private sealed record ApplicableRow(Guid? ContractId, string? ContractNumber, Guid? SalaryId, decimal? Amount, decimal? HourlyRate,
        string? PayBasis, string? Currency, DateOnly? EffectiveDate);

    public async Task<Result<ApplicableSalaryDto>> ApplicableSalaryAsync(Guid employeeId, DateOnly date, CancellationToken ct = default)
    {
        if (!await CanSeeSalaryOfAsync(employeeId, ct) || !await ExistsAsync(employeeId, ct)) return NotFound();
        var r = await db.Database.SqlQuery<ApplicableRow>($"""
            SELECT c.id AS "ContractId", c.contract_number AS "ContractNumber", h.id AS "SalaryId", h.amount AS "Amount",
                   h.hourly_rate AS "HourlyRate", h.pay_basis AS "PayBasis", cur.code AS "Currency", h.effective_date AS "EffectiveDate"
            FROM (SELECT 1) one
            LEFT JOIN LATERAL (
                SELECT * FROM nexora.employee_contract x
                WHERE x.employee_id = {employeeId} AND x.company_id = {Company} AND x.status IN ('active','ended','terminated')
                  AND x.start_date <= {date} AND (x.end_date IS NULL OR x.end_date >= {date})
                ORDER BY x.start_date DESC LIMIT 1) c ON true
            LEFT JOIN LATERAL (
                SELECT * FROM nexora.salary_history s WHERE s.contract_id = c.id AND s.effective_date <= {date}
                ORDER BY s.effective_date DESC LIMIT 1) h ON true
            LEFT JOIN nexora.currency cur ON cur.id = h.currency_id
            """).FirstAsync(ct);
        return Result.Success(new ApplicableSalaryDto(date, r.ContractId, r.ContractNumber, r.SalaryId, r.Amount, r.HourlyRate, r.PayBasis, r.Currency, r.EffectiveDate));
    }

    // ================================================================== Éléments récurrents (§20)

    private sealed record ElementRow(
        Guid Id, Guid ComponentId, string ComponentCode, string ComponentName, string Kind, string Category, string CalcMethod,
        decimal? Amount, decimal? Rate, string Currency, DateOnly StartDate, DateOnly? EndDate, string Status, string? Note);

    private IQueryable<ElementRow> ElementQuery() => db.Database.SqlQuery<ElementRow>($"""
        SELECT x.id AS "Id", x.component_id AS "ComponentId", pc.code AS "ComponentCode", pc.name AS "ComponentName", pc.kind AS "Kind",
               pc.category AS "Category", x.calc_method AS "CalcMethod", x.amount AS "Amount", x.rate AS "Rate", cur.code AS "Currency",
               x.start_date AS "StartDate", x.end_date AS "EndDate", x.status AS "Status", x.note AS "Note"
        FROM nexora.employee_pay_element x
        JOIN nexora.pay_component pc ON pc.id = x.component_id
        JOIN nexora.currency cur ON cur.id = x.currency_id
        WHERE x.company_id = {Company}
        """);

    private sealed record ElementEmployeeRow(Guid Id, Guid EmployeeId);

    private static PayElementDto ToDto(ElementRow r) => new(r.Id, r.ComponentId, r.ComponentCode, r.ComponentName, r.Kind, r.Category,
        r.CalcMethod, r.Amount, r.Rate, r.Currency, r.StartDate, r.EndDate, r.Status, r.Note);

    public async Task<Result<IReadOnlyList<PayElementDto>>> PayElementsAsync(Guid employeeId, CancellationToken ct = default)
    {
        if (!await CanSeeSalaryOfAsync(employeeId, ct) || !await ExistsAsync(employeeId, ct)) return NotFound();
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.employee_pay_element WHERE employee_id = {employeeId} AND company_id = {Company}
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<PayElementDto>>((await ElementQuery().Where(x => ids.Contains(x.Id)).OrderBy(x => x.Status)
            .ThenBy(x => x.StartDate).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private sealed record ComponentPick(Guid Id, string Kind, string Category, bool RequiresLegalBasis);

    public async Task<Result<PayElementDto>> AddPayElementAsync(Guid employeeId, SavePayElementRequest r, CancellationToken ct = default)
    {
        if (!await ExistsAsync(employeeId, ct)) return NotFound();
        var component = await db.Database.SqlQuery<ComponentPick>($"""
            SELECT id AS "Id", kind AS "Kind", category AS "Category", requires_legal_basis AS "RequiresLegalBasis"
            FROM nexora.pay_component
            WHERE code = {r.ComponentCode.Trim().ToUpperInvariant()} AND status = 'active' AND (company_id = {Company} OR company_id IS NULL)
            ORDER BY company_id NULLS LAST LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (component is null) return Error.Validation("payroll.component", $"Composant « {r.ComponentCode} » inconnu ou inactif.");
        if (component.Category is "base" or "overtime" or "absence" or "advance" or "loan" or "reimbursement" or "tax" or "social")
            return Error.Validation("payroll.component", "Ce composant est calculé par le moteur de paie : il ne peut pas être un élément récurrent.");
        if (component.RequiresLegalBasis)
            return Error.Validation("payroll.component", "Une retenue disciplinaire se saisit comme variable ponctuelle avec sa base légale.");
        if (r.CalcMethod is not ("fixed" or "percent_of_base" or "percent_of_sales" or "per_day_worked"))
            return Error.Validation("payroll.calc_method", $"Mode de calcul « {r.CalcMethod} » inconnu.");
        if (r.CalcMethod is "fixed" or "per_day_worked" && r.Amount is not > 0m)
            return Error.Validation("payroll.element_amount", "Montant positif obligatoire.");
        if (r.CalcMethod is "percent_of_base" or "percent_of_sales" && r.Rate is not (> 0m and <= 100m))
            return Error.Validation("payroll.element_rate", "Taux compris entre 0 et 100 % obligatoire.");
        if (r.CalcMethod == "percent_of_sales" && component.Kind != "earning")
            return Error.Validation("payroll.calc_method", "Une commission sur ventes est forcément un gain.");
        if (r.EndDate is { } end && end < r.StartDate) return Error.Validation("payroll.element_dates", "La fin précède le début.");

        var currencyId = await db.Database.SqlQuery<Guid?>($"""
            SELECT COALESCE(
                (SELECT currency_id FROM nexora.employee_contract WHERE employee_id = {employeeId} AND status = 'active'
                  ORDER BY start_date DESC LIMIT 1),
                (SELECT default_currency_id FROM nexora.company WHERE id = {Company})) AS "Value"
            """).FirstAsync(ct);
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.employee_pay_element
                (id, company_id, employee_id, component_id, calc_method, amount, rate, currency_id, start_date, end_date, note, created_by)
            VALUES ({id}, {Company}, {employeeId}, {component.Id}, {r.CalcMethod},
                    {(r.CalcMethod is "fixed" or "per_day_worked" ? PayrollRules.Money(r.Amount!.Value) : null)},
                    {(r.CalcMethod is "percent_of_base" or "percent_of_sales" ? r.Rate : null)},
                    {currencyId}, {r.StartDate}, {r.EndDate}, {r.Note}, {UserId})
            """, ct);
        return Result.Success(ToDto(await ElementQuery().Where(x => x.Id == id).FirstAsync(ct)));
    }

    public async Task<Result<PayElementDto>> EndPayElementAsync(Guid elementId, EndPayElementRequest r, CancellationToken ct = default)
    {
        var el = await ElementQuery().Where(x => x.Id == elementId).FirstOrDefaultAsync(ct);
        if (el is null) return Error.NotFound("payroll.element_not_found", "Élément introuvable.");
        if (el.Status != "active") return Error.Conflict("payroll.element_closed", "Élément déjà terminé.");
        if (r.EndDate < el.StartDate) return Error.Validation("payroll.element_dates", "La fin précède le début.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.employee_pay_element SET status = 'ended', end_date = {r.EndDate}, ended_by = {UserId}
            WHERE id = {elementId} AND company_id = {Company}
            """, ct);
        return Result.Success(ToDto(await ElementQuery().Where(x => x.Id == elementId).FirstAsync(ct)));
    }

    // ================================================================== Documents RH (§7)

    private sealed record DocumentRow(
        Guid Id, Guid EmployeeId, string DocumentType, string Title, string FileName, string ContentType, int SizeBytes, string Sha256,
        bool VisibleToEmployee, Guid? SupersedesId, DateTimeOffset? ArchivedAt, string? UploadedByName, DateTimeOffset CreatedAt);

    private IQueryable<DocumentRow> DocumentQuery() => db.Database.SqlQuery<DocumentRow>($"""
        SELECT d.id AS "Id", d.employee_id AS "EmployeeId", d.document_type AS "DocumentType", d.title AS "Title", d.file_name AS "FileName",
               d.content_type AS "ContentType", d.size_bytes AS "SizeBytes", d.sha256_hash AS "Sha256",
               d.visible_to_employee AS "VisibleToEmployee", d.supersedes_id AS "SupersedesId", d.archived_at AS "ArchivedAt",
               u.full_name AS "UploadedByName", d.created_at AS "CreatedAt"
        FROM nexora.employee_document d
        LEFT JOIN nexora.app_user u ON u.id = d.uploaded_by
        WHERE d.company_id = {Company}
        """);

    private static EmployeeDocumentDto ToDto(DocumentRow r) => new(r.Id, r.EmployeeId, r.DocumentType, r.Title, r.FileName, r.ContentType,
        r.SizeBytes, r.Sha256, r.VisibleToEmployee, r.SupersedesId, r.ArchivedAt, r.UploadedByName, r.CreatedAt);

    /// <summary>Documents RH : dossier complet pour les droits RH/paie ; l'employé ne voit que ceux qu'on lui a rendus visibles.</summary>
    private async Task<(bool Full, bool Self)> DocumentAccessAsync(Guid employeeId, CancellationToken ct)
    {
        var full = await access.HasAnyAsync([Permissions.Hr.ManageEmployees, Permissions.Payroll.ViewSalaries, Permissions.Payroll.Audit], ct);
        var self = await access.HasAsync(Permissions.Payroll.SelfService, ct) && await access.OwnEmployeeIdAsync(ct) == employeeId;
        return (full, self);
    }

    public async Task<Result<IReadOnlyList<EmployeeDocumentDto>>> DocumentsAsync(Guid employeeId, CancellationToken ct = default)
    {
        var (full, self) = await DocumentAccessAsync(employeeId, ct);
        if (!(full || self) || !await ExistsAsync(employeeId, ct)) return NotFound();
        var rows = await DocumentQuery().Where(d => d.EmployeeId == employeeId).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        return Result.Success<IReadOnlyList<EmployeeDocumentDto>>(rows.Where(d => full || d.VisibleToEmployee).Select(ToDto).ToList());
    }

    public async Task<Result<EmployeeDocumentDto>> UploadDocumentAsync(Guid employeeId, UploadEmployeeDocumentRequest r, CancellationToken ct = default)
    {
        if (!await ExistsAsync(employeeId, ct)) return NotFound();
        var stored = await StoreDocumentAsync(employeeId, r, ct);
        if (!stored.IsSuccess) return stored.Error!;
        return Result.Success(ToDto(await DocumentQuery().Where(d => d.Id == stored.Value).FirstAsync(ct)));
    }

    /// <summary>Dépôt d'un document RH (utilisé aussi pour le justificatif d'une note de frais).</summary>
    internal async Task<Result<Guid>> StoreDocumentAsync(Guid employeeId, UploadEmployeeDocumentRequest r, CancellationToken ct)
    {
        if (!PayrollRules.DocumentTypes.Contains(r.DocumentType))
            return Error.Validation("hr.document_type", $"Type de document « {r.DocumentType} » inconnu.");
        if (string.IsNullOrWhiteSpace(r.Title) || string.IsNullOrWhiteSpace(r.FileName))
            return Error.Validation("hr.document_title", "Titre et nom de fichier obligatoires.");
        byte[] content;
        try
        {
            content = Convert.FromBase64String(r.ContentBase64 ?? "");
        }
        catch (FormatException)
        {
            return Error.Validation("hr.document_content", "Contenu du fichier illisible (base64 attendu).");
        }
        if (content.Length == 0) return Error.Validation("hr.document_content", "Fichier vide.");
        if (content.Length > 10 * 1024 * 1024) return Error.Validation("hr.document_size", "Fichier trop volumineux (10 Mo au plus).");
        var id = Guid.NewGuid();
        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.employee_document
                (id, company_id, employee_id, document_type, title, file_name, content, content_type, size_bytes, sha256_hash,
                 visible_to_employee, supersedes_id, uploaded_by)
            VALUES ({id}, {Company}, {employeeId}, {r.DocumentType}, {r.Title.Trim()}, {r.FileName.Trim()}, {content},
                    {(string.IsNullOrWhiteSpace(r.ContentType) ? "application/octet-stream" : r.ContentType)}, {content.Length}, {hash},
                    {r.VisibleToEmployee}, {r.SupersedesId}, {UserId})
            """, ct);
        return Result.Success(id);
    }

    private sealed record ContentRow(Guid EmployeeId, string FileName, string ContentType, byte[] Content, bool VisibleToEmployee);

    public async Task<Result<EmployeeDocumentContent>> DocumentContentAsync(Guid documentId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<ContentRow>($"""
            SELECT employee_id AS "EmployeeId", file_name AS "FileName", content_type AS "ContentType", content AS "Content",
                   visible_to_employee AS "VisibleToEmployee"
            FROM nexora.employee_document WHERE id = {documentId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("hr.document_not_found", "Document introuvable.");
        var (full, self) = await DocumentAccessAsync(row.EmployeeId, ct);
        if (!full && !(self && row.VisibleToEmployee)) return Error.NotFound("hr.document_not_found", "Document introuvable.");
        await access.LogSensitiveAccessAsync("employee_document", documentId, "download", ct);
        return Result.Success(new EmployeeDocumentContent(row.FileName, row.ContentType, row.Content));
    }

    public async Task<Result<EmployeeDocumentDto>> ArchiveDocumentAsync(Guid documentId, CancellationToken ct = default)
    {
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.employee_document SET archived_at = now(), archived_by = {UserId}
            WHERE id = {documentId} AND company_id = {Company} AND archived_at IS NULL
            """, ct);
        var doc = await DocumentQuery().Where(d => d.Id == documentId).FirstOrDefaultAsync(ct);
        if (doc is null) return Error.NotFound("hr.document_not_found", "Document introuvable.");
        if (n == 0 && doc.ArchivedAt is null) return Error.Conflict("hr.document_archived", "Document non archivable.");
        return Result.Success(ToDto(doc));
    }
}
