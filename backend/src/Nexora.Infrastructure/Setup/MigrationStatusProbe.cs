using System.Data;
using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.Infrastructure.Setup;

/// <summary>
/// État des migrations lu avec la connexion applicative (finalisation phase 18) : sonde readiness, checklist de mise
/// en production, vue « Production ». Lecture seule — le rôle applicatif ne migre jamais.
/// </summary>
public sealed class MigrationStatusProbe(NexoraDbContext db)
{
    public async Task<MigrationStatus> GetAsync(CancellationToken ct = default)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != ConnectionState.Open;
        if (opened) await db.Database.OpenConnectionAsync(ct);
        try
        {
            return await MigrationRunner.StatusAsync(conn, ct);
        }
        finally
        {
            if (opened) await db.Database.CloseConnectionAsync();
        }
    }
}
