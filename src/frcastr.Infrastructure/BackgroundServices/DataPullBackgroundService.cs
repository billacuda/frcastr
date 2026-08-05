using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class DataPullBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<DataPullBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var adapters = scope.ServiceProvider.GetServices<IDataPullAdapter>().ToList();
                var weatherData = scope.ServiceProvider.GetRequiredService<IWeatherDataService>();
                var statusService = scope.ServiceProvider.GetRequiredService<IDataSourceStatusService>();

                await PollDueSourcesAsync(db, adapters, weatherData, statusService, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "DataPullBackgroundService tick failed.");
            }
        }
    }

    private async Task PollDueSourcesAsync(
        ApplicationDbContext db,
        IReadOnlyList<IDataPullAdapter> adapters,
        IWeatherDataService weatherData,
        IDataSourceStatusService statusService,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var pullTypes = new[] { DataSourceType.Pull, DataSourceType.AirQuality };

        var dueSources = await db.DataSources
            .Where(s => s.IsEnabled && pullTypes.Contains(s.Type)
                     && (s.LastPolledAt == null
                      || s.LastPolledAt < now.AddSeconds(-s.PollIntervalSeconds)))
            .ToListAsync(ct);

        foreach (var source in dueSources)
        {
            try
            {
                var adapter = ResolveAdapter(adapters, source);
                if (adapter is null)
                {
                    logger.LogWarning("No adapter found for source {SourceId} ({Name}).", source.Id, source.Name);
                    continue;
                }

                var readings = await adapter.FetchAsync(source, ct);
                var timestamp = DateTime.UtcNow;

                foreach (var (channel, rawValue, unit) in readings)
                {
                    // Temperature, humidity and dew point come from the station's own devices; a
                    // regional figure on the same channel contaminates the history line and the
                    // all-time records. Dropped here rather than in each adapter so a generic HTTP
                    // source whose fieldMapping names one of these channels is covered too.
                    if (ChannelLogPolicy.IsPullBlocked(channel)) continue;

                    var value = ChannelProcessing.ApplyAndValidate(channel, rawValue, source.Config);
                    if (value is null)
                    {
                        logger.LogWarning("Source {SourceId}: reading for '{Channel}' = {Value} dropped (out of bounds).",
                            source.Id, channel, rawValue);
                        continue;
                    }

                    db.WeatherReadings.Add(new WeatherReading
                    {
                        ChannelName = channel,
                        Value = value.Value,
                        Unit = unit,
                        Timestamp = timestamp,
                        SourceId = source.Id
                    });

                    statusService.RecordReading(channel);
                    await weatherData.UpdateChannelRecordAsync(channel, value.Value, timestamp, source.Id, ct);
                }

                source.LastPolledAt = now;
                source.LastError = null;
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pull failed for source {SourceId} ({Name}).", source.Id, source.Name);
                source.LastError = ex.Message;
                source.LastPolledAt = now;
                try { await db.SaveChangesAsync(ct); } catch { /* best effort */ }
            }
        }
    }

    private static IDataPullAdapter? ResolveAdapter(
        IReadOnlyList<IDataPullAdapter> adapters, DataSource source)
    {
        var providerHint = ExtractProvider(source.Config);
        if (providerHint is null && source.Url?.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase) == true)
            providerHint = "openmeteo";
        else if (providerHint is null && !string.IsNullOrWhiteSpace(source.Url))
            providerHint = "generic";
        if (providerHint is not null)
            return adapters.FirstOrDefault(a =>
                a.Provider.Equals(providerHint, StringComparison.OrdinalIgnoreCase));

        return adapters.FirstOrDefault(a =>
            a.Provider.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
            ?? adapters.FirstOrDefault();
    }

    private static string? ExtractProvider(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(config);
            return doc.RootElement.TryGetProperty("provider", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }
}
