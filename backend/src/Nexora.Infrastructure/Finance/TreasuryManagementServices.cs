using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Finance.Abstractions;
using Nexora.Contracts.Finance;
using Nexora.Domain.Common;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Finance;

/// <summary>Comptes de trésorerie et mouvements (phase 7) — voir <see cref="ITreasuryManagementService"/>.</summary>
public sealed class SqlTreasuryManagementService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IFinanceOperations ops, IDateTimeProvider clock) : ITreasuryManagementService
{
    private static readonly string[] Types = ["cash", "bank", "mobile_money", "card_processor", "wallet", "other"];
    /// <summary>Comptes dont le solde ne peut pas devenir négatif (argent physique ou porte-monnaie).</summary>
    private static readonly string[] NoOverdraft = ["cash", "mobile_money", "wallet"];

    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    internal sealed record AccountRow(Guid Id, string Name, string AccountType, string Currency, string Status, decimal Balance, int Movements);

    private sealed record LockedBalance(Guid Id, decimal Balance);

    /// <summary>Soldes des comptes, lignes verrouillées jusqu'à la fin de la transaction, toujours dans l'ordre des identifiants.</summary>
    internal async Task<Dictionary<Guid, decimal>> LockBalancesAsync(Guid[] ids, CancellationToken ct)
        => (await db.Database.SqlQuery<LockedBalance>($"""
                SELECT id AS "Id", current_balance AS "Balance" FROM nexora.financial_account
                WHERE id = ANY({ids}) AND company_id = {Company}
                ORDER BY id
                FOR UPDATE
                """).ToListAsync(ct))
            .ToDictionary(x => x.Id, x => x.Balance);

    private sealed class RefusedPosting(Error error) : InvalidOperationException(error.Message)
    {
        public Error Error { get; } = error;
    }

    internal async Task<AccountRow?> AccountAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<AccountRow>($"""
            SELECT fa.id AS "Id", fa.name AS "Name", fa.account_type AS "AccountType", c.code AS "Currency",
                   fa.status AS "Status", fa.current_balance AS "Balance",
                   (SELECT COUNT(*)::int FROM nexora.financial_transaction t WHERE t.financial_account_id = fa.id) AS "Movements"
            FROM nexora.financial_account fa JOIN nexora.currency c ON c.id = fa.currency_id
            WHERE fa.id = {id} AND fa.company_id = {Company}
            """).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<FinancialAccountDetailDto>> AccountsAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<FinancialAccountDetailDto>($"""
            SELECT fa.id AS "Id", fa.name AS "Name", fa.account_type AS "AccountType", c.code AS "Currency",
                   fa.account_number_masked AS "AccountNumberMasked", fa.ledger_account_id AS "LedgerAccountId",
                   a.code AS "LedgerAccountCode", a.name AS "LedgerAccountName", fa.current_balance AS "Balance",
                   fa.status AS "Status",
                   (SELECT COUNT(*)::int FROM nexora.financial_transaction t WHERE t.financial_account_id = fa.id) AS "TransactionCount",
                   (SELECT MAX(t.occurred_at) FROM nexora.financial_transaction t WHERE t.financial_account_id = fa.id) AS "LastMovementAt"
            FROM nexora.financial_account fa
            JOIN nexora.currency c ON c.id = fa.currency_id
            LEFT JOIN nexora.account a ON a.id = fa.ledger_account_id
            WHERE fa.company_id = {Company}
            ORDER BY fa.status = 'active' DESC, fa.account_type, fa.name
            """).ToListAsync(ct);

    public async Task<IReadOnlyList<LedgerAccountOptionDto>> LedgerOptionsAsync(string accountType, CancellationToken ct = default)
        => await db.Database.SqlQuery<LedgerAccountOptionDto>($"""
            SELECT a.id AS "Id", a.code AS "Code", a.name AS "Name", a.account_type AS "AccountType"
            FROM nexora.account a
            WHERE a.company_id = {Company} AND a.account_type = {accountType} AND a.is_postable AND a.status = 'active'
            ORDER BY a.code
            """).ToListAsync(ct);

    public async Task<FinanceLookupsDto> LookupsAsync(CancellationToken ct = default)
    {
        var currencies = await db.Database.SqlQuery<FinanceCurrencyOptionDto>($"""
            SELECT c.code AS "Code", c.name AS "Name", (c.id = co.default_currency_id) AS "IsCompanyDefault"
            FROM nexora.currency c CROSS JOIN nexora.company co
            WHERE co.id = {Company}
            ORDER BY (c.id = co.default_currency_id) DESC, c.code
            """).ToListAsync(ct);
        var suppliers = await db.Database.SqlQuery<FinanceSupplierOptionDto>($"""
            SELECT s.id AS "Id", s.name AS "Name" FROM nexora.supplier s
            WHERE s.company_id = {Company} AND s.status = 'active' ORDER BY s.name
            """).ToListAsync(ct);
        return new FinanceLookupsDto(currencies, suppliers);
    }

    private async Task<Result<(Guid CurrencyId, string Name)>> ValidateAsync(SaveFinancialAccountRequest r, Guid? existingId, CancellationToken ct)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 120) return Error.Validation("treasury.account_name", "Nom du compte requis (120 caractères au plus).");
        if (!Types.Contains(r.AccountType)) return Error.Validation("treasury.account_type", $"Type de compte « {r.AccountType} » inconnu.");
        var currency = await db.Database.SqlQuery<Guid?>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {r.Currency}""").FirstOrDefaultAsync(ct);
        if (currency is null) return Error.Validation("treasury.currency", $"Devise « {r.Currency} » inconnue.");
        var dup = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.financial_account WHERE company_id = {Company} AND lower(name) = lower({name})
                          AND ({existingId}::uuid IS NULL OR id <> {existingId})) AS "Value"
            """).FirstAsync(ct);
        if (dup) return Error.Conflict("treasury.account_exists", $"Un compte « {name} » existe déjà.");
        if (r.LedgerAccountId is { } gl)
        {
            var ok = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.account WHERE id = {gl} AND company_id = {Company} AND account_type = 'asset'
                              AND is_postable AND status = 'active') AS "Value"
                """).FirstAsync(ct);
            if (!ok) return Error.Validation("treasury.ledger_account", "Le compte comptable doit être un compte d'actif mouvementable.");
        }
        return Result.Success((currency.Value, name));
    }

    public async Task<Result<Guid>> CreateAccountAsync(SaveFinancialAccountRequest r, CancellationToken ct = default)
    {
        var v = await ValidateAsync(r, null, ct);
        if (!v.IsSuccess) return v.Error!;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.financial_account (id, company_id, account_type, name, account_number_masked, currency_id,
                                                  opening_balance, current_balance, ledger_account_id, status)
            VALUES ({id}, {Company}, {r.AccountType}, {v.Value.Name}, {Clean(r.AccountNumberMasked)}, {v.Value.CurrencyId},
                    0, 0, {r.LedgerAccountId}, 'active')
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> UpdateAccountAsync(Guid id, SaveFinancialAccountRequest r, CancellationToken ct = default)
    {
        var current = await AccountAsync(id, ct);
        if (current is null) return Error.NotFound("treasury.account_not_found", "Compte introuvable.");
        var v = await ValidateAsync(r, id, ct);
        if (!v.IsSuccess) return v.Error!;
        var ledger = await db.Database.SqlQuery<Guid?>($"""SELECT ledger_account_id AS "Value" FROM nexora.financial_account WHERE id = {id}""").FirstAsync(ct);
        // Type, devise et compte comptable figés dès le premier mouvement : sinon le solde du compte et le grand
        // livre divergeraient.
        if (current.Movements > 0 && (r.AccountType != current.AccountType || !string.Equals(r.Currency, current.Currency, StringComparison.OrdinalIgnoreCase)
                                      || r.LedgerAccountId != ledger))
            return Error.Conflict("treasury.account_locked",
                "Ce compte a déjà des mouvements : son type, sa devise et son compte comptable ne peuvent plus changer.");
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.financial_account
               SET name = {v.Value.Name}, account_type = {r.AccountType}, currency_id = {v.Value.CurrencyId},
                   account_number_masked = {Clean(r.AccountNumberMasked)}, ledger_account_id = {r.LedgerAccountId}, updated_at = now()
             WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result> SetAccountStatusAsync(Guid id, FinancialAccountStatusRequest r, CancellationToken ct = default)
    {
        if (r.Status is not ("active" or "inactive" or "closed")) return Error.Validation("treasury.status", "Statut invalide.");
        if (string.IsNullOrWhiteSpace(r.Reason)) return Error.Validation("treasury.reason_required", "Un motif est obligatoire.");
        var a = await AccountAsync(id, ct);
        if (a is null) return Error.NotFound("treasury.account_not_found", "Compte introuvable.");
        if (a.Status == "closed") return Error.Conflict("treasury.account_closed", "Un compte clôturé ne se rouvre pas.");
        if (r.Status == "closed" && a.Balance != 0m)
            return Error.Conflict("treasury.balance_not_zero", $"Solde de {a.Balance} {a.Currency} : transférez-le avant de clôturer le compte.");
        if (r.Status != "active")
        {
            var usedByRegister = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.cash_session s JOIN nexora.cash_register cr ON cr.id = s.cash_register_id
                              WHERE cr.financial_account_id = {id} AND s.status IN ('open', 'closing')) AS "Value"
                """).FirstAsync(ct);
            if (usedByRegister) return Error.Conflict("treasury.session_open", "Une session de caisse est ouverte sur ce compte.");
        }
        await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.financial_account
               SET status = {r.Status}, settings = settings || jsonb_build_object('status_reason', {r.Reason.Trim()}), updated_at = now()
             WHERE id = {id} AND company_id = {Company}
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<FinancialTransactionDto>>> TransactionsAsync(
        Guid accountId, DateOnly? from, DateOnly? to, int limit, CancellationToken ct = default)
    {
        if (await AccountAsync(accountId, ct) is null) return Error.NotFound("treasury.account_not_found", "Compte introuvable.");
        limit = Math.Clamp(limit, 1, 500);
        var rows = await db.Database.SqlQuery<FinancialTransactionDto>($"""
            SELECT t.id AS "Id", t.occurred_at AS "OccurredAt", t.transaction_type AS "TransactionType", t.amount AS "Amount",
                   c.code AS "Currency", t.reference_type AS "ReferenceType", t.reference_id AS "ReferenceId",
                   COALESCE(t.description,
                       CASE t.reference_type
                           WHEN 'sale' THEN 'Vente ' || (SELECT s.document_number FROM nexora.sale s WHERE s.id = t.reference_id)
                           WHEN 'payment' THEN 'Règlement ' || (SELECT p.payment_number FROM nexora.payment p WHERE p.id = t.reference_id)
                           WHEN 'expense' THEN 'Dépense ' || (SELECT e.document_number FROM nexora.expense e WHERE e.id = t.reference_id)
                           WHEN 'financial_transfer' THEN 'Transfert'
                           WHEN 'bank_charge' THEN 'Frais bancaires'
                           ELSE t.reference_type END) AS "Label",
                   (SELECT fa2.name FROM nexora.financial_transaction t2 JOIN nexora.financial_account fa2 ON fa2.id = t2.financial_account_id
                     WHERE t.reference_type = 'financial_transfer' AND t2.source_operation_uuid = t.source_operation_uuid
                       AND t2.id <> t.id LIMIT 1) AS "CounterpartyAccountName",
                   (SELECT je.entry_number FROM nexora.journal_entry je
                     WHERE je.company_id = t.company_id AND je.source_operation_uuid = t.source_operation_uuid LIMIT 1) AS "JournalEntryNumber",
                   t.status AS "Status", u.full_name AS "CreatedByName"
            FROM nexora.financial_transaction t
            JOIN nexora.currency c ON c.id = t.currency_id
            LEFT JOIN nexora.app_user u ON u.id = t.created_by
            WHERE t.financial_account_id = {accountId} AND t.company_id = {Company}
              AND ({from}::date IS NULL OR t.occurred_at >= {from}::date)
              AND ({to}::date IS NULL OR t.occurred_at < ({to}::date + 1))
            ORDER BY t.occurred_at DESC, t.created_at DESC
            LIMIT {limit}
            """).ToListAsync(ct);
        return Result.Success<IReadOnlyList<FinancialTransactionDto>>(rows);
    }

    public async Task<Result<PostingResultResponse>> TransferAsync(TreasuryTransferRequest r, CancellationToken ct = default)
    {
        if (r.Amount <= 0m) return Error.Validation("treasury.amount", "Le montant doit être positif.");
        if (r.FromAccountId == r.ToAccountId) return Error.Validation("treasury.same_account", "Les comptes source et destination doivent être différents.");
        var from = await AccountAsync(r.FromAccountId, ct);
        var to = await AccountAsync(r.ToAccountId, ct);
        if (from is null || to is null) return Error.NotFound("treasury.account_not_found", "Compte introuvable.");
        if (from.Status != "active" || to.Status != "active")
            return Error.Conflict("treasury.account_inactive", "Les deux comptes doivent être actifs.");
        if (!string.Equals(from.Currency, to.Currency, StringComparison.OrdinalIgnoreCase))
            return Error.Validation("treasury.currency_mismatch",
                $"Transfert entre devises ({from.Currency} → {to.Currency}) non pris en charge : passez par une opération de change.");
        var amount = Math.Round(r.Amount, 2, MidpointRounding.ToEven);

        // Contrôle du découvert sous verrou (audit 2026-10 éd. 2) : lu sans verrou, deux transferts simultanés passaient
        // chacun le contrôle et mettaient la caisse en négatif. Les deux comptes sont verrouillés dans un ordre fixe
        // (deux transferts croisés A→B et B→A ne s'interbloquent pas).
        Result<PostingResultResponse> posted;
        try
        {
            posted = await uow.ExecuteInTransactionAsync(async token =>
            {
                var balances = await LockBalancesAsync([from.Id, to.Id], token);
                if (NoOverdraft.Contains(from.AccountType) && balances[from.Id] < amount && !await IsReplayAsync(r.OperationUuid, token))
                    throw new RefusedPosting(Error.Conflict("treasury.insufficient_balance",
                        $"Solde insuffisant sur « {from.Name} » : {balances[from.Id]} {from.Currency}."));
                var result = await ops.RecordTransferAsync(
                    new RecordTransferRequest(r.OperationUuid, clock.UtcNow, from.Id, to.Id, amount, from.Currency), token);
                return result.IsSuccess ? result : throw new RefusedPosting(result.Error!);
            }, ct);
        }
        catch (RefusedPosting refused) { return refused.Error; }
        if (posted.IsSuccess && !string.IsNullOrWhiteSpace(r.Note))
            await DescribeAsync(r.OperationUuid, $"Transfert — {r.Note.Trim()}", ct);
        return posted;
    }

    /// <summary>
    /// Solde d'ouverture (audit 2026-10, A-023). Un compte était toujours créé à zéro et aucune fonction ne permettait
    /// d'enregistrer l'argent déjà en caisse ou en banque à la mise en service : trésorerie et grand livre partaient
    /// d'un écart égal à ce montant. Seulement sur un compte sans mouvement ; idempotent par identifiant d'opération.
    /// </summary>
    public async Task<Result<PostingResultResponse>> RecordOpeningBalanceAsync(Guid accountId, TreasuryOpeningBalanceRequest r,
        CancellationToken ct = default)
    {
        if (r.Amount == 0m) return Error.Validation("treasury.amount", "Le solde d'ouverture doit être différent de zéro.");
        var account = await AccountAsync(accountId, ct);
        if (account is null) return Error.NotFound("treasury.account_not_found", "Compte introuvable.");
        if (account.Status != "active") return Error.Conflict("treasury.account_inactive", "Le compte doit être actif.");
        var replay = await IsReplayAsync(r.OperationUuid, ct);
        if (account.Movements > 0 && !replay)
            return Error.Conflict("treasury.opening_after_movements",
                "Le compte a déjà des mouvements : le solde d'ouverture se saisit avant toute opération.");
        if (r.Amount < 0m && NoOverdraft.Contains(account.AccountType))
            return Error.Validation("treasury.amount", "Une caisse ou un porte-monnaie ne s'ouvre pas en négatif.");
        var at = r.Date is { } d ? BusinessDate.Noon(d) : clock.UtcNow;
        if (at > clock.UtcNow) return Error.Validation("treasury.opening_date", "La date d'ouverture ne peut pas être future.");

        var amount = Math.Round(r.Amount, 2, MidpointRounding.ToEven);
        var posted = await ops.RecordTreasuryOpeningAsync(new RecordTreasuryOpeningRequest(r.OperationUuid, at, account.Id, amount, account.Currency), ct);
        if (posted.IsSuccess)
            await DescribeAsync(r.OperationUuid, string.IsNullOrWhiteSpace(r.Note) ? "Solde d'ouverture" : $"Solde d'ouverture — {r.Note.Trim()}", ct);
        return posted;
    }

    public async Task<Result<PostingResultResponse>> ChargeAsync(TreasuryChargeRequest r, CancellationToken ct = default)
    {
        if (r.Amount <= 0m) return Error.Validation("treasury.amount", "Le montant doit être positif.");
        if (string.IsNullOrWhiteSpace(r.Description)) return Error.Validation("treasury.description", "Libellé obligatoire.");
        var a = await AccountAsync(r.AccountId, ct);
        if (a is null) return Error.NotFound("treasury.account_not_found", "Compte introuvable.");
        if (a.Status != "active") return Error.Conflict("treasury.account_inactive", "Le compte doit être actif.");
        if (a.AccountType is not ("bank" or "mobile_money" or "card_processor"))
            return Error.Validation("treasury.charge_account", "Les frais s'imputent sur un compte bancaire, mobile money ou de monétique.");
        var amount = Math.Round(r.Amount, 2, MidpointRounding.ToEven);
        var posted = await ops.RecordBankChargeAsync(new RecordBankChargeRequest(r.OperationUuid, clock.UtcNow, a.Id, amount, a.Currency, r.Description.Trim()), ct);
        if (posted.IsSuccess) await DescribeAsync(r.OperationUuid, r.Description.Trim(), ct);
        return posted;
    }

    private Task<bool> IsReplayAsync(Guid op, CancellationToken ct)
        => db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.journal_entry WHERE company_id = {Company} AND source_operation_uuid = {op}) AS "Value"
            """).FirstAsync(ct);

    private async Task DescribeAsync(Guid op, string description, CancellationToken ct)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.financial_transaction SET description = {description}, updated_at = now()
            WHERE company_id = {Company} AND source_operation_uuid = {op} AND description IS NULL
            """, ct);

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>Dépenses (phase 7) — voir <see cref="IExpenseService"/>.</summary>
public sealed class SqlExpenseService(
    NexoraDbContext db, ITenantContext tenant, IUnitOfWork uow, IFinanceOperations ops,
    IDocumentNumberGenerator numbers, IDateTimeProvider clock, IBusinessCalendar calendar) : IExpenseService
{
    private Guid Company => tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();

    public async Task<IReadOnlyList<ExpenseCategoryDto>> CategoriesAsync(CancellationToken ct = default)
        => await db.Database.SqlQuery<ExpenseCategoryDto>($"""
            SELECT c.id AS "Id", c.name AS "Name", c.code AS "Code", c.ledger_account_id AS "LedgerAccountId",
                   a.code AS "LedgerAccountCode", a.name AS "LedgerAccountName", c.status AS "Status"
            FROM nexora.expense_category c LEFT JOIN nexora.account a ON a.id = c.ledger_account_id
            WHERE c.company_id = {Company}
            ORDER BY c.status = 'active' DESC, c.name
            """).ToListAsync(ct);

    private async Task<Result> ValidateCategoryAsync(SaveExpenseCategoryRequest r, Guid? id, CancellationToken ct)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length is 0 or > 120) return Error.Validation("expense.category_name", "Nom de catégorie requis.");
        if (r.Status is not ("active" or "inactive")) return Error.Validation("expense.category_status", "Statut invalide.");
        var dup = await db.Database.SqlQuery<bool>($"""
            SELECT EXISTS(SELECT 1 FROM nexora.expense_category WHERE company_id = {Company} AND lower(name) = lower({name})
                          AND ({id}::uuid IS NULL OR id <> {id})) AS "Value"
            """).FirstAsync(ct);
        if (dup) return Error.Conflict("expense.category_exists", $"La catégorie « {name} » existe déjà.");
        if (r.LedgerAccountId is { } gl && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.account WHERE id = {gl} AND company_id = {Company} AND account_type = 'expense'
                              AND is_postable AND status = 'active') AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("expense.ledger_account", "Le compte comptable doit être un compte de charge mouvementable.");
        return Result.Success();
    }

    public async Task<Result<Guid>> CreateCategoryAsync(SaveExpenseCategoryRequest r, CancellationToken ct = default)
    {
        var v = await ValidateCategoryAsync(r, null, ct);
        if (!v.IsSuccess) return v.Error!;
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.expense_category (id, company_id, name, code, ledger_account_id, status)
            VALUES ({id}, {Company}, {r.Name.Trim()}, {Clean(r.Code)}, {r.LedgerAccountId}, {r.Status})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> UpdateCategoryAsync(Guid id, SaveExpenseCategoryRequest r, CancellationToken ct = default)
    {
        var v = await ValidateCategoryAsync(r, id, ct);
        if (!v.IsSuccess) return v.Error!;
        var n = await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.expense_category SET name = {r.Name.Trim()}, code = {Clean(r.Code)}, ledger_account_id = {r.LedgerAccountId},
                   status = {r.Status}, updated_at = now()
            WHERE id = {id} AND company_id = {Company}
            """, ct);
        if (n == 0) return Error.NotFound("expense.category_not_found", "Catégorie introuvable.");
        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }

    private FormattableString Select(Guid? id, string? status, DateOnly? from, DateOnly? to, int limit) => $"""
        SELECT e.id AS "Id", e.document_number AS "DocumentNumber", e.description AS "Description",
               e.category_id AS "CategoryId", c.name AS "CategoryName", e.supplier_id AS "SupplierId", su.name AS "SupplierName",
               e.amount AS "Amount", e.tax_amount AS "TaxAmount", (e.amount + e.tax_amount)::numeric AS "Total",
               cur.code AS "Currency", e.expense_date AS "ExpenseDate", e.reference AS "Reference", e.status AS "Status",
               e.financial_account_id AS "FinancialAccountId", fa.name AS "FinancialAccountName",
               e.created_by AS "CreatedBy", cu.full_name AS "CreatedByName", au.full_name AS "ApprovedByName", e.approved_at AS "ApprovedAt",
               e.paid_at AS "PaidAt", pu.full_name AS "PaidByName", je.entry_number AS "JournalEntryNumber",
               e.decision_reason AS "DecisionReason", e.created_at AS "CreatedAt"
        FROM nexora.expense e
        JOIN nexora.currency cur ON cur.id = e.currency_id
        LEFT JOIN nexora.expense_category c ON c.id = e.category_id
        LEFT JOIN nexora.supplier su ON su.id = e.supplier_id
        LEFT JOIN nexora.financial_account fa ON fa.id = e.financial_account_id
        LEFT JOIN nexora.app_user cu ON cu.id = e.created_by
        LEFT JOIN nexora.app_user au ON au.id = e.approved_by
        LEFT JOIN nexora.app_user pu ON pu.id = e.paid_by
        LEFT JOIN nexora.journal_entry je ON je.id = e.journal_entry_id
        WHERE e.company_id = {Company} AND ({id}::uuid IS NULL OR e.id = {id})
          AND ({status}::text IS NULL OR e.status = {status})
          AND ({from}::date IS NULL OR e.expense_date >= {from}) AND ({to}::date IS NULL OR e.expense_date <= {to})
        ORDER BY e.expense_date DESC, e.created_at DESC
        LIMIT {limit}
        """;

    public async Task<IReadOnlyList<ExpenseDto>> ListAsync(string? status, DateOnly? from, DateOnly? to, int limit, CancellationToken ct = default)
        => await db.Database.SqlQuery<ExpenseDto>(Select(null, status, from, to, Math.Clamp(limit, 1, 500))).ToListAsync(ct);

    public async Task<Result<ExpenseDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var e = await db.Database.SqlQuery<ExpenseDto>(Select(id, null, null, null, 1)).FirstOrDefaultAsync(ct);
        return e is null ? Error.NotFound("expense.not_found", "Dépense introuvable.") : Result.Success(e);
    }

    private sealed record Head(Guid Id, string Status, Guid? CreatedBy, decimal Amount, decimal TaxAmount, string Currency,
        string Description, Guid? CategoryId, string DocumentNumber, DateOnly ExpenseDate);

    private async Task<Head?> HeadAsync(Guid id, CancellationToken ct)
        => await db.Database.SqlQuery<Head>($"""
            SELECT e.id AS "Id", e.status AS "Status", e.created_by AS "CreatedBy", e.amount AS "Amount", e.tax_amount AS "TaxAmount",
                   c.code AS "Currency", e.description AS "Description", e.category_id AS "CategoryId",
                   e.document_number AS "DocumentNumber", e.expense_date AS "ExpenseDate"
            FROM nexora.expense e JOIN nexora.currency c ON c.id = e.currency_id
            WHERE e.id = {id} AND e.company_id = {Company}
            FOR UPDATE OF e
            """).FirstOrDefaultAsync(ct);

    private async Task<Result<Guid>> ValidateAsync(SaveExpenseRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Description) || r.Description.Trim().Length > 500)
            return Error.Validation("expense.description", "Description obligatoire (500 caractères au plus).");
        if (r.Amount <= 0m) return Error.Validation("expense.amount", "Le montant hors taxe doit être positif.");
        if (r.TaxAmount < 0m || r.TaxAmount > r.Amount) return Error.Validation("expense.tax", "Taxe invalide (entre 0 et le montant HT).");
        if (r.ExpenseDate > calendar.Today.AddDays(1))
            return Error.Validation("expense.date", "La date de la dépense ne peut être dans le futur.");
        var category = await db.Database.SqlQuery<string?>($"""
            SELECT status AS "Value" FROM nexora.expense_category WHERE id = {r.CategoryId} AND company_id = {Company}
            """).FirstOrDefaultAsync(ct);
        if (category is null) return Error.Validation("expense.category", "Catégorie inconnue.");
        if (category != "active") return Error.Validation("expense.category_inactive", "Cette catégorie n'est plus active.");
        if (r.SupplierId is { } sid && !await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS(SELECT 1 FROM nexora.supplier WHERE id = {sid} AND company_id = {Company}) AS "Value"
                """).FirstAsync(ct))
            return Error.Validation("expense.supplier", "Fournisseur inconnu.");
        var currency = await db.Database.SqlQuery<Guid?>($"""SELECT id AS "Value" FROM nexora.currency WHERE code = {r.Currency}""").FirstOrDefaultAsync(ct);
        return currency is { } c ? Result.Success(c) : Error.Validation("expense.currency", $"Devise « {r.Currency} » inconnue.");
    }

    public async Task<Result<Guid>> CreateAsync(SaveExpenseRequest r, CancellationToken ct = default)
    {
        var v = await ValidateAsync(r, ct);
        if (!v.IsSuccess) return v.Error!;
        var id = Guid.NewGuid();
        var number = await numbers.NextAsync("expense", null, ct);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.expense (id, company_id, category_id, supplier_id, document_number, description, amount, tax_amount,
                                        currency_id, expense_date, reference, status, submitted_at, created_by)
            VALUES ({id}, {Company}, {r.CategoryId}, {r.SupplierId}, {number}, {r.Description.Trim()}, {Math.Round(r.Amount, 2)},
                    {Math.Round(r.TaxAmount, 2)}, {v.Value}, {r.ExpenseDate}, {Clean(r.Reference)},
                    {(r.Submit ? "submitted" : "draft")}, {(r.Submit ? clock.UtcNow : (DateTimeOffset?)null)}, {tenant.UserId})
            """, ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(id);
    }

    public async Task<Result> UpdateAsync(Guid id, SaveExpenseRequest r, CancellationToken ct = default)
        => await InTransaction(async token =>
        {
            var h = await HeadAsync(id, token);
            if (h is null) return Error.NotFound("expense.not_found", "Dépense introuvable.");
            if (h.Status is not ("draft" or "submitted"))
                return Error.Conflict("expense.locked", $"La dépense {h.DocumentNumber} est « {h.Status} » : elle n'est plus modifiable.");
            var v = await ValidateAsync(r, token);
            if (!v.IsSuccess) return v.Error!;
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.expense SET category_id = {r.CategoryId}, supplier_id = {r.SupplierId}, description = {r.Description.Trim()},
                       amount = {Math.Round(r.Amount, 2)}, tax_amount = {Math.Round(r.TaxAmount, 2)}, currency_id = {v.Value},
                       expense_date = {r.ExpenseDate}, reference = {Clean(r.Reference)},
                       status = CASE WHEN {r.Submit} THEN 'submitted' ELSE status END,
                       submitted_at = CASE WHEN {r.Submit} AND submitted_at IS NULL THEN now() ELSE submitted_at END, updated_at = now()
                WHERE id = {id}
                """, token);
            return Result.Success();
        }, ct);

    public Task<Result> SubmitAsync(Guid id, CancellationToken ct = default)
        => Transition(id, ["draft"], "submitted", null, "submitted_at = now()", ct);

    public async Task<Result> ApproveAsync(Guid id, CancellationToken ct = default)
        => await InTransaction(async token =>
        {
            var h = await HeadAsync(id, token);
            if (h is null) return Error.NotFound("expense.not_found", "Dépense introuvable.");
            if (h.Status != "submitted") return Error.Conflict("expense.transition", $"Seule une dépense soumise s'approuve (état « {h.Status} »).");
            // Quatre yeux : le demandeur n'approuve jamais sa propre dépense.
            if (h.CreatedBy is { } creator && creator == tenant.UserId)
                return Error.Forbidden("expense.self_approval", "Vous ne pouvez pas approuver votre propre dépense.");
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.expense SET status = 'approved', approved_by = {tenant.UserId}, approved_at = now(), updated_at = now() WHERE id = {id}
                """, token);
            return Result.Success();
        }, ct);

    public Task<Result> RejectAsync(Guid id, ExpenseDecisionRequest r, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(r.Reason)
            ? Task.FromResult<Result>(Error.Validation("expense.reason_required", "Le motif du refus est obligatoire."))
            : Transition(id, ["submitted"], "rejected", r.Reason.Trim(), null, ct);

    public Task<Result> CancelAsync(Guid id, ExpenseDecisionRequest r, CancellationToken ct = default)
        => string.IsNullOrWhiteSpace(r.Reason)
            ? Task.FromResult<Result>(Error.Validation("expense.reason_required", "Le motif d'annulation est obligatoire."))
            : Transition(id, ["draft", "submitted", "approved"], "cancelled", r.Reason.Trim(), null, ct);

    private Task<Result> Transition(Guid id, string[] from, string to, string? reason, string? extra, CancellationToken ct)
        => InTransaction(async token =>
        {
            var h = await HeadAsync(id, token);
            if (h is null) return Error.NotFound("expense.not_found", "Dépense introuvable.");
            if (!from.Contains(h.Status)) return Error.Conflict("expense.transition", $"Transition interdite : {h.Status} → {to}.");
            if (extra is not null)
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.expense SET status = {to}, submitted_at = now(), updated_at = now() WHERE id = {id}", token);
            else
                await db.Database.ExecuteSqlAsync($"UPDATE nexora.expense SET status = {to}, decision_reason = {reason}, updated_at = now() WHERE id = {id}", token);
            return Result.Success();
        }, ct);

    public async Task<Result<ExpenseDto>> PayAsync(Guid id, PayExpenseRequest r, CancellationToken ct = default)
    {
        var paid = await InTransaction(async token =>
        {
            var h = await HeadAsync(id, token);
            if (h is null) return Error.NotFound("expense.not_found", "Dépense introuvable.");
            if (h.Status == "paid")
            {
                // Rejeu de la même opération : on renvoie la dépense payée ; une autre opération est refusée.
                var same = await db.Database.SqlQuery<bool>($"""
                    SELECT EXISTS(SELECT 1 FROM nexora.expense e JOIN nexora.journal_entry je ON je.id = e.journal_entry_id
                                  WHERE e.id = {id} AND je.source_operation_uuid = {r.OperationUuid}) AS "Value"
                    """).FirstAsync(token);
                return same ? Result.Success() : Error.Conflict("expense.already_paid", $"La dépense {h.DocumentNumber} est déjà payée.");
            }
            if (h.Status != "approved") return Error.Conflict("expense.not_approved", "La dépense doit être approuvée avant d'être payée.");

            var treasury = new SqlTreasuryManagementService(db, tenant, uow, ops, clock);
            var account = await treasury.AccountAsync(r.FinancialAccountId, token);
            if (account is null || account.Status != "active") return Error.Validation("expense.account", "Compte de paiement inconnu ou inactif.");
            if (!string.Equals(account.Currency, h.Currency, StringComparison.OrdinalIgnoreCase))
                return Error.Validation("expense.currency_mismatch", $"Le compte « {account.Name} » est en {account.Currency}, la dépense en {h.Currency}.");
            var total = h.Amount + h.TaxAmount;
            // Solde relu sous verrou (audit 2026-10 éd. 2) : deux paiements simultanés sur la même caisse passaient chacun.
            var balance = (await treasury.LockBalancesAsync([account.Id], token))[account.Id];
            if (account.AccountType is "cash" or "mobile_money" or "wallet" && balance < total)
                return Error.Conflict("expense.insufficient_balance", $"Solde insuffisant sur « {account.Name} » : {balance} {account.Currency}.");

            var ledgerCode = h.CategoryId is { } cat
                ? await db.Database.SqlQuery<string?>($"""
                    SELECT a.code AS "Value" FROM nexora.expense_category c JOIN nexora.account a ON a.id = c.ledger_account_id WHERE c.id = {cat}
                    """).FirstOrDefaultAsync(token)
                : null;
            var posted = await ops.RecordExpenseAsync(new RecordExpenseRequest(r.OperationUuid, id, clock.UtcNow, h.Amount, h.TaxAmount,
                h.Currency, account.Id, ledgerCode, $"Dépense {h.DocumentNumber} — {h.Description}"), token);
            if (!posted.IsSuccess) throw new ExpensePostingException(posted.Error!);

            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.expense SET status = 'paid', financial_account_id = {account.Id}, paid_at = now(), paid_by = {tenant.UserId},
                       journal_entry_id = {posted.Value!.JournalEntryId}, updated_at = now()
                WHERE id = {id}
                """, token);
            return Result.Success();
        }, ct);
        return paid.IsSuccess ? await GetAsync(id, ct) : paid.Error!;
    }

    private sealed class ExpensePostingException(Error error) : InvalidOperationException(error.Message)
    {
        public Error Error { get; } = error;
    }

    private async Task<Result> InTransaction(Func<CancellationToken, Task<Result>> work, CancellationToken ct)
    {
        try
        {
            return await uow.ExecuteInTransactionAsync(async token =>
            {
                var r = await work(token);
                // Un refus métier ne doit rien écrire : on annule la transaction.
                if (!r.IsSuccess) throw new RefusedException(r.Error!);
                return r;
            }, ct);
        }
        catch (RefusedException ex) { return ex.Error; }
        catch (ExpensePostingException ex) { return ex.Error; }
    }

    private sealed class RefusedException(Error error) : Exception(error.Message)
    {
        public Error Error { get; } = error;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
