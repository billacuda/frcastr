using frcastr.Core.Auth;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Adapters;
using frcastr.Infrastructure.Auth;
using frcastr.Infrastructure.BackgroundServices;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Repositories;
using frcastr.Infrastructure.Services;
using frcastr.Web.Health;
using frcastr.Web.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
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
});

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
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddSingleton<IDataSourceStatusService, DataSourceStatusService>();

// ── Adapters ──────────────────────────────────────────────────────────────────
builder.Services.AddScoped<IForecastAdapter, NwsAdapter>();
builder.Services.AddScoped<IForecastAdapter, OpenWeatherMapAdapter>();
builder.Services.AddScoped<IForecastAdapter, WeatherApiAdapter>();
builder.Services.AddScoped<IForecastAdapter, GenericJsonForecastAdapter>();
builder.Services.AddScoped<IDataPullAdapter, OpenWeatherMapAdapter>();
builder.Services.AddScoped<IDataPullAdapter, GenericHttpAdapter>();
builder.Services.AddScoped<IDataSinkAdapter, WeatherUndergroundSinkAdapter>();
builder.Services.AddScoped<IDataSinkAdapter, PwsWeatherSinkAdapter>();
builder.Services.AddScoped<IDataSinkAdapter, GenericHttpSinkAdapter>();

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

// ── HTTP clients ──────────────────────────────────────────────────────────────
builder.Services.AddHttpClient("nws", client =>
{
    client.BaseAddress = new Uri("https://api.weather.gov");
    client.DefaultRequestHeaders.Add("User-Agent",
        "frcastr/1.0 (weather station app; github.com/frcastr)");
    client.DefaultRequestHeaders.Add("Accept", "application/geo+json");
});
builder.Services.AddHttpClient("webhook");

// ── Output caching ────────────────────────────────────────────────────────────
builder.Services.AddOutputCache(opts =>
{
    opts.AddPolicy("WeatherCurrent", p =>
        p.Expire(TimeSpan.FromSeconds(15)).Tag("weather-current"));
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

// ── Pages + Controllers ───────────────────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddControllers();

// ── Build ─────────────────────────────────────────────────────────────────────
var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseHsts();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseOutputCache();
app.UseRateLimiter();
app.UseMiddleware<SetupMiddleware>();

app.MapRazorPages();
app.MapControllers();

app.MapHealthChecks("/health").AllowAnonymous();
app.MapOpenApi("/api/openapi");

var scalar = app.MapScalarApiReference("/scalar");
if (!app.Environment.IsDevelopment())
    scalar.RequireAuthorization("AdministratorOnly");

app.Run();
