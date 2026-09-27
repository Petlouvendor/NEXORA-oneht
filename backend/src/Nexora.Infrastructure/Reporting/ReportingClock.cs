using Microsoft.EntityFrameworkCore;
using Nexora.Infrastructure.Persistence;

namespace Nexora.Infrastructure.Reporting;

/// <summary>
/// Date métier « aujourd'hui » pour le pilotage (Phase 7 §6). Lue depuis la base
/// (<c>current_date</c>, fuseau du serveur) pour rester cohérente avec les colonnes
/// <c>sold_at::date</c>, <c>expense_date</c> et <c>due_date</c> — jamais l'UTC du process,
/// qui bascule un jour trop tôt en soirée.
/// </summary>
internal static class ReportingClock
{
    private sealed record DateRow(DateOnly Value);

    public static async Task<DateOnly> TodayAsync(NexoraDbContext db, CancellationToken ct)
        => (await db.Database.SqlQuery<DateRow>($"SELECT current_date AS \"Value\"").ToListAsync(ct))[0].Value;
}
