using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nexora.Application;
using Nexora.Infrastructure;
using Nexora.RecetteSeed;
using Npgsql;

// Générateur du jeu de données de recette (Phase 55). Passe exclusivement par les services
// métier (ventes, achats, paiements, stock, comptabilité) : les données produites sont cohérentes
// exactement comme si elles avaient été saisies dans l'application.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage : Nexora.RecetteSeed \"<chaîne de connexion vers la base de recette>\"");
    return 2;
}

var conn = new NpgsqlConnectionStringBuilder(args[0]);
if (string.Equals(conn.Database, "nexora", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("REFUS : 'nexora' est la base modèle des tests d'intégration (anomalie A3). Utilisez nexora_recette.");
    return 1;
}

var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Database:ConnectionString"] = conn.ConnectionString,
    ["Jwt:SigningKey"] = "recette-seed-signing-key-not-used-for-tokens-000000",
}).Build();

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddSingleton<IConfiguration>(config);
services.AddApplication();
services.AddInfrastructure(config);
await using var provider = services.BuildServiceProvider();

var started = DateTime.UtcNow;
try
{
    var seeder = new RecetteSeeder(provider, conn.ConnectionString);
    await seeder.RunAsync();
    Console.WriteLine($"Jeu de recette généré en {(DateTime.UtcNow - started).TotalSeconds:0} s.");
    return 0;
}
catch (SeedException ex)
{
    Console.Error.WriteLine($"ÉCHEC : {ex.Message}");
    return 1;
}
