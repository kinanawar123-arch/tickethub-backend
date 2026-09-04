using Microsoft.AspNetCore.Mvc;
using TicketHub.Contracts.Common;

namespace TicketHub.Api.Controllers;

/// <summary>
/// The base every controller inherits. Its whole job is translating a
/// <see cref="ServiceResult"/> into an HTTP status code.
/// </summary>
/// <remarks>
/// THIS IS THE SEAM BETWEEN THE BUSINESS LAYER AND HTTP.
/// The service says "not found"; this decides that means 404. The service says "conflict";
/// this decides that means 409. Written once, here, so forty actions cannot each invent a
/// slightly different answer for the same situation.
/// <para/>
/// A controller in this project should be about five lines: bind the request, call the
/// service, hand the result to <see cref="ToActionResult{T}"/>. If a controller starts
/// containing <c>if</c> statements about business rules, those rules are in the wrong file.
/// </remarks>
[ApiController]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>Turns a result-with-a-value into 200 / 404 / 400 / 409 / 403.</summary>
    protected ActionResult<T> ToActionResult<T>(ServiceResult<T> result)
    {
        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return MapFailure(result);
    }

    /// <summary>Turns a result-with-no-value into 204 or an error.</summary>
    protected ActionResult ToActionResult(ServiceResult result)
        => result.IsSuccess ? NoContent() : MapFailure(result);

    /// <summary>
    /// 201 Created, with the Location header pointing at the new resource.
    /// </summary>
    /// <remarks>
    /// 201 rather than 200 is not pedantry: it tells the client something was created and
    /// where to find it, which is exactly what a well-behaved HTTP client is looking for.
    /// </remarks>
    protected ActionResult<T> CreatedResult<T>(ServiceResult<T> result, string actionName, object routeValues)
        => result.IsSuccess
            ? CreatedAtAction(actionName, routeValues, result.Value)
            : MapFailure(result);

    private ObjectResult MapFailure(ServiceResult result)
    {
        // Field-level validation errors get ValidationProblemDetails, which is the same shape
        // ASP.NET Core produces automatically when [ApiController] rejects a bad model. Same
        // shape from both sources means the front end has one error handler, not two.
        if (result.Status == ResultStatus.ValidationFailed && result.ValidationErrors is not null)
        {
            var problem = new ValidationProblemDetails(
                result.ValidationErrors.ToDictionary(kv => kv.Key, kv => kv.Value))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = result.Error ?? "One or more validation errors occurred."
            };

            return new ObjectResult(problem) { StatusCode = StatusCodes.Status400BadRequest };
        }

        var status = result.Status switch
        {
            ResultStatus.NotFound => StatusCodes.Status404NotFound,
            ResultStatus.ValidationFailed => StatusCodes.Status400BadRequest,

            // 409 Conflict, not 400. The request was well formed and the caller did nothing
            // wrong — the *state of the resource* is what makes it impossible right now.
            // "You cannot resolve a cancelled ticket" is a conflict; "title is too short"
            // is a bad request. The distinction tells the client whether retrying could ever
            // help.
            ResultStatus.Conflict => StatusCodes.Status409Conflict,

            // 403, not 401. 401 means "I do not know who you are"; 403 means "I know exactly
            // who you are and the answer is still no". Returning 401 here makes every
            // well-written front end try to refresh the token and retry — forever. A refresh
            // loop hammering your login endpoint is almost always this bug.
            ResultStatus.Forbidden => StatusCodes.Status403Forbidden,

            _ => StatusCodes.Status400BadRequest
        };

        return new ObjectResult(new ProblemDetails
        {
            Status = status,
            Title = result.Error ?? "The request could not be completed.",
            Instance = HttpContext.Request.Path
        })
        {
            StatusCode = status
        };
    }
}
