using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Domain.Identity;
using Nexora.Domain.Organization;

namespace Nexora.Infrastructure.Persistence;

public sealed class UserRepository(NexoraDbContext db, ITenantContext tenant) : IUserRepository
{
    public async Task<AppUser?> FindByEmailAsync(string email, CancellationToken ct = default)
    {
        using (tenant.BeginSystemScope())   // résolution d'identité : hors périmètre entreprise
            return await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
    }

    public Task<AppUser?> FindByIdAsync(Guid id, CancellationToken ct = default)
    {
        using (tenant.BeginSystemScope())
            return db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public async Task TouchLastLoginAsync(Guid id, DateTimeOffset at, CancellationToken ct = default)
    {
        using (tenant.BeginSystemScope())
            await db.Users.Where(u => u.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, at), ct);
    }

    // Protection contre les essais répétés (Phase 33 §7, §4, §48) — seuil et durée fixes.
    private const int LockThreshold = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public async Task<DateTimeOffset?> RegisterFailedLoginAsync(Guid id, DateTimeOffset now, CancellationToken ct = default)
    {
        var lockUntil = now.Add(LockDuration);
        using (tenant.BeginSystemScope())
        {
            // Les deux CASE désignent la valeur d'avant-UPDATE de `failed_login_count` (sémantique SQL).
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user
                SET locked_until = CASE WHEN failed_login_count + 1 >= {LockThreshold} THEN {lockUntil} ELSE locked_until END,
                    failed_login_count = CASE WHEN failed_login_count + 1 >= {LockThreshold} THEN 0 ELSE failed_login_count + 1 END,
                    updated_at = now()
                WHERE id = {id}
                """, ct);
            var locked = await db.Database.SqlQuery<DateTimeOffset?>($"""
                SELECT locked_until AS "Value" FROM nexora.app_user WHERE id = {id}
                """).FirstOrDefaultAsync(ct);
            return locked is { } u && u > now ? locked : null;
        }
    }

    public async Task ResetFailedLoginsAsync(Guid id, CancellationToken ct = default)
    {
        using (tenant.BeginSystemScope())
            await db.Database.ExecuteSqlAsync($"""
                UPDATE nexora.app_user SET failed_login_count = 0, locked_until = NULL, updated_at = now()
                WHERE id = {id} AND (failed_login_count <> 0 OR locked_until IS NOT NULL)
                """, ct);
    }
}

public sealed class CompanyRepository(NexoraDbContext db) : ICompanyRepository
{
    // Aucun filtre company_id explicite : PostgreSQL RLS restreint déjà au périmètre (Phase 3 §11).
    public async Task<IReadOnlyList<Company>> ListForCurrentScopeAsync(CancellationToken ct = default)
        => await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public Task<Company?> GetAsync(Guid id, CancellationToken ct = default)
        => db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
}
