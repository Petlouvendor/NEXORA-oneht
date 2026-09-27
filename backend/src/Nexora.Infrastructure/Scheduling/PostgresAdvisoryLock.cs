using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Infrastructure.Persistence;
using Npgsql;

namespace Nexora.Infrastructure.Scheduling;

/// <summary>
/// Verrou distribué léger via PostgreSQL Advisory Lock (Production Readiness — REQ-01).
///
/// <para>
/// Utilise <c>pg_try_advisory_lock(bigint)</c> — non bloquant, libéré automatiquement en fin
/// de session (filet de sécurité si le process crashe). Garantit qu'un seul passage du
/// <see cref="SchedulerRunner"/> s'exécute à la fois, même sur N instances horizontales.
/// </para>
///
/// <para>
/// Une <b>connexion dédiée</b> (hors du pool EF Core) est ouverte pour chaque tentative :
/// les advisory locks de session sont liés à la connexion physique, pas à la transaction.
/// Utiliser le <c>NexoraDbContext</c> EF risquerait de libérer le lock au mauvais moment
/// si EF réutilise/recycle la connexion.
/// </para>
///
/// <para>Enregistré en <b>Singleton</b> — sans état propre, réentrant.</para>
/// </summary>
public sealed class PostgresAdvisoryLock(
    IOptions<NexoraDbOptions> dbOptions,
    ILogger<PostgresAdvisoryLock> logger)
{
    /// <summary>
    /// Clé fixe dérivée du nom logique "nexora-scheduler".
    /// Doit rester constante entre les versions pour que les instances d'une même app
    /// se coordonnent correctement.
    /// </summary>
    public const long SchedulerLockKey = unchecked((long)0xBE57_3C48_A1D2_4F90UL);

    /// <summary>
    /// Tente d'acquérir le lock, exécute <paramref name="action"/>, libère le lock dans
    /// un bloc <c>finally</c> (même en cas d'exception).
    /// </summary>
    /// <returns>
    /// <c>true</c> si le lock a été acquis et l'action exécutée ;
    /// <c>false</c> si une autre instance détient déjà le lock (passage ignoré silencieusement).
    /// </returns>
    public async Task<bool> TryExecuteAsync(
        Func<CancellationToken, Task> action,
        CancellationToken ct = default)
    {
        var connectionString = dbOptions.Value.ConnectionString;

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Pas de base configurée (mode setup / tests sans DB) → exécute sans lock.
            logger.LogDebug("[advisory-lock] pas de chaîne de connexion — exécution sans lock.");
            await action(ct);
            return true;
        }

        NpgsqlConnection? conn = null;
        try
        {
            conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            // pg_try_advisory_lock retourne immédiatement : true = lock acquis, false = déjà pris.
            await using var cmdTry = conn.CreateCommand();
            cmdTry.CommandText = "SELECT pg_try_advisory_lock($1)";
            cmdTry.Parameters.AddWithValue(SchedulerLockKey);

            var acquired = (bool)(await cmdTry.ExecuteScalarAsync(ct))!;
            if (!acquired)
            {
                logger.LogDebug(
                    "[advisory-lock] lock {Key} non disponible — autre instance en cours, passage ignoré.",
                    SchedulerLockKey);
                return false;
            }

            logger.LogDebug("[advisory-lock] lock {Key} acquis.", SchedulerLockKey);

            try
            {
                await action(ct);
            }
            finally
            {
                // Libération explicite — ne pas attendre la fermeture de connexion.
                await using var cmdRelease = conn.CreateCommand();
                cmdRelease.CommandText = "SELECT pg_advisory_unlock($1)";
                cmdRelease.Parameters.AddWithValue(SchedulerLockKey);
                await cmdRelease.ExecuteScalarAsync(CancellationToken.None);

                logger.LogDebug("[advisory-lock] lock {Key} libéré.", SchedulerLockKey);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            // Annulation propre — ne pas loguer comme erreur.
            throw;
        }
        catch (NpgsqlException ex)
        {
            // PostgreSQL injoignable : on laisse le scheduler passer ce tick sans lock
            // plutôt que de crasher l'instance. Le prochain tick réessaiera.
            logger.LogWarning(ex,
                "[advisory-lock] PostgreSQL injoignable lors de la tentative d'acquisition — passage ignoré.");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[advisory-lock] exception inattendue — passage ignoré.");
            return false;
        }
        finally
        {
            if (conn is not null)
                await conn.DisposeAsync();
        }
    }
}
