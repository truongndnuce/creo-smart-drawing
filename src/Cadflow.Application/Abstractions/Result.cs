namespace Cadflow.Application.Abstractions;

public enum ErrorKind { Validation, NotFound, Forbidden, Conflict }

public sealed record AppError(ErrorKind Kind, string Message, IReadOnlyList<ParamIssue>? Issues = null);

public sealed record ParamIssue(string Message, IReadOnlyList<string> Params);

/// <summary>Outcome of a use case: a value or an error. The Api layer maps ErrorKind to an HTTP status.</summary>
public readonly record struct Result<T>(T? Value, AppError? Error)
{
    public bool Ok => Error is null;
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Fail(ErrorKind kind, string message, IReadOnlyList<ParamIssue>? issues = null) => new(default, new AppError(kind, message, issues));
}
