using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nexora.Application.Abstractions;

namespace Nexora.Infrastructure.Persistence;

/// <summary>
/// Pose les variables de session PostgreSQL consommées par les politiques RLS (Phase 2 §34, Phase 3 §11)
/// à chaque ouverture de connexion. Le pool Npgsql réinitialise (RESET ALL) au retour :
/// aucune fuite de contexte entre requêtes.
/// </summary>
public sealed class RlsConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
        => await ApplyAsync(connection, ct).ConfigureAwait(false);

    private void Apply(DbConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildScript();
        cmd.ExecuteNonQuery();
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = BuildScript();
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private string BuildScript()
    {
        if (tenant.BypassRls)
            return "SET nexora.bypass_rls = 'on';";

        var companies = string.Join(",", tenant.CompanyIds.Select(g => g.ToString("D", CultureInfo.InvariantCulture)));
        var groups = string.Join(",", tenant.GroupIds.Select(g => g.ToString("D", CultureInfo.InvariantCulture)));
        var user = tenant.UserId?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty;
        // Vide = aucune restriction de succursale (résumé fonctionnel §4, V071).
        var establishments = tenant.EstablishmentIds is { } e
            ? string.Join(",", e.Select(g => g.ToString("D", CultureInfo.InvariantCulture)))
            : string.Empty;

        // Fuseau de session = fuseau de l'entreprise active (analyse 2026-10 §5.5) : current_date, les conversions
        // horodatage → date et les valeurs par défaut des colonnes date donnent le jour civil de l'entreprise, comme
        // IBusinessCalendar côté application. Le fuseau est validé à l'écriture (V095) ; sans entreprise active, celui
        // du serveur reste en place.
        var zone = tenant.ActiveCompanyId is { } active
            ? $"""
              SELECT set_config('TimeZone', c.timezone, false) FROM nexora.company c
               WHERE c.id = '{active.ToString("D", CultureInfo.InvariantCulture)}';
              """
            : string.Empty;

        // Valeurs = uuid/uuid-lists uniquement (aucune entrée utilisateur libre) → littéraux sûrs.
        return $"""
            SET nexora.bypass_rls = 'off';
            SET nexora.company_ids = '{companies}';
            SET nexora.group_ids = '{groups}';
            SET nexora.user_id = '{user}';
            SET nexora.establishment_ids = '{establishments}';
            {zone}
            """;
    }
}
