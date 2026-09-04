namespace TicketHub.Contracts.Common;

/// <summary>
/// The vocabulary the business layer uses to say what happened, without knowing
/// that HTTP exists.
/// </summary>
/// <remarks>
/// A service must not return <c>NotFound()</c> or <c>BadRequest()</c> — those are
/// <see cref="Microsoft.AspNetCore.Mvc.ControllerBase"/> methods, and the moment a service
/// touches them it can no longer be called from a background job, a console tool or a unit
/// test without dragging ASP.NET Core along. So the service returns one of these, and the
/// controller does the one-line translation into a status code.
/// </remarks>
public enum ResultStatus
{
    /// <summary>It worked. → 200 / 201.</summary>
    Success = 0,

    /// <summary>The id does not exist (or the caller may not see it). → 404.</summary>
    NotFound = 1,

    /// <summary>The input is wrong. → 400.</summary>
    ValidationFailed = 2,

    /// <summary>Input is fine, but the rules say no — e.g. resolving an already-closed ticket. → 409.</summary>
    Conflict = 3,

    /// <summary>Caller is known but not allowed to touch this row. → 403.</summary>
    Forbidden = 4
}

/// <summary>A result that carries no payload — used by delete, assign, and similar.</summary>
public class ServiceResult
{
    public ResultStatus Status { get; init; } = ResultStatus.Success;
    public string? Error { get; init; }

    /// <summary>Field name → what is wrong with it. Feeds a 400 ProblemDetails.</summary>
    public IDictionary<string, string[]>? ValidationErrors { get; init; }

    public bool IsSuccess => Status == ResultStatus.Success;

    public static ServiceResult Success() => new();

    public static ServiceResult NotFound(string message = "The requested resource was not found.")
        => new() { Status = ResultStatus.NotFound, Error = message };

    public static ServiceResult Invalid(string message)
        => new() { Status = ResultStatus.ValidationFailed, Error = message };

    public static ServiceResult Invalid(IDictionary<string, string[]> errors)
        => new() { Status = ResultStatus.ValidationFailed, Error = "One or more validation errors occurred.", ValidationErrors = errors };

    public static ServiceResult Conflict(string message)
        => new() { Status = ResultStatus.Conflict, Error = message };

    public static ServiceResult Forbidden(string message = "You do not have access to this resource.")
        => new() { Status = ResultStatus.Forbidden, Error = message };
}

/// <summary>The same thing, but it carries a value when it succeeds.</summary>
/// <typeparam name="T">Always a DTO. Never an entity — see the note in TicketService.</typeparam>
public class ServiceResult<T> : ServiceResult
{
    /// <summary>The payload. Only meaningful when <see cref="ServiceResult.IsSuccess"/> is true.</summary>
    public T? Value { get; init; }

    public static ServiceResult<T> Success(T value) => new() { Value = value };

    public static new ServiceResult<T> NotFound(string message = "The requested resource was not found.")
        => new() { Status = ResultStatus.NotFound, Error = message };

    public static new ServiceResult<T> Invalid(string message)
        => new() { Status = ResultStatus.ValidationFailed, Error = message };

    public static new ServiceResult<T> Invalid(IDictionary<string, string[]> errors)
        => new() { Status = ResultStatus.ValidationFailed, Error = "One or more validation errors occurred.", ValidationErrors = errors };

    public static new ServiceResult<T> Conflict(string message)
        => new() { Status = ResultStatus.Conflict, Error = message };

    public static new ServiceResult<T> Forbidden(string message = "You do not have access to this resource.")
        => new() { Status = ResultStatus.Forbidden, Error = message };
}
