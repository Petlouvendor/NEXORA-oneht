using Nexora.Application.Abstractions;

namespace Nexora.IntegrationTests;

/// <summary>
/// Jour civil des entreprises de test (fuseau America/Port-au-Prince), comme l'application le calcule
/// (IBusinessCalendar) — jamais le jour UTC, déjà au lendemain le soir en Haïti.
/// </summary>
internal static class TestDates
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(BusinessDate.DefaultZoneId);

    public static DateOnly Today => BusinessDate.Of(DateTimeOffset.UtcNow, Zone);
}
