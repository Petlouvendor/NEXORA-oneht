namespace Nexora.Domain.Common;

public enum ErrorKind { Validation, NotFound, Conflict, Forbidden, Unauthorized, Unexpected }

public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);
    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
    public static Error Forbidden(string code, string message) => new(ErrorKind.Forbidden, code, message);
    public static Error Unauthorized(string code, string message) => new(ErrorKind.Unauthorized, code, message);
    public static Error Unexpected(string code, string message) => new(ErrorKind.Unexpected, code, message);
}

/// <summary>Résultat d'opération sans exception pour le flux métier attendu (§3, §15).</summary>
public readonly struct Result
{
    public bool IsSuccess { get; }
    public Error? Error { get; }

    private Result(bool ok, Error? error) { IsSuccess = ok; Error = error; }

    public static Result Success() => new(true, null);
    public static Result Failure(Error error) => new(false, error);
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);
    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);

    public static implicit operator Result(Error error) => Failure(error);
}

public readonly struct Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public Error? Error { get; }

    private Result(bool ok, T? value, Error? error) { IsSuccess = ok; Value = value; Error = error; }

    public static Result<T> Success(T value) => new(true, value, null);
    public static Result<T> Failure(Error error) => new(false, default, error);

    public static implicit operator Result<T>(Error error) => Failure(error);
}
