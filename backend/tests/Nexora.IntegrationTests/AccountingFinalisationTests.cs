using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Application.Abstractions;
using Nexora.Application.Finance.Ledger;
using Nexora.Contracts.Finance;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation produit, phase 8 — comptabilité : lignes invalides refusées proprement (la base renvoyait son message
/// brut), écriture en devise présentée dans sa devise, plan comptable géré depuis l'application avec ses garde-fous.
/// </summary>
[Collection("finance")]
public sealed class AccountingFinalisationTests(FinanceEngineFixture fx)
{
    private static readonly Guid Company = Guid.Parse("dce00000-0000-0000-0000-0000000000c1");
    private static readonly Guid Direction = Guid.Parse("dce00000-0000-0000-0000-0000000000d1");

    private AsyncServiceScope Scope()
    {
        var s = fx.Provider.CreateAsyncScope();
        var tc = (TestTenantContext)s.ServiceProvider.GetRequiredService<ITenantContext>();
        tc.SetUser(Direction);
        tc.SetScope([Company], [], Company);
        return s;
    }

    private static string U() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public async Task Invalid_lines_are_refused_with_a_clear_error_and_a_foreign_currency_entry_keeps_its_currency()
    {
        await using var s = Scope();
        var j = s.ServiceProvider.GetRequiredService<IManualJournalService>();

        var both = await j.CreateDraftAsync(new CreateJournalEntryRequest("OD", Today, "HTG", "x",
            [new JournalEntryLineInput("6800", 10m, 5m), new JournalEntryLineInput("5711", 0m, 5m)]));
        both.Error!.Code.Should().Be("accounting.line_amount");
        both.Error.Message.Should().Contain("Ligne 1").And.NotContain("violates");
        (await j.CreateDraftAsync(new CreateJournalEntryRequest("OD", Today, "ZZZ", "x",
            [new JournalEntryLineInput("6800", 10m, 0m), new JournalEntryLineInput("5711", 0m, 10m)])))
            .Error!.Code.Should().Be("accounting.currency_unknown");
        (await j.CreateDraftAsync(new CreateJournalEntryRequest("OD", Today.AddDays(10), "HTG", "x",
            [new JournalEntryLineInput("6800", 10m, 0m), new JournalEntryLineInput("5711", 0m, 10m)])))
            .Error!.Code.Should().Be("accounting.future_date");

        var usd = await j.CreateDraftAsync(new CreateJournalEntryRequest("OD", Today, "USD", "Achat en dollars",
            [new JournalEntryLineInput("6800", 10m, 0m), new JournalEntryLineInput("5711", 0m, 10m)]));
        usd.IsSuccess.Should().BeTrue(usd.Error?.Message);
        var draft = (await j.GetAsync(usd.Value)).Value!;
        draft.Currency.Should().Be("USD");
        draft.TotalDebit.Should().Be(10m, "le total est dans la devise de l'écriture (affichait 1 350 USD)");
        draft.BaseTotal.Should().BeGreaterThan(10m);
        draft.FunctionalCurrency.Should().Be("HTG");
        (await j.PostAsync(usd.Value)).IsSuccess.Should().BeTrue();

        var found = await j.SearchAsync("posted", Today, Today, "OD", "dollars", 50);
        found.Should().Contain(e => e.Id == usd.Value);
    }

    [Fact]
    public async Task Chart_of_accounts_is_managed_with_guards()
    {
        await using var s = Scope();
        var chart = s.ServiceProvider.GetRequiredService<IChartManagementService>();
        var j = s.ServiceProvider.GetRequiredService<IManualJournalService>();
        var code = $"68{U()}";

        (await chart.CreateAsync(new SaveChartAccountRequest("6800", "Doublon", "expense", true, null, null)))
            .Error!.Code.Should().Be("accounting.account_exists");
        var postable = (await chart.AccountsAsync()).First(a => a.Code == "6800");
        (await chart.CreateAsync(new SaveChartAccountRequest(code, "Sous 6800", "expense", true, postable.Id, null)))
            .Error!.Code.Should().Be("accounting.account_parent", "un compte mouvementable ne regroupe pas");

        var group = (await chart.CreateAsync(new SaveChartAccountRequest($"G{code}", "Frais de maintenance", "expense", false, null, null))).Value;
        var created = await chart.CreateAsync(new SaveChartAccountRequest(code, "Maintenance groupe électrogène", "expense", true, group, null));
        created.IsSuccess.Should().BeTrue(created.Error?.Message);
        (await chart.UpdateAsync(group, new SaveChartAccountRequest($"G{code}", "Frais de maintenance", "expense", true, null, null)))
            .Error!.Code.Should().Be("accounting.account_has_children");

        // Mouvementé : code et type figés ; désactivation refusée tant qu'il a un solde.
        var e = await j.CreateDraftAsync(new CreateJournalEntryRequest("OD", Today, "HTG", "Maintenance",
            [new JournalEntryLineInput(code, 500m, 0m), new JournalEntryLineInput("5711", 0m, 500m)]));
        (await j.PostAsync(e.Value)).IsSuccess.Should().BeTrue();
        (await chart.UpdateAsync(created.Value, new SaveChartAccountRequest(code, "Maintenance", "asset", true, group, null)))
            .Error!.Code.Should().Be("accounting.account_locked");
        (await chart.UpdateAsync(created.Value, new SaveChartAccountRequest(code, "Maintenance groupe", "expense", true, group, null)))
            .IsSuccess.Should().BeTrue("l'intitulé reste modifiable");
        (await chart.SetStatusAsync(created.Value, new ChartAccountStatusRequest("inactive", "Plus utilisé")))
            .Error!.Code.Should().Be("accounting.account_balance");
        var row = (await chart.AccountsAsync()).Single(a => a.Id == created.Value);
        row.Balance.Should().Be(500m);
        row.ParentCode.Should().Be($"G{code}");

        // Compte des écritures automatiques : jamais désactivable.
        var cash = (await chart.AccountsAsync()).First(a => a.Code == "5711");
        cash.UsedBy.Should().NotBeEmpty();
        (await chart.SetStatusAsync(cash.Id, new ChartAccountStatusRequest("inactive", "Test"))).Error!.Code.Should().Be("accounting.account_in_use");

        // Soldé par contrepassation, il peut être désactivé.
        (await j.ReverseAsync(e.Value, new ReverseJournalEntryRequest("Erreur d'imputation"))).IsSuccess.Should().BeTrue();
        (await chart.SetStatusAsync(created.Value, new ChartAccountStatusRequest("inactive", "Plus utilisé"))).IsSuccess.Should().BeTrue();
    }
}
