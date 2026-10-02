using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance;
using Nexora.Application.Integrations;
using Nexora.Contracts.Integrations;
using Nexora.Contracts.Finance;
using Nexora.Infrastructure.Persistence;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 7 — trésorerie et dépenses : comptes gérés depuis l'application, transferts (qui
/// échouaient tous), contrôles de devise / solde / compte identique, historique des mouvements, dépenses avec
/// approbation à quatre yeux et paiement comptabilisé une seule fois.
/// </summary>
[Collection("finance")]
public sealed class TreasuryManagementTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");
    private static readonly Guid CashAccount = Guid.Parse("dce00000-0000-0000-0000-00000000a051");
    private static readonly Guid UsdBank = Guid.Parse("dce00000-0000-0000-0000-00000000a052");

    private AsyncServiceScope Scope(Guid? user = null)
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(user ?? Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private static string U() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<Guid> NewUser()
    {
        var id = Guid.NewGuid();
        await using var c = new Npgsql.NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand($"""
            INSERT INTO nexora.app_user (id, email, password_hash, full_name, status)
            VALUES ('{id}', 'TRS-{id:N}@test.demo', crypt('x', gen_salt('bf', 4)), 'Approbateur', 'active')
            """, c);
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<decimal> Balance(IServiceProvider sp, Guid account)
        => await sp.GetRequiredService<NexoraDbContext>().Database
            .SqlQuery<decimal>($"SELECT current_balance AS \"Value\" FROM nexora.financial_account WHERE id = {account}").FirstAsync();

    [Fact]
    public async Task Accounts_are_managed_and_a_transfer_moves_money_once_with_controls()
    {
        await using var s = Scope();
        var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();

        var name = $"Coffre {U()}";
        var created = await t.CreateAccountAsync(new SaveFinancialAccountRequest(name, "cash", "HTG", null, null));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        (await t.CreateAccountAsync(new SaveFinancialAccountRequest(name.ToLowerInvariant(), "cash", "HTG", null, null)))
            .Error!.Code.Should().Be("treasury.account_exists");
        (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"X {U()}", "cash", "ZZZ", null, null)))
            .Error!.Code.Should().Be("treasury.currency");
        var safe = created.Value;
        (await Balance(s.ServiceProvider, safe)).Should().Be(0m, "un nouveau compte part à zéro");

        // Transfert caisse → coffre : les deux jambes, une seule écriture, rejouable sans doublon.
        var cashBefore = await Balance(s.ServiceProvider, CashAccount);
        var op = Guid.NewGuid();
        var r = await t.TransferAsync(new TreasuryTransferRequest(op, CashAccount, safe, 150m, "Mise au coffre"));
        r.IsSuccess.Should().BeTrue(r.Error?.Message);
        (await t.TransferAsync(new TreasuryTransferRequest(op, CashAccount, safe, 150m, "Mise au coffre"))).Value!.WasReplay.Should().BeTrue();
        (await Balance(s.ServiceProvider, CashAccount)).Should().Be(cashBefore - 150m);
        (await Balance(s.ServiceProvider, safe)).Should().Be(150m);

        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), safe, safe, 1m, null))).Error!.Code.Should().Be("treasury.same_account");
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), safe, UsdBank, 1m, null))).Error!.Code.Should().Be("treasury.currency_mismatch");
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), safe, CashAccount, 151m, null))).Error!.Code.Should().Be("treasury.insufficient_balance");

        var history = (await t.TransactionsAsync(safe, null, null, 50)).Value!;
        history.Should().ContainSingle();
        history[0].Amount.Should().Be(150m);
        history[0].Label.Should().Contain("Mise au coffre");
        history[0].JournalEntryNumber.Should().NotBeNullOrEmpty();

        // Compte mouvementé : devise figée ; clôture refusée tant qu'il reste un solde.
        (await t.UpdateAccountAsync(safe, new SaveFinancialAccountRequest(name, "cash", "USD", null, null))).Error!.Code.Should().Be("treasury.account_locked");
        (await t.SetAccountStatusAsync(safe, new FinancialAccountStatusRequest("closed", "Plus utilisé"))).Error!.Code.Should().Be("treasury.balance_not_zero");
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), safe, CashAccount, 150m, null))).IsSuccess.Should().BeTrue();
        (await t.SetAccountStatusAsync(safe, new FinancialAccountStatusRequest("closed", "Plus utilisé"))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Expense_needs_another_approver_and_is_paid_and_posted_once()
    {
        Guid expenseId, category;
        await using (var s = Scope())
        {
            var e = s.ServiceProvider.GetRequiredService<IExpenseService>();
            var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
            var ledger = (await t.LedgerOptionsAsync("expense")).First(a => a.Code == "6800");
            category = (await e.CreateCategoryAsync(new SaveExpenseCategoryRequest($"Entretien {U()}", null, ledger.Id))).Value;
            (await e.CreateAsync(new SaveExpenseRequest("", category, null, 10m, 0m, "HTG", DateOnly.FromDateTime(DateTime.UtcNow), null)))
                .Error!.Code.Should().Be("expense.description");
            var created = await e.CreateAsync(new SaveExpenseRequest("Réparation climatiseur", category, null, 1000m, 100m, "HTG",
                DateOnly.FromDateTime(DateTime.UtcNow), "FACT-77"));
            created.IsSuccess.Should().BeTrue(created.Error?.Message);
            expenseId = created.Value;
            (await e.ApproveAsync(expenseId)).Error!.Code.Should().Be("expense.self_approval");
            (await e.PayAsync(expenseId, new PayExpenseRequest(Guid.NewGuid(), CashAccount))).Error!.Code.Should().Be("expense.not_approved");
        }

        var approver = await NewUser();
        await using (var s = Scope(approver))
        {
            var e = s.ServiceProvider.GetRequiredService<IExpenseService>();
            (await e.ApproveAsync(expenseId)).IsSuccess.Should().BeTrue();
            (await e.PayAsync(expenseId, new PayExpenseRequest(Guid.NewGuid(), UsdBank))).Error!.Code.Should().Be("expense.currency_mismatch");

            var before = await Balance(s.ServiceProvider, CashAccount);
            var op = Guid.NewGuid();
            var paid = await e.PayAsync(expenseId, new PayExpenseRequest(op, CashAccount));
            paid.IsSuccess.Should().BeTrue(paid.Error?.Message);
            paid.Value!.Status.Should().Be("paid");
            paid.Value.Total.Should().Be(1100m);
            paid.Value.JournalEntryNumber.Should().NotBeNullOrEmpty();
            (await Balance(s.ServiceProvider, CashAccount)).Should().Be(before - 1100m);

            (await e.PayAsync(expenseId, new PayExpenseRequest(op, CashAccount))).IsSuccess.Should().BeTrue("rejeu de la même opération");
            (await e.PayAsync(expenseId, new PayExpenseRequest(Guid.NewGuid(), CashAccount))).Error!.Code.Should().Be("expense.already_paid");
            (await Balance(s.ServiceProvider, CashAccount)).Should().Be(before - 1100m, "jamais payée deux fois");

            // Écriture : 1 000 en charge 6800, 100 de taxe récupérable, 1 100 au crédit de la caisse.
            var lines = await s.ServiceProvider.GetRequiredService<NexoraDbContext>().Database.SqlQuery<decimal>($"""
                SELECT l.base_debit AS "Value" FROM nexora.journal_entry_line l JOIN nexora.account a ON a.id = l.account_id
                JOIN nexora.expense x ON x.journal_entry_id = l.journal_entry_id
                WHERE x.id = {expenseId} AND a.code = '6800'
                """).ToListAsync();
            lines.Should().ContainSingle().Which.Should().Be(1000m);
        }

        await using (var s = Scope())
        {
            var e = s.ServiceProvider.GetRequiredService<IExpenseService>();
            var other = (await e.CreateAsync(new SaveExpenseRequest("Taxi", category, null, 50m, 0m, "HTG", DateOnly.FromDateTime(DateTime.UtcNow), null))).Value;
            (await e.CancelAsync(other, new ExpenseDecisionRequest(" "))).Error!.Code.Should().Be("expense.reason_required");
            (await e.CancelAsync(other, new ExpenseDecisionRequest("Saisie en double"))).IsSuccess.Should().BeTrue();
            (await e.GetAsync(other)).Value!.DecisionReason.Should().Be("Saisie en double");
        }
    }
    [Fact]
    public async Task Bank_reconciliation_is_controlled_from_import_to_validation()
    {
        await using var s = Scope();
        var t = s.ServiceProvider.GetRequiredService<ITreasuryManagementService>();
        var bank = s.ServiceProvider.GetRequiredService<IBankReconciliationService>();
        var htgBank = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Banque HTG {U()}", "bank", "HTG", "****1234", null))).Value;
        var otherBank = (await t.CreateAccountAsync(new SaveFinancialAccountRequest($"Autre banque {U()}", "bank", "HTG", null, null))).Value;
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), CashAccount, htgBank, 500m, "Dépôt"))).IsSuccess.Should().BeTrue();
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), CashAccount, otherBank, 75m, null))).IsSuccess.Should().BeTrue();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        (await bank.ImportStatementAsync(new ImportStatementRequest(htgBank, "R1", today, 0m, 999m,
            [new StatementLineInput(today, 500m, "Dépôt", "DEP-1")]))).Error!.Code.Should().Be("integrations.statement_unbalanced");

        var first = (await bank.ImportStatementAsync(new ImportStatementRequest(htgBank, "R1", today, 0m, 425m,
            [new StatementLineInput(today, 500m, "Dépôt", "DEP-1"), new StatementLineInput(today, -75m, "Frais de tenue", "FEE-1")]))).Value!;
        first.AutoMatched.Should().Be(1);

        // Relevé suivant reprenant la même ligne : reconnue comme doublon (la comparaison « 500 » / « 500.0000 » échouait).
        var second = await bank.ImportStatementAsync(new ImportStatementRequest(htgBank, "R2", today, 425m, 925m,
            [new StatementLineInput(today, 500m, "Dépôt", "DEP-1")]));
        second.Error!.Code.Should().Be("integrations.statement_all_duplicates");

        var fee = (await bank.LinesAsync(first.StatementId)).Value!.Single(l => l.Amount == -75m);
        (await bank.ValidateAsync(first.StatementId)).Error!.Code.Should().Be("integrations.statement_unmatched");

        // Un mouvement d'un autre compte, même montant, est refusé.
        var foreign = (await t.TransactionsAsync(otherBank, null, null, 10)).Value!.Single();
        (await bank.MatchLineAsync(fee.Id, new MatchLineRequest(foreign.Id))).Error!.Code.Should().Be("integrations.match_account");

        // Les frais sont enregistrés, proposés comme candidat, rapprochés, puis le relevé est validé.
        (await t.ChargeAsync(new TreasuryChargeRequest(Guid.NewGuid(), htgBank, 75m, "Frais de tenue de compte"))).IsSuccess.Should().BeTrue();
        var candidate = (await bank.CandidatesAsync(fee.Id)).Value!.Single();
        (await bank.MatchLineAsync(fee.Id, new MatchLineRequest(candidate.TransactionId))).IsSuccess.Should().BeTrue();
        (await bank.ValidateAsync(first.StatementId)).IsSuccess.Should().BeTrue();
        (await bank.UnmatchLineAsync(fee.Id)).Error!.Code.Should().Be("integrations.statement_reconciled");

        // Nettoyage : comptes soldés vers la caisse puis clôturés (la position de trésorerie partagée reste intacte).
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), htgBank, CashAccount, 425m, null))).IsSuccess.Should().BeTrue();
        (await t.TransferAsync(new TreasuryTransferRequest(Guid.NewGuid(), otherBank, CashAccount, 75m, null))).IsSuccess.Should().BeTrue();
        (await t.SetAccountStatusAsync(htgBank, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
        (await t.SetAccountStatusAsync(otherBank, new FinancialAccountStatusRequest("closed", "Fin de test"))).IsSuccess.Should().BeTrue();
    }
}
