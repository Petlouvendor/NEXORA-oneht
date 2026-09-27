using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Application.Finance.Events;
using Nexora.Application.Security;
using Nexora.Contracts.Payroll;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Payroll;

/// <summary>
/// Workflow de validation (§22) : contrôle RH/comptable → validation (verrouillage) → approbation finale
/// (écritures de paie, récupérations d'avances, intégration des variables) → paiement → clôture. Les anomalies
/// bloquantes empêchent d'avancer (§23) ; les avertissements doivent être reconnus explicitement.
/// </summary>
public sealed partial class SqlPayrollService
{
    private sealed record WorkflowRow(Guid Id, string RunNumber, Guid PeriodId, string Status, bool Stale, Guid? CalculatedBy, Guid? ReviewedBy,
        Guid? ValidatedBy, string PeriodLabel, DateOnly EndDate);

    private async Task<WorkflowRow?> LockRunAsync(Guid runId, CancellationToken ct)
        => await db.Database.SqlQuery<WorkflowRow>($"""
            SELECT r.id AS "Id", r.run_number AS "RunNumber", r.period_id AS "PeriodId", r.status AS "Status", r.stale AS "Stale",
                   r.calculated_by AS "CalculatedBy", r.reviewed_by AS "ReviewedBy", r.validated_by AS "ValidatedBy",
                   p.label AS "PeriodLabel", p.end_date AS "EndDate"
            FROM nexora.payroll_run r JOIN nexora.payroll_period p ON p.id = r.period_id
            WHERE r.id = {runId} AND r.company_id = {Company}
            FOR UPDATE OF r
            """).FirstOrDefaultAsync(ct);

    private sealed record AnomalyCountRow(int Items, int Blocking, int Warnings, int NegativeNet);

    private async Task<AnomalyCountRow> AnomalyCountsAsync(Guid runId, CancellationToken ct)
        => await db.Database.SqlQuery<AnomalyCountRow>($"""
            SELECT count(*)::int AS "Items", COALESCE(sum(blocking_count), 0)::int AS "Blocking",
                   COALESCE(sum(warning_count), 0)::int AS "Warnings", count(*) FILTER (WHERE net_amount < 0)::int AS "NegativeNet"
            FROM nexora.payroll_item WHERE run_id = {runId} AND status NOT IN ('excluded','cancelled')
            """).FirstAsync(ct);

    private async Task SetStatusAsync(WorkflowRow run, string status, CancellationToken ct)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.payroll_period SET status = {PeriodStatusFor(status)} WHERE id = {run.PeriodId}
            """, ct);

    /// <summary>Exécute une étape du workflow dans une transaction, avec relecture de la paie verrouillée.</summary>
    private async Task<Result<PayrollRunDto>> StepAsync(Guid runId, Func<WorkflowRow, CancellationToken, Task> step, CancellationToken ct)
    {
        try
        {
            await uow.ExecuteInTransactionAsync(async token =>
            {
                var run = await LockRunAsync(runId, token) ?? throw new PayrollRefusal(RunNotFound());
                await step(run, token);
                return 0;
            }, ct);
        }
        catch (PayrollRefusal refusal)
        {
            return refusal.Error;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Étape de paie refusée pour {Run}", runId);
            return Translate(ex, "payroll.workflow_failed");
        }
        return await RunAsync(runId, ct);
    }

    private static PayrollRefusal Refuse(Error e) => new(e);

    // ------------------------------------------------------------------ Exclusion d'un employé

    public async Task<Result<PayrollRunDto>> ExcludeItemAsync(Guid itemId, ExcludeItemRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            return Error.Validation("payroll.exclude_reason", "Le motif d'exclusion est obligatoire (5 caractères au moins).");
        var runId = await db.Database.SqlQuery<Guid?>($"""
            SELECT run_id AS "Value" FROM nexora.payroll_item WHERE id = {itemId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (runId is null) return Error.NotFound("payroll.item_not_found", "Ligne de paie introuvable.");
        return await StepAsync(runId.Value, async (run, token) =>
        {
            if (run.Status is not ("calculated" or "reviewed"))
                throw Refuse(Error.Conflict("payroll.run_locked", "Seule une paie calculée non validée permet d'exclure un employé."));
            var n = await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_item SET status = 'excluded', excluded_reason = {r.Reason.Trim()}
                WHERE id = {itemId} AND status = 'calculated'
                """, token);
            if (n == 0) throw Refuse(Error.Conflict("payroll.item_state", "Cette ligne est déjà exclue."));
            // Toute modification du périmètre annule le contrôle déjà fait.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'calculated', reviewed_by = NULL, reviewed_at = NULL, review_note = NULL,
                       warnings_acknowledged = false
                WHERE id = {run.Id}
                """, token);
            await SetStatusAsync(run, "calculated", token);
        }, ct);
    }

    public async Task<Result<PayrollRunDto>> IncludeItemAsync(Guid itemId, CancellationToken ct = default)
    {
        var runId = await db.Database.SqlQuery<Guid?>($"""
            SELECT run_id AS "Value" FROM nexora.payroll_item WHERE id = {itemId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (runId is null) return Error.NotFound("payroll.item_not_found", "Ligne de paie introuvable.");
        return await StepAsync(runId.Value, async (run, token) =>
        {
            if (run.Status is not ("calculated" or "reviewed"))
                throw Refuse(Error.Conflict("payroll.run_locked", "Seule une paie calculée non validée permet de réintégrer un employé."));
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_item SET status = 'calculated', excluded_reason = NULL WHERE id = {itemId} AND status = 'excluded'
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'calculated', reviewed_by = NULL, reviewed_at = NULL, review_note = NULL,
                       warnings_acknowledged = false
                WHERE id = {run.Id}
                """, token);
            await SetStatusAsync(run, "calculated", token);
        }, ct);
    }

    // ------------------------------------------------------------------ Contrôle RH / comptable (§22, §23)

    public async Task<Result<PayrollRunDto>> ReviewAsync(Guid runId, WorkflowNoteRequest r, CancellationToken ct = default)
    {
        var result = await StepAsync(runId, async (run, token) =>
        {
            if (run.Status != "calculated") throw Refuse(Error.Conflict("payroll.run_state", "La paie doit être calculée pour être contrôlée."));
            if (run.Stale)
                throw Refuse(Error.Conflict("payroll.run_stale", "Des présences, absences, variables ou salaires ont changé depuis le calcul : recalculer."));
            var counts = await AnomalyCountsAsync(run.Id, token);
            if (counts.Items == 0) throw Refuse(Error.Conflict("payroll.run_empty", "Aucun employé à payer dans cette paie."));
            if (counts.Blocking > 0)
                throw Refuse(Error.Conflict("payroll.blocking_anomalies",
                    $"{counts.Blocking} anomalie(s) bloquante(s) : corriger le dossier puis recalculer, ou exclure l'employé avec un motif."));
            if (counts.Warnings > 0 && !r.AcknowledgeWarnings)
                throw Refuse(Error.Conflict("payroll.warnings_not_acknowledged",
                    $"{counts.Warnings} avertissement(s) à examiner : confirmer leur prise en compte pour poursuivre."));
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'reviewed', reviewed_by = {UserId}, reviewed_at = now(), review_note = {r.Note?.Trim()},
                       warnings_acknowledged = {counts.Warnings > 0}
                WHERE id = {run.Id}
                """, token);
            await SetStatusAsync(run, "reviewed", token);
        }, ct);
        if (result.IsSuccess)
            await notifier.ToPermissionAsync(Permissions.Payroll.Validate, "Paie en attente de validation",
                $"{result.Value!.RunNumber} — {result.Value.PeriodLabel} contrôlée par {result.Value.ReviewedByName}.",
                $"/hr/payroll/{runId}", "payroll_run", runId, $"payroll:{runId}:reviewed:{result.Value.CalculationCount}", ct);
        return result;
    }

    // ------------------------------------------------------------------ Validation = verrouillage (§22, §37 test 27)

    public async Task<Result<PayrollRunDto>> ValidateAsync(Guid runId, WorkflowNoteRequest r, CancellationToken ct = default)
    {
        var sod = (await config.ReadAsync(ct)).SegregationOfDuties;
        var result = await StepAsync(runId, async (run, token) =>
        {
            if (run.Status != "reviewed") throw Refuse(Error.Conflict("payroll.run_state", "La paie doit être contrôlée avant sa validation."));
            if (run.Stale) throw Refuse(Error.Conflict("payroll.run_stale", "Des données ont changé depuis le calcul : recalculer."));
            if (sod && run.CalculatedBy == UserId)
                throw Refuse(Error.Forbidden("payroll.self_approval", "Séparation des tâches : la personne qui a calculé la paie ne la valide pas."));
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'validated', validated_by = {UserId}, validated_at = now(), validation_note = {r.Note?.Trim()}
                WHERE id = {run.Id}
                """, token);
            await SetStatusAsync(run, "validated", token);
        }, ct);
        if (result.IsSuccess)
            await notifier.ToPermissionAsync(Permissions.Payroll.Approve, "Paie en attente d'approbation finale",
                $"{result.Value!.RunNumber} — {result.Value.PeriodLabel} validée par {result.Value.ValidatedByName}.",
                $"/hr/payroll/{runId}", "payroll_run", runId, $"payroll:{runId}:validated", ct);
        return result;
    }

    public async Task<Result<PayrollRunDto>> ReturnAsync(Guid runId, WorkflowNoteRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Note)) return Error.Validation("payroll.return_note", "Indiquer ce qui doit être corrigé.");
        if (!await access.HasAnyAsync([Permissions.Payroll.Review, Permissions.Payroll.Validate, Permissions.Payroll.Approve], ct))
            return Error.Forbidden("payroll.forbidden", "Renvoyer une paie en correction exige un droit de contrôle, de validation ou d'approbation.");
        return await StepAsync(runId, async (run, token) =>
        {
            if (run.Status is not ("reviewed" or "validated"))
                throw Refuse(Error.Conflict("payroll.run_state", "Seule une paie contrôlée ou validée, non approuvée, peut être renvoyée."));
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'calculated', reviewed_by = NULL, reviewed_at = NULL, review_note = NULL,
                       warnings_acknowledged = false, validated_by = NULL, validated_at = NULL, validation_note = NULL,
                       returned_note = {r.Note.Trim()}
                WHERE id = {run.Id}
                """, token);
            await SetStatusAsync(run, "calculated", token);
        }, ct);
    }

    public async Task<Result<PayrollRunDto>> CancelAsync(Guid runId, CancelRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Reason) || r.Reason.Trim().Length < 5)
            return Error.Validation("payroll.cancel_reason", "Le motif d'annulation est obligatoire (5 caractères au moins).");
        return await StepAsync(runId, async (run, token) =>
        {
            if (run.Status is not ("draft" or "calculated" or "reviewed" or "validated"))
                throw Refuse(Error.Conflict("payroll.run_locked", "Une paie approuvée ne s'annule pas : passer par une régularisation (§36)."));
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_item SET status = 'cancelled' WHERE run_id = {run.Id} AND status IN ('calculated','excluded')
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'cancelled', cancelled_by = {UserId}, cancelled_at = now(), cancel_reason = {r.Reason.Trim()}
                WHERE id = {run.Id}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_period SET status = 'cancelled', cancelled_by = {UserId}, cancelled_at = now(), cancel_reason = {r.Reason.Trim()}
                WHERE id = {run.PeriodId}
                """, token);
        }, ct);
    }

    // ------------------------------------------------------------------ Approbation finale : obligations salariales (§22, §32)

    private sealed record PostingItemRow(
        Guid Id, Guid EmployeeId, string EmployeeName, string EmployeeNumber, string Currency, decimal Gross, decimal AbsenceDeduction,
        decimal BenefitOffset, decimal Reimbursements, decimal EmployerContributions, decimal LegalDeductions, decimal AdvanceRecovery,
        decimal LoanRecovery, decimal OtherDeductions, decimal Net);

    private sealed record RecoveryLineRow(Guid DeductionId, Guid ItemId, Guid AdvanceId, decimal Amount, Guid CurrencyId, string AdvanceNumber);

    public async Task<Result<PayrollRunDto>> ApproveAsync(Guid runId, WorkflowNoteRequest r, CancellationToken ct = default)
    {
        var sod = (await config.ReadAsync(ct)).SegregationOfDuties;
        var now = clock.UtcNow;
        var result = await StepAsync(runId, async (run, token) =>
        {
            if (run.Status != "validated") throw Refuse(Error.Conflict("payroll.run_state", "La paie doit être validée avant l'approbation finale."));
            if (sod && (run.CalculatedBy == UserId || run.ValidatedBy == UserId))
                throw Refuse(Error.Forbidden("payroll.self_approval",
                    "Séparation des tâches : l'approbation finale revient à une autre personne que celles qui ont calculé et validé la paie."));
            var counts = await AnomalyCountsAsync(run.Id, token);
            if (counts.Blocking > 0 || counts.NegativeNet > 0)
                throw Refuse(Error.Conflict("payroll.blocking_anomalies", "Anomalies bloquantes ou net négatif : approbation impossible."));

            var items = await db.Database.SqlQuery<PostingItemRow>($"""
                SELECT i.id AS "Id", i.employee_id AS "EmployeeId", i.employee_name AS "EmployeeName", i.employee_number AS "EmployeeNumber",
                       cur.code AS "Currency", i.gross_amount AS "Gross", i.absence_deduction AS "AbsenceDeduction",
                       i.benefit_offset AS "BenefitOffset", i.reimbursement_amount AS "Reimbursements",
                       i.employer_contributions AS "EmployerContributions", i.legal_deductions AS "LegalDeductions",
                       i.advance_recovery AS "AdvanceRecovery", i.loan_recovery AS "LoanRecovery", i.other_deductions AS "OtherDeductions",
                       i.net_amount AS "Net"
                FROM nexora.payroll_item i JOIN nexora.currency cur ON cur.id = i.currency_id
                WHERE i.run_id = {run.Id} AND i.status = 'calculated'
                """).ToListAsync(token);
            if (items.Count == 0) throw Refuse(Error.Conflict("payroll.run_empty", "Aucune ligne de paie à approuver."));

            // 1. Écriture de paie (journal PA), datée de la fin de période — une avance récupérée solde la créance, pas une charge.
            var operation = Guid.NewGuid();
            var posted = await engine.HandleAsync(new PayrollApproved(Company, operation,
                new DateTimeOffset(run.EndDate.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero), run.Id, run.RunNumber, run.PeriodLabel,
                items.Select(i => new PayrollEmployeePosting(i.EmployeeId, $"{i.EmployeeNumber} {i.EmployeeName}", i.Currency,
                    i.Gross - i.AbsenceDeduction - i.BenefitOffset, i.Reimbursements, i.EmployerContributions, i.LegalDeductions,
                    i.AdvanceRecovery, i.LoanRecovery, i.OtherDeductions, i.Net)).ToList()) { UserId = tenant.UserId }, token);
            if (!posted.IsSuccess) throw Refuse(posted.Error!);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'approved', approved_by = {UserId}, approved_at = {now}, approval_note = {r.Note?.Trim()},
                       journal_entry_id = {posted.Value!.JournalEntryId}, posting_operation_uuid = {operation}
                WHERE id = {run.Id}
                """, token);
            await SetStatusAsync(run, "approved", token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_item SET status = 'approved' WHERE run_id = {run.Id} AND status = 'calculated'
                """, token);

            // 2. Récupérations d'avances et de prêts : chaque retenue diminue le solde de l'avance visée (§13.8, §16).
            var recoveries = await db.Database.SqlQuery<RecoveryLineRow>($"""
                SELECT d.id AS "DeductionId", d.payroll_item_id AS "ItemId", d.source_id AS "AdvanceId", d.amount AS "Amount",
                       i.currency_id AS "CurrencyId", a.advance_number AS "AdvanceNumber"
                FROM nexora.payroll_deduction d
                JOIN nexora.payroll_item i ON i.id = d.payroll_item_id
                JOIN nexora.employee_advance a ON a.id = d.source_id
                WHERE i.run_id = {run.Id} AND i.status = 'approved' AND d.category IN ('advance','loan') AND d.amount > 0
                """).ToListAsync(token);
            foreach (var rec in recoveries)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.advance_repayment
                        (company_id, advance_id, source, payroll_item_id, payroll_deduction_id, amount, currency_id, repaid_at, journal_entry_id,
                         reference, created_by)
                    VALUES ({Company}, {rec.AdvanceId}, 'payroll', {rec.ItemId}, {rec.DeductionId}, {rec.Amount}, {rec.CurrencyId}, {now},
                            {posted.Value.JournalEntryId}, {run.RunNumber}, {UserId})
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.employee_advance_event (company_id, advance_id, event_type, amount, balance_after, reference, note, actor_id, occurred_at)
                    SELECT {Company}, a.id, 'recovered_payroll', {rec.Amount}, a.balance, {run.RunNumber}, {"Retenue sur la paie " + run.PeriodLabel}, {UserId}, clock_timestamp()
                    FROM nexora.employee_advance a WHERE a.id = {rec.AdvanceId}
                    """, token);
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.employee_advance_event (company_id, advance_id, event_type, amount, balance_after, reference, note, actor_id, occurred_at)
                    SELECT {Company}, a.id, 'settled', NULL, 0, {run.RunNumber}, 'Avance soldée', {UserId}, clock_timestamp()
                    FROM nexora.employee_advance a WHERE a.id = {rec.AdvanceId} AND a.status = 'settled'
                    """, token);
            }

            // 3. Éléments intégrés : heures sup., variables, régularisations et frais ne pourront plus être payés deux fois.
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.overtime_record o SET payroll_item_id = e.payroll_item_id
                FROM nexora.payroll_earning e JOIN nexora.payroll_item i ON i.id = e.payroll_item_id
                WHERE i.run_id = {run.Id} AND i.status = 'approved' AND e.source_type = 'overtime' AND e.source_id = o.id
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_variable v SET payroll_item_id = i.id
                FROM nexora.payroll_item i
                WHERE i.run_id = {run.Id} AND i.status = 'approved' AND v.employee_id = i.employee_id AND v.period_id = i.period_id
                  AND v.status = 'approved' AND v.payroll_item_id IS NULL
                  AND (EXISTS (SELECT 1 FROM nexora.payroll_earning e WHERE e.payroll_item_id = i.id AND e.source_type = 'variable' AND e.source_id = v.id)
                    OR EXISTS (SELECT 1 FROM nexora.payroll_deduction d WHERE d.payroll_item_id = i.id AND d.source_type = 'variable' AND d.source_id = v.id))
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_adjustment a SET status = 'applied', applied_item_id = i.id, applied_at = {now}
                FROM nexora.payroll_item i
                WHERE i.run_id = {run.Id} AND i.status = 'approved' AND a.employee_id = i.employee_id AND a.status = 'approved'
                  AND (EXISTS (SELECT 1 FROM nexora.payroll_earning e WHERE e.payroll_item_id = i.id AND e.source_type = 'adjustment' AND e.source_id = a.id)
                    OR EXISTS (SELECT 1 FROM nexora.payroll_deduction d WHERE d.payroll_item_id = i.id AND d.source_type = 'adjustment' AND d.source_id = a.id))
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.employee_expense_claim c SET status = 'in_payroll', payroll_item_id = e.payroll_item_id
                FROM nexora.payroll_earning e JOIN nexora.payroll_item i ON i.id = e.payroll_item_id
                WHERE i.run_id = {run.Id} AND i.status = 'approved' AND e.source_type = 'expense_claim' AND e.source_id = c.id
                  AND c.status = 'approved'
                """, token);
        }, ct);
        if (result.IsSuccess)
        {
            await notifier.ToPermissionAsync(Permissions.Payroll.Pay, "Paie approuvée — paiements à effectuer",
                $"{result.Value!.RunNumber} — {result.Value.PeriodLabel} approuvée par {result.Value.ApprovedByName} ; écriture {result.Value.JournalEntryNumber}.",
                $"/hr/payroll/{runId}", "payroll_run", runId, $"payroll:{runId}:approved", ct);
            logger.LogInformation("Paie {Run} approuvée (écriture {Entry})", result.Value.RunNumber, result.Value.JournalEntryNumber);
        }
        return result;
    }

    // ------------------------------------------------------------------ Clôture (§22)

    public async Task<Result<PayrollRunDto>> CloseAsync(Guid runId, WorkflowNoteRequest r, CancellationToken ct = default)
        => await StepAsync(runId, async (run, token) =>
        {
            if (run.Status != "paid")
            {
                var unpaid = await db.Database.SqlQuery<int>($"""
                    SELECT count(*)::int AS "Value" FROM nexora.payroll_item WHERE run_id = {run.Id} AND status = 'approved' AND net_amount > 0
                    """).FirstAsync(token);
                if (run.Status != "approved" || unpaid > 0)
                    throw Refuse(Error.Conflict("payroll.run_state",
                        run.Status == "approved" ? $"{unpaid} salaire(s) restent à payer avant la clôture." : "Seule une paie payée peut être clôturée."));
            }
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_run SET status = 'closed', closed_by = {UserId}, closed_at = now(),
                       paid_at = COALESCE(paid_at, now())
                WHERE id = {run.Id}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.payroll_period SET status = 'closed', closed_by = {UserId}, closed_at = now() WHERE id = {run.PeriodId}
                """, token);
        }, ct);
}
