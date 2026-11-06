using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Guide fonctionnel développeur §7, §27, §28 — le caissier est un utilisateur POS : l'API (pas
/// seulement le menu) lui refuse le pilotage, la finance, le stock global, le catalogue complet, le
/// remboursement électronique, un autre tenant, et l'assistant ne le laisse pas se déclarer
/// « direction ». Compte démo <c>caisse@abc-group.demo</c> (rôle cashier sur la Pharmacie c1).
/// Lecture seule sur la base de développement (règle A3 : aucune écriture de données partagées).
/// </summary>
public sealed class CashierAccessTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Restaurant = "dce00000-0000-0000-0000-0000000000c2";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    // §28 « Caissier tente API finance → Accès refusé » ; §7.2 pilotage, trésorerie, dettes, rapports.
    [Theory]
    [InlineData("GET", "/api/v1/dashboard/command-center")]
    [InlineData("GET", "/api/v1/dashboard/alerts")]
    [InlineData("GET", "/api/v1/reports/sales/journal")]
    [InlineData("GET", "/api/v1/reports/sales/top-products")]
    [InlineData("GET", "/api/v1/kpis")]
    [InlineData("GET", "/api/v1/insights")]
    [InlineData("GET", "/api/v1/financial-accounts")]
    [InlineData("GET", "/api/v1/cashflow/forecast")]
    [InlineData("POST", "/api/v1/expenses")]
    public async Task Cashier_is_refused_management_and_finance_apis(string method, string url)
        => (await Send(await Cashier(), method, url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

    // Audit 2026-10 éd. 2, A-045 : les écritures financières brutes à montants libres (/finance/*) contournaient les
    // contrôles des services métier — un comptable seul payait une dépense sans approbation. Aucun écran ne les
    // appelait : elles sont retirées.
    [Theory]
    [InlineData("/api/v1/finance/expenses")]
    [InlineData("/api/v1/finance/supplier-payments")]
    [InlineData("/api/v1/finance/customer-payments")]
    [InlineData("/api/v1/finance/cash-counts")]
    [InlineData("/api/v1/finance/cash-sales")]
    [InlineData("/api/v1/finance/transfers")]
    public async Task Raw_finance_routes_no_longer_exist(string url)
        => (await Send(await Cashier(), "POST", url)).StatusCode.Should().Be(HttpStatusCode.NotFound, url);

    // §28 « Caissier tente stock global → Accès refusé » ; §7.2 catalogue complet, prix d'achat.
    [Theory]
    [InlineData("GET", "/api/v1/inventory/balance")]
    [InlineData("GET", "/api/v1/stock/balances")]
    [InlineData("GET", "/api/v1/reports/stock/ruptures")]
    [InlineData("GET", "/api/v1/catalog/products")]
    [InlineData("GET", "/api/v1/catalog/products/00000000-0000-0000-0000-000000000001")]
    [InlineData("POST", "/api/v1/catalog/pricing/quote")]
    public async Task Cashier_is_refused_global_stock_and_full_catalog(string method, string url)
        => (await Send(await Cashier(), method, url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

    // §10 : rembourser (paiement électronique, retour exécuté directement, validation d'une demande)
    // n'est jamais à la main du caissier seul — il ne peut que demander (POST /pos/return-requests).
    [Theory]
    [InlineData("/api/v1/integrations/payments/intents/00000000-0000-0000-0000-000000000001/refund")]
    [InlineData("/api/v1/pos/returns")]
    [InlineData("/api/v1/sales/00000000-0000-0000-0000-000000000001/returns")]
    [InlineData("/api/v1/pos/return-requests/00000000-0000-0000-0000-000000000001/approve")]
    [InlineData("/api/v1/pos/return-requests/00000000-0000-0000-0000-000000000001/reject")]
    public async Task Cashier_cannot_refund_or_approve_a_return(string url)
        => (await Send(await Cashier(), "POST", url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

    [Fact]
    public async Task Cashier_can_look_up_a_sale_and_list_his_return_requests()
    {
        var client = await Cashier();
        (await client.GetAsync("/api/v1/pos/sales/lookup?number=INEXISTANT")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v1/pos/return-requests")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // §28 « Caissier change tenant → Accès refusé ».
    [Fact]
    public async Task Cashier_switching_to_a_company_he_is_not_assigned_to_is_refused()
    {
        var client = await Cashier();
        client.DefaultRequestHeaders.Remove("X-Company-Id");
        client.DefaultRequestHeaders.Add("X-Company-Id", Restaurant);
        (await client.GetAsync("/api/v1/me/permissions")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // §27 « Faire confiance au rôle envoyé par le frontend » : l'assistant décide du rôle côté serveur.
    [Fact]
    public async Task Assistant_ignores_a_role_claimed_by_the_client()
    {
        var client = await Cashier();
        (await client.GetAsync("/api/v1/assistant/briefing?role=direction")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/v1/assistant/briefing?role=manager")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var own = await client.GetAsync("/api/v1/assistant/briefing?role=caissier");
        own.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // §19 : l'assistant ne répond qu'avec les données autorisées — aucun chiffre de marge ou trésorerie.
    [Theory]
    [InlineData("Quelle est notre marge ce mois-ci ?")]
    [InlineData("Quelle est la trésorerie ?")]
    [InlineData("Quels clients ont des créances échues ?")]
    [InlineData("Combien avons-nous vendu ce mois-ci ?")]
    public async Task Assistant_refuses_financial_questions_to_a_cashier(string question)
    {
        var client = await Cashier();
        var res = await client.PostAsJsonAsync("/api/v1/assistant/query", new { question });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var answer = await res.Content.ReadFromJsonAsync<AskShape>();
        answer!.Intent.Should().Be("forbidden");
        answer.Facts.Should().BeEmpty();
    }

    // Étape 7 — aucune reconstitution du CA : ni liste des factures / reçus / devis de l'entreprise,
    // ni créances des clients. Le caissier démo n'a émis aucun document dans la base modèle.
    [Theory]
    [InlineData("/api/v1/documents?limit=500")]
    [InlineData("/api/v1/invoices")]
    [InlineData("/api/v1/quotes")]
    public async Task Cashier_document_lists_contain_only_his_own_documents(string url)
    {
        var direction = await Login("direction@abc-group.demo");
        var cashier = await Cashier();
        var all = await direction.GetFromJsonAsync<List<IdShape>>(url);
        var mine = await cashier.GetFromJsonAsync<List<IdShape>>(url);
        mine.Should().BeEmpty($"la direction en voit {all!.Count}, le caissier aucun qu'il n'ait émis");
    }

    [Fact]
    public async Task Cashier_customer_list_hides_receivables_and_credit_limits()
    {
        var customers = await (await Cashier()).GetFromJsonAsync<List<CustomerShape>>("/api/v1/customers");
        customers.Should().NotBeEmpty().And.OnlyContain(c => c.OpenReceivables == null && c.CreditLimit == null);
    }

    // Validation sur place : un mauvais mot de passe du responsable est un 422, jamais un 401 — un 401
    // ferait déconnecter le caissier par le client web. Sa session reste valide.
    [Fact]
    public async Task Wrong_manager_password_on_the_cashier_terminal_never_logs_the_cashier_out()
    {
        var cashier = await Cashier();
        var res = await cashier.PostAsJsonAsync("/api/v1/pos/manager-override", new
        {
            action = "approve_return", targetId = Guid.NewGuid(), email = "inconnu@abc-group.demo",
            password = "mauvais", mfaCode = (string?)null, note = (string?)null,
        });
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await cashier.GetAsync("/api/v1/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Garde-fou de non-régression : la direction garde tout (le masquage ne touche que les rôles limités).
    [Fact]
    public async Task Direction_still_gets_financial_kpis_on_the_command_center()
    {
        var client = await Login("direction@abc-group.demo");
        var cc = await client.GetFromJsonAsync<CommandCenterShape>("/api/v1/dashboard/command-center");
        cc!.Kpis.Select(k => k.Key).Should().Contain(["gross_margin", "cash", "open_payables"]);
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string url)
        => method == "GET"
            ? client.GetAsync(url)
            : client.PostAsJsonAsync(url, new { });

    private Task<HttpClient> Cashier() => Login("caisse@abc-group.demo");

    private async Task<HttpClient> Login(string email)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "Nexora!2026", companyId = Pharmacy,
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK, $"connexion {email}");
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", Pharmacy);
        return client;
    }

    private sealed record TokenShape(string AccessToken);
    private sealed record IdShape(Guid Id);
    private sealed record CustomerShape(Guid Id, decimal? OpenReceivables, decimal? CreditLimit);
    private sealed record AskShape(string Intent, string Answer, List<object> Facts);
    private sealed record KpiShape(string Key);
    private sealed record CommandCenterShape(List<KpiShape> Kpis);
}
