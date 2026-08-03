using Microsoft.AspNetCore.Mvc;
// Not ViewFeatures, where this interface lived until ASP.NET Core 10.
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace frcastr.Web.Auth;

/// <summary>
/// Turns a rejected antiforgery token into an explanation. The framework's default is an empty 400,
/// which reaches the admin pages as a bare "HTTP 400" alert and gives no hint that the fix is to
/// reload — and a stale token is the ordinary outcome of leaving a tab open until the session cookie
/// rolls over, not an attack.
/// </summary>
/// <remarks>
/// <see cref="IAlwaysRunResultFilter"/> rather than the ordinary result filter interface: the
/// antiforgery filter short-circuits the pipeline, so a filter that only runs on a completed action
/// never sees this result.
/// </remarks>
public sealed class AntiforgeryFailureFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult) return;

        context.Result = new ObjectResult(
            "This page's security token is stale — reload the page and try again.")
        {
            StatusCode = StatusCodes.Status400BadRequest
        };
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}
