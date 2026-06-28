using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace frcastr.Infrastructure.Services;

public class SetupService : ISetupService
{
    private readonly ApplicationDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IAuditService _audit;

    public SetupService(
        ApplicationDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        IConfiguration configuration,
        IHostEnvironment environment,
        IAuditService audit)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _roleManager = roleManager;
        _configuration = configuration;
        _environment = environment;
        _audit = audit;
    }

    public async Task<bool> IsSetupCompleteAsync(CancellationToken ct = default)
    {
        if (!await IsDatabaseConfiguredAsync(ct)) return false;
        try
        {
            var flag = await _dbContext.Settings
                .FirstOrDefaultAsync(s => s.Key == "Setup.IsComplete", ct);
            return flag?.Value == "true";
        }
        catch { return false; }
    }

    public Task<bool> IsDatabaseConfiguredAsync(CancellationToken ct = default)
    {
        var cs = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs)) return Task.FromResult(false);

        try
        {
            var connection = _dbContext.Database.GetDbConnection();
            return Task.FromResult(connection is not null);
        }
        catch { return Task.FromResult(false); }
    }

    public async Task<bool> IsAdminCreatedAsync(CancellationToken ct = default)
    {
        try { return await _dbContext.Users.AnyAsync(ct); }
        catch { return false; }
    }

    public async Task<bool> IsStationConfiguredAsync(CancellationToken ct = default)
    {
        try
        {
            return await _dbContext.Settings
                .AnyAsync(s => s.Key == "Station.Latitude", ct);
        }
        catch { return false; }
    }

    public Task<bool> IsBrandingConfiguredAsync(CancellationToken ct = default)
        => Task.FromResult(!string.IsNullOrWhiteSpace(_configuration["Branding:AppName"]));

    public async Task SetupDatabaseAsync(string server, string database, string? username, string? password, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
            throw new ArgumentException("Server name and database name are required.");

        var masterCs = BuildConnectionString(server, "master", username, password);

        using (var conn = new SqlConnection(masterCs))
        {
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{Esc(database)}'";
            var exists = (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
            if (exists == 0)
            {
                using var create = conn.CreateCommand();
                create.CommandText = $"CREATE DATABASE [{Esc(database)}] COLLATE Latin1_General_100_CI_AS_SC_UTF8";
                await create.ExecuteNonQueryAsync(ct);
            }
        }

        var appCs = BuildConnectionString(server, database, username, password);
        _configuration["ConnectionStrings:DefaultConnection"] = appCs;
        MergeSetupConfiguration(cfg => cfg["ConnectionStrings"] = new JsonObject { ["DefaultConnection"] = appCs });

        var opts = new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlServer(appCs).Options;
        using (var ctx = new ApplicationDbContext(opts))
            await ctx.Database.MigrateAsync(ct);

        await GrantServiceAccountAsync(server, database, username, password, ct);
    }

    public async Task CompleteSetupAsync(string adminEmail, string adminPassword, CancellationToken ct = default)
    {
        await EnsureRoleAsync("Administrator", ct);
        await SeedViewerRoleAsync(ct);

        var user = new ApplicationUser { UserName = adminEmail, Email = adminEmail, EmailConfirmed = true };
        var result = await _userManager.CreateAsync(user, adminPassword);
        if (!result.Succeeded)
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));

        await _userManager.AddToRoleAsync(user, "Administrator");
    }

    public async Task SaveStationAsync(string name, decimal latitude, decimal longitude, decimal elevation, string timezone, CancellationToken ct = default)
    {
        await UpsertAsync("Station.Name", name, "Weather station display name.", ct);
        await UpsertAsync("Station.Latitude", latitude.ToString("G"), "Station latitude.", ct);
        await UpsertAsync("Station.Longitude", longitude.ToString("G"), "Station longitude.", ct);
        await UpsertAsync("Station.Elevation", elevation.ToString("G"), "Station elevation in meters.", ct);
        await UpsertAsync("Station.TimeZone", timezone, "Station IANA time zone.", ct);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task SaveEmailAsync(string? smtpHost, int port, bool useSsl, string? username, string? password, string? fromAddress, CancellationToken ct = default)
    {
        await UpsertAsync("Email.SmtpHost", smtpHost ?? string.Empty, "SMTP host name.", ct);
        await UpsertAsync("Email.SmtpPort", port.ToString(), "SMTP port.", ct);
        await UpsertAsync("Email.SmtpUseSsl", useSsl ? "true" : "false", "Use SSL for SMTP.", ct);
        await UpsertAsync("Email.SmtpUsername", username ?? string.Empty, "SMTP username.", ct);
        await UpsertAsync("Email.SmtpPassword", password ?? string.Empty, "SMTP password.", ct);
        await UpsertAsync("Email.FromAddress", fromAddress ?? string.Empty, "Sender email address.", ct);
        await UpsertAsync("Email.OutboundConfigured",
            string.IsNullOrWhiteSpace(smtpHost) ? "false" : "true",
            "Whether outbound email is configured.", ct);
        await _dbContext.SaveChangesAsync(ct);
    }

    public Task SaveBrandingAsync(string appName, string primaryColor, CancellationToken ct = default)
    {
        MergeSetupConfiguration(cfg => cfg["Branding"] = new JsonObject
        {
            ["AppName"] = appName,
            ["PrimaryColor"] = primaryColor
        });
        _configuration["Branding:AppName"] = appName;
        _configuration["Branding:PrimaryColor"] = primaryColor;
        return Task.CompletedTask;
    }

    public async Task FinalizeSetupAsync(CancellationToken ct = default)
    {
        await UpsertAsync("Setup.IsComplete", "true", "Set by the setup wizard when all steps are complete.", ct);
        await SeedDefaultSettingsAsync(ct);
        await SeedDefaultWidgetsAsync(ct);
        await _dbContext.SaveChangesAsync(ct);
        await _audit.LogAsync("Setup.Completed", ct: ct);
    }

    private async Task SeedViewerRoleAsync(CancellationToken ct)
    {
        await EnsureRoleAsync("Viewer", ct);

        var viewerRole = await _roleManager.FindByNameAsync("Viewer");
        if (viewerRole is null) return;

        string[] resources = ["History", "DataSources", "Alerts"];
        foreach (var resource in resources)
        {
            var exists = await _dbContext.Permissions.AnyAsync(
                p => p.RoleId == viewerRole.Id && p.Resource == resource && p.Action == "Read", ct);
            if (!exists)
                _dbContext.Permissions.Add(new Permission
                {
                    Name = $"{resource}.Read",
                    IsSystemPermission = true,
                    RoleId = viewerRole.Id,
                    Resource = resource,
                    Action = "Read"
                });
        }
        await _dbContext.SaveChangesAsync(ct);
    }

    private async Task SeedDefaultSettingsAsync(CancellationToken ct)
    {
        var defaults = new (string Key, string Value, string Desc)[]
        {
            ("Dashboard.PollIntervalSeconds", "30",   "Dashboard auto-refresh interval in seconds."),
            ("Dashboard.KioskMode",           "false","Enable kiosk mode (hides nav and disables editing)."),
            ("Display.TemperatureUnit",        "C",    "Temperature unit: C or F."),
            ("Display.WindUnit",               "kmh",  "Wind speed unit: kmh, mph, or ms."),
            ("Display.PressureUnit",           "hPa",  "Pressure unit: hPa, inHg, or mmHg."),
            ("Display.RainUnit",               "mm",   "Rainfall unit: mm or in."),
            ("Display.Theme",                  "auto", "UI theme: auto, light, or dark."),
            ("Trend.SampleCount",              "10",   "Number of readings to use for trend calculation."),
            ("Trend.ThresholdDegrees",         "0.5",  "Minimum change in degrees to classify as rising or falling."),
            ("Weather.RawRetentionDays",        "30",   "Days to retain raw readings before hourly aggregation."),
            ("Weather.HourlyRetentionDays",     "365",  "Days to retain hourly aggregates before daily aggregation."),
            ("Weather.DailyRetentionDays",      "0",    "Days to retain daily aggregates (0 = forever)."),
            ("Records.RetentionDays",           "0",    "Days to retain all-time channel records (0 = forever)."),
            ("Forecast.CacheRetentionHours",    "48",   "Hours to retain forecast cache entries."),
            ("Alerts.CacheRetentionHours",      "24",   "Hours to retain alert cache entries."),
            ("Alerts.SensorOfflineThresholdMinutes", "10", "Minutes without a reading before a sensor is considered offline."),
            ("Alerts.WebhookCooldownMinutes",   "30",   "Minimum minutes between repeated webhook notifications."),
            ("Alerts.EmailOnWebhookTrigger",    "false","Send email in addition to webhook on threshold breach."),
            ("Email.SmtpPort",                  "587",  "SMTP port."),
            ("Email.SmtpUseSsl",                "true", "Use SSL for SMTP."),
            ("Email.DigestHour",                "7",    "Hour of day (UTC) to send daily digest email."),
            ("Email.OutboundConfigured",        "false","Whether outbound email is configured."),
            ("Auth.LocalAccountsEnabled",       "true", "Allow local account login."),
            ("Auth.SessionTimeoutHours",        "0",    "Max session duration in hours (0 = 30-day sliding session)."),
            ("Branding.Logo",                   "",     "Path to navbar logo image (empty = show app name text)."),
        };

        foreach (var (key, value, desc) in defaults)
        {
            var exists = await _dbContext.Settings.AnyAsync(s => s.Key == key, ct);
            if (!exists)
                await UpsertAsync(key, value, desc, ct);
        }
    }

    private async Task SeedDefaultWidgetsAsync(CancellationToken ct)
    {
        if (await _dbContext.WidgetDefinitions.AnyAsync(ct)) return;

        var widgets = new[]
        {
            new WidgetDefinition { Type = WidgetType.DateTime,          Title = "Date & Time",       GridX = 0,  GridY = 0, GridW = 3, GridH = 2, SortOrder = 0 },
            new WidgetDefinition { Type = WidgetType.WeatherAnimation,  Title = "Current Conditions",GridX = 3,  GridY = 0, GridW = 3, GridH = 4, SortOrder = 1 },
            new WidgetDefinition { Type = WidgetType.TemperatureOutdoor,Title = "Outdoor Temp",      GridX = 6,  GridY = 0, GridW = 2, GridH = 2, SortOrder = 2, Config = """{"channel":"temperature.outdoor","unit":"C","trendSamples":10}""" },
            new WidgetDefinition { Type = WidgetType.TemperatureIndoor, Title = "Indoor Temp",       GridX = 8,  GridY = 0, GridW = 2, GridH = 2, SortOrder = 3, Config = """{"channel":"temperature.indoor","unit":"C","trendSamples":10}""" },
            new WidgetDefinition { Type = WidgetType.HumidityOutdoor,   Title = "Outdoor Humidity",  GridX = 10, GridY = 0, GridW = 2, GridH = 2, SortOrder = 4, Config = """{"channel":"humidity.outdoor"}""" },
            new WidgetDefinition { Type = WidgetType.FeelsLike,         Title = "Feels Like",        GridX = 0,  GridY = 2, GridW = 3, GridH = 2, SortOrder = 5 },
            new WidgetDefinition { Type = WidgetType.Wind,              Title = "Wind",              GridX = 6,  GridY = 2, GridW = 2, GridH = 2, SortOrder = 6 },
            new WidgetDefinition { Type = WidgetType.Pressure,          Title = "Pressure",          GridX = 8,  GridY = 2, GridW = 2, GridH = 2, SortOrder = 7 },
            new WidgetDefinition { Type = WidgetType.HumidityIndoor,    Title = "Indoor Humidity",   GridX = 10, GridY = 2, GridW = 2, GridH = 2, SortOrder = 8, Config = """{"channel":"humidity.indoor"}""" },
            new WidgetDefinition { Type = WidgetType.Forecast,          Title = "Forecast",          GridX = 0,  GridY = 4, GridW = 6, GridH = 3, SortOrder = 9 },
            new WidgetDefinition { Type = WidgetType.Moon,              Title = "Moon Phase",        GridX = 6,  GridY = 4, GridW = 2, GridH = 3, SortOrder = 10 },
            new WidgetDefinition { Type = WidgetType.SunriseSunset,     Title = "Sunrise & Sunset",  GridX = 8,  GridY = 4, GridW = 2, GridH = 3, SortOrder = 11 },
            new WidgetDefinition { Type = WidgetType.Alerts,            Title = "Weather Alerts",    GridX = 10, GridY = 4, GridW = 2, GridH = 3, SortOrder = 12 },
        };

        _dbContext.WidgetDefinitions.AddRange(widgets);
    }

    private async Task EnsureRoleAsync(string roleName, CancellationToken ct)
    {
        if (!await _roleManager.RoleExistsAsync(roleName))
        {
            var result = await _roleManager.CreateAsync(new IdentityRole(roleName));
            if (!result.Succeeded)
                throw new InvalidOperationException($"Failed to create role '{roleName}': {string.Join(" ", result.Errors.Select(e => e.Description))}");
        }
    }

    private async Task UpsertAsync(string key, string value, string description, CancellationToken ct)
    {
        var existing = await _dbContext.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (existing is not null)
        {
            existing.Value = value;
            existing.LastModifiedAt = DateTime.UtcNow;
            existing.LastModifiedBy = "system";
        }
        else
        {
            _dbContext.Settings.Add(new Setting
            {
                Key = key, Value = value, Description = description,
                IsSystemSetting = true,
                CreatedAt = DateTime.UtcNow, LastModifiedAt = DateTime.UtcNow, LastModifiedBy = "system"
            });
        }
    }

    private async Task GrantServiceAccountAsync(string server, string database, string? adminUser, string? adminPassword, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        try
        {
            var account = WindowsIdentity.GetCurrent().Name;
            var masterCs = BuildConnectionString(server, "master", adminUser, adminPassword);
            using var conn = new SqlConnection(masterCs);
            await conn.OpenAsync(ct);

            await ExecAsync(conn, $"IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'{Esc(account)}') CREATE LOGIN [{Esc(account)}] FROM WINDOWS", ct);
            conn.ChangeDatabase(database);
            await ExecAsync(conn, $"IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'{Esc(account)}') CREATE USER [{Esc(account)}] FOR LOGIN [{Esc(account)}]", ct);
            await ExecAsync(conn, $"ALTER ROLE db_owner ADD MEMBER [{Esc(account)}]", ct);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Warning: Could not grant service account permissions: {ex.Message}");
        }
    }

    private static async Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string BuildConnectionString(string server, string database, string? username, string? password)
    {
        var b = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Encrypt = true,
            TrustServerCertificate = true
        };
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            b.UserID = username;
            b.Password = password;
        }
        else
        {
            b.IntegratedSecurity = true;
        }
        return b.ConnectionString;
    }

    private void MergeSetupConfiguration(Action<JsonObject> update)
    {
        var path = Path.Combine(_environment.ContentRootPath, "setup-generated.json");
        JsonObject root;
        if (File.Exists(path))
        {
            try { root = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject(); }
            catch { root = new JsonObject(); }
        }
        else
        {
            root = new JsonObject();
        }
        update(root);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
