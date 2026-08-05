using Microsoft.AspNetCore.DataProtection;
using frcastr.Core.Auth;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Adapters;
using frcastr.Infrastructure.Auth;
using frcastr.Infrastructure.BackgroundServices;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using frcastr.Infrastructure.Repositories;
using frcastr.Infrastructure.Services;
using frcastr.Web.Health;
using frcastr.Web.Middleware;
using frcastr.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Scalar.AspNetCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Configuration.AddJsonFile("setup-generated.json", optional: true, reloadOnChange: true);

// ── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ApplicationDbContext>((provider, options) =>
{
    var cs = provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
    if (!string.IsNullOrWhiteSpace(cs))
        options.UseSqlServer(cs);
    options.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
});

// ── Data Protection ───────────────────────────────────────────────────────────
// This key ring now protects stored data source secrets as well as auth cookies, so losing it costs
// more than a round of logouts. Two things follow:
//
//   - SetApplicationName is explicit. The default discriminator is derived from the content root
//     path, so moving or renaming the IIS site silently produced a different key isolation scope
//     and everything encrypted under the old one stopped decrypting. A fixed name unbinds the data
//     from where the app happens to live.
//   - deploy.ps1 excludes data-protection-keys from its mirror, and the folder needs to be in
//     whatever backs up the server. See "Data source secrets" in the README.
builder.Services.AddDataProtection()
    .SetApplicationName("frcastr")
    .PersistKeysToFileSystem(new System.IO.DirectoryInfo(
        Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys")));

// ── Identity ──────────────────────────────────────────────────────────────────
builder.Services.AddDefaultIdentity<ApplicationUser>(opts =>
{
    opts.SignIn.RequireConfirmedAccount = false;
    opts.Password.RequiredLength = 8;
    opts.Password.RequireNonAlphanumeric = false;
})
.AddRoles<IdentityRole>()
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultUI();

builder.Services.ConfigureApplicationCookie(opts =>
{
    opts.SlidingExpiration = true;
    opts.ExpireTimeSpan    = TimeSpan.FromDays(30);
});

// Overrides the above from Settings when configured. Registered as a post-configure hook so it
// survives an options rebuild; see the type's remarks.
builder.Services.AddSingleton<
    Microsoft.Extensions.Options.IPostConfigureOptions<
        Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>,
    frcastr.Web.Auth.SessionTimeoutCookieOptions>();

// ── Authorization / RBAC ──────────────────────────────────────────────────────
builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("AdministratorOnly", p => p.RequireRole("Administrator"));
});
builder.Services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

// ── Repository ────────────────────────────────────────────────────────────────
builder.Services.AddScoped(typeof(IRepository<>), typeof(Repository<>));

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddScoped<ISettingsService, SettingsService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<ISetupService, SetupService>();
builder.Services.AddScoped<IPermissionService, PermissionService>();
builder.Services.AddScoped<IWeatherDataService, WeatherDataService>();
builder.Services.AddScoped<IForecastService, ForecastService>();
builder.Services.AddScoped<ISunriseSunsetService, SunriseSunsetService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddSingleton<IDataSourceStatusService, DataSourceStatusService>();
builder.Services.AddScoped<IWeatherCacheInvalidator, OutputCacheWeatherInvalidator>();

// ── Adapters ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IForecastAdapter, NwsAdapter>();
builder.Services.AddScoped<IForecastAdapter, OpenWeatherMapAdapter>();
builder.Services.AddScoped<IForecastAdapter, WeatherApiAdapter>();
builder.Services.AddScoped<IForecastAdapter, OpenMeteoAdapter>();
builder.Services.AddScoped<IForecastAdapter, GenericJsonForecastAdapter>();
builder.Services.AddScoped<IDataPullAdapter, OpenWeatherMapAdapter>();
builder.Services.AddScoped<IDataPullAdapter, OpenMeteoAdapter>();
builder.Services.AddScoped<IDataPullAdapter, GenericHttpAdapter>();
builder.Services.AddScoped<IDataPullAdapter, NwsDataPullAdapter>();
builder.Services.AddScoped<IDataPullAdapter, AirNowAdapter>();
builder.Services.AddScoped<IDataSinkAdapter, WeatherUndergroundSinkAdapter>();
builder.Services.AddScoped<IDataSinkAdapter, PwsWeatherSinkAdapter>();
builder.Services.AddScoped<IDataSinkAdapter, GenericHttpSinkAdapter>();
builder.Services.AddScoped<IDataSourceTestService, DataSourceTestService>();

// ── Background services ───────────────────────────────────────────────────────
builder.Services.AddHostedService<HourlyAggregationBackgroundService>();
builder.Services.AddHostedService<DailyAggregationBackgroundService>();
builder.Services.AddHostedService<DataPullBackgroundService>();
builder.Services.AddHostedService<MqttBackgroundService>();
builder.Services.AddHostedService<OutboundUploadBackgroundService>();
builder.Services.AddHostedService<ForecastRefreshBackgroundService>();
builder.Services.AddHostedService<AlertsRefreshBackgroundService>();
builder.Services.AddHostedService<SensorOfflineBackgroundService>();
builder.Services.AddHostedService<WebhookAlertBackgroundService>();
// One-shot, guarded by a settings key: repairs the all-time records a pull source is no longer
// allowed to hold. A no-op on every boot after the first.
builder.Services.AddHostedService<PullChannelCleanupBackgroundService>();

// ── HTTP clients ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("nws", client =>
{
    client.BaseAddress = new Uri("https://api.weather.gov");
    client.DefaultRequestHeaders.Add("User-Agent",
        "frcastr/1.0 (weather station app; github.com/frcastr)");
    client.DefaultRequestHeaders.Add("Accept", "application/geo+json");
});
builder.Services.AddHttpClient("openmeteo", client =>
{
    client.BaseAddress = new Uri("https://api.open-meteo.com");
});
builder.Services.AddHttpClient("airnow", client =>
{
    client.BaseAddress = new Uri("https://www.airnowapi.org");
});
builder.Services.AddHttpClient("webhook");

// ── Output caching ────────────────────────────────────────────────────────────
builder.Services.AddOutputCache(opts =>
{
    opts.AddPolicy("WeatherCurrent", p =>
        p.Expire(TimeSpan.FromSeconds(15)).Tag("weather-current"));
    opts.AddPolicy("SunMoon", p =>
        p.Expire(TimeSpan.FromHours(1)).Tag("sun-moon"));
});

// ── Rate limiting ─────────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("IngestKeyPolicy", context =>
    {
        var apiKey = context.Request.Headers["X-Api-Key"].ToString();
        return !string.IsNullOrEmpty(apiKey)
            ? RateLimitPartition.GetFixedWindowLimiter(apiKey, _ =>
                new FixedWindowRateLimiterOptions
                {
                    Window = TimeSpan.FromMinutes(1),
                    PermitLimit = 1000
                })
            : RateLimitPartition.GetNoLimiter(string.Empty);
    });

    // History and export are anonymous and can each ask for a year of data. The global 100/min is
    // far too generous for a request that size, so they get their own much tighter window.
    opts.AddPolicy("WeatherBulkPolicy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 10
            }));

    opts.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ =>
            new FixedWindowRateLimiterOptions
            {
                Window = TimeSpan.FromMinutes(1),
                PermitLimit = 100
            });
    });

    opts.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        await context.HttpContext.Response.WriteAsync("Too Many Requests", ct);
    };
});

// ── Health checks ─────────────────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddCheck<DbHealthCheck>("database", failureStatus: HealthStatus.Unhealthy);

// ── OpenAPI ───────────────────────────────────────────────────────────────────
builder.Services.AddOpenApi();

// ── CSRF ──────────────────────────────────────────────────────────────────────
// The admin API is cookie-authenticated JSON, and MVC does not validate antiforgery tokens for
// controllers unless told to. SameSite=Lax on the Identity cookie blocks the cross-site form post
// today, but that leaves the whole mutating surface resting on one cookie attribute — setting
// SameSite=None to embed a dashboard would open it with no other signal. The header name is what
// the browser sends it back as; see adminFetch in wwwroot/js/admin-api.js.
builder.Services.AddAntiforgery(opts => opts.HeaderName = "X-CSRF-TOKEN");

builder.Services.AddRazorPages();
builder.Services.AddControllers(opts =>
    {
        // Every non-GET/HEAD/OPTIONS/TRACE action on every controller. Endpoints that cannot carry
        // a token — the API-key ingest routes — opt out with [IgnoreAntiforgeryToken].
        opts.Filters.Add<Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute>();
        opts.Filters.Add<frcastr.Web.Auth.AntiforgeryFailureFilter>();
    })
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.Converters.Add(
            new System.Text.Json.Serialization.JsonStringEnumConverter()));

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Apply any pending EF Core migrations on startup
using (var _migrateScope = app.Services.CreateScope())
{
    _migrateScope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
        .Database.Migrate();
}

// Encrypt any data source config still stored in the clear from before ProtectedStringConverter
// existed. It cannot be a SQL migration: the encryption is done by the app with its own key ring,
// which a migration script has no access to. Reading a legacy row yields plaintext and writing it
// back encrypts it, so the pass is just a load-and-resave, and the LIKE below keeps it from running
// on every subsequent boot.
using (var _protectScope = app.Services.CreateScope())
{
    try
    {
        var _db = _protectScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var _plaintextRows = _db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM [DataSources] " +
                $"WHERE [Config] IS NOT NULL AND [Config] NOT LIKE '{ProtectedStringConverter.Marker}%'")
            .AsEnumerable()
            .First();

        if (_plaintextRows > 0)
        {
            var _sources = _db.DataSources.ToList();
            foreach (var _source in _sources)
            {
                // Read already decrypted (or passed through); marking it modified makes the
                // converter write it back encrypted.
                _db.Entry(_source).Property(s => s.Config).IsModified = true;
            }
            _db.SaveChanges();
            app.Logger.LogInformation(
                "Encrypted {Count} data source config value(s) that were stored in plaintext.",
                _plaintextRows);
        }
    }
    catch (Exception ex)
    {
        // Pre-setup databases have no schema yet. A failure here leaves the rows readable —
        // the converter passes plaintext through — so it is not a reason to refuse to start.
        app.Logger.LogWarning(ex, "Could not encrypt stored data source secrets on startup.");
    }
}

// Prime the sensor-staleness tracker from stored readings. It is in-memory, so without this a
// restart leaves it empty and a sensor that died beforehand never registers as stale — it is only
// ever marked by a reading arriving, which by definition is not going to happen.
using (var _seedScope = app.Services.CreateScope())
{
    try
    {
        var _db = _seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var _status = _seedScope.ServiceProvider.GetRequiredService<IDataSourceStatusService>();

        var _latest = _db.WeatherReadings
            .GroupBy(r => new { r.ChannelName, r.DeviceId })
            .Select(g => new { g.Key.ChannelName, g.Key.DeviceId, LastAt = g.Max(r => r.Timestamp) })
            .ToList();

        // A channel a pull source used to write and no longer may would otherwise be seeded from
        // its final internet reading, go quiet forever, and be reported as an offline sensor for
        // the life of the install. For those channels the seed comes from the last reading a
        // device produced instead, and a pair with no device reading at all is not seeded — there
        // is no sensor there to be offline. A device genuinely reporting temperature.outdoor is
        // unaffected.
        var _blockedChannels = _latest
            .Where(x => ChannelLogPolicy.IsPullBlocked(x.ChannelName))
            .Select(x => x.ChannelName)
            .Distinct()
            .ToList();

        if (_blockedChannels.Count > 0)
        {
            var _pullSourceIds = _db.DataSources
                .Where(s => s.Type == DataSourceType.Pull || s.Type == DataSourceType.AirQuality)
                .Select(s => s.Id)
                .ToList();

            var _deviceLatest = _db.WeatherReadings
                .Where(r => _blockedChannels.Contains(r.ChannelName)
                         && !_pullSourceIds.Contains(r.SourceId))
                .GroupBy(r => new { r.ChannelName, r.DeviceId })
                .Select(g => new { g.Key.ChannelName, g.Key.DeviceId, LastAt = g.Max(r => r.Timestamp) })
                .ToList()
                .ToDictionary(x => (x.ChannelName, x.DeviceId), x => x.LastAt);

            _latest = _latest
                .Select(x =>
                {
                    if (!ChannelLogPolicy.IsPullBlocked(x.ChannelName)) return x;
                    return _deviceLatest.TryGetValue((x.ChannelName, x.DeviceId), out var deviceAt)
                        ? new { x.ChannelName, x.DeviceId, LastAt = deviceAt }
                        : null;
                })
                .Where(x => x is not null)
                .Select(x => x!)
                .ToList();
        }

        _status.Seed(_latest.Select(x =>
            new KeyValuePair<StaleChannel, DateTime>(
                new StaleChannel(x.ChannelName, x.DeviceId),
                DateTime.SpecifyKind(x.LastAt, DateTimeKind.Utc))));
    }
    catch (Exception ex)
    {
        // Pre-setup databases have no schema yet. Staleness is a display concern, not a reason to
        // refuse to boot.
        app.Logger.LogWarning(ex, "Could not seed sensor status from stored readings.");
    }
}

if (!app.Environment.IsDevelopment())
{
    // Without these an unhandled exception returned a bare 500 with an empty body and Pages/Error
    // was unreachable. Development keeps the framework's developer exception page.
    app.UseExceptionHandler("/Error");
    app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Applied before the static-file handlers so uploaded assets are covered too — an admin-supplied
// SVG is same-origin script otherwise. No CSP here: the pages load Bootstrap and Chart.js from
// wwwroot but also use inline handlers and inline <script> blocks throughout, so a meaningful
// policy needs those reworked first.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    await next();
});

app.UseStaticFiles();

// Serve uploads from a persistent directory outside the published wwwroot so files
// survive deployments that overwrite wwwroot.
var persistentUploadsPath = Path.Combine(app.Environment.ContentRootPath, "uploads");
Directory.CreateDirectory(persistentUploadsPath);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(persistentUploadsPath),
    RequestPath = "/uploads"
});

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();
app.UseRateLimiter();
app.UseMiddleware<SetupMiddleware>();

app.MapRazorPages();
app.MapControllers();

app.MapHealthChecks("/health").AllowAnonymous();

// The document describes every admin route and its shape, so it is gated with the UI in front of
// it rather than left open behind an authorized viewer.
var openApi = app.MapOpenApi("/api/openapi");
var scalar = app.MapScalarApiReference("/scalar");
if (!app.Environment.IsDevelopment())
{
    openApi.RequireAuthorization("AdministratorOnly");
    scalar.RequireAuthorization("AdministratorOnly");
}

app.Run();
