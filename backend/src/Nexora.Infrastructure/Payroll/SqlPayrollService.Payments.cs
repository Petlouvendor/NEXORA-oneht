using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Finance.Events;
using Nexora.Application.Printing;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Contracts.Printing;
using Nexora.Domain.Common;
using Nexora.Domain.Finance;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Paiement des salaires (§24) — une instruction = un paiement (§39), sortie de trésorerie et écriture (§32) —,
/// bulletins figés (§25) et rapprochement RH ↔ Finance (§32, §37 test 30).
/// </summary>
public sealed partial class SqlPayrollService
{
    // ================================================================== Paiements

    private sealed record PaymentRow(
        Guid Id, string PaymentNumber, Guid PayrollItemId, Guid RunId, string RunNumber, Guid EmployeeId, string EmployeeName, decimal Amount,
        string Currency, string PaymentMethod, Guid FinancialAccountId, string FinancialAccountName, string? Reference, string? BeneficiaryAccount,
        DateTimeOffset PaidAt, string Status, Guid JournalEntryId, string? CancelReason, DateTimeOffset? CancelledAt, Guid? PayslipId,
        string? PayslipNumber, Guid OperationUuid);

    private IQueryable<PaymentRow> PaymentQuery() => db.Database.SqlQuery<PaymentRow>($"""
        SELECT sp.id AS "Id", sp.payment_number AS "PaymentNumber", sp.payroll_item_id AS "PayrollItemId", sp.run_id AS "RunId",
               r.run_number AS "RunNumber", sp.employee_id AS "EmployeeId", e.first_name || ' ' || e.last_name AS "EmployeeName",
               sp.amount AS "Amount", cur.code AS "Currency", sp.payment_method AS "PaymentMethod", sp.financial_account_id AS "FinancialAccountId",
               fa.name AS "FinancialAccountName", sp.reference AS "Reference", sp.beneficiary_account AS "BeneficiaryAccount",
               sp.paid_at AS "PaidAt", sp.status AS "Status", sp.journal_entry_id AS "JournalEntryId", sp.cancel_reason AS "CancelReason",
               sp.cancelled_at AS "CancelledAt", ps.id AS "PayslipId", ps.payslip_number AS "PayslipNumber", sp.operation_uuid AS "OperationUuid"
        FROM nexora.salary_payment sp
        JOIN nexora.payroll_run r ON r.id = sp.run_id
        JOIN nexora.employee e ON e.id = sp.employee_id
        JOIN nexora.currency cur ON cur.id = sp.currency_id
        JOIN nexora.financial_account fa ON fa.id = sp.financial_account_id
        LEFT JOIN nexora.payslip ps ON ps.salary_payment_id = sp.id
        WHERE sp.company_id = {Company}
        """);

    private static SalaryPaymentDto ToDto(PaymentRow r) => new(r.Id, r.PaymentNumber, r.PayrollItemId, r.RunId, r.RunNumber, r.EmployeeId,
        r.EmployeeName, r.Amount, r.Currency, r.PaymentMethod, r.FinancialAccountId, r.FinancialAccountName, r.Reference, r.BeneficiaryAccount,
        r.PaidAt, r.Status, r.JournalEntryId, r.CancelReason, r.CancelledAt, r.PayslipId, r.PayslipNumber);

    public async Task<Result<IReadOnlyList<SalaryPaymentDto>>> PaymentsAsync(Guid runId, CancellationToken ct = default)
    {
        if (!await CanReadPayrollAsync(ct)) return RunNotFound();
        return Result.Success<IReadOnlyList<SalaryPaymentDto>>(
            (await PaymentQuery().Where(p => p.RunId == runId).OrderBy(p => p.EmployeeName).ToListAsync(ct)).Select(ToDto).ToList());
    }

    private sealed record PayItemRow(
        Guid Id, Guid EmployeeId, string EmployeeName, decimal Net, string Currency, Guid CurrencyId, string Status, string PaymentMethod,
        bool HasBank, bool HasMobile, string? BankLast4, string? MobileLast4, Guid? UserId);
    private sealed record AccountRow(Guid Id, string Status, string Currency, string AccountType);

    public async Task<Result<PayRunResultDto>> PayAsync(Guid runId, PayRunRequest r, CancellationToken ct = default)
    {
        if (r.OperationUuid == Guid.Empty) return Error.Validation("payroll.operation_uuid", "Identifiant d'opération obligatoire (anti double paiement).");
        var run = await RunQuery().Where(x => x.Id == runId).FirstOrDefaultAsync(ct);
        if (run is null) return RunNotFound();
        if (run.Status is not ("approved" or "paid"))
            return Error.Conflict("payroll.run_state", "Seule une paie approuvée peut être payée (§22).");
        if (r.PaymentMethod is { } forced && !PayrollRules.PaymentMethods.Contains(forced))
            return Error.Validation("payroll.payment_method", $"Moyen de paiement « {forced} » inconnu.");
        var account = await db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "Id", fa.status AS "Status", cur.code AS "Currency", fa.account_type AS "AccountType"
            FROM nexora.financial_account fa JOIN nexora.currency cur ON cur.id = fa.currency_id
            WHERE fa.id = {r.FinancialAccountId} AND fa.company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (account is null || account.Status != "active")
            return Error.Validation("payroll.financial_account", "Compte de paiement introuvable ou inactif.");

        Guid? sessionAccount = null;
        if (r.CashSessionId is { } sid)
        {
            sessionAccount = await db.Database.SqlQuery<Guid?>($"""
                SELECT cr.financial_account_id AS "Value"
                FROM nexora.cash_session cs JOIN nexora.cash_register cr ON cr.id = cs.cash_register_id
                WHERE cs.id = {sid} AND cs.company_id = {Company} AND cs.status = 'open'
                """).FirstOrDefaultAsync(ct);
            if (sessionAccount is null) return Error.Conflict("payroll.cash_session_closed", "La session de caisse n'est pas ouverte.");
        }

        var ids = r.ItemIds?.Distinct().ToArray();
        var items = await db.Database.SqlQuery<PayItemRow>($"""
            SELECT i.id AS "Id", i.employee_id AS "EmployeeId", i.employee_name AS "EmployeeName", i.net_amount AS "Net", cur.code AS "Currency",
                   i.currency_id AS "CurrencyId", i.status AS "Status", COALESCE(i.payment_method, e.payment_method) AS "PaymentMethod",
                   (e.bank_account_encrypted IS NOT NULL) AS "HasBank", (e.mobile_money_encrypted IS NOT NULL) AS "HasMobile",
                   e.bank_account_last4 AS "BankLast4", e.mobile_money_last4 AS "MobileLast4", e.user_id AS "UserId"
            FROM nexora.payroll_item i
            JOIN nexora.employee e ON e.id = i.employee_id
            JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE i.run_id = {runId} AND i.company_id = {Company} AND i.status IN ('approved','paid') AND i.net_amount > 0
              AND ({ids}::uuid[] IS NULL OR i.id = ANY({ids}::uuid[]))
            ORDER BY i.employee_name
            """).ToListAsync(ct);
        if (ids is { Length: > 0 } && items.Count < ids.Length)
            return Error.NotFound("payroll.item_not_found", "Ligne de paie introuvable, non approuvée ou sans net à payer.");

        var paidAt = r.PaidOn is { } day ? new DateTimeOffset(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero) : clock.UtcNow;
        int paid = 0, alreadyPaid = 0, skipped = 0;
        var errors = new List<string>();
        var done = new List<Guid>();
        var notify = new List<(Guid? User, string Name, decimal Net, string Currency, Guid PaymentId)>();

        foreach (var item in items)
        {
            var operation = PayrollRules.Derive(r.OperationUuid, item.Id);
            // Double envoi (§37 test 26) : la même instruction rejoue le paiement existant, jamais un second.
            var replay = await db.Database.SqlQuery<Guid?>($"""
                SELECT id AS "Value" FROM nexora.salary_payment WHERE operation_uuid = {operation} AND company_id = {Company}
                """).FirstOrDefaultAsync(ct);
            if (replay is { } existing) { alreadyPaid++; done.Add(existing); continue; }
            if (item.Status == "paid") { alreadyPaid++; errors.Add($"{item.EmployeeName} : déjà payé."); continue; }
            if (item.Currency != account.Currency)
            { skipped++; errors.Add($"{item.EmployeeName} : net en {item.Currency}, compte en {account.Currency}."); continue; }
            var method = r.PaymentMethod ?? item.PaymentMethod;
            if (method == "bank_transfer" && !item.HasBank)
            { skipped++; errors.Add($"{item.EmployeeName} : virement impossible sans compte bancaire enregistré (§23)."); continue; }
            if (method == "mobile_money" && !item.HasMobile)
            { skipped++; errors.Add($"{item.EmployeeName} : aucun numéro mobile money enregistré."); continue; }
            var beneficiary = method switch
            {
                "bank_transfer" => PayrollRules.Masked(item.BankLast4),
                "mobile_money" => PayrollRules.Masked(item.MobileLast4),
                _ => null,
            };

            try
            {
                var paymentId = await uow.ExecuteInTransactionAsync(async token =>
                {
                    var id = Guid.NewGuid();
                    if (await PayrollRules.CashShortfallAsync(db, Company, account.Id, item.Net, token) is { } shortfall)
                        throw new PayrollRefusal(shortfall);
                    var number = await PayrollRules.NextNumberAsync(db, Company, "salary_payment", "VSAL-", paidAt.Year, token);
                    var posted = await engine.HandleAsync(new SalaryPaid(Company, operation, paidAt, id, item.EmployeeId,
                        new Money(item.Net, item.Currency), account.Id, number) { UserId = tenant.UserId }, token);
                    if (!posted.IsSuccess) throw new PayrollRefusal(posted.Error!);
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO nexora.salary_payment
                            (id, company_id, payment_number, payroll_item_id, run_id, employee_id, amount, currency_id, payment_method,
                             financial_account_id, cash_session_id, reference, beneficiary_account, paid_at, status, journal_entry_id,
                             operation_uuid, created_by)
                        VALUES ({id}, {Company}, {number}, {item.Id}, {runId}, {item.EmployeeId}, {item.Net}, {item.CurrencyId}, {method},
                                {account.Id}, {r.CashSessionId}, {r.Reference}, {beneficiary}, {paidAt}, 'completed',
                                {posted.Value!.JournalEntryId}, {operation}, {UserId})
                        """, token);
                    var updated = await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.payroll_item SET status = 'paid' WHERE id = {item.Id} AND status = 'approved'
                        """, token);
                    if (updated != 1) throw new PayrollRefusal(Error.Conflict("payroll.already_paid", $"{item.EmployeeName} : payé entre-temps."));
                    await db.Database.ExecuteSqlAsync($"""
                        UPDATE nexora.employee_expense_claim SET status = 'paid', paid_at = {paidAt}, paid_by = {UserId}
                        WHERE payroll_item_id = {item.Id} AND status = 'in_payroll'
                        """, token);
                    if (r.CashSessionId is { } session && sessionAccount == account.Id)
                        await db.Database.ExecuteSqlAsync($"""
                            INSERT INTO nexora.cash_movement
                                (company_id, cash_session_id, movement_type, amount, currency_id, reference_type, reference_id, note, occurred_at, user_id)
                            VALUES ({Company}, {session}, 'payout', {-item.Net}, {item.CurrencyId}, 'salary_payment', {id},
                                    {"Salaire " + item.EmployeeName + " — " + number}, {paidAt}, {UserId})
                            """, token);
                    await IssuePayslipAsync(item.Id, id, token);
                    return id;
                }, ct);
                paid++;
                done.Add(paymentId);
                notify.Add((item.UserId, item.EmployeeName, item.Net, item.Currency, paymentId));
            }
            catch (PayrollRefusal refusal)
            {
                skipped++;
                errors.Add($"{item.EmployeeName} : {refusal.Error.Message}");
            }
            catch (InvalidOperationException ex)
            {
                skipped++;
                errors.Add($"{item.EmployeeName} : {Translate(ex, "payroll.payment_failed").Message}");
                logger.LogWarning(ex, "Paiement de salaire refusé pour la ligne {Item}", item.Id);
            }
        }

        // Toutes les lignes à net positif payées → paie « payée » (§22).
        var status = await RefreshPaidStatusAsync(runId, ct);
        foreach (var n in notify)
            await notifier.ToUsersAsync([n.User], "Salaire versé — bulletin disponible",
                $"{run.PeriodLabel} : {n.Net:N2} {n.Currency} versé(s). Votre bulletin est disponible dans votre espace.",
                "/me/hr?tab=payslips", "salary_payment", n.PaymentId, $"salary_payment:{n.PaymentId}", ct);
        var payments = done.Count == 0 ? [] : (await PaymentQuery().Where(p => done.Contains(p.Id)).ToListAsync(ct)).Select(ToDto).ToList();
        return Result.Success(new PayRunResultDto(paid, alreadyPaid, skipped, payments, errors, status));
    }

    private async Task<string> RefreshPaidStatusAsync(Guid runId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_run SET status = 'paid', paid_at = now()
            WHERE id = {runId} AND status = 'approved'
              AND NOT EXISTS (SELECT 1 FROM nexora.payroll_item i WHERE i.run_id = {runId} AND i.status = 'approved' AND i.net_amount > 0)
            """, ct);
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_period p SET status = 'paid'
            FROM nexora.payroll_run r WHERE r.id = {runId} AND p.id = r.period_id AND r.status = 'paid' AND p.status = 'approved'
            """, ct);
        return await db.Database.SqlQuery<string>($"""SELECT status AS "Value" FROM nexora.payroll_run WHERE id = {runId}""").FirstAsync(ct);
    }

    private sealed record CancelPaymentRow(
        Guid Id, string PaymentNumber, Guid PayrollItemId, Guid RunId, string RunStatus, string Status, decimal Amount, Guid CurrencyId,
        Guid FinancialAccountId, Guid JournalEntryId);

    public async Task<Result<SalaryPaymentDto>> CancelPaymentAsync(Guid paymentId, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            return Error.Validation("payroll.cancel_reason", "Le motif d'annulation est obligatoire (5 caractères au moins).");
        var now = clock.UtcNow;
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var p = await db.Database.SqlQuery<CancelPaymentRow>($"""
                    SELECT sp.id AS "Id", sp.payment_number AS "PaymentNumber", sp.payroll_item_id AS "PayrollItemId", sp.run_id AS "RunId",
                           r.status AS "RunStatus", sp.status AS "Status", sp.amount AS "Amount", sp.currency_id AS "CurrencyId",
                           sp.financial_account_id AS "FinancialAccountId", sp.journal_entry_id AS "JournalEntryId"
                    FROM nexora.salary_payment sp JOIN nexora.payroll_run r ON r.id = sp.run_id
                    WHERE sp.id = {paymentId} AND sp.company_id = {Company}
                    FOR UPDATE OF sp
                    """).FirstOrDefaultAsync(token)
                    ?? throw new PayrollRefusal(Error.NotFound("payroll.payment_not_found", "Paiement introuvable."));
                if (p.Status != "completed") throw new PayrollRefusal(Error.Conflict("payroll.payment_cancelled", "Paiement déjà annulé."));
                if (p.RunStatus == "closed")
                    throw new PayrollRefusal(Error.Conflict("payroll.run_locked", "Paie clôturée : l'annulation d'un paiement passe par une régularisation."));

                // Extourne de l'écriture (jamais supprimée) + retour des fonds sur le compte (§32, §39).
                var reversed = await reversals.ReverseAsync(p.JournalEntryId, $"Annulation du paiement de salaire {p.PaymentNumber} — {r.Reason.Trim()}",
                    await PayrollRules.TodayAsync(db, token), token);
                if (!reversed.IsSuccess) throw new PayrollRefusal(reversed.Error!);
                await financialAccounts.AddTransactionAsync(new FinancialTransaction
                {
                    Id = Guid.NewGuid(), CompanyId = Company, FinancialAccountId = p.FinancialAccountId, TransactionType = "deposit",
                    Amount = p.Amount, CurrencyId = p.CurrencyId, ReferenceType = "salary_payment_cancel", ReferenceId = p.Id,
                    Description = $"Annulation du paiement de salaire {p.PaymentNumber}", OccurredAt = now, Status = "cleared",
                    CreatedBy = tenant.UserId,
                }, token);
                await financialAccounts.AdjustBalanceAsync(p.FinancialAccountId, p.Amount, token);

                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.salary_payment SET status = 'cancelled', cancelled_by = {UserId}, cancelled_at = {now},
                           cancel_reason = {r.Reason.Trim()}, reversal_journal_entry_id = {reversed.Value}
                    WHERE id = {p.Id}
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payslip SET status = 'cancelled', cancelled_at = {now}, cancel_reason = {"Paiement annulé — " + r.Reason.Trim()}
                    WHERE salary_payment_id = {p.Id} AND status = 'issued'
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payroll_item SET status = 'approved' WHERE id = {p.PayrollItemId} AND status = 'paid'
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.employee_expense_claim SET status = 'in_payroll', paid_at = NULL, paid_by = NULL
                    WHERE payroll_item_id = {p.PayrollItemId} AND status = 'paid'
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payroll_run SET status = 'approved' WHERE id = {p.RunId} AND status = 'paid'
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    UPDATE nexora.payroll_period pp SET status = 'approved'
                    FROM nexora.payroll_run r WHERE r.id = {p.RunId} AND pp.id = r.period_id AND pp.status = 'paid'
                    """, token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            return Translate(ex, "payroll.payment_cancel_failed");
        }
        return Result.Success(ToDto(await PaymentQuery().Where(x => x.Id == paymentId).FirstAsync(ct)));
    }

    // ================================================================== Bulletins (§25)

    private sealed record SlipSourceRow(
        Guid ItemId, Guid RunId, Guid PeriodId, Guid EmployeeId, string EmployeeNumber, string EmployeeName, string? JobTitle, string? Department,
        string? ContractType, DateOnly HireDate, string? TaxLast4, string PeriodLabel, DateOnly PeriodStart, DateOnly PeriodEnd, string Currency,
        decimal BaseSalary, string? PayBasis, decimal DaysEmployed, decimal AbsenceDays, decimal Gross, decimal Reimbursements,
        decimal AdvanceRecovery, decimal LoanRecovery, decimal TotalDeductions, decimal Net, decimal EmployerCost, string RunNumber,
        string EmployerName, string? EmployerTaxId, decimal RemainingBalance);
    private sealed record SlipLineRow(string Label, decimal? Base, decimal? Rate, decimal Amount, string Category, string? BorneBy);
    private sealed record SlipPaymentRow(string PaymentNumber, DateTimeOffset PaidAt, string PaymentMethod, string? Reference, string? BeneficiaryAccount);

    /// <summary>Émet le bulletin figé d'une ligne payée (dans la transaction courante). Rejouable : un bulletin actif suffit.</summary>
    private async Task<Guid> IssuePayslipAsync(Guid itemId, Guid? paymentId, CancellationToken ct)
    {
        var existing = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.payslip WHERE payroll_item_id = {itemId} AND status = 'issued'
            """).FirstOrDefaultAsync(ct);
        if (existing is { } id0) return id0;

        var s = await db.Database.SqlQuery<SlipSourceRow>($"""
            SELECT i.id AS "ItemId", i.run_id AS "RunId", i.period_id AS "PeriodId", i.employee_id AS "EmployeeId",
                   i.employee_number AS "EmployeeNumber", i.employee_name AS "EmployeeName", i.job_title AS "JobTitle",
                   i.department_name AS "Department", i.contract_type AS "ContractType", e.hire_date AS "HireDate", e.tax_id_last4 AS "TaxLast4",
                   p.label AS "PeriodLabel", p.start_date AS "PeriodStart", p.end_date AS "PeriodEnd", cur.code AS "Currency",
                   i.base_salary AS "BaseSalary", i.pay_basis AS "PayBasis", i.days_employed AS "DaysEmployed", i.absence_days AS "AbsenceDays",
                   i.gross_amount AS "Gross", i.reimbursement_amount AS "Reimbursements", i.advance_recovery AS "AdvanceRecovery",
                   i.loan_recovery AS "LoanRecovery", i.total_deductions AS "TotalDeductions", i.net_amount AS "Net",
                   i.employer_cost AS "EmployerCost", r.run_number AS "RunNumber", c.name AS "EmployerName", c.tax_identifier AS "EmployerTaxId",
                   COALESCE((SELECT sum(a.balance) FROM nexora.employee_advance a WHERE a.employee_id = i.employee_id
                               AND a.status IN ('paid','in_repayment')), 0) AS "RemainingBalance"
            FROM nexora.payroll_item i
            JOIN nexora.employee e ON e.id = i.employee_id
            JOIN nexora.payroll_period p ON p.id = i.period_id
            JOIN nexora.payroll_run r ON r.id = i.run_id
            JOIN nexora.currency cur ON cur.id = i.currency_id
            JOIN nexora.company c ON c.id = i.company_id
            WHERE i.id = {itemId} AND i.company_id = {Company}
            """).FirstAsync(ct);
        var earnings = await db.Database.SqlQuery<SlipLineRow>($"""
            SELECT label AS "Label", COALESCE(base_amount, quantity) AS "Base", rate AS "Rate", amount AS "Amount", category AS "Category",
                   NULL::text AS "BorneBy"
            FROM nexora.payroll_earning WHERE payroll_item_id = {itemId} AND amount > 0 ORDER BY sort_order
            """).ToListAsync(ct);
        var deductions = await db.Database.SqlQuery<SlipLineRow>($"""
            SELECT label AS "Label", base_amount AS "Base", rate AS "Rate", amount AS "Amount", category AS "Category", borne_by AS "BorneBy"
            FROM nexora.payroll_deduction WHERE payroll_item_id = {itemId} AND amount > 0 ORDER BY sort_order
            """).ToListAsync(ct);
        var payment = paymentId is { } pid ? await db.Database.SqlQuery<SlipPaymentRow>($"""
            SELECT payment_number AS "PaymentNumber", paid_at AS "PaidAt", payment_method AS "PaymentMethod", reference AS "Reference",
                   beneficiary_account AS "BeneficiaryAccount"
            FROM nexora.salary_payment WHERE id = {pid}
            """).FirstOrDefaultAsync(ct) : null;
        var cfg = await config.ReadAsync(ct);
        var number = await PayrollRules.NextNumberAsync(db, Company, "payslip", "BUL-", s.PeriodEnd.Year, ct);
        var issuedAt = clock.UtcNow;
        var snapshot = new PayslipSnapshot(number, issuedAt, s.EmployerName, s.EmployerTaxId, s.EmployeeNumber, s.EmployeeName, s.JobTitle,
            s.Department, s.ContractType, s.HireDate, s.TaxLast4 is null ? null : PayrollRules.Masked(s.TaxLast4), s.PeriodLabel, s.PeriodStart,
            s.PeriodEnd, s.Currency, s.BaseSalary, s.PayBasis, s.DaysEmployed, s.AbsenceDays,
            earnings.Select(l => new PayslipLine(l.Label, l.Base, l.Rate, l.Amount, l.Category)).ToList(), s.Gross, s.Reimbursements,
            deductions.Where(l => l.BorneBy != "employer").Select(l => new PayslipLine(l.Label, l.Base, l.Rate, l.Amount, l.Category)).ToList(),
            deductions.Where(l => l.BorneBy == "employer").Select(l => new PayslipLine(l.Label, l.Base, l.Rate, l.Amount, l.Category)).ToList(),
            s.AdvanceRecovery, s.LoanRecovery, s.RemainingBalance, s.TotalDeductions, s.Net, s.EmployerCost, payment?.PaymentNumber,
            payment?.PaidAt, payment?.PaymentMethod, payment?.Reference, payment?.BeneficiaryAccount, s.RunNumber, cfg.PayslipLegalMentions);

        // Empreinte calculée sur la forme canonique conservée par la base (jsonb) : vérifiable plus tard (§25 identifiant unique).
        var canonical = await db.Database.SqlQuery<string>($"""
            SELECT ({JsonSerializer.Serialize(snapshot, Json)}::jsonb)::text AS "Value"
            """).FirstAsync(ct);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.payslip
                (id, company_id, payslip_number, payroll_item_id, run_id, period_id, employee_id, salary_payment_id, snapshot, sha256_hash, issued_by, issued_at)
            VALUES ({id}, {Company}, {number}, {itemId}, {s.RunId}, {s.PeriodId}, {s.EmployeeId}, {paymentId}, {canonical}::jsonb, {hash},
                    {UserId}, {issuedAt})
            """, ct);
        return id;
    }

    private sealed record PayslipRow(
        Guid Id, string PayslipNumber, Guid PayrollItemId, Guid RunId, Guid EmployeeId, string EmployeeName, string PeriodLabel,
        DateOnly PeriodStart, DateOnly PeriodEnd, string Currency, decimal Gross, decimal Net, string Status, DateTimeOffset IssuedAt,
        string Sha256, Guid? SalaryPaymentId, string? PaymentReference);

    private IQueryable<PayslipRow> PayslipQuery() => db.Database.SqlQuery<PayslipRow>($"""
        SELECT ps.id AS "Id", ps.payslip_number AS "PayslipNumber", ps.payroll_item_id AS "PayrollItemId", ps.run_id AS "RunId",
               ps.employee_id AS "EmployeeId", i.employee_name AS "EmployeeName", p.label AS "PeriodLabel", p.start_date AS "PeriodStart",
               p.end_date AS "PeriodEnd", cur.code AS "Currency", i.gross_amount AS "Gross", i.net_amount AS "Net", ps.status AS "Status",
               ps.issued_at AS "IssuedAt", ps.sha256_hash AS "Sha256", ps.salary_payment_id AS "SalaryPaymentId",
               sp.payment_number AS "PaymentReference"
        FROM nexora.payslip ps
        JOIN nexora.payroll_item i ON i.id = ps.payroll_item_id
        JOIN nexora.payroll_period p ON p.id = ps.period_id
        JOIN nexora.currency cur ON cur.id = i.currency_id
        LEFT JOIN nexora.salary_payment sp ON sp.id = ps.salary_payment_id
        WHERE ps.company_id = {Company}
        """);

    private static PayslipDto ToDto(PayslipRow r) => new(r.Id, r.PayslipNumber, r.PayrollItemId, r.RunId, r.EmployeeId, r.EmployeeName,
        r.PeriodLabel, r.PeriodStart, r.PeriodEnd, r.Currency, r.Gross, r.Net, r.Status, r.IssuedAt, r.Sha256, r.SalaryPaymentId, r.PaymentReference);

    public async Task<Result<IReadOnlyList<PayslipDto>>> PayslipsForRunAsync(Guid runId, CancellationToken ct = default)
    {
        if (!await CanReadPayrollAsync(ct)) return RunNotFound();
        return Result.Success<IReadOnlyList<PayslipDto>>(
            (await PayslipQuery().Where(p => p.RunId == runId).OrderBy(p => p.EmployeeName).ToListAsync(ct)).Select(ToDto).ToList());
    }

    public async Task<Result<IReadOnlyList<PayslipDto>>> GeneratePayslipsAsync(Guid runId, CancellationToken ct = default)
    {
        var run = await RunQuery().Where(x => x.Id == runId).FirstOrDefaultAsync(ct);
        if (run is null) return RunNotFound();
        if (run.Status is not ("approved" or "paid" or "closed"))
            return Error.Conflict("payroll.run_state", "Les bulletins sont émis pour une paie approuvée, au paiement de chaque salaire.");
        var missing = await db.Database.SqlQuery<MissingSlipRow>($"""
            SELECT i.id AS "ItemId", sp.id AS "PaymentId"
            FROM nexora.payroll_item i
            LEFT JOIN nexora.salary_payment sp ON sp.payroll_item_id = i.id AND sp.status = 'completed'
            WHERE i.run_id = {runId} AND ((i.status = 'paid') OR (i.status = 'approved' AND i.net_amount = 0))
              AND NOT EXISTS (SELECT 1 FROM nexora.payslip ps WHERE ps.payroll_item_id = i.id AND ps.status = 'issued')
            """).ToListAsync(ct);
        foreach (var m in missing)
            await uow.ExecuteInTransactionAsync(async token => await IssuePayslipAsync(m.ItemId, m.PaymentId, token), ct);
        return await PayslipsForRunAsync(runId, ct);
    }

    public async Task<Result<IReadOnlyList<PayslipDto>>> PayslipsForEmployeeAsync(Guid employeeId, CancellationToken ct = default)
    {
        var own = await access.HasAsync(Permissions.Payroll.SelfService, ct) && await access.OwnEmployeeIdAsync(ct) == employeeId;
        if (!own && !await access.CanViewSalariesAsync(ct)) return Error.NotFound("hr.employee_not_found", "Employé introuvable.");
        return Result.Success<IReadOnlyList<PayslipDto>>(
            (await PayslipQuery().Where(p => p.EmployeeId == employeeId).OrderByDescending(p => p.PeriodEnd).ThenByDescending(p => p.IssuedAt)
                .ToListAsync(ct)).Select(ToDto).ToList());
    }

    private sealed record MissingSlipRow(Guid ItemId, Guid? PaymentId);

    private sealed record SlipFileRow(Guid EmployeeId, string PayslipNumber, string Snapshot, string Sha256, string Status);

    /// <summary>Bulletin en PDF (A4) — l'employé n'obtient que les siens ; tout autre identifiant répond 404 (§5).</summary>
    public async Task<Result<RenderedFile>> PayslipFileAsync(Guid payslipId, string format, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<SlipFileRow>($"""
            SELECT employee_id AS "EmployeeId", payslip_number AS "PayslipNumber", snapshot::text AS "Snapshot", sha256_hash AS "Sha256",
                   status AS "Status"
            FROM nexora.payslip WHERE id = {payslipId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("payroll.payslip_not_found", "Bulletin introuvable.");
        var own = await access.HasAsync(Permissions.Payroll.SelfService, ct) && await access.OwnEmployeeIdAsync(ct) == row.EmployeeId;
        if (!own && !await access.CanViewSalariesAsync(ct)) return Error.NotFound("payroll.payslip_not_found", "Bulletin introuvable.");
        var fmt = PrintFormats.Normalize(format);
        if (fmt is not (PrintFormats.Pdf or PrintFormats.Xlsx or PrintFormats.Csv))
            return Error.Validation("print.format", "Format de bulletin : pdf, xlsx ou csv.");
        var snapshot = JsonSerializer.Deserialize<PayslipSnapshot>(row.Snapshot, Json)!;
        var document = PayslipDocuments.Build(snapshot, row.Status == "cancelled", row.Sha256);
        var file = await print.RenderAsync(document, fmt, "payslip", $"bulletin-{row.PayslipNumber}", "restricted", payslipId, null, ct);
        return Result.Success(file);
    }

    public async Task<Result<PayslipVerificationDto>> VerifyPayslipAsync(Guid payslipId, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<SlipFileRow>($"""
            SELECT employee_id AS "EmployeeId", payslip_number AS "PayslipNumber", snapshot::text AS "Snapshot", sha256_hash AS "Sha256",
                   status AS "Status"
            FROM nexora.payslip WHERE id = {payslipId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("payroll.payslip_not_found", "Bulletin introuvable.");
        var own = await access.HasAsync(Permissions.Payroll.SelfService, ct) && await access.OwnEmployeeIdAsync(ct) == row.EmployeeId;
        if (!own && !await CanReadPayrollAsync(ct)) return Error.NotFound("payroll.payslip_not_found", "Bulletin introuvable.");
        var computed = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(row.Snapshot)));
        return Result.Success(new PayslipVerificationDto(payslipId, row.PayslipNumber, computed == row.Sha256, row.Sha256, computed));
    }

    // ================================================================== Rapprochement RH ↔ Finance (§32)

    private sealed record HrSideRow(string Currency, decimal SalaryExpense, decimal EmployerCharges, decimal Withholdings, decimal Advances,
        decimal Loans, decimal Others, decimal Reimbursements, decimal Net);
    private sealed record LedgerSideRow(string MappingKey, string Currency, decimal Debit, decimal Credit);
    private sealed record EmployeeReconRow(Guid EmployeeId, string EmployeeName, string Currency, decimal NetApproved, decimal Paid, decimal LedgerPayable);

    public async Task<Result<ReconciliationDto>> ReconcileAsync(Guid runId, CancellationToken ct = default)
    {
        if (!await CanReadPayrollAsync(ct)) return RunNotFound();
        var run = await RunQuery().Where(x => x.Id == runId).FirstOrDefaultAsync(ct);
        if (run is null) return RunNotFound();
        if (run.JournalEntryId is null) return Error.Conflict("payroll.run_state", "La paie n'est pas encore approuvée : aucune écriture à rapprocher.");

        var hr = await db.Database.SqlQuery<HrSideRow>($"""
            SELECT cur.code AS "Currency",
                   sum(i.gross_amount - i.absence_deduction - i.benefit_offset) AS "SalaryExpense",
                   sum(i.employer_contributions) AS "EmployerCharges", sum(i.legal_deductions + i.employer_contributions) AS "Withholdings",
                   sum(i.advance_recovery) AS "Advances", sum(i.loan_recovery) AS "Loans", sum(i.other_deductions) AS "Others",
                   sum(i.reimbursement_amount) AS "Reimbursements", sum(i.net_amount) AS "Net"
            FROM nexora.payroll_item i JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE i.run_id = {runId} AND i.status IN ('approved','paid')
            GROUP BY cur.code
            """).ToListAsync(ct);
        var ledger = await db.Database.SqlQuery<LedgerSideRow>($"""
            SELECT m.mapping_key AS "MappingKey", cur.code AS "Currency", sum(l.debit) AS "Debit", sum(l.credit) AS "Credit"
            FROM nexora.journal_entry_line l
            JOIN nexora.currency cur ON cur.id = l.currency_id
            JOIN nexora.account_mapping m ON m.account_id = l.account_id AND m.company_id = l.company_id
                 AND m.mapping_key IN ('salary_expense','employer_charges_expense','payroll_withholdings','employee_advances','employee_loans',
                                       'payroll_other_deductions','employee_reimbursements_payable','salaries_payable')
            WHERE l.journal_entry_id = {run.JournalEntryId}
            GROUP BY m.mapping_key, cur.code
            """).ToListAsync(ct);

        decimal Ledger(string key, string currency, bool debit)
        {
            var x = ledger.FirstOrDefault(l => l.MappingKey == key && l.Currency == currency);
            return x is null ? 0m : debit ? x.Debit - x.Credit : x.Credit - x.Debit;
        }
        var lines = new List<ReconciliationLineDto>();
        foreach (var h in hr)
        {
            void Add(string key, string label, decimal hrAmount, bool debit)
            {
                var l = Ledger(key, h.Currency, debit);
                lines.Add(new ReconciliationLineDto(key, label, h.Currency, hrAmount, l, hrAmount - l));
            }
            Add("salary_expense", "Rémunérations (brut − absences − avantages en nature)", h.SalaryExpense, true);
            Add("employer_charges_expense", "Charges patronales", h.EmployerCharges, true);
            Add("employee_reimbursements_payable", "Frais remboursés par la paie", h.Reimbursements, true);
            Add("payroll_withholdings", "Retenues et cotisations à reverser", h.Withholdings, false);
            Add("employee_advances", "Récupérations d'avances", h.Advances, false);
            Add("employee_loans", "Échéances de prêts", h.Loans, false);
            Add("payroll_other_deductions", "Autres retenues", h.Others, false);
            Add("salaries_payable", "Net à payer", h.Net, false);
        }

        // Par employé : net approuvé − paiements effectués = solde du compte « rémunérations dues » pour cette paie.
        var employees = await db.Database.SqlQuery<EmployeeReconRow>($"""
            SELECT i.employee_id AS "EmployeeId", i.employee_name AS "EmployeeName", cur.code AS "Currency", i.net_amount AS "NetApproved",
                   COALESCE((SELECT sum(sp.amount) FROM nexora.salary_payment sp WHERE sp.payroll_item_id = i.id AND sp.status = 'completed'), 0) AS "Paid",
                   COALESCE((SELECT sum(l.credit - l.debit) FROM nexora.journal_entry_line l
                             JOIN nexora.account_mapping m ON m.account_id = l.account_id AND m.company_id = l.company_id
                                  AND m.mapping_key = 'salaries_payable'
                             WHERE l.third_party_type = 'employee' AND l.third_party_id = i.employee_id
                               AND (l.journal_entry_id = {run.JournalEntryId}
                                    OR l.journal_entry_id IN (SELECT sp.journal_entry_id FROM nexora.salary_payment sp WHERE sp.payroll_item_id = i.id)
                                    OR l.journal_entry_id IN (SELECT sp.reversal_journal_entry_id FROM nexora.salary_payment sp
                                                              WHERE sp.payroll_item_id = i.id AND sp.reversal_journal_entry_id IS NOT NULL))), 0)
                       AS "LedgerPayable"
            FROM nexora.payroll_item i JOIN nexora.currency cur ON cur.id = i.currency_id
            WHERE i.run_id = {runId} AND i.status IN ('approved','paid')
            ORDER BY i.employee_name
            """).ToListAsync(ct);
        var perEmployee = employees.Select(e => new ReconciliationEmployeeDto(e.EmployeeId, e.EmployeeName, e.Currency, e.NetApproved, e.Paid,
            e.LedgerPayable, e.NetApproved - e.Paid == e.LedgerPayable)).ToList();
        var reconciled = lines.All(l => l.Difference == 0m) && perEmployee.All(e => e.Ok);
        return Result.Success(new ReconciliationDto(runId, run.RunNumber, reconciled, lines, perEmployee));
    }
}
