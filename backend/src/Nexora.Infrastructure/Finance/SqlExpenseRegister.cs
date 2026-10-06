using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Infrastructure.Persistence;
using Nexora.Domain.Common;

namespace Nexora.Infrastructure.Finance;

/// <summary>Pièce de dépense payée (recette BUG-016) — voir <see cref="IExpenseRegister"/>.</summary>
public sealed class SqlExpenseRegister(NexoraDbContext db, ITenantContext tenant) : IExpenseRegister
{
    public async Task LinkJournalEntryAsync(Guid expenseId, Guid journalEntryId, CancellationToken ct = default)
        => await db.Database.ExecuteSqlAsync($"""
            UPDATE nexora.expense SET journal_entry_id = {journalEntryId}, updated_at = now()
            WHERE id = {expenseId} AND journal_entry_id IS NULL
            """, ct);

    public async Task RegisterPaidAsync(Guid expenseId, DateOnly date, decimal net, decimal tax, string currency,
        Guid financialAccountId, string? description, CancellationToken ct = default)
    {
        var company = tenant.ActiveCompanyId ?? throw TenantErrors.ActiveCompanyRequired();
        // Rejeu (même pièce) : rien à créer, et aucun numéro de séquence consommé pour rien.
        if (await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT 1 FROM nexora.expense WHERE id = {expenseId}) AS "Value"
                """).FirstAsync(ct))
            return;
        var number = (await db.Database.SqlQuery<string>($"""
            SELECT nexora.next_document_number({company}, 'expense', {(Guid?)null}, {date.Year}) AS "Value"
            """).ToListAsync(ct))[0];
        // Idempotent : un rejeu (même identifiant de pièce) ne crée pas de doublon.
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO nexora.expense (id, company_id, financial_account_id, document_number, description, amount,
                                        tax_amount, currency_id, expense_date, status, paid_at, created_by)
            VALUES ({expenseId}, {company}, {financialAccountId}, {number}, {description ?? "Dépense"}, {net}, {tax},
                    (SELECT id FROM nexora.currency WHERE code = {currency}), {date}, 'paid', now(), {tenant.UserId})
            ON CONFLICT (id) DO NOTHING
            """, ct);
    }
}
