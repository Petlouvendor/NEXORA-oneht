using Microsoft.EntityFrameworkCore;
using Npgsql;
using Nexora.Application.Abstractions;

namespace Nexora.Infrastructure.Persistence;

public sealed class UnitOfWork(NexoraDbContext db) : IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken ct = default) => db.SaveChangesAsync(ct);

    /// <summary>
    /// Exécute l'action dans une transaction unique : rollback complet en cas d'échec (Phase 3 §15).
    /// Si une transaction est déjà ouverte (moteur financier appelé depuis un autre service),
    /// l'action s'y greffe sans en créer une seconde. Les violations de contraintes / triggers
    /// PostgreSQL deviennent des <see cref="InvalidOperationException"/> porteuses du message métier (ou d'un message
    /// neutre pour une contrainte : <see cref="DatabaseErrors"/>).
    /// </summary>
    public async Task<T> ExecuteInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action, CancellationToken ct = default)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            var inner = await action(ct);
            await db.SaveChangesAsync(ct);
            return inner;
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var result = await action(ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            await SafeRollback(tx, ct);
            db.ChangeTracker.Clear();
            throw new InvalidOperationException(DatabaseErrors.ClientMessage(pg), ex);
        }
        catch
        {
            await SafeRollback(tx, ct);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static async Task SafeRollback(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx, CancellationToken ct)
    {
        try { await tx.RollbackAsync(ct); }
        catch (InvalidOperationException) { /* transaction déjà terminée côté serveur */ }
    }
}
