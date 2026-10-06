using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Ledger;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

/// <summary>Plan comptable (phase 8) — voir <see cref="IChartManagementService"/>.</summary>
public sealed class SqlChartManagementService(NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow) : IChartManagementService
{
    private static readonly string[] Types = ["asset", "liability", "equity", "revenue", "expense"];
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    private sealed record Row(Guid Id, string Code, string Name, string AccountType, bool IsPostable, string Status,
        Guid? ParentAccountId, string? ParentCode, string? Currency, decimal Balance, int LineCount,
        string[] Mappings, string[] TreasuryAccounts, string[] ExpenseCategories, int Children);

    private FormattableString Select(Guid? id) => $"""
        SELECT a.id AS "Id", a.code AS "Code", a.name AS "Name", a.account_type AS "AccountType", a.is_postable AS "IsPostable",
               a.status AS "Status", a.parent_account_id AS "ParentAccountId", p.code AS "ParentCode", cur.code AS "Currency",
               COALESCE((SELECT SUM(l.base_debit - l.base_credit) FROM nexora.journal_entry_line l
                         JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                         WHERE l.account_id = a.id AND e.status IN ('posted', 'reversed')), 0) AS "Balance",
               (SELECT COUNT(*)::int FROM nexora.journal_entry_line l WHERE l.account_id = a.id) AS "LineCount",
               ARRAY(SELECT m.mapping_key FROM nexora.account_mapping m WHERE m.account_id = a.id ORDER BY 1) AS "Mappings",
               ARRAY(SELECT f.name FROM nexora.financial_account f WHERE f.ledger_account_id = a.id AND f.status <> 'closed' ORDER BY 1) AS "TreasuryAccounts",
               ARRAY(SELECT c.name FROM nexora.expense_category c WHERE c.ledger_account_id = a.id AND c.status = 'active' ORDER BY 1) AS "ExpenseCategories",
               (SELECT COUNT(*)::int FROM nexora.account ch WHERE ch.parent_account_id = a.id) AS "Children"
        FROM nexora.account a
        LEFT JOIN nexora.account p ON p.id = a.parent_account_id
        LEFT JOIN nexora.currency cur ON cur.id = a.currency_id
        WHERE a.company_id = {Company} AND ({id}::uuid IS NULL OR a.id = {id})
        ORDER BY a.code
        """;

    private static IReadOnlyList<string> Usage(Row r)
        => r.Mappings.Select(m => $"écritures automatiques ({m})")
            .Concat(r.TreasuryAccounts.Select(t => $"compte de trésorerie « {t} »"))
            .Concat(r.ExpenseCategories.Select(c => $"catégorie de dépense « {c} »"))
            .ToList();

    private static ChartAccountDto Map(Row r) => new(r.Id, r.Code, r.Name, r.AccountType, r.IsPostable, r.Status,
        r.ParentAccountId, r.ParentCode, r.Currency, Math.Round(r.Balance, 2), r.LineCount, Usage(r));

    public async Task<IReadOnlyList<ChartAccountDto>> AccountsAsync(CancellationToken ct = default)
        => (await db.Database.SqlQuery<Row>(Select(null)).ToListAsync(ct)).Select(Map).ToList();

    public async Task<IReadOnlyList<FinanceCurrencyOptionDto>> CurrenciesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<FinanceCurrencyOptionDto>($"""
            SELECT c.code AS "Code", c.name AS "Name", (c.id = co.default_currency_id) AS "IsCompanyDefault"
            FROM nexora.currency c CROSS JOIN nexora.company co
            WHERE co.id = {Company}
            ORDER BY (c.id = co.default_currency_id) DESC, c.code
            """).ToListAsync(ct);

    private async Task<Row?> GetAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<Row>(Select(id)).FirstOrDefaultAsync(ct);

    private async Task<Result<(Guid ChartId, Guid? CurrencyId)>> ValidateAsync(SaveChartAccountRequest r, Guid? id, CancellationToken ct)
    {
        var code = (r.Code ?? "").Trim();
        if (code.Length is 0 or > 20 || !code.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '-'))
            return Error.Validation("accounting.account_code", "Code requis : 20 caractères au plus, lettres, chiffres, point ou tiret.");
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 150) return Error.Validation("accounting.account_name", "Intitulé requis (150 caractères au plus).");
        if (!Types.Contains(r.AccountType)) return Error.Validation("accounting.account_type", $"Type « {r.AccountType} » inconnu.");

        var chart = await db.Database.SqlQuery<Guid?>($"""
            SELECT id AS "Value" FROM nexora.chart_of_accounts WHERE company_id = {Company} AND status = 'active'
            ORDER BY created_at LIMIT 1
            """).FirstOrDefaultAsync(ct);
        if (chart is null) return Error.Conflict("accounting.no_chart", "Aucun plan comptable actif pour cette entreprise.");

        var dup = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.account WHERE company_id = {Company} AND lower(code) = lower({code})
                          AND ({id}::uuid IS NULL OR id <> {id})) AS "Value"
            """).FirstAsync(ct);
        if (dup) return Error.Conflict("accounting.account_exists", $"Le compte {code} existe déjà.");

        if (r.ParentAccountId is { } parentId)
        {
            if (parentId == id) return Error.Validation("accounting.account_parent", "Un compte ne peut être son propre regroupement.");
            var parent = await GetAsync(parentId, ct);
            if (parent is null) return Error.Validation("accounting.account_parent", "Compte de regroupement inconnu.");
            if (parent.IsPostable)
                return Error.Validation("accounting.account_parent", $"Le compte {parent.Code} est mouvementable : choisissez un compte de regroupement.");
            if (parent.AccountType != r.AccountType)
                return Error.Validation("accounting.account_parent", "Le compte de regroupement doit être du même type.");
            if (id is { } self && await IsDescendantAsync(parentId, self, ct))
                return Error.Validation("accounting.account_parent", "Ce regroupement créerait une boucle.");
        }

        Guid? currencyId = null;
        if (!string.IsNullOrWhiteSpace(r.Currency))
        {
            currencyId = await db.Database.SqlQuery<Guid?>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {r.Currency}""").FirstOrDefaultAsync(ct);
            if (currencyId is null) return Error.Validation("accounting.currency_unknown", $"Devise « {r.Currency} » inconnue.");
        }
        return Result.Success((chart.Value, currencyId));
    }

    private Task<bool> IsDescendantAsync(Guid candidate, Guid ancestor, CancellationToken ct)
        => db.Database.SqlQuery<bool>($"""
            WITH RECURSIVE up AS (
                SELECT id, parent_account_id FROM nexora.account WHERE id = {candidate}
                UNION ALL
                SELECT a.id, a.parent_account_id FROM nexora.account a JOIN up ON a.id = up.parent_account_id)
            SELECT EXISTS(SELECT 1 FROM up WHERE id = {ancestor}) AS "Value"
            """).FirstAsync(ct);

    public async Task<Result<Guid>> CreateAsync(SaveChartAccountRequest r, CancellationToken ct = default)
    {
        var v = await ValidateAsync(r, null, ct);
        if (!v.IsSuccess) return v.Error!;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.account (id, chart_of_accounts_id, company_id, code, name, account_type, parent_account_id,
                                        is_postable, currency_id, status)
            VALUES ({id}, {v.Value.ChartId}, {Company}, {r.Code.Trim()}, {r.Name.Trim()}, {r.AccountType}, {r.ParentAccountId},
                    {r.IsPostable}, {v.Value.CurrencyId}, 'active')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> UpdateAsync(Guid id, SaveChartAccountRequest r, CancellationToken ct = default)
    {
        var current = await GetAsync(id, ct);
        if (current is null) return Error.NotFound("accounting.account_not_found", "Compte introuvable.");
        // Le verrou d'un compte mouvementé prime : c'est la vraie raison du refus, avant tout autre contrôle de forme.
        var structural = !string.Equals(current.Code, (r.Code ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                         || current.AccountType != r.AccountType || current.IsPostable != r.IsPostable
                         || !string.Equals(current.Currency ?? "", r.Currency ?? "", StringComparison.OrdinalIgnoreCase);
        if (structural && current.LineCount > 0)
            return Error.Conflict("accounting.account_locked",
                $"Le compte {current.Code} a déjà {current.LineCount} ligne(s) d'écriture : seuls l'intitulé et le regroupement restent modifiables.");
        var v = await ValidateAsync(r, id, ct);
        if (!v.IsSuccess) return v.Error!;
        if (!r.IsPostable && current.IsPostable && current.Mappings.Length > 0)
            return Error.Conflict("accounting.account_in_use", "Ce compte sert aux écritures automatiques : il doit rester mouvementable.");
        if (r.IsPostable && current.Children > 0)
            return Error.Conflict("accounting.account_has_children", "Ce compte regroupe d'autres comptes : il ne peut pas devenir mouvementable.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.account SET code = {r.Code!.Trim()}, name = {r.Name.Trim()}, account_type = {r.AccountType},
                   parent_account_id = {r.ParentAccountId}, is_postable = {r.IsPostable}, currency_id = {v.Value.CurrencyId}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> SetStatusAsync(Guid id, ChartAccountStatusRequest r, CancellationToken ct = default)
    {
        if (r.Status is not ("active" or "inactive")) return Error.Validation("accounting.account_status", "Statut invalide.");
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("accounting.reason_required", "Un motif est obligatoire.");
        var a = await GetAsync(id, ct);
        if (a is null) return Error.NotFound("accounting.account_not_found", "Compte introuvable.");
        if (r.Status == "inactive")
        {
            var usage = Usage(a);
            if (usage.Count > 0)
                return Error.Conflict("accounting.account_in_use", $"Compte utilisé par : {string.Join(", ", usage)}. Réaffectez-les d'abord.");
            if (Math.Round(a.Balance, 2) != 0m)
                return Error.Conflict("accounting.account_balance", $"Solde de {Math.Round(a.Balance, 2)} : soldez le compte par une écriture avant de le désactiver.");
            var drafts = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.journal_entry_line l JOIN nexora.journal_entry e ON e.id = l.journal_entry_id
                              WHERE l.account_id = {id} AND e.status = 'draft') AS "Value"
                """).FirstAsync(ct);
            if (drafts) return Error.Conflict("accounting.account_in_drafts", "Des brouillons utilisent ce compte.");
        }
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.account SET status = {r.Status}, updated_at = now() WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}
