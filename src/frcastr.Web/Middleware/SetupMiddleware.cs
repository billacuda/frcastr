using frcastr.Core.Interfaces;

namespace frcastr.Web.Middleware;

public class SetupMiddleware(RequestDelegate next, IServiceScopeFactory scopeFactory)
{
    private static volatile bool _setupComplete;

    private static readonly string[] ExemptPrefixes =
    [
        "/setup", "/identity", "/_framework", "/favicon", "/health"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (_setupComplete) { await next(context); return; }

        var path = context.Request.Path.Value ?? string.Empty;

        if (ExemptPrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            await next(context);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<ISetupService>();

        if (await setup.IsSetupCompleteAsync())
        {
            _setupComplete = true;
            await next(context);
        }
        else
        {
            context.Response.Redirect("/Setup");
        }
    }
}
