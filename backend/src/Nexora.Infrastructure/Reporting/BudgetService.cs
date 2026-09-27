using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Reporting;
using Nexora.Contracts.Reporting;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Budgets (Phase 42 §33-34) — active le CRUD sur <c>budget</c>/<c>budget_line</c> (V011, jusque-là
/// lecture vs-réel seule via <see cref="IBudgetReports"/>). Cycle <c>draft → pending_approval →
/// active → closed</c> (approbateur ≠ soumissionnaire, comme le workflow Phase 11) ; une révision
/// crée un nouveau budget brouillon lié au précédent par <c>revised_from</c> (§33).
/// </summary>
public sealed class SqlBudgetService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IBudgetService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw new InvalidOperationException("Entreprise active requise.");

    private sealed record Row(Guid Id, string Name, int FiscalYear, string CurrencyCode, string Status,
        Guid? RevisedFrom, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt);

    private const string Select = """
        SELECT b.id AS "Id", b.name AS "Name", b.fiscal_year AS "FiscalYear", c.code AS "CurrencyCode",
               b.status AS "Status", b.revised_from AS "RevisedFrom", b.approved_by AS "ApprovedBy",
               b.approved_at AS "ApprovedAt", b.updated_at AS "UpdatedAt"
        FROM nexora.budget b JOIN nexora.currency c ON c.id = b.currency_id
        """;

    public async Task<IReadOnlyList<BudgetDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQueryRaw<Row>(Select + " WHERE b.company_id = {0} ORDER BY b.fiscal_year DESC, b.name", Company).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<Result<BudgetDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQueryRaw<Row>(Select + " WHERE b.id = {0} AND b.company_id = {1}", id, Company).FirstOrDefaultAsync(ct);
        return row is null ? Error.NotFound("budget.not_found", "Budget introuvable.") : Result.Success(ToDto(row));
    }

    public async Task<Result<BudgetDto>> CreateAsync(CreateBudgetRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Error.Validation("budget.name", "Nom requis.");
        if (request.FiscalYear is < 2000 or > 2100) return Error.Validation("budget.fiscal_year", "Exercice invalide.");

        var currencyId = await db.Database.SqlQuery<Guid>($"""
            SELECT id AS "Value" FROM nexora.currency WHERE code = {request.CurrencyCode.Trim().ToUpperInvariant()}
            """).FirstOrDefaultAsync(ct);
        if (currencyId == Guid.Empty) return Error.Validation("budget.currency", "Devise inconnue.");

        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.budget (id, company_id, name, fiscal_year, currency_id)
            VALUES ({id}, {Company}, {request.Name}, {request.FiscalYear}, {currencyId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<IReadOnlyList<BudgetLineDto>>> GetLinesAsync(Guid id, CancellationToken ct = default)
    {
        var status = await StatusOf(id, ct);
        if (status is null) return Error.NotFound("budget.not_found", "Budget introuvable.");

        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT id AS "Id", account_id AS "AccountId", cost_center_id AS "CostCenterId",
                   establishment_id AS "EstablishmentId", period_month AS "PeriodMonth", amount AS "Amount"
            FROM nexora.budget_line WHERE budget_id = {id} AND company_id = {Company} ORDER BY period_month NULLS FIRST
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<BudgetLineDto>>(
            lines.Select(l => new BudgetLineDto(l.Id, l.AccountId, l.CostCenterId, l.EstablishmentId, l.PeriodMonth, l.Amount)).ToList());
    }

    public async Task<Result<IReadOnlyList<BudgetLineDto>>> SetLinesAsync(Guid id, SetBudgetLinesRequest request, CancellationToken ct = default)
    {
        var status = await StatusOf(id, ct);
        if (status is null) return Error.NotFound("budget.not_found", "Budget introuvable.");
        if (status != "draft") return Error.Conflict("budget.not_draft", "Les lignes ne se modifient qu'en brouillon.");
        foreach (var l in request.Lines)
            if (l.Amount < 0) return Error.Validation("budget.line_amount", "Un montant de ligne ne peut pas être négatif.");

        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"DELETE FROM nexora.budget_line WHERE budget_id = {id} AND company_id = {Company}", token);
            foreach (var l in request.Lines)
            {
                await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO nexora.budget_line (id, budget_id, company_id, account_id, cost_center_id, establishment_id, period_month, amount)
                    VALUES ({Guid.NewGuid()}, {id}, {Company}, {l.AccountId}, {l.CostCenterId}, {l.EstablishmentId}, {l.PeriodMonth}, {l.Amount})
                    """, token);
            }
            return 0;
        }, ct);

        var lines = await db.Database.SqlQuery<LineRow>($"""
            SELECT id AS "Id", account_id AS "AccountId", cost_center_id AS "CostCenterId",
                   establishment_id AS "EstablishmentId", period_month AS "PeriodMonth", amount AS "Amount"
            FROM nexora.budget_line WHERE budget_id = {id} ORDER BY period_month NULLS FIRST
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<BudgetLineDto>>(
            lines.Select(l => new BudgetLineDto(l.Id, l.AccountId, l.CostCenterId, l.EstablishmentId, l.PeriodMonth, l.Amount)).ToList());
    }

    private sealed record LineRow(Guid Id, Guid? AccountId, Guid? CostCenterId, Guid? EstablishmentId, int? PeriodMonth, decimal Amount);

    public async Task<Result<BudgetDto>> SubmitAsync(Guid id, CancellationToken ct = default)
    {
        var status = await StatusOf(id, ct);
        if (status is null) return Error.NotFound("budget.not_found", "Budget introuvable.");
        if (status != "draft") return Error.Conflict("budget.not_draft", "Seul un budget brouillon peut être soumis.");
        var hasLines = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.budget_line WHERE budget_id = {id}) AS "Value"
            """).FirstAsync(ct);
        if (!hasLines) return Error.Validation("budget.no_lines", "Le budget n'a aucune ligne.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.budget SET status = 'pending_approval', submitted_by = {tenant.UserId}, updated_at = now() WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<BudgetDto>> ApproveAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Database.SqlQuery<StatusRow>($"""
            SELECT status AS "Status", submitted_by AS "SubmittedBy" FROM nexora.budget WHERE id = {id} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (row is null) return Error.NotFound("budget.not_found", "Budget introuvable.");
        if (row.Status != "pending_approval") return Error.Conflict("budget.not_pending", "Le budget n'est pas en attente d'approbation.");
        if (row.SubmittedBy is not null && row.SubmittedBy == tenant.UserId)
            return Error.Forbidden("budget.four_eyes", "Le demandeur ne peut pas approuver son propre budget.");

        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.budget SET status = 'active', approved_by = {tenant.UserId}, approved_at = now(), updated_at = now() WHERE id = {id}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private sealed record StatusRow(string Status, Guid? SubmittedBy);

    public async Task<Result<BudgetDto>> CloseAsync(Guid id, CancellationToken ct = default)
    {
        var status = await StatusOf(id, ct);
        if (status is null) return Error.NotFound("budget.not_found", "Budget introuvable.");
        if (status != "active") return Error.Conflict("budget.not_active", "Seul un budget actif peut être clos.");

        await db.Database.ExecuteSqlAsync($"UPDATE nexora.budget SET status = 'closed', updated_at = now() WHERE id = {id}", ct);
        await uow.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task<Result<BudgetDto>> ReviseAsync(Guid id, CancellationToken ct = default)
    {
        var original = await GetAsync(id, ct);
        if (!original.IsSuccess) return original;

        var newId = Guid.NewGuid();
        await uow.ExecuteInTransactionAsync(async token =>
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.budget (id, company_id, name, fiscal_year, currency_id, revised_from)
                SELECT {newId}, company_id, name || ' (révision)', fiscal_year, currency_id, {id}
                FROM nexora.budget WHERE id = {id} AND company_id = {Company}
                """, token);
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO nexora.budget_line (id, budget_id, company_id, account_id, cost_center_id, establishment_id, period_month, amount)
                SELECT gen_random_uuid(), {newId}, company_id, account_id, cost_center_id, establishment_id, period_month, amount
                FROM nexora.budget_line WHERE budget_id = {id} AND company_id = {Company}
                """, token);
            return 0;
        }, ct);

        return await GetAsync(newId, ct);
    }

    private async Task<string?> StatusOf(Guid id, CancellationToken ct) =>
        await db.Database.SqlQuery<string>($"SELECT status AS \"Value\" FROM nexora.budget WHERE id = {id} AND company_id = {Company}")
            .FirstOrDefaultAsync(ct);

    private static BudgetDto ToDto(Row r) => new(r.Id, r.Name, r.FiscalYear, r.CurrencyCode, r.Status, r.RevisedFrom, r.ApprovedBy, r.ApprovedAt, r.UpdatedAt);
}
