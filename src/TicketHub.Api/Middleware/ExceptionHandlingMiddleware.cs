using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace TicketHub.Api.Middleware;

/// <summary>
/// The last line of defence: turns any unhandled exception into a clean ProblemDetails
/// response instead of a stack trace.
/// </summary>
/// <remarks>
/// WHY MIDDLEWARE AND NOT try/catch IN EVERY CONTROLLER.
/// A try/catch per action is fifty places to forget one, fifty slightly different error
/// shapes, and fifty chances to leak an internal message. Middleware wraps the entire
/// pipeline: one place, one shape, every endpoint — including the ones nobody has written yet.
/// <para/>
/// ⚠ THE MOST IMPORTANT RULE HERE: in production the response says "something went wrong" and
/// nothing else. Exception messages leak table names, file paths, connection strings and
/// library versions — a free reconnaissance report for anyone probing your API. The detail
/// goes to the log, where you can read it and they cannot.
/// <para/>
/// The <c>TraceId</c> is the bridge: the user quotes it in a support ticket and you find the
/// exact log entry in seconds.
/// </remarks>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _log;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> log,
        IHostEnvironment environment)
    {
        _next = next;
        _log = log;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // Call the next thing in the pipeline. Everything downstream — routing, the
            // controller, the service, EF — runs inside this await, so any exception any of
            // them throw comes back out here.
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleAsync(context, ex);
        }
    }

    private async Task HandleAsync(HttpContext context, Exception exception)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        // Map the exceptions we understand to sensible status codes. Anything unrecognised
        // is a 500 — and being a 500 is correct, because we genuinely do not know what
        // happened and pretending otherwise helps nobody.
        var (status, title) = exception switch
        {
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict,
                "The record was changed by someone else. Reload and try again."),

            DbUpdateException => (StatusCodes.Status409Conflict,
                "The change conflicts with existing data."),

            UnauthorizedAccessException => (StatusCodes.Status403Forbidden,
                "You do not have access to this resource."),

            KeyNotFoundException => (StatusCodes.Status404NotFound,
                "The requested resource was not found."),

            // A cancelled request is not an error. The user navigated away or closed the tab,
            // and 499 (a non-standard but widely used code) keeps these out of your 500 rate,
            // where they would otherwise look like an outage.
            OperationCanceledException => (499, "The request was cancelled."),

            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };

        if (status >= 500)
        {
            _log.LogError(exception,
                "Unhandled exception on {Method} {Path}. TraceId {TraceId}",
                context.Request.Method, context.Request.Path, traceId);
        }
        else
        {
            _log.LogWarning(exception,
                "Handled exception on {Method} {Path}: {Message}",
                context.Request.Method, context.Request.Path, exception.Message);
        }

        // Headers can only be set before the response starts. If something already wrote to
        // the body — a streaming file download that failed halfway — it is too late, and
        // trying anyway throws a second, more confusing exception.
        if (context.Response.HasStarted)
        {
            _log.LogWarning("Response already started; cannot write an error body.");
            return;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Instance = context.Request.Path,
            Type = $"https://httpstatuses.io/{status}"
        };

        problem.Extensions["traceId"] = traceId;

        // The detail ONLY in development. This single condition is what separates a helpful
        // debugging experience from handing an attacker your internals.
        if (_environment.IsDevelopment())
        {
            problem.Detail = exception.ToString();
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";

        await context.Response.WriteAsync(
            JsonSerializer.Serialize(problem, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));
    }
}

/// <summary>
/// Logs one line per request with its outcome and duration.
/// </summary>
/// <remarks>
/// ASP.NET Core already logs requests, but at Information level and in several lines. This
/// gives you one structured line you can actually query: "show me every request over 500ms",
/// or "every 500 in the last hour".
/// <para/>
/// Note it logs the <b>route</b>, never the query string. Query strings contain search terms,
/// email addresses and ids — personal data that then lives in your logs forever and gets
/// shipped to whatever aggregator you use.
/// </remarks>
public class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _log;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();

            // A slow request is worth noticing even when it succeeded — it is the early
            // warning for the N+1 query somebody just introduced.
            var level = stopwatch.ElapsedMilliseconds > 1000 ? LogLevel.Warning : LogLevel.Information;

            _log.Log(level,
                "{Method} {Path} → {StatusCode} in {Elapsed}ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
