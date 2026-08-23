using System.Diagnostics.CodeAnalysis;

namespace Bifrost.Core.Model;

/// <summary>
/// Success, or a <see cref="PrinterError"/>. Used instead of exceptions for expected failures.
/// </summary>
/// <remarks>
/// A printer being out of paper is not exceptional — it happens several times a day. Exceptions are
/// reserved for programming errors. Putting the failure in the type means the caller cannot ignore
/// it. See Docs/04-implementation/03-coding-standards.md §2.1.
///
/// Deliberately in-house rather than a functional library: one concept does not justify a
/// dependency (IMP-01 §7).
/// </remarks>
public readonly record struct Result
{
    private Result(PrinterError? error) => Error = error;

    public PrinterError? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    public static Result Ok() => new(null);

    public static Result Fail(PrinterError error) => new(error);

    public static implicit operator Result(PrinterError error) => Fail(error);
}

/// <summary>A value, or a <see cref="PrinterError"/>.</summary>
public readonly record struct Result<T>
{
    private Result(T? value, PrinterError? error)
    {
        _value = value;
        Error = error;
    }

    private readonly T? _value;

    public PrinterError? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => Error is not null;

    /// <summary>The value. Throws if this is a failure — check <see cref="IsSuccess"/> first.</summary>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException($"Result is a failure: {Error.Code}");

    public static Result<T> Ok(T value) => new(value, null);

    public static Result<T> Fail(PrinterError error) => new(default, error);

    public static implicit operator Result<T>(T value) => Ok(value);

    public static implicit operator Result<T>(PrinterError error) => Fail(error);
}
