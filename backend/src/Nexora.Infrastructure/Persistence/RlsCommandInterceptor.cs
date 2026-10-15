using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Nexora.Infrastructure.Persistence;

/// <summary>
/// Réapplique le contexte RLS devenu périmé (début ou fin d'une portée système sur une connexion déjà ouverte) juste
/// avant l'exécution de la commande suivante — audit 2026-10, A-009. Voir <see cref="RlsConnectionInterceptor"/>.
/// </summary>
public sealed class RlsCommandInterceptor(RlsConnectionInterceptor rls) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        rls.BeforeCommand(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
    {
        await rls.BeforeCommandAsync(command, ct).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        rls.BeforeCommand(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        await rls.BeforeCommandAsync(command, ct).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        rls.BeforeCommand(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken ct = default)
    {
        await rls.BeforeCommandAsync(command, ct).ConfigureAwait(false);
        return result;
    }
}
