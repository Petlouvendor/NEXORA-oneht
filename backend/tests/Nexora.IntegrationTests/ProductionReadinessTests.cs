using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Application.Operations;
using Nexora.Contracts.Setup;
using Nexora.Infrastructure.Identity;
using Nexora.Infrastructure.Setup;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Finalisation phase 18 — moteur de migrations de production (commande « migrate », sonde readiness, checklist) et
/// premier démarrage d'une installation par conteneurs (commande « bootstrap »), sur la base de test clonée.
/// </summary>
[Collection("finance")]
public sealed class ProductionReadinessTests(FinanceEngineFixture fx)
{
    private async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        return c;
    }

    private async Task<string?> ChecksumAsync(NpgsqlConnection c, string version)
    {
        await using var cmd = new NpgsqlCommand("SELECT checksum FROM nexora.schema_migration WHERE version = @v", c);
        cmd.Parameters.AddWithValue("v", version);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private static async Task SetChecksumAsync(NpgsqlConnection c, string version, string? checksum)
    {
        await using var cmd = new NpgsqlCommand("UPDATE nexora.schema_migration SET checksum = @c WHERE version = @v", c);
        cmd.Parameters.AddWithValue("c", (object?)checksum ?? DBNull.Value);
        cmd.Parameters.AddWithValue("v", version);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Every_embedded_migration_is_applied_and_the_database_is_not_ahead_of_the_application()
    {
        await using var c = await OpenAsync();
        var status = await MigrationRunner.StatusAsync(c);

        status.Embedded.Should().BeGreaterThan(90);
        status.Pending.Should().BeEmpty("la base de test est migrée par apply.sh avec les mêmes fichiers");
        status.Unknown.Should().BeEmpty();
        status.LatestApplied.Should().Be(status.LatestEmbedded);
        // Rien à appliquer : zéro migration, et le verrou consultatif est bien relâché (un second passage passe).
        (await MigrationRunner.ApplyPendingAsync(c, allowDrift: true)).Should().Be(0);
        (await MigrationRunner.ApplyPendingAsync(c, allowDrift: true)).Should().Be(0);
    }

    [Fact]
    public async Task A_modified_applied_migration_blocks_migrate_until_realigned()
    {
        await using var c = await OpenAsync();
        var original = await ChecksumAsync(c, "V094");
        try
        {
            await SetChecksumAsync(c, "V094", new string('0', 64));

            var status = await MigrationRunner.StatusAsync(c);
            status.Drifted.Should().Contain("V094__security_policy_enforce_mfa.sql");

            var migrate = () => MigrationRunner.ApplyPendingAsync(c);
            await migrate.Should().ThrowAsync<InvalidOperationException>().WithMessage("*V094*ne se modifie jamais*");

            await using (var scope = fx.Provider.CreateAsyncScope())
            {
                var golive = await scope.ServiceProvider.GetRequiredService<IOperationsService>().GoLiveReadinessAsync();
                var check = golive.Checks.Single(x => x.Key == "migrations_applied");
                check.Status.Should().Be("warn");
                check.Detail.Should().Contain("V094");
            }

            // V094 est idempotente : rejouée sans erreur, son empreinte est réalignée.
            await MigrationRunner.RealignAsync(c, "V094");
            (await MigrationRunner.StatusAsync(c)).Drifted.Should().NotContain("V094__security_policy_enforce_mfa.sql");
        }
        finally
        {
            await SetChecksumAsync(c, "V094", original);
        }
    }

    [Fact]
    public async Task Realign_refuses_a_migration_that_is_not_applied_or_not_shipped()
    {
        await using var c = await OpenAsync();
        var unknown = () => MigrationRunner.RealignAsync(c, "V999");
        await unknown.Should().ThrowAsync<InvalidOperationException>().WithMessage("*inconnue*");
    }

    [Fact]
    public async Task A_database_newer_than_the_application_is_reported()
    {
        await using var c = await OpenAsync();
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO nexora.schema_migration (version, filename, checksum) VALUES ('V999', 'V999__future.sql', 'x')", c))
            await insert.ExecuteNonQueryAsync();
        try
        {
            var status = await MigrationRunner.StatusAsync(c);
            status.Unknown.Should().Equal("V999");
            status.Consistent.Should().BeFalse();
            status.UpToDate.Should().BeTrue();
        }
        finally
        {
            await using var delete = new NpgsqlCommand("DELETE FROM nexora.schema_migration WHERE version = 'V999'", c);
            await delete.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Bootstrap_validates_like_the_wizard_and_refuses_an_initialised_database()
    {
        var config = new ConfigurationBuilder().Build();
        var installer = new SetupInstaller(SetupState.FromConfiguration(config, AppContext.BaseDirectory),
            new BcryptPasswordHasher(4), NullLogger<SetupInstaller>.Instance);
        await using var c = await OpenAsync();

        var weak = await installer.BootstrapAsync(c, new SetupCompanyInput("Essai", "supermarket", "HTG"),
            new SetupAdminInput("Direction", "direction@essai.ht", "court"));
        weak.IsSuccess.Should().BeFalse();
        weak.Error!.Kind.Should().Be(Nexora.Domain.Common.ErrorKind.Validation);

        var unknownActivity = await installer.BootstrapAsync(c, new SetupCompanyInput("Essai", "fusées", "HTG"),
            new SetupAdminInput("Direction", "direction@essai.ht", "Tres-Long-Mot2Passe!"));
        unknownActivity.Error!.Code.Should().Be("setup.company_activity");

        var initialised = await installer.BootstrapAsync(c, new SetupCompanyInput("Essai", "supermarket", "HTG"),
            new SetupAdminInput("Direction", "direction@essai.ht", "Tres-Long-Mot2Passe!"));
        initialised.Error!.Code.Should().Be("setup.database_not_empty");
    }
}
