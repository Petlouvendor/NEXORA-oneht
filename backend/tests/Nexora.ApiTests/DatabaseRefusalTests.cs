using FluentAssertions;
using Nexora.Api.Middleware;
using Nexora.Domain.Common;
using Npgsql;

namespace Nexora.ApiTests;

/// <summary>
/// Audit 2026-10 éd. 2 : une erreur PostgreSQL attendue (refus d'un déclencheur, contrainte, conflit de concurrence)
/// levée hors d'une unité de travail sortait en 500 « erreur inattendue ». Elle devient un refus explicite ; une vraie
/// panne (erreur de syntaxe, colonne absente) reste une 500.
/// </summary>
public sealed class DatabaseRefusalTests
{
    private static PostgresException Pg(string sqlState, string message = "Refus métier")
        => new(message, "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData(PostgresErrorCodes.RaiseException, ErrorKind.Conflict, "database.refused")]
    [InlineData(PostgresErrorCodes.UniqueViolation, ErrorKind.Conflict, "database.conflict")]
    [InlineData(PostgresErrorCodes.CheckViolation, ErrorKind.Validation, "database.invalid")]
    [InlineData(PostgresErrorCodes.DeadlockDetected, ErrorKind.Conflict, "database.retry")]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, ErrorKind.Forbidden, "database.forbidden")]
    public void Expected_database_errors_become_explicit_refusals(string sqlState, ErrorKind kind, string code)
    {
        var refusal = ExceptionHandlingMiddleware.DatabaseRefusal(Pg(sqlState));
        refusal.Should().NotBeNull();
        refusal!.Kind.Should().Be(kind);
        refusal.Code.Should().Be(code);
    }

    [Fact]
    public void A_trigger_message_is_shown_as_is()
        => ExceptionHandlingMiddleware.DatabaseRefusal(Pg(PostgresErrorCodes.RaiseException, "La période est clôturée."))!
            .Message.Should().Be("La période est clôturée.");

    [Theory]
    [InlineData(PostgresErrorCodes.UndefinedColumn)]
    [InlineData(PostgresErrorCodes.SyntaxError)]
    public void A_real_failure_stays_an_internal_error(string sqlState)
        => ExceptionHandlingMiddleware.DatabaseRefusal(Pg(sqlState)).Should().BeNull();

    [Fact]
    public void Other_exceptions_are_not_database_refusals()
        => ExceptionHandlingMiddleware.DatabaseRefusal(new InvalidOperationException("x")).Should().BeNull();
}
