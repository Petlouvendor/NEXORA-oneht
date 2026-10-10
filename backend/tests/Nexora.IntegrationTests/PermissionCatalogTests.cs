using System.Reflection;
using FluentAssertions;
using Nexora.Application.Security;
using Npgsql;

namespace Nexora.IntegrationTests;

/// <summary>
/// Analyse exhaustive 2026-10, §6.2 : 102 permissions attribuables n'étaient vérifiées nulle part. Une permission
/// proposée aux administrateurs doit protéger une action réelle, et toute permission vérifiée par le code doit pouvoir
/// être attribuée.
/// </summary>
[Collection("finance")]
public sealed class PermissionCatalogTests(FinanceEngineFixture fx)
{
    private static HashSet<string> Declared() => typeof(Permissions).GetNestedTypes(BindingFlags.Public)
        .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .ToHashSet();

    private async Task<HashSet<string>> InDatabase()
    {
        await using var c = new NpgsqlConnection(fx.TestConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT code FROM nexora.permission", c);
        await using var r = await cmd.ExecuteReaderAsync();
        var codes = new HashSet<string>();
        while (await r.ReadAsync()) codes.Add(r.GetString(0));
        return codes;
    }

    [Fact]
    public async Task Every_assignable_permission_is_declared_by_the_code()
        => (await InDatabase()).Except(Declared()).Should().BeEmpty(
            "une permission proposée dans l'administration doit protéger une action (sinon la retirer d'un rôle ne restreint rien)");

    [Fact]
    public async Task Every_permission_checked_by_the_code_can_be_assigned()
        => Declared().Except(await InDatabase()).Should().BeEmpty(
            "une permission vérifiée par le code doit exister en base pour pouvoir être attribuée à un rôle");
}
