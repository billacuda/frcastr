using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace frcastr.Web.Auth;

/// <summary>
/// Applies the DB-configured <c>Auth.SessionTimeoutHours</c> to the Identity application cookie,
/// overriding the 30-day sliding default when a value is set.
/// </summary>
/// <remarks>
/// This has to be a post-configure hook rather than a one-off mutation at startup. The options
/// instance is rebuilt whenever the underlying configuration reloads — and <c>setup-generated.json</c>
/// is registered with <c>reloadOnChange</c>, so it does — which silently reverted an override
/// applied by hand to the instance held at boot. Running as part of configuration means every
/// rebuild gets it.
/// </remarks>
public sealed class SessionTimeoutCookieOptions(
    IServiceScopeFactory scopeFactory,
    ILogger<SessionTimeoutCookieOptions> logger)
    : IPostConfigureOptions<CookieAuthenticationOptions>
{
    public void PostConfigure(string? name, CookieAuthenticationOptions options)
    {
        if (name != IdentityConstants.ApplicationScheme) return;

        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var value = db.Settings
                .Where(s => s.Key == "Auth.SessionTimeoutHours")
                .Select(s => s.Value)
                .FirstOrDefault();

            if (int.TryParse(value, out var hours) && hours > 0)
            {
                options.ExpireTimeSpan = TimeSpan.FromHours(hours);
                options.SlidingExpiration = false;
            }
        }
        catch (Exception ex)
        {
            // No schema yet (pre-setup) or the database is briefly unreachable. Falling back to the
            // configured default is right; refusing to build the auth cookie would lock everyone out.
            logger.LogWarning(ex, "Could not read Auth.SessionTimeoutHours; using the default cookie lifetime.");
        }
    }
}
