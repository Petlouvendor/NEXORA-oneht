using Npgsql;

namespace Nexora.Infrastructure.Persistence;

/// <summary>
/// Message d'un refus de la base présentable à l'utilisateur (audit 2026-10, priorité 8). Les refus métier levés par
/// les déclencheurs (<c>RAISE EXCEPTION</c>, souvent sous le code <c>check_violation</c>) sont rédigés pour lui et
/// gardés tels quels. Les autres — contrainte d'unicité, clé étrangère, contrôle, politique RLS… — citaient au client
/// noms de tables, de contraintes et valeurs (« duplicate key value violates unique constraint … ») : remplacés par un
/// message neutre. L'exception d'origine reste attachée pour la journalisation et pour les appelants qui testent
/// <see cref="PostgresException.ConstraintName"/>.
/// </summary>
public static class DatabaseErrors
{
    public static bool IsBusinessRefusal(PostgresException pg)
        => pg.SqlState == PostgresErrorCodes.RaiseException
           || (string.IsNullOrEmpty(pg.ConstraintName) && pg.Where?.Contains("at RAISE", StringComparison.Ordinal) == true);

    public static string ClientMessage(PostgresException pg)
    {
        if (IsBusinessRefusal(pg)) return pg.MessageText;
        return pg.SqlState switch
        {
            PostgresErrorCodes.UniqueViolation => "Cet enregistrement existe déjà.",
            PostgresErrorCodes.ForeignKeyViolation => "Un élément référencé est introuvable, ou l'élément est encore utilisé ailleurs.",
            PostgresErrorCodes.NotNullViolation => "Une information obligatoire manque.",
            PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ExclusionViolation
                => "Une valeur saisie ne respecte pas les règles de l'application.",
            PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.LockNotAvailable
                => "Une autre opération modifiait les mêmes données : réessayez.",
            PostgresErrorCodes.InsufficientPrivilege => "Opération non autorisée sur ces données.",
            _ => "L'opération a été refusée par la base de données.",
        };
    }
}
