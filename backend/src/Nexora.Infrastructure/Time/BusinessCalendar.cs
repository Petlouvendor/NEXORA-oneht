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
    // Fuseau gardé pour l'entreprise qui l'a demandé (audit 2026-10 éd. 2) : le planificateur parcourt toutes les
    // entreprises dans une même portée, et chacune recevait le fuseau de la première.
    private TimeZoneInfo? _zone;
    private Guid? _zoneCompany;

    private sealed record ZoneRow(string Value);

    private TimeZoneInfo Remember(Guid? company, TimeZoneInfo zone)
    {
        _zoneCompany = company;
        return _zone = zone;
    }

    public TimeZoneInfo Zone
    {
        get
        {
            var id = CompanyId();
            if (_zone is not null && _zoneCompany == id) return _zone;
            if (id is not { } company) return Remember(null, CompanyTimeZoneCache.Default);
            if (cache.TryGet(company, out var cached)) return Remember(company, cached);
            return Remember(company, cache.Store(company, Query(company).ToList().FirstOrDefault()?.Value));
        }
    }

    public DateOnly Today => BusinessDate.Of(clock.UtcNow, Zone);

    public DateOnly DateOf(DateTimeOffset instant) => BusinessDate.Of(instant, Zone);

    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct = default)
    {
        var id = CompanyId();
        if (_zone is not null && _zoneCompany == id) return _zone;
        if (id is not { } company) return Remember(null, CompanyTimeZoneCache.Default);
        if (cache.TryGet(company, out var cached)) return Remember(company, cached);
        return Remember(company, cache.Store(company, (await Query(company).ToListAsync(ct)).FirstOrDefault()?.Value));
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
