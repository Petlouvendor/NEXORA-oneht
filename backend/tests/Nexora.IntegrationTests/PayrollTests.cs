using System.Globalization;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Identity;
using Nexora.Application.Payroll;
using Nexora.Application.Reporting;
using Nexora.Contracts.Identity;
using Nexora.Contracts.Payroll;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Module « Paie, RH & avances sur salaire » — grille d'acceptation §37 (tests 11 à 30 du cahier,
/// docs/module-paie-rh-avances.md) prouvée au niveau des services, avec de vrais utilisateurs par rôle
/// (RH, comptable en chef, direction, manager, employé, caissier) et la base (verrous, RLS, triggers).
/// Chaque scénario utilise ses propres périodes et des contrats bornés à ces périodes : aucun ne pollue un autre.
/// </summary>
[Collection("finance")]
public sealed class PayrollTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Restaurant = Guid.Parse("dce00000-0000-0000-0000-0000000000c2");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly DateOnly FarCutoff = new(2026, 12, 31);

    private AsyncServiceScope Scope() => fx.Provider.CreateAsyncScope();

    private static void As(IServiceProvider sp, Guid userId, Guid? company = null)
    {
        var tc = (TestTenantContext)sp.GetRequiredService<ITenantContext>();
        tc.SetUser(userId);
        tc.SetScope([company ?? Company], [], company ?? Company);
    }

    private static readonly SemaphoreSlim FundingLock = new(1, 1);
    private static readonly HashSet<string> Funded = [];

    /// <summary>
    /// Les paiements en espèces contrôlent le solde de la caisse (audit 2026-10 éd. 2). La caisse de démonstration est
    /// alimentée une fois par base de test, comme en exploitation : banque ouverte puis transfert comptabilisé.
    /// </summary>
    private async Task FundCashAsync()
    {
        await FundingLock.WaitAsync();
        try
        {
            if (!Funded.Add(fx.TestConnectionString)) return;
            await using var s = Scope();
            As(s.ServiceProvider, Direction);
            var tm = s.ServiceProvider.GetRequiredService<Nexora.Application.Finance.ITreasuryManagementService>();
            var bank = (await tm.CreateAccountAsync(new Nexora.Contracts.Finance.SaveFinancialAccountRequest(
                $"Fonds paie {Guid.NewGuid():N}"[..20], "bank", "HTG", null, null))).Value;
            Ok(await tm.RecordOpeningBalanceAsync(bank, new Nexora.Contracts.Finance.TreasuryOpeningBalanceRequest(Guid.NewGuid(), 5_000_000m)));
            Ok(await tm.TransferAsync(new Nexora.Contracts.Finance.TreasuryTransferRequest(Guid.NewGuid(), bank, CashAccount, 5_000_000m,
                "Fonds de paie des tests")));
        }
        finally
        {
            FundingLock.Release();
        }
    }

    private async Task<T> Act<T>(Guid user, Func<IServiceProvider, Task<T>> action, Guid? company = null)
    {
        await FundCashAsync();
        await using var s = Scope();
        As(s.ServiceProvider, user, company);
        return await action(s.ServiceProvider);
    }

    private static T Ok<T>(Result<T> r)
    {
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        return r.Value!;
    }

    private async Task<Guid> UserWithRole(string role, Guid? company = null)
    {
        var id = Guid.NewGuid();
        await Exec($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'PAY-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), '{role} paie', 'active')
            """);
        await using var s = Scope();
        As(s.ServiceProvider, Direction, company);
        var granted = await s.ServiceProvider.GetRequiredService<IUserAssignmentService>()
            .GrantAsync(id, new GrantAssignmentRequest(role, "COMPANY", company ?? Company, null, null, null, null));
        granted.IsSuccess.Should().BeTrue(granted.Error?.Message);
        return id;
    }

    private async Task Exec(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<T> Scalar<T>(string sql)
    {
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default! : (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
    }

    private static CreateContractRequest Contract(DateOnly start, DateOnly? end, decimal? salary, string frequency = "monthly",
        string type = "fixed_term") => new(end is null && type == "fixed_term" ? "permanent" : type, start, end, null, frequency, "monthly", "HTG",
        48m, "Agent", null, null, salary, null, null);

    private Task<EmployeeDto> Hire(Guid hr, string firstName, DateOnly hire, CreateContractRequest contract, Guid? manager = null,
        string payment = "cash", string? bankAccount = null, Guid? company = null)
        => Act(hr, async sp => Ok(await sp.GetRequiredService<IEmployeeService>().CreateAsync(new SaveEmployeeRequest(
            EmployeeNumber: null, FirstName: firstName, LastName: "Paie" + Guid.NewGuid().ToString("N")[..6], BirthDate: new DateOnly(1990, 5, 4),
            Phone: "+509 3700 0000", Email: null, Address: null, City: "Port-au-Prince", EmergencyContactName: null, EmergencyContactPhone: null,
            JobTitle: "Agent", DepartmentId: null, ManagerEmployeeId: manager, EstablishmentId: null, HireDate: hire, PaymentMethod: payment,
            BankName: bankAccount is null ? null : "Unibank", BankAccount: bankAccount, MobileMoney: null, TaxId: "003-123-456-7", SocialId: null,
            TaxParameters: null, Notes: null, Contract: contract))), company);

    private Task Link(Guid employeeId, Guid userId)
        => Act(Direction, async sp => Ok(await sp.GetRequiredService<IEmployeeService>().LinkUserAsync(employeeId, new LinkEmployeeUserRequest(userId))));

    private Task<PayrollPeriodDto> Period(Guid actor, string frequency, DateOnly start, DateOnly end, string kind = "regular", Guid[]? scope = null)
        => Act(actor, async sp => Ok(await sp.GetRequiredService<IPayrollService>().CreatePeriodAsync(
            new CreatePeriodRequest(frequency, start, end, FarCutoff, end, kind, null, scope))));

    private Task<PayrollRunDto> Calculate(Guid actor, Guid periodId)
        => Act(actor, async sp => Ok(await sp.GetRequiredService<IPayrollService>().CalculateAsync(periodId)));

    private async Task<PayrollRunDto> ApproveCycle(Guid runId, Guid reviewer, Guid validator, Guid approver)
    {
        Ok(await Act(reviewer, sp => sp.GetRequiredService<IPayrollService>().ReviewAsync(runId, new WorkflowNoteRequest("Contrôlé", true))));
        Ok(await Act(validator, sp => sp.GetRequiredService<IPayrollService>().ValidateAsync(runId, new WorkflowNoteRequest("Validé"))));
        return Ok(await Act(approver, sp => sp.GetRequiredService<IPayrollService>().ApproveAsync(runId, new WorkflowNoteRequest("Approuvé"))));
    }

    private Task<PayRunResultDto> Pay(Guid actor, Guid runId, Guid operation, IReadOnlyList<Guid>? items = null)
        => Act(actor, async sp => Ok(await sp.GetRequiredService<IPayrollService>().PayAsync(runId,
            new PayRunRequest(CashAccount, "cash", items, "Lot de paie", null, null, operation))));

    private static PayrollItemSummaryDto ItemOf(PayrollRunDto run, Guid employeeId) => run.Items.Single(i => i.EmployeeId == employeeId);

    private Task<PayrollItemDetailDto> Detail(Guid actor, Guid itemId)
        => Act(actor, async sp => Ok(await sp.GetRequiredService<IPayrollService>().ItemAsync(itemId)));

    /// <summary>Solde grand livre (transaction) d'un compte pour un tiers employé.</summary>
    private Task<decimal> LedgerBalance(Guid employeeId, string accountCode) => Scalar<decimal>($"""
        SELECT COALESCE(SUM(l.debit - l.credit), 0) FROM nexora.journal_entry_line l JOIN nexora.account a ON a.id = l.account_id
        WHERE l.third_party_type = 'employee' AND l.third_party_id = '{employeeId}' AND a.code = '{accountCode}'
        """);

    // ---- §37 tests 11 et 12 : employé, contrat, salaire ; historique de rémunération ------------------------------
    [Fact]
    public async Task Employee_contract_and_salary_history_are_kept_immutable_and_reconstructible()
    {
        var hr = await UserWithRole("hr_manager");
        // Contrat bihebdomadaire : jamais calculé par les autres scénarios (tous mensuels ou hebdomadaires).
        var e = await Hire(hr, "Nadège", new DateOnly(2026, 1, 5), Contract(new DateOnly(2026, 1, 5), null, 45_000m, "biweekly", "permanent"),
            payment: "bank_transfer", bankAccount: "001-2345-6789");
        e.Compensation!.BaseSalary.Should().Be(45_000m);
        e.BankAccountMasked.Should().Be("•••• 6789");
        (await Scalar<string>($"SELECT bank_account_encrypted FROM nexora.employee WHERE id = '{e.Id}'")).Should().NotContain("6789",
            "§7 : informations bancaires protégées (chiffrées au repos)");

        Ok(await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().ChangeSalaryAsync(e.Id,
            new ChangeSalaryRequest(null, new DateOnly(2026, 7, 1), 50_000m, null, "Promotion chef d'équipe"))));
        (await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().ChangeSalaryAsync(e.Id,
            new ChangeSalaryRequest(null, new DateOnly(2026, 8, 1), 52_000m, null, "")))).Error!.Code.Should().Be("payroll.salary_reason");
        (await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().ChangeSalaryAsync(e.Id,
            new ChangeSalaryRequest(null, new DateOnly(2026, 7, 1), 51_000m, null, "Doublon")))).Error!.Code.Should().Be("payroll.salary_duplicate");

        var history = Ok(await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().SalaryHistoryAsync(e.Id)));
        history.Should().HaveCount(2);
        history[0].Should().Match<SalaryHistoryDto>(h => h.Amount == 50_000m && h.PreviousAmount == 45_000m
            && h.Reason == "Promotion chef d'équipe" && h.CreatedByName != null);
        Ok(await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().ApplicableSalaryAsync(e.Id, new DateOnly(2026, 6, 30)))).Amount.Should().Be(45_000m);
        Ok(await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().ApplicableSalaryAsync(e.Id, new DateOnly(2026, 7, 1)))).Amount.Should().Be(50_000m);

        // L'ancien salaire n'est jamais écrasé ; un dossier et un contrat terminé ne se suppriment pas (§8, §39).
        var update = () => Exec($"UPDATE nexora.salary_history SET amount = 1 WHERE employee_id = '{e.Id}'");
        await update.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*immuable*");
        var contract = Ok(await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().ContractsAsync(e.Id))).Single();
        Ok(await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().EndContractAsync(contract.Id,
            new EndContractRequest(new DateOnly(2026, 12, 31), "Fin de mission")))).Status.Should().Be("ended");
        var delete = () => Exec($"DELETE FROM nexora.employee_contract WHERE id = '{contract.Id}'");
        await delete.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*suppression physique interdite*");
        var deleteEmployee = () => Exec($"DELETE FROM nexora.employee WHERE id = '{e.Id}'");
        await deleteEmployee.Should().ThrowAsync<Npgsql.PostgresException>();

        // Identifiants en clair : lecture journalisée (§35).
        var sensitive = Ok(await Act(Direction, sp => sp.GetRequiredService<IEmployeeService>().SensitiveAsync(e.Id)));
        sensitive.BankAccount.Should().Be("001-2345-6789");
        (await Scalar<long>($"SELECT count(*) FROM nexora.audit_log WHERE entity_id = '{e.Id}' AND action = 'READ_SENSITIVE'")).Should().Be(1);
    }

    // ---- §37 tests 13 à 17 : période, paie simple, prime, absence, heures supplémentaires validées -------------------
    [Fact]
    public async Task March_payroll_combines_base_bonus_absences_and_validated_overtime_with_four_eyes_approvals()
    {
        var hr = await UserWithRole("hr_manager");
        var managerUser = await UserWithRole("manager");
        var employeeUser = await UserWithRole("employee");
        var manager = await Hire(hr, "Chef", new DateOnly(2026, 3, 1), Contract(new DateOnly(2026, 3, 1), null, 90_000m, "biweekly", "permanent"));
        await Link(manager.Id, managerUser);
        var e = await Hire(hr, "Jean", new DateOnly(2026, 3, 1), Contract(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), 60_000m),
            manager: manager.Id);
        await Link(e.Id, employeeUser);
        var period = await Period(hr, "monthly", new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));
        period.Label.Should().Be("Paie mars 2026");

        // Prime fixe 25 000 (RH) validée par le Manager ; le Manager ne voit pas le salaire (§5, §6).
        var bonus = Ok(await Act(hr, sp => sp.GetRequiredService<IHrTimeService>().SubmitVariableAsync(
            new SubmitVariableRequest(e.Id, period.Id, "BONUS", 25_000m, "Prime fixe objectif trimestre", null))));
        (await Act(hr, sp => sp.GetRequiredService<IHrTimeService>().DecideVariableAsync(bonus.Id, true, new DecisionRequest(null))))
            .Error!.Code.Should().Be("payroll.self_approval");
        Ok(await Act(managerUser, sp => sp.GetRequiredService<IHrTimeService>().DecideVariableAsync(bonus.Id, true, new DecisionRequest("OK"))));
        (await Act(managerUser, sp => sp.GetRequiredService<IEmployeeService>().GetAsync(e.Id)))!.Compensation.Should().BeNull();

        // Absence non rémunérée demandée par l'employé lui-même, approuvée par son Manager.
        var leave = Ok(await Act(employeeUser, sp => sp.GetRequiredService<IHrTimeService>().RequestLeaveAsync(
            new RequestLeaveRequest(e.Id, "unpaid", new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 11), null, null, "Démarches"))));
        leave.Paid.Should().BeFalse();
        Ok(await Act(managerUser, sp => sp.GetRequiredService<IHrTimeService>().DecideLeaveAsync(leave.Id, true, new DecisionRequest(null))));

        // Heures supplémentaires : coefficient configuré figé à la saisie, preuve de l'approbation conservée (§19).
        var overtime = Ok(await Act(employeeUser, sp => sp.GetRequiredService<IHrTimeService>().RecordOvertimeAsync(
            new RecordOvertimeRequest(e.Id, new DateOnly(2026, 3, 20), 10m, "standard", "Inventaire"))));
        overtime.Coefficient.Should().Be(1.5m);
        Ok(await Act(managerUser, sp => sp.GetRequiredService<IHrTimeService>().DecideOvertimeAsync(overtime.Id, true, new DecisionRequest(null))))
            .DecidedByName.Should().NotBeNullOrEmpty();

        // Présence : absence injustifiée saisie par la RH, validée par le Manager.
        var imported = Ok(await Act(hr, sp => sp.GetRequiredService<IHrTimeService>().RecordAttendanceAsync(new RecordAttendanceRequest(
            [new AttendanceInput(e.Id, new DateOnly(2026, 3, 5), "absent", null, false, null),
             new AttendanceInput(Guid.NewGuid(), new DateOnly(2026, 3, 5), "present", 8m, false, null)], "import"))));
        imported.Created.Should().Be(1);
        imported.Errors.Should().ContainSingle();
        var absent = (await Act(hr, sp => sp.GetRequiredService<IHrTimeService>().AttendanceAsync(e.Id, null, null, null))).Single();
        Ok(await Act(managerUser, sp => sp.GetRequiredService<IHrTimeService>().DecideAttendanceAsync(absent.Id, true, new DecisionRequest(null))));

        var run = await Calculate(hr, period.Id);
        var item = ItemOf(run, e.Id);
        var detail = await Detail(hr, item.Id);
        // Base 60 000 ; heures sup. 10 h × 288,4615 × 1,5 = 4 326,92 ; prime 25 000 ; absences 3 j × 2 000 = 6 000.
        detail.BaseAmount.Should().Be(60_000m);
        detail.Earnings.Select(x => x.Category).Should().BeEquivalentTo(["base", "overtime", "bonus"]);
        item.Gross.Should().Be(89_326.92m);
        detail.AbsenceDays.Should().Be(3m);
        detail.AbsenceDeduction.Should().Be(6_000m);
        item.Net.Should().Be(83_326.92m);
        detail.Trace.Should().Contain(t => t.Step == "Net à payer");
        run.Items.Should().NotContain(i => i.EmployeeId == manager.Id, "contrat bihebdomadaire hors de la période mensuelle");
    }

    // ---- §11, §31 : règles légales configurables, validées par une autre personne, versionnées -----------------------
    [Fact]
    public async Task Legal_rules_are_validated_by_another_person_frozen_and_versioned()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var rules = (IServiceProvider sp) => sp.GetRequiredService<IPayrollRuleService>();
        var april1 = new DateOnly(2026, 4, 1);
        var april30 = new DateOnly(2026, 4, 30);
        SavePayrollRuleRequest Rule(string code, string type, string baseType, string method, decimal? rate, IReadOnlyList<RuleBracketDto>? brackets = null,
            DateOnly? from = null) => new(code, code, "HT", type, baseType, method, rate, null, brackets, brackets is not null, null, null, null, null,
            from ?? april1, april30, "Test", null);

        var social = Ok(await Act(Direction, sp => rules(sp).CreateRuleAsync(Rule("T_SOC_E", "employee_contribution", "social", "percentage", 6m))));
        var employer = Ok(await Act(Direction, sp => rules(sp).CreateRuleAsync(Rule("T_SOC_R", "employer_contribution", "social", "percentage", 6m))));
        var tax = Ok(await Act(Direction, sp => rules(sp).CreateRuleAsync(Rule("T_IRI", "income_tax", "taxable_after_social", "progressive", null,
            [new(0m, 60_000m, 0m), new(60_000m, 240_000m, 10m), new(240_000m, 480_000m, 15m), new(480_000m, null, 25m)]))));
        (await Act(Direction, sp => rules(sp).ValidateAsync(social.Id, new ValidateRuleRequest("Contrôlé par l'auteur"))))
            .Error!.Code.Should().Be("payroll.self_approval");
        foreach (var r in new[] { social, employer, tax })
            Ok(await Act(chief, sp => rules(sp).ValidateAsync(r.Id, new ValidateRuleRequest("Taux vérifiés — jeu de test"))));

        var freeze = () => Exec($"UPDATE nexora.payroll_rule SET rate = 9 WHERE id = '{social.Id}'");
        await freeze.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*figés*");

        // Nouvelle version au 15 avril (7 %) : c'est elle qui s'applique à la paie d'avril, la v1 prend fin la veille.
        var v2 = Ok(await Act(Direction, sp => rules(sp).NewVersionAsync(social.Id,
            Rule("T_SOC_E", "employee_contribution", "social", "percentage", 7m, from: new DateOnly(2026, 4, 15)))));
        v2.Version.Should().Be(2);
        Ok(await Act(chief, sp => rules(sp).ValidateAsync(v2.Id, new ValidateRuleRequest("Nouveau taux au 15 avril"))));
        (await Act(Direction, sp => rules(sp).RulesAsync(false))).Single(x => x.Id == social.Id).ValidTo.Should().Be(new DateOnly(2026, 4, 14));
        (await Act(Direction, sp => rules(sp).RulesAsync(true))).Should().Contain(x => x.IsTemplate && x.Code == "HT_IRI");

        var e = await Hire(hr, "Rose", april1, Contract(april1, april30, 50_000m));
        var run = await Calculate(hr, (await Period(hr, "monthly", april1, april30)).Id);
        var item = ItemOf(run, e.Id);
        var detail = await Detail(hr, item.Id);
        // Social 7 % = 3 500 ; base IRI 46 500 → 558 000/an → 18 000 + 36 000 + 19 500 = 73 500 → 6 125/mois ; patronal 6 % = 3 000.
        detail.LegalDeductions.Should().Be(9_625m);
        detail.EmployerContributions.Should().Be(3_000m);
        item.Net.Should().Be(40_375m);
        item.EmployerCost.Should().Be(53_000m);
        detail.Deductions.Single(d => d.Code == "T_SOC_E").RuleVersion.Should().Be(2);
        detail.Deductions.Should().OnlyContain(d => d.Category != "tax" && d.Category != "social" && d.Category != "employer" || d.RuleId != null);
    }

    // ---- §14 + §37 tests 18, 19, 20, 26, 27, 29, 30 : l'exemple du cahier de bout en bout ------------------------------
    [Fact]
    public async Task Spec_example_advance_of_20000_is_recovered_over_two_payrolls_with_locking_payslips_and_reconciliation()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var employeeUser = await UserWithRole("employee");
        var sep1 = new DateOnly(2026, 9, 1);
        var e = await Hire(hr, "Marie", sep1, Contract(sep1, null, 60_000m, type: "permanent"));
        await Link(e.Id, employeeUser);
        var advances = (IServiceProvider sp) => sp.GetRequiredService<ISalaryAdvanceService>();

        // 18. Demande par l'employé ; contrôles de politique (§13.2).
        var advance = Ok(await Act(employeeUser, sp => advances(sp).RequestAsync(e.Id, new RequestAdvanceRequest(
            "salary_advance", 20_000m, "Frais scolaires", "installments", 2, null, new DateOnly(2026, 9, 15), null))));
        advance.AdvanceNumber.Should().StartWith("AVS-");
        advance.Checks.Should().OnlyContain(c => c.Passed || !c.Blocking);
        (await Act(employeeUser, sp => advances(sp).ApproveAsync(advance.Id, new ApproveAdvanceRequest(null, null, null, null, null, null))))
            .Error!.Code.Should().Be("payroll.forbidden");

        // 19. Approbation (RH) puis versement relié à la caisse : créance sur l'employé, pas une charge (§14, §39).
        Ok(await Act(hr, sp => advances(sp).ApproveAsync(advance.Id, new ApproveAdvanceRequest(null, "Accordé", null, null, null, null))))
            .Status.Should().Be("approved");
        var payOp = Guid.NewGuid();
        var paid = Ok(await Act(chief, sp => advances(sp).PayAsync(advance.Id,
            new PayAdvanceRequest(CashAccount, "cash", "REC-AVS", null, new DateOnly(2026, 9, 10), payOp))));
        paid.Status.Should().Be("paid");
        paid.Balance.Should().Be(20_000m);
        paid.Schedule.Select(s => (s.DueDate, s.Amount)).Should().Equal((new DateOnly(2026, 9, 15), 10_000m), (new DateOnly(2026, 10, 15), 10_000m));
        Ok(await Act(chief, sp => advances(sp).PayAsync(advance.Id,
            new PayAdvanceRequest(CashAccount, "cash", "REC-AVS", null, null, payOp)))).Id.Should().Be(advance.Id);
        (await Scalar<long>($"SELECT count(*) FROM nexora.financial_transaction WHERE reference_type = 'employee_advance' AND reference_id = '{advance.Id}'"))
            .Should().Be(1, "un double envoi du versement ne sort l'argent qu'une fois");
        (await LedgerBalance(e.Id, "4250")).Should().Be(20_000m);
        (await Scalar<decimal>($"SELECT COALESCE(sum(l.debit), 0) FROM nexora.journal_entry_line l JOIN nexora.account a ON a.id = l.account_id WHERE a.code = '6410' AND l.journal_entry_id = '{paid.JournalEntryId}'"))
            .Should().Be(0m);

        // Paie de septembre : 60 000 − 10 000 récupérés = 50 000.
        var sep = await Period(hr, "monthly", sep1, new DateOnly(2026, 9, 30));
        var run = await Calculate(hr, sep.Id);
        var item = ItemOf(run, e.Id);
        item.AdvanceRecovery.Should().Be(10_000m);
        item.Net.Should().Be(50_000m);

        var mass = await Act(Direction, sp => sp.GetRequiredService<IAskNexora>().AskAsync(new AskRequest("Quelle est la masse salariale de ce mois ?", null)));
        mass.Intent.Should().Be("payroll_mass");

        Ok(await Act(hr, sp => sp.GetRequiredService<IPayrollService>().ReviewAsync(run.Id, new WorkflowNoteRequest("Contrôlé", true))));
        Ok(await Act(chief, sp => sp.GetRequiredService<IPayrollService>().ValidateAsync(run.Id, new WorkflowNoteRequest("Validé"))));

        // 27. Paie validée = verrouillée : ni recalcul, ni donnée de la période, ni modification directe.
        (await Act(hr, sp => sp.GetRequiredService<IPayrollService>().CalculateAsync(sep.Id))).Error!.Code.Should().Be("payroll.run_locked");
        (await Act(employeeUser, sp => sp.GetRequiredService<IHrTimeService>().RequestLeaveAsync(
            new RequestLeaveRequest(e.Id, "unpaid", new DateOnly(2026, 9, 21), new DateOnly(2026, 9, 21), null, null, "Tardif"))))
            .Error!.Code.Should().Be("payroll.period_locked");
        var tamper = () => Exec($"UPDATE nexora.payroll_item SET net_amount = net_amount + 1, gross_amount = gross_amount + 1 WHERE id = '{item.Id}'");
        await tamper.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*verrouillée*");

        var approved = Ok(await Act(Direction, sp => sp.GetRequiredService<IPayrollService>().ApproveAsync(run.Id, new WorkflowNoteRequest("OK"))));
        approved.JournalEntryNumber.Should().NotBeNullOrEmpty();
        var afterSep = Ok(await Act(hr, sp => advances(sp).GetAsync(advance.Id)));
        afterSep.Balance.Should().Be(10_000m);
        afterSep.Status.Should().Be("in_repayment");
        (await LedgerBalance(e.Id, "4250")).Should().Be(10_000m, "la récupération diminue la créance d'avance (§32)");

        // 26. Double envoi du paiement : une instruction = un paiement.
        var op = Guid.NewGuid();
        var first = await Pay(chief, run.Id, op);
        first.Paid.Should().Be(1);
        first.RunStatus.Should().Be("paid");
        var replay = await Pay(chief, run.Id, op);
        replay.Paid.Should().Be(0);
        replay.AlreadyPaid.Should().Be(1);
        var another = await Pay(chief, run.Id, Guid.NewGuid());
        another.Paid.Should().Be(0);
        (await Scalar<long>($"SELECT count(*) FROM nexora.salary_payment WHERE payroll_item_id = '{item.Id}'")).Should().Be(1);

        // 29. Bulletin figé, identifiant unique, empreinte vérifiable, vrai PDF.
        var slip = Ok(await Act(hr, sp => sp.GetRequiredService<IPayrollService>().PayslipsForRunAsync(run.Id))).Single(p => p.EmployeeId == e.Id);
        slip.PayslipNumber.Should().StartWith("BUL-");
        slip.Net.Should().Be(50_000m);
        slip.PaymentReference.Should().StartWith("VSAL-");
        Ok(await Act(hr, sp => sp.GetRequiredService<IPayrollService>().VerifyPayslipAsync(slip.Id))).IntegrityOk.Should().BeTrue();
        var pdf = Ok(await Act(employeeUser, sp => sp.GetRequiredService<IPayrollService>().PayslipFileAsync(slip.Id, "pdf")));
        System.Text.Encoding.ASCII.GetString(pdf.Content[..4]).Should().Be("%PDF");
        var snapshot = await Scalar<string>($"SELECT snapshot::text FROM nexora.payslip WHERE id = '{slip.Id}'");
        snapshot.Should().Contain("\"advanceRecovered\": 10000").And.Contain("\"remainingAdvanceBalance\": 10000");
        var slipTamper = () => Exec($"UPDATE nexora.payslip SET snapshot = '{{}}'::jsonb WHERE id = '{slip.Id}'");
        await slipTamper.Should().ThrowAsync<Npgsql.PostgresException>();

        // 30. Rapprochement RH ↔ Finance.
        var recon = Ok(await Act(chief, sp => sp.GetRequiredService<IPayrollService>().ReconcileAsync(run.Id)));
        recon.Reconciled.Should().BeTrue(string.Join(" ; ", recon.Lines.Where(l => l.Difference != 0).Select(l => $"{l.Key} {l.Difference}")));
        recon.Employees.Single(x => x.EmployeeId == e.Id).Should().Match<ReconciliationEmployeeDto>(x => x.Paid == 50_000m && x.LedgerPayable == 0m);
        Ok(await Act(chief, sp => sp.GetRequiredService<IPayrollService>().CloseAsync(run.Id, new WorkflowNoteRequest(null)))).Status.Should().Be("closed");

        // 20. Paie d'octobre : la seconde échéance solde l'avance.
        var oct = await Period(hr, "monthly", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var octRun = await Calculate(hr, oct.Id);
        ItemOf(octRun, e.Id).AdvanceRecovery.Should().Be(10_000m);
        await ApproveCycle(octRun.Id, hr, chief, Direction);
        // Paies impayées : net, déjà payé, reste à payer (audit 2026-10 éd. 2 : les paiements n'étaient pas déduits).
        var unpaid = Ok(await Act(Direction, sp => sp.GetRequiredService<IPayrollReports>().ReportAsync("unpaid",
            new PayrollReportQuery(null, null, null, null, null))));
        unpaid.Columns.Select(c => c.Key).Should().ContainInOrder("net", "paid", "remaining");
        var line = unpaid.Rows.Single(r => Equals(r[0], octRun.RunNumber));
        line[5].Should().Be(0m);
        line[6].Should().Be(ItemOf(octRun, e.Id).Net);
        (await Pay(chief, octRun.Id, Guid.NewGuid())).Paid.Should().Be(1);
        Ok(await Act(Direction, sp => sp.GetRequiredService<IPayrollReports>().ReportAsync("unpaid",
            new PayrollReportQuery(null, null, null, null, null)))).Rows.Should().NotContain(r => Equals(r[0], octRun.RunNumber));
        var settled = Ok(await Act(hr, sp => advances(sp).HistoryAsync(advance.Id)));
        settled.Advance.Status.Should().Be("settled");
        settled.Advance.Balance.Should().Be(0m);
        settled.Repayments.Should().HaveCount(2).And.OnlyContain(r => r.Source == "payroll" && r.Amount == 10_000m && r.RunNumber != null);
        settled.Events.Select(x => x.EventType).Should().ContainInOrder("requested", "approved", "paid", "schedule_set", "recovered_payroll", "recovered_payroll", "settled");
        (await LedgerBalance(e.Id, "4250")).Should().Be(0m);
        Ok(await Act(chief, sp => sp.GetRequiredService<IPayrollService>().ReconcileAsync(octRun.Id))).Reconciled.Should().BeTrue();

        // Historique financier de l'employé (§26) et rapports exportables (§27).
        var history = Ok(await Act(employeeUser, sp => sp.GetRequiredService<IPayrollReports>().EmployeeHistoryAsync(e.Id)));
        history.Select(h => h.Type).Should().Contain(["salary_calculated", "salary_paid", "advance_paid", "advance_recovered_payroll", "payslip"]);
        var xlsx = Ok(await Act(Direction, sp => sp.GetRequiredService<IPayrollReports>().ExportAsync("payroll_mass",
            new PayrollReportQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), null, null, null), "xlsx")));
        System.Text.Encoding.ASCII.GetString(xlsx.Content[..2]).Should().Be("PK");
    }

    // ---- §16 + §37 tests 21, 22 : plusieurs avances séparées, jamais au-delà du solde ; frais remboursés ---------------
    [Fact]
    public async Task Several_advances_stay_separate_recovery_never_exceeds_balance_and_expense_claims_are_reimbursed()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var employeeUser = await UserWithRole("employee");
        var cashier = await UserWithRole("cashier");
        var jan5 = new DateOnly(2026, 1, 5);
        // Hebdomadaire, 52 000 / mois → 12 000 par semaine.
        var e = await Hire(hr, "Hebdo", jan5, Contract(jan5, null, 52_000m, "weekly", "permanent"));
        await Link(e.Id, employeeUser);
        var advances = (IServiceProvider sp) => sp.GetRequiredService<ISalaryAdvanceService>();
        var dec7 = new DateOnly(2026, 12, 7);

        (await Act(hr, sp => advances(sp).RequestAsync(e.Id, new RequestAdvanceRequest("salary_advance", 30_000m, "Trop", null, null, null, null, null))))
            .Error!.Code.Should().Be("payroll.advance_policy", "au-delà de 50 % du salaire mensuel");
        var a = Ok(await Act(hr, sp => advances(sp).RequestAsync(e.Id, new RequestAdvanceRequest("salary_advance", 5_000m, "Loyer", "next_payroll", null, null, dec7, null))));
        var b = Ok(await Act(hr, sp => advances(sp).RequestAsync(e.Id, new RequestAdvanceRequest("acompte", 3_000m, "Acompte", null, null, null, dec7, null))));
        b.AdvanceNumber.Should().StartWith("ACP-");
        foreach (var x in new[] { a, b })
        {
            (await Act(hr, sp => advances(sp).ApproveAsync(x.Id, new ApproveAdvanceRequest(null, null, null, null, null, null))))
                .Error!.Code.Should().Be("payroll.self_approval");
            Ok(await Act(Direction, sp => advances(sp).ApproveAsync(x.Id, new ApproveAdvanceRequest(null, null, null, null, null, null))));
            Ok(await Act(chief, sp => advances(sp).PayAsync(x.Id, new PayAdvanceRequest(CashAccount, "cash", null, null, null, Guid.NewGuid()))));
        }

        // L'assistant répond avec les soldes tenus par le serveur ; le caissier n'y a pas droit.
        var answer = await Act(Direction, sp => sp.GetRequiredService<IAskNexora>().AskAsync(new AskRequest("Combien reste-t-il d'avances à récupérer ?", null)));
        answer.Intent.Should().Be("payroll_outstanding_advances");
        answer.Facts.Should().Contain(k => k.Key == "payroll_advances_outstanding" && k.Value >= 8_000m);
        (await Act(cashier, sp => sp.GetRequiredService<IAskNexora>().AskAsync(new AskRequest("Combien reste-t-il d'avances à récupérer ?", null))))
            .Intent.Should().Be("forbidden");

        // 22. Récupération supérieure au solde : refusée par le service ET par la base.
        (await Act(chief, sp => advances(sp).RepayAsync(a.Id, new RepayAdvanceRequest(6_000m, CashAccount, "cash", null, null, Guid.NewGuid()))))
            .Error!.Code.Should().Be("payroll.repayment_exceeds_balance");
        var forced = () => Exec($"""
            INSERT INTO nexora.advance_repayment (company_id, advance_id, source, amount, currency_id, financial_account_id, operation_uuid)
            SELECT company_id, id, 'manual', 6000, currency_id, '{CashAccount}', gen_random_uuid() FROM nexora.employee_advance WHERE id = '{a.Id}'
            """);
        await forced.Should().ThrowAsync<Npgsql.PostgresException>().WithMessage("*supérieure au solde*");
        Ok(await Act(chief, sp => advances(sp).RepayAsync(a.Id, new RepayAdvanceRequest(1_000m, CashAccount, "cash", "Remise espèces", null, Guid.NewGuid()))))
            .Balance.Should().Be(4_000m);

        // Note de frais de l'employé, validée par la RH : dette de l'entreprise, remboursée par la paie.
        var claim = Ok(await Act(employeeUser, sp => sp.GetRequiredService<IExpenseClaimService>().SubmitAsync(new SubmitExpenseClaimRequest(
            e.Id, new DateOnly(2026, 9, 20), "transport", "Taxi livraison client", 1_500m, null, "payroll", null,
            new UploadEmployeeDocumentRequest("expense_receipt", "Reçu taxi", "recu.txt", "text/plain", Convert.ToBase64String("Taxi 1500"u8.ToArray()))))));
        claim.ReceiptDocumentId.Should().NotBeNull();
        Ok(await Act(hr, sp => sp.GetRequiredService<IExpenseClaimService>().ApproveAsync(claim.Id, new ApproveExpenseClaimRequest(null, null))))
            .ApprovalJournalEntryId.Should().NotBeNull();
        (await LedgerBalance(e.Id, "4286")).Should().Be(-1_500m, "dette envers l'employé (crédit)");

        // 21. Paie hebdomadaire : chaque avance a sa propre ligne de retenue.
        var run = await Calculate(hr, (await Period(hr, "weekly", dec7, new DateOnly(2026, 12, 13))).Id);
        var item = ItemOf(run, e.Id);
        var detail = await Detail(hr, item.Id);
        detail.BaseAmount.Should().Be(12_000m);
        detail.Deductions.Where(d => d.Category == "advance").Select(d => (d.SourceId, d.Amount))
            .Should().BeEquivalentTo([((Guid?)b.Id, 3_000m), ((Guid?)a.Id, 4_000m)]);
        detail.Reimbursements.Should().Be(1_500m);
        item.Net.Should().Be(6_500m);
        await ApproveCycle(run.Id, hr, chief, Direction);
        (await Pay(chief, run.Id, Guid.NewGuid())).Paid.Should().Be(1);
        Ok(await Act(hr, sp => advances(sp).GetAsync(a.Id))).Status.Should().Be("settled");
        Ok(await Act(hr, sp => advances(sp).GetAsync(b.Id))).Status.Should().Be("settled");
        (await Act(hr, sp => sp.GetRequiredService<IExpenseClaimService>().ListAsync(e.Id, null))).Single().Status.Should().Be("paid");
        (await LedgerBalance(e.Id, "4286")).Should().Be(0m);
        Ok(await Act(chief, sp => sp.GetRequiredService<IPayrollService>().ReconcileAsync(run.Id))).Reconciled.Should().BeTrue();
        (await Act(Direction, sp => sp.GetRequiredService<IPayrollAlerts>().ScanAsync())).Should().BeGreaterThanOrEqualTo(0);
    }

    // ---- §5 + §37 tests 23, 24 : l'employé ne voit que lui ; le caissier ne voit aucun salaire ------------------------
    [Fact]
    public async Task Employee_sees_only_own_payslips_and_cashier_sees_no_salary()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var employeeUser = await UserWithRole("employee");
        var cashier = await UserWithRole("cashier");
        var jul1 = new DateOnly(2026, 7, 1);
        var jul31 = new DateOnly(2026, 7, 31);
        var mine = await Hire(hr, "Moi", jul1, Contract(jul1, jul31, 40_000m));
        var other = await Hire(hr, "Autre", jul1, Contract(jul1, jul31, 55_000m));
        await Link(mine.Id, employeeUser);
        var run = await Calculate(hr, (await Period(hr, "monthly", jul1, jul31)).Id);
        await ApproveCycle(run.Id, hr, chief, Direction);
        (await Pay(chief, run.Id, Guid.NewGuid())).Paid.Should().Be(2);
        var slips = Ok(await Act(hr, sp => sp.GetRequiredService<IPayrollService>().PayslipsForRunAsync(run.Id)));
        var otherSlip = slips.Single(s => s.EmployeeId == other.Id);

        var payroll = (IServiceProvider sp) => sp.GetRequiredService<IPayrollService>();
        Ok(await Act(employeeUser, sp => payroll(sp).PayslipsForEmployeeAsync(mine.Id))).Should().ContainSingle();
        (await Act(employeeUser, sp => payroll(sp).PayslipsForEmployeeAsync(other.Id))).Error!.Kind.Should().Be(ErrorKind.NotFound);
        (await Act(employeeUser, sp => payroll(sp).PayslipFileAsync(otherSlip.Id, "pdf"))).Error!.Kind.Should().Be(ErrorKind.NotFound,
            "un identifiant de bulletin deviné ne donne jamais accès au bulletin d'un autre (§5)");
        (await Act(employeeUser, sp => payroll(sp).ItemAsync(ItemOf(run, other.Id).Id))).Error!.Kind.Should().Be(ErrorKind.NotFound);
        (await Act(employeeUser, sp => sp.GetRequiredService<IEmployeeService>().SalaryHistoryAsync(other.Id))).Error!.Kind.Should().Be(ErrorKind.NotFound);
        Ok(await Act(employeeUser, sp => sp.GetRequiredService<IEmployeeService>().SalaryHistoryAsync(mine.Id))).Single().Amount.Should().Be(40_000m);
        Ok(await Act(employeeUser, sp => sp.GetRequiredService<IHrSelfService>().ProfileAsync())).BaseSalary.Should().Be(40_000m);

        // Caissier : aucune donnée salariale, par aucun chemin.
        (await Act(cashier, sp => sp.GetRequiredService<IEmployeeService>().ListAsync(null, null, null))).Should().BeEmpty();
        (await Act(cashier, sp => sp.GetRequiredService<IEmployeeService>().GetAsync(other.Id))).Should().BeNull();
        (await Act(cashier, sp => payroll(sp).PeriodsAsync(null, null))).Should().BeEmpty();
        (await Act(cashier, sp => payroll(sp).RunAsync(run.Id))).Error!.Kind.Should().Be(ErrorKind.NotFound);
        (await Act(cashier, sp => payroll(sp).PayslipFileAsync(otherSlip.Id, "pdf"))).Error!.Kind.Should().Be(ErrorKind.NotFound);
        (await Act(cashier, sp => sp.GetRequiredService<ISalaryAdvanceService>().ListAsync(null, null, null))).Should().BeEmpty();
        (await Act(cashier, sp => sp.GetRequiredService<IHrSelfService>().ProfileAsync())).Error!.Code.Should().Be("hr.no_employee_record");
    }

    // ---- §37 test 25 : isolation entre deux entreprises (service + RLS de la base) ------------------------------------
    [Fact]
    public async Task Employees_of_another_company_are_invisible_to_the_service_and_to_the_database()
    {
        var foreign = await Hire(Direction, "Restaurant", new DateOnly(2026, 2, 1),
            Contract(new DateOnly(2026, 2, 1), null, 30_000m, "biweekly", "permanent"), company: Restaurant);
        (await Act(Direction, sp => sp.GetRequiredService<IEmployeeService>().GetAsync(foreign.Id))).Should().BeNull();
        (await Act(Direction, sp => sp.GetRequiredService<IEmployeeService>().ListAsync(null, null, null))).Should().NotContain(x => x.Id == foreign.Id);
        (await Act(Direction, sp => sp.GetRequiredService<IEmployeeService>().SalaryHistoryAsync(foreign.Id))).Error!.Kind.Should().Be(ErrorKind.NotFound);

        await using var appRole = fx.CreateAppRoleProvider();
        await using var s = appRole.CreateAsyncScope();
        As(s.ServiceProvider, Direction, Company);
        var db = s.ServiceProvider.GetRequiredService<NexoraDbContext>();
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM nexora.employee WHERE id = {foreign.Id}").FirstAsync())
            .Should().Be(0, "la politique RLS cache le dossier d'une autre entreprise, même en SQL direct");
        (await db.Database.SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM nexora.salary_history WHERE employee_id = {foreign.Id}").FirstAsync())
            .Should().Be(0);
    }

    // ---- §36 + §37 test 28 : régularisation après clôture, reliée à la paie d'origine ------------------------------------
    [Fact]
    public async Task A_closed_payroll_is_corrected_only_through_a_linked_approved_regularization()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var feb1 = new DateOnly(2026, 2, 1);
        var feb28 = new DateOnly(2026, 2, 28);
        var e = await Hire(hr, "Régul", feb1, Contract(feb1, feb28, 40_000m));
        var febPeriod = await Period(hr, "monthly", feb1, feb28);
        var run = await Calculate(hr, febPeriod.Id);
        await ApproveCycle(run.Id, hr, chief, Direction);
        await Pay(chief, run.Id, Guid.NewGuid());
        Ok(await Act(chief, sp => sp.GetRequiredService<IPayrollService>().CloseAsync(run.Id, new WorkflowNoteRequest("Clôture février"))))
            .Status.Should().Be("closed");
        var original = ItemOf(run, e.Id);

        var reopen = () => Exec($"UPDATE nexora.payroll_run SET status = 'calculated' WHERE id = '{run.Id}'");
        await reopen.Should().ThrowAsync<Npgsql.PostgresException>();
        (await Act(hr, sp => sp.GetRequiredService<IPayrollService>().CancelAsync(run.Id, new CancelRequest("Erreur de prime"))))
            .Error!.Code.Should().Be("payroll.run_locked");

        var adjustments = (IServiceProvider sp) => sp.GetRequiredService<IPayrollAdjustmentService>();
        var adj = Ok(await Act(chief, sp => adjustments(sp).RequestAsync(new RequestAdjustmentRequest(original.Id, "earning", 5_000m, true,
            "Prime de février oubliée", null))));
        adj.OriginalRunNumber.Should().Be(run.RunNumber);
        (await Act(hr, sp => adjustments(sp).ApproveAsync(adj.Id, new DecisionRequest(null)))).Error!.Code.Should().Be("payroll.forbidden");
        (await Act(chief, sp => adjustments(sp).ApproveAsync(adj.Id, new DecisionRequest(null)))).Error!.Code.Should().Be("payroll.self_approval");
        Ok(await Act(Direction, sp => adjustments(sp).ApproveAsync(adj.Id, new DecisionRequest("Justifié")))).Status.Should().Be("approved");

        // Paie hors cycle limitée à cet employé : la régularisation y est intégrée avec sa référence.
        // Novembre : aucun autre scénario (consolidation de mars comprise) ne lit les écritures de ce mois.
        var offCycle = await Period(hr, "monthly", new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 6), "off_cycle", [e.Id]);
        var fix = await Calculate(hr, offCycle.Id);
        fix.Items.Should().ContainSingle();
        var fixItem = ItemOf(fix, e.Id);
        fixItem.Gross.Should().Be(5_000m);
        (await Detail(hr, fixItem.Id)).Earnings.Single().Label.Should().Contain(adj.AdjustmentNumber);
        await ApproveCycle(fix.Id, hr, chief, Direction);
        var applied = (await Act(hr, sp => adjustments(sp).ListAsync(e.Id, null))).Single();
        applied.Status.Should().Be("applied");
        applied.AppliedRunNumber.Should().Be(fix.RunNumber);
        (await Scalar<decimal>($"SELECT net_amount FROM nexora.payroll_item WHERE id = '{original.Id}'")).Should().Be(original.Net,
            "la paie d'origine n'est jamais modifiée silencieusement (§36)");
    }

    // ---- §22-§23, §39 : séparation des tâches, anomalies bloquantes, équipe du Manager ---------------------------------
    [Fact]
    public async Task Segregation_of_duties_blocking_anomalies_and_manager_team_scope_are_enforced()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var managerUser = await UserWithRole("manager");
        var aug1 = new DateOnly(2026, 8, 1);
        var aug31 = new DateOnly(2026, 8, 31);
        var manager = await Hire(hr, "Chef", aug1, Contract(aug1, null, 80_000m, "biweekly", "permanent"));
        await Link(manager.Id, managerUser);
        var teamMember = await Hire(hr, "Équipe", aug1, Contract(aug1, aug31, 45_000m), manager: manager.Id);
        var outsider = await Hire(hr, "Autre", aug1, Contract(aug1, aug31, null));

        // Le Manager ne voit que son équipe et ne décide que pour elle.
        var visible = await Act(managerUser, sp => sp.GetRequiredService<IEmployeeService>().ListAsync(null, null, null));
        visible.Select(x => x.Id).Should().Contain(teamMember.Id).And.NotContain(outsider.Id);
        visible.Single(x => x.Id == teamMember.Id).CurrentSalary.Should().BeNull("le Manager ne voit pas le salaire de son équipe (§5)");
        // La recherche globale applique le même périmètre (audit 2026-10 éd. 2 : elle cherchait dans toute l'entreprise).
        async Task<List<Guid>> Found(string name) => (await Act(managerUser, sp => sp
                .GetRequiredService<Nexora.Application.Experience.IGlobalSearchService>().SearchAsync(name, 10)))
            .Groups.Where(g => g.Kind == "employee").SelectMany(g => g.Hits).Select(h => h.Id).ToList();
        (await Found(teamMember.LastName)).Should().Contain(teamMember.Id);
        (await Found(outsider.LastName)).Should().NotContain(outsider.Id);
        var leave = Ok(await Act(hr, sp => sp.GetRequiredService<IHrTimeService>().RequestLeaveAsync(
            new RequestLeaveRequest(outsider.Id, "annual", new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4), null, null, null))));
        (await Act(managerUser, sp => sp.GetRequiredService<IHrTimeService>().DecideLeaveAsync(leave.Id, true, new DecisionRequest(null))))
            .Error!.Kind.Should().Be(ErrorKind.NotFound);

        var run = await Calculate(hr, (await Period(hr, "monthly", aug1, aug31)).Id);
        var blocked = ItemOf(run, outsider.Id);
        blocked.Anomalies.Should().Contain(a => a.Code == "no_salary" && a.Severity == "blocking");
        var payroll = (IServiceProvider sp) => sp.GetRequiredService<IPayrollService>();
        (await Act(hr, sp => payroll(sp).ReviewAsync(run.Id, new WorkflowNoteRequest(null, true)))).Error!.Code.Should().Be("payroll.blocking_anomalies");
        Ok(await Act(hr, sp => payroll(sp).ExcludeItemAsync(blocked.Id, new ExcludeItemRequest("Salaire non encore fixé"))));
        (await Act(hr, sp => payroll(sp).ReviewAsync(run.Id, new WorkflowNoteRequest(null, false)))).Error!.Code.Should().Be("payroll.warnings_not_acknowledged");
        Ok(await Act(hr, sp => payroll(sp).ReviewAsync(run.Id, new WorkflowNoteRequest("Contrôle RH", true))));
        (await Act(hr, sp => payroll(sp).ValidateAsync(run.Id, new WorkflowNoteRequest(null)))).Error!.Code.Should().Be("payroll.self_approval");
        Ok(await Act(chief, sp => payroll(sp).ValidateAsync(run.Id, new WorkflowNoteRequest(null))));
        (await Act(chief, sp => payroll(sp).ApproveAsync(run.Id, new WorkflowNoteRequest(null)))).Error!.Code.Should().Be("payroll.self_approval");
        (await Act(hr, sp => payroll(sp).ApproveAsync(run.Id, new WorkflowNoteRequest(null)))).Error!.Code.Should().Be("payroll.self_approval");
        var approved = Ok(await Act(Direction, sp => payroll(sp).ApproveAsync(run.Id, new WorkflowNoteRequest(null))));
        approved.Items.Single(i => i.EmployeeId == outsider.Id).Status.Should().Be("excluded");
        (await Scalar<string>($"SELECT approved_by::text FROM nexora.payroll_run WHERE id = '{run.Id}'")).Should().Be(Direction.ToString(),
            "les approbateurs sont identifiés (§22)");
        (await Scalar<long>($"SELECT count(*) FROM nexora.audit_log WHERE entity_type = 'payroll_run' AND entity_id = '{run.Id}'"))
            .Should().BeGreaterThanOrEqualTo(4);
    }

    // ---- Audit 2026-10 éd. 2 : date d'entrée, document remplacé, solde de caisse, date de versement ------------------
    [Fact]
    public async Task Hire_date_documents_cash_balance_and_payment_date_are_controlled()
    {
        var hr = await UserWithRole("hr_manager");
        var chief = await UserWithRole("chief_accountant");
        var apr1 = new DateOnly(2026, 4, 1);
        var e = await Hire(hr, "Contrôles", apr1, Contract(apr1, null, 60_000m, "monthly", "permanent"));

        // La date d'entrée ne passe pas après le début du contrat.
        (await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().UpdateAsync(e.Id, new SaveEmployeeRequest(
            EmployeeNumber: null, FirstName: e.FirstName, LastName: e.LastName, BirthDate: e.BirthDate, Phone: e.Phone, Email: null,
            Address: null, City: e.City, EmergencyContactName: null, EmergencyContactPhone: null, JobTitle: e.JobTitle, DepartmentId: null,
            ManagerEmployeeId: null, EstablishmentId: null, HireDate: apr1.AddDays(10), PaymentMethod: "cash", BankName: null,
            BankAccount: null, MobileMoney: null, TaxId: null, SocialId: null, TaxParameters: null, Notes: null))))
            .Error!.Code.Should().Be("hr.hire_date");

        // Le document remplacé appartient au même dossier.
        (await Act(hr, sp => sp.GetRequiredService<IEmployeeService>().UploadDocumentAsync(e.Id, new UploadEmployeeDocumentRequest(
            "contract", "Avenant", "avenant.txt", "text/plain", Convert.ToBase64String("Avenant"u8.ToArray()), SupersedesId: Guid.NewGuid()))))
            .Error!.Code.Should().Be("hr.document_supersedes");

        // Une caisse vide ne verse pas une avance ; une date future est refusée.
        Guid emptyCash;
        await using (var s = Scope())
        {
            As(s.ServiceProvider, Direction);
            emptyCash = (await s.ServiceProvider.GetRequiredService<Nexora.Application.Finance.ITreasuryManagementService>().CreateAccountAsync(
                new Nexora.Contracts.Finance.SaveFinancialAccountRequest($"Caisse vide {Guid.NewGuid():N}"[..20], "cash", "HTG", null, null))).Value;
        }
        var advances = (IServiceProvider sp) => sp.GetRequiredService<ISalaryAdvanceService>();
        var advance = Ok(await Act(hr, sp => advances(sp).RequestAsync(e.Id, new RequestAdvanceRequest("salary_advance", 4_000m, "Loyer", "next_payroll", null, null, null, null))));
        Ok(await Act(Direction, sp => advances(sp).ApproveAsync(advance.Id, new ApproveAdvanceRequest(null, null, null, null, null, null))));
        (await Act(chief, sp => advances(sp).PayAsync(advance.Id, new PayAdvanceRequest(emptyCash, "cash", null, null, null, Guid.NewGuid()))))
            .Error!.Code.Should().Be("payroll.insufficient_balance");
        (await Act(chief, sp => advances(sp).PayAsync(advance.Id, new PayAdvanceRequest(CashAccount, "cash", null, null,
                DateOnly.FromDateTime(DateTime.UtcNow).AddDays(5), Guid.NewGuid()))))
            .Error!.Code.Should().Be("payroll.paid_on");
        Ok(await Act(chief, sp => advances(sp).PayAsync(advance.Id, new PayAdvanceRequest(CashAccount, "cash", null, null, null, Guid.NewGuid()))));
    }

    // ---- Audit 2026-10 éd. 2 : deux demandes d'absence simultanées sur les mêmes dates — une seule passe ----------
    [Fact]
    public async Task Two_simultaneous_leave_requests_on_the_same_dates_record_one()
    {
        var hr = await UserWithRole("hr_manager");
        var may1 = new DateOnly(2026, 5, 1);
        var e = await Hire(hr, "Absences", may1, Contract(may1, null, 40_000m, "monthly", "permanent"));
        var start = new TaskCompletionSource();
        async Task<bool> Request()
        {
            await start.Task;
            var r = await Act(hr, sp => sp.GetRequiredService<IHrTimeService>().RequestLeaveAsync(
                new RequestLeaveRequest(e.Id, "annual", new DateOnly(2026, 5, 18), new DateOnly(2026, 5, 20), null, null, null)));
            if (!r.IsSuccess) r.Error!.Code.Should().Be("hr.leave_overlap");
            return r.IsSuccess;
        }
        var requests = Enumerable.Range(0, 4).Select(_ => Request()).ToList();
        start.SetResult();
        (await Task.WhenAll(requests)).Count(ok => ok).Should().Be(1);
        (await Scalar<long>($"SELECT COUNT(*) FROM nexora.leave_record WHERE employee_id = '{e.Id}'")).Should().Be(1);
    }
}
