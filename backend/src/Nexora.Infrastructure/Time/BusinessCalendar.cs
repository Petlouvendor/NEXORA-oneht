using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Nexora.Application.Abstractions;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Time;

/// <summary>
/// Fuseaux des entreprises, gardés quelques minutes en mémoire : le fuseau change rarement (création, réglage) et
/// chaque requête en a besoin. Un fuseau inconnu du système retombe sur Haïti plutôt que de casser l'opération.
/// </summary>
public sealed class CompanyTimeZoneCache(IDateTimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<Guid, (TimeZoneInfo Zone, DateTimeOffset LoadedAt)> _zones = new();

    public static TimeZoneInfo Default { get; } = Resolve(BusinessDate.DefaultZoneId);

    public bool TryGet(Guid companyId, out TimeZoneInfo zone)
    {
        if (_zones.TryGetValue(companyId, out var e) && clock.UtcNow - e.LoadedAt < Ttl)
        {
            zone = e.Zone;
            return true;
        }
        zone = Default;
        return false;
    }

    public TimeZoneInfo Store(Guid companyId, string? zoneId)
    {
        var zone = Resolve(zoneId);
        _zones[companyId] = (zone, clock.UtcNow);
        return zone;
    }

    public void Invalidate(Guid companyId) => _zones.TryRemove(companyId, out _);

    public static TimeZoneInfo Resolve(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { /* repli ci-dessous */ }
            catch (InvalidTimeZoneException) { /* repli ci-dessous */ }
        }
        return id == BusinessDate.DefaultZoneId ? TimeZoneInfo.Utc : Resolve(BusinessDate.DefaultZoneId);
    }
}

/// <summary>Date métier de l'entreprise active (voir <see cref="IBusinessCalendar"/>).</summary>
public sealed class BusinessCalendar(
    NexoraDbContext db, ITenantContext tenant, IDateTimeProvider clock, CompanyTimeZoneCache cache) : IBusinessCalendar
{
    private TimeZoneInfo? _zone;

    private sealed record ZoneRow(string Value);

    public TimeZoneInfo Zone
    {
        get
        {
            if (_zone is not null) return _zone;
            if (CompanyId() is not { } id) return _zone = CompanyTimeZoneCache.Default;
            if (cache.TryGet(id, out var cached)) return _zone = cached;
            return _zone = cache.Store(id, Query(id).ToList().FirstOrDefault()?.Value);
        }
    }

    public DateOnly Today => BusinessDate.Of(clock.UtcNow, Zone);

    public DateOnly DateOf(DateTimeOffset instant) => BusinessDate.Of(instant, Zone);

    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct = default)
    {
        if (_zone is not null) return _zone;
        if (CompanyId() is not { } id) return _zone = CompanyTimeZoneCache.Default;
        if (cache.TryGet(id, out var cached)) return _zone = cached;
        return _zone = cache.Store(id, (await Query(id).ToListAsync(ct)).FirstOrDefault()?.Value);
    }

    private Guid? CompanyId()
        => tenant.ActiveCompanyId ?? (tenant.CompanyIds.Count == 1 ? tenant.CompanyIds.First() : null);

    private IQueryable<ZoneRow> Query(Guid id)
        => db.Database.SqlQuery<ZoneRow>($"""SELECT timezone AS "Value" FROM nexora.company WHERE id = {id}""");

    public async Task<DateOnly> TodayAsync(CancellationToken ct = default)
        => BusinessDate.Of(clock.UtcNow, await ZoneAsync(ct));

    public async Task<DateOnly> DateOfAsync(DateTimeOffset instant, CancellationToken ct = default)
        => BusinessDate.Of(instant, await ZoneAsync(ct));
}
