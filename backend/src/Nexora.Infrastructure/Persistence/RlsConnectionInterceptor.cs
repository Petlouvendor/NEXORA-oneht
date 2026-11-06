using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Nexora.Application.Abstractions;

namespace Nexora.Infrastructure.Persistence;

/// <summary>
/// Pose les variables de session PostgreSQL consommées par les politiques RLS (Phase 2 §34, Phase 3 §11)
/// à chaque ouverture de connexion. Le pool Npgsql réinitialise (RESET ALL) au retour :
/// aucune fuite de contexte entre requêtes. Les réapplique aussi sur la connexion déjà ouverte (transaction en cours)
/// quand une portée système commence ou se termine (audit 2026-10, A-009).
/// </summary>
public sealed class RlsConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ITenantContext tenant;
    private DbConnection? _open;
    private bool _stale;

    public RlsConnectionInterceptor(ITenantContext tenant)
    {
        this.tenant = tenant;
        tenant.SystemScopeChanged += Reapply;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        _open = connection;
        _stale = false;
        Apply(connection);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken ct = default)
    {
        _open = connection;
        _stale = false;
        await ApplyAsync(connection, ct).ConfigureAwait(false);
    }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (ReferenceEquals(_open, connection)) _open = null;
    }

    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (ReferenceEquals(_open, connection)) _open = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Connexion ouverte (transaction en cours) : le contexte est marqué périmé et réappliqué juste avant la commande
    /// suivante (<see cref="RlsCommandInterceptor"/>). Pas tout de suite : la portée peut se terminer pendant qu'une
    /// requête est encore en cours sur la connexion.
    /// </summary>
    private void Reapply()
    {
        if (_open is { State: System.Data.ConnectionState.Open }) _stale = true;
    }

    internal void BeforeCommand(DbCommand command)
    {
        if (!_stale || command.Connection is not { } connection || !ReferenceEquals(connection, _open)) return;
        _stale = false;
        Apply(connection);
    }

    internal async Task BeforeCommandAsync(DbCommand command, CancellationToken ct)
    {
        if (!_stale || command.Connection is not { } connection || !ReferenceEquals(connection, _open)) return;
        _stale = false;
        await ApplyAsync(connection, ct).ConfigureAwait(false);
    }

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
