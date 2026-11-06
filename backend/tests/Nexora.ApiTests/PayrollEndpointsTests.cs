using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Nexora.ApiTests;

/// <summary>
/// Module « Paie, RH & avances » — §5 (le caissier n'a aucun accès aux salaires, contrôle côté API et non par le menu),
/// §29 (routes indicatives), §39 (clé d'idempotence obligatoire pour tout paiement) et §27 (export réel). Lecture seule
/// sur la base de développement (règle A3) : les écritures sont prouvées par <c>PayrollTests</c> sur une base clonée.
/// Chaque route est appelée au moins une fois : une collision de routes (500 à l'exécution) serait détectée ici.
/// </summary>
public sealed class PayrollEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string Unknown = "00000000-0000-0000-0000-000000000001";

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    // §5 « Le caissier POS n'a aucun accès aux salaires des employés » — aucune route de paie ne lui répond.
    [Theory]
    [InlineData("GET", "/api/v1/employees")]
    [InlineData("GET", $"/api/v1/employees/{Unknown}")]
    [InlineData("POST", "/api/v1/employees")]
    [InlineData("GET", $"/api/v1/employees/{Unknown}/salary-history")]
    [InlineData("GET", $"/api/v1/employees/{Unknown}/compensation")]
    [InlineData("GET", $"/api/v1/employees/{Unknown}/payslips")]
    [InlineData("GET", $"/api/v1/employees/{Unknown}/sensitive")]
    [InlineData("GET", $"/api/v1/employees/{Unknown}/history")]
    [InlineData("POST", $"/api/v1/employees/{Unknown}/salary-changes")]
    [InlineData("POST", $"/api/v1/employees/{Unknown}/salary-advances")]
    [InlineData("GET", "/api/v1/departments")]
    [InlineData("GET", "/api/v1/payroll/periods")]
    [InlineData("POST", "/api/v1/payroll/periods")]
    [InlineData("POST", $"/api/v1/payroll/periods/{Unknown}/calculate")]
    [InlineData("GET", $"/api/v1/payroll/{Unknown}")]
    [InlineData("GET", $"/api/v1/payroll/items/{Unknown}")]
    [InlineData("POST", $"/api/v1/payroll/{Unknown}/approve")]
    [InlineData("POST", $"/api/v1/payroll/{Unknown}/pay")]
    [InlineData("GET", $"/api/v1/payroll/{Unknown}/reconciliation")]
    [InlineData("GET", $"/api/v1/payslips/{Unknown}/file")]
    [InlineData("GET", "/api/v1/payroll/rules")]
    [InlineData("GET", "/api/v1/payroll/adjustments")]
    [InlineData("GET", "/api/v1/salary-advances")]
    [InlineData("POST", $"/api/v1/salary-advances/{Unknown}/approve")]
    [InlineData("POST", $"/api/v1/salary-advances/{Unknown}/pay")]
    [InlineData("POST", $"/api/v1/salary-advances/{Unknown}/repay")]
    [InlineData("GET", "/api/v1/hr/leaves")]
    [InlineData("GET", "/api/v1/hr/approvals")]
    [InlineData("GET", "/api/v1/hr/expense-claims")]
    [InlineData("GET", "/api/v1/hr/dashboard")]
    [InlineData("GET", "/api/v1/hr/reports/payroll_mass")]
    [InlineData("GET", "/api/v1/hr/reports/staff_debt/export")]
    [InlineData("GET", "/api/v1/hr/audit?entityType=employee")]
    public async Task Cashier_is_refused_every_salary_and_payroll_route(string method, string url)
        => (await Send(await Login("caisse@abc-group.demo"), method, url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, url);

    // §5, §6 « Employé » : le caissier a son espace personnel, mais sans dossier rattaché il n'obtient rien.
    [Fact]
    public async Task Cashier_self_service_space_exists_but_returns_nothing_without_an_employee_record()
    {
        var cashier = await Login("caisse@abc-group.demo");
        var profile = await cashier.GetAsync("/api/v1/me/hr/profile");
        profile.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await profile.Content.ReadAsStringAsync()).Should().Contain("hr.no_employee_record");
        (await cashier.GetAsync("/api/v1/me/hr/payslips")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await cashier.GetAsync($"/api/v1/me/hr/payslips/{Unknown}/file")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Direction_reads_payroll_screens_and_unknown_identifiers_answer_404()
    {
        var direction = await Login("direction@abc-group.demo");
        foreach (var url in new[]
                 {
                     "/api/v1/employees", "/api/v1/departments", "/api/v1/payroll/periods", "/api/v1/salary-advances", "/api/v1/hr/leaves",
                     "/api/v1/hr/overtime", "/api/v1/hr/attendance", "/api/v1/hr/variables", "/api/v1/hr/approvals", "/api/v1/hr/expense-claims",
                     "/api/v1/hr/dashboard", "/api/v1/hr/insights", "/api/v1/payroll/components", "/api/v1/payroll/adjustments",
                     "/api/v1/payroll/payment-accounts", "/api/v1/hr/audit?entityType=payroll_run",
                 })
            (await direction.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);

        foreach (var url in new[]
                 {
                     $"/api/v1/employees/{Unknown}", $"/api/v1/employees/{Unknown}/salary-history", $"/api/v1/payroll/{Unknown}",
                     $"/api/v1/payroll/items/{Unknown}", $"/api/v1/payslips/{Unknown}/file", $"/api/v1/salary-advances/{Unknown}",
                     $"/api/v1/salary-advances/{Unknown}/history", $"/api/v1/employee-documents/{Unknown}/content",
                 })
            (await direction.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, url);

        (await direction.GetAsync("/api/v1/hr/audit?entityType=sale")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity,
            "l'audit RH est borné aux objets RH et paie");
        var rules = await direction.GetFromJsonAsync<List<RuleShape>>("/api/v1/payroll/rules");
        rules!.Should().Contain(r => r.IsTemplate && r.Code == "HT_IRI" && r.Status == "template",
            "les barèmes fournis restent des modèles à valider, jamais appliqués d'office (§11)");
    }

    // §39 « ne jamais créer deux paiements pour une même instruction » : sans clé d'idempotence, aucun paiement n'est traité.
    [Fact]
    public async Task Payments_require_an_idempotency_key()
    {
        var direction = await Login("direction@abc-group.demo");
        var body = new { financialAccountId = Unknown, paymentMethod = "cash", operationUuid = Guid.NewGuid() };
        await StepUpTestHelper.ShouldRequireStepUpAsync(await direction.PostAsJsonAsync($"/api/v1/payroll/{Unknown}/pay", body), "payment");
        await StepUpTestHelper.AddStepUpAsync(direction, "Nexora!2026", "payment");
        (await direction.PostAsJsonAsync($"/api/v1/payroll/{Unknown}/pay", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await direction.PostAsJsonAsync($"/api/v1/salary-advances/{Unknown}/pay", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        direction.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString());
        (await direction.PostAsJsonAsync($"/api/v1/payroll/{Unknown}/pay", body)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // §27 : rapports exportables en vrais fichiers (Excel = classeur Office Open XML, PDF).
    [Fact]
    public async Task Payroll_reports_are_listed_and_exported_as_real_files()
    {
        var direction = await Login("direction@abc-group.demo");
        var catalog = await direction.GetFromJsonAsync<List<ReportShape>>("/api/v1/hr/reports");
        catalog!.Select(r => r.Key).Should().Contain(["payroll_mass", "net_by_period", "gross_by_department", "deductions_by_category",
            "advances_open", "advances_settled", "advances_by_employee", "staff_debt", "unpaid", "period_variance", "employer_cost"]);
        (await direction.GetAsync("/api/v1/hr/reports/staff_debt")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await direction.GetAsync("/api/v1/hr/reports/gross_by_department")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var xlsx = await direction.GetAsync("/api/v1/hr/reports/payroll_mass/export?format=xlsx");
        xlsx.StatusCode.Should().Be(HttpStatusCode.OK);
        (await xlsx.Content.ReadAsByteArrayAsync())[..2].Should().Equal((byte)'P', (byte)'K');
        var pdf = await direction.GetAsync("/api/v1/hr/reports/staff_debt/export?format=pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        System.Text.Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]).Should().Be("%PDF");
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string url) => method switch
    {
        "GET" => client.GetAsync(url),
        "PUT" => client.PutAsJsonAsync(url, new { name = "x" }),
        _ => client.PostAsJsonAsync(url, new { }),
    };

    private async Task<HttpClient> Login(string email)
    {
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Nexora!2026", companyId = Pharmacy });
        login.StatusCode.Should().Be(HttpStatusCode.OK, $"connexion {email}");
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", Pharmacy);
        return client;
    }

    private sealed record TokenShape(string AccessToken);
    private sealed record ReportShape(string Key);
    private sealed record RuleShape(string Code, bool IsTemplate, string Status);
}
