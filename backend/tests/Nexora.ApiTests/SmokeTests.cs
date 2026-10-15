using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Nexora.ApiTests;

/// <summary>
/// Phase 49 §12, §52 — parcours de fumée : rejoue, dans l'ordre exact du cahier des charges,
/// Login → Dashboard → Créer client → Vente → Paiement → Stock → Facture → Recherche → Logout →
/// Health, sur l'entreprise démo Pharmacie ABC (c1), contre la vraie API HTTP (jamais un appel de
/// service direct) — la preuve que le chemin critique fonctionne bout-en-bout, pas seulement en
/// développement (§2).
///
/// <b>Constat Phase 49</b> — « Créer client » (§52) n'a pas d'équivalent API dans NEXORA ONE : ni
/// <c>SuppliersController</c> ni aucun <c>CustomersController</c> n'exposent de création ; les tiers
/// (clients/fournisseurs) ne s'introduisent aujourd'hui que par le pipeline de migration (Phase 39,
/// <c>ImportsController</c>) ou par les jeux de données de démonstration/SQL. Ce test utilise donc le
/// client pré-existant de la démo et documente ce manque plutôt que de la contourner silencieusement
/// — voir <c>docs/phase-49-implementation.md</c>.
/// </summary>
public sealed class SmokeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string DevConnString = ApiTestDatabase.App;
    private const string Pharmacy = "dce00000-0000-0000-0000-0000000000c1";
    private const string InventoryLocation = "dce00000-0000-0000-0000-00000000a003";  // Officine
    private const string Store = "dce00000-0000-0000-0000-00000000a001";              // Comptoir
    private const string Customer = "dce00000-0000-0000-0000-00000000b001";           // Clinique Bon Samaritain (pré-existant)
    private const string Product = "dce00000-0000-0000-0000-00000000a041";            // PARA500
    private const string ExemptTax = "dce00000-0000-0000-0000-00000000a021";          // Exonéré 0 %
    private const string CashAccount = "dce00000-0000-0000-0000-00000000a051";        // Caisse officine

    private HttpClient Client() => factory.WithWebHostBuilder(b =>
        b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
            new Dictionary<string, string?> { ["Database:ConnectionString"] = DevConnString }))).CreateClient();

    [Fact]
    public async Task Golden_path_login_dashboard_sale_stock_invoice_search_logout_health()
    {
        // ---- 0. Health, à froid, sans jeton (§52 vérifié aussi en fin de parcours) --------------
        (await Client().GetAsync("/api/v1/ready")).StatusCode.Should().Be(HttpStatusCode.OK);

        // ---- 1. Login --------------------------------------------------------------------------
        var client = Client();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email = "direction@abc-group.demo", password = "Nexora!2026", companyId = Pharmacy });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<TokenShape>())!.AccessToken;
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
        client.DefaultRequestHeaders.Add("X-Company-Id", Pharmacy);

        // ---- 2. Dashboard ----------------------------------------------------------------------
        (await client.GetAsync("/api/v1/dashboard/command-center")).StatusCode.Should().Be(HttpStatusCode.OK);

        // ---- 3. Créer client — voir le constat en tête de fichier ; client démo réutilisé -------
        var statement = await client.GetAsync($"/api/v1/credit/statements/{Customer}");
        statement.StatusCode.Should().Be(HttpStatusCode.OK, "le client démo doit être un tiers réel et accessible avant la vente");

        // ---- Préalable technique : le stock démo est vide (aucune réception n'a jamais été faite
        // sur cette entreprise) et PARA500 suit les lots (FEFO, §18) — un lot réel avec quantité
        // positive est indispensable, le stock négatif ne s'applique jamais à un lot. On amorce un
        // lot via un mouvement 'opening' réel (jamais une écriture directe de lot_stock/stock_balance :
        // le trigger nexora.stock_movement_apply() est le SEUL chemin qui les tient cohérents, exactement
        // comme le ferait un import de stock d'ouverture, Phase 39 §14) — laisse la base dans un état
        // propre et cohérent après le test, sans dérive ni policy modifiée à l'échelle de l'entreprise.
        var lotNumber = $"SMK-{Guid.NewGuid():N}"[..20];
        await using (var db = new NpgsqlConnection(DevConnString))
        {
            await db.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                SET nexora.bypass_rls = 'on';
                WITH new_lot AS (
                    INSERT INTO nexora.lot (company_id, product_id, lot_number, expiration_date, status)
                    VALUES ('{Pharmacy}', '{Product}', '{lotNumber}', (current_date + interval '1 year')::date, 'active')
                    RETURNING id
                )
                INSERT INTO nexora.stock_movement
                    (company_id, product_id, inventory_location_id, lot_id, movement_type, quantity, reference_type)
                SELECT '{Pharmacy}', '{Product}', '{InventoryLocation}', id, 'opening', 10, 'opening' FROM new_lot;
                """, db);
            await cmd.ExecuteNonQueryAsync();
        }

        // ---- 4-5. Vente + Paiement — atomiques dans cette architecture (Phase 6, §12) -----------
        var operationUuid = Guid.NewGuid();
        client.DefaultRequestHeaders.Add("Idempotency-Key", operationUuid.ToString());
        var sale = await client.PostAsJsonAsync("/api/v1/sales", new
        {
            operationUuid,
            inventoryLocationId = InventoryLocation,
            storeId = Store,
            posTerminalId = (string?)null,
            cashSessionId = (string?)null,
            customerId = Customer,
            currency = "HTG",
            lines = new[] { new { productId = Product, quantity = 1, unitPriceOverride = (decimal?)null, lineDiscount = 0m, taxId = ExemptTax } },
            tenders = new[] { new { financialAccountId = CashAccount, paymentMethodId = (string?)null, amount = 75m, reference = "smoke-test" } },
            amountTendered = 75m,
            creditDueDate = (DateOnly?)null,
            couponCode = (string?)null,
            creditOverrideReason = (string?)null,
            soldAt = (DateTimeOffset?)null,
        });
        sale.StatusCode.Should().Be(HttpStatusCode.OK, await sale.Content.ReadAsStringAsync());
        var result = await sale.Content.ReadFromJsonAsync<CheckoutResultShape>();
        result!.AmountPaid.Should().Be(result.Total, "le paiement complet en espèces doit solder la vente dans la même transaction atomique");
        result.CreditAmount.Should().Be(0);

        // ---- 6. Stock — le mouvement de sortie a bien été journalisé ----------------------------
        var movements = await client.GetFromJsonAsync<List<MovementShape>>(
            $"/api/v1/stock/movements?productId={Product}&locationId={InventoryLocation}");
        movements!.Should().Contain(m => m.MovementType == "sale" || m.ReferenceType == "sale");

        // ---- 7. Facture — un numéro de document définitif a été émis dans la même transaction ---
        result.DocumentNumber.Should().NotBeNullOrWhiteSpace();

        // ---- 8. Recherche — le produit vendu reste trouvable par la recherche tolérante ---------
        var found = await client.GetFromJsonAsync<List<SearchHitShape>>("/api/v1/catalog/search?q=Paracetamol");
        found!.Should().Contain(h => h.Sku == "PARA500");

        // ---- 9. Logout — NEXORA ONE ne révoque pas la session serveur au logout (constat Phase 49,
        // AuthContext.logout() ne fait que vider le stockage local du navigateur) ; ce test reproduit
        // fidèlement ce comportement réel plutôt que d'appeler une route que le produit n'invoque pas ---
        client.DefaultRequestHeaders.Remove("Authorization");
        (await client.GetAsync("/api/v1/dashboard/command-center")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // ---- 10. Health, à nouveau, en clôture du parcours --------------------------------------
        (await Client().GetAsync("/api/v1/ready")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record TokenShape(string AccessToken);
    private sealed record CheckoutResultShape(Guid SaleId, string DocumentNumber, decimal Total, decimal AmountPaid, decimal CreditAmount);
    private sealed record MovementShape(string MovementType, string? ReferenceType);
    private sealed record SearchHitShape(string Sku);
}
