using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.ApiTests;

/// <summary>
/// Audit 2026-10, T-002 : la chaîne de connexion déclarée par une fabrique de tests
/// (<c>ConfigureAppConfiguration</c>) était ignorée — l'infrastructure lisait la configuration à
/// l'enregistrement des services, et toutes les exécutions écrivaient dans la base de développement.
/// Aucun accès à une base ici : on lit seulement la chaîne que le contexte utiliserait.
/// </summary>
public sealed class TestDatabaseTargetTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Connection_string_declared_by_the_test_factory_is_the_one_used()
    {
        const string probe = "nexora_t002_probe";
        using var custom = factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:ConnectionString"] =
                    $"Host=localhost;Port=5432;Database={probe};Username=nexora_app;Search Path=nexora,public",
            })));

        using var scope = custom.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();

        new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString()).Database.Should().Be(probe);
    }
}
