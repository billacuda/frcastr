using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

public class OutboundUploadBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboundUploadBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var adapters = scope.ServiceProvider.GetServices<IDataSinkAdapter>().ToList();
                await UploadAsync(db, adapters, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Outbound upload tick failed.");
            }
        }
    }

    private async Task UploadAsync(ApplicationDbContext db,
        IReadOnlyList<IDataSinkAdapter> adapters, CancellationToken ct)
    {
        var sinks = await db.DataSources
            .Where(s => s.IsEnabled && s.Type == DataSourceType.DataSink)
            .ToListAsync(ct);

        if (sinks.Count == 0 || adapters.Count == 0) return;

        // Latest reading per channel (last 10 minutes)
        var since = DateTime.UtcNow.AddMinutes(-10);
        var latestIds = await db.WeatherReadings
            .Where(r => r.Timestamp >= since)
            .GroupBy(r => r.ChannelName)
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);

        var latestReadings = await db.WeatherReadings
            .Where(r => latestIds.Contains(r.Id))
            .Select(r => new { r.ChannelName, r.Value, r.Unit, r.Timestamp })
            .ToListAsync(ct);

        var payload = latestReadings
            .Select(r => (r.ChannelName, r.Value, r.Unit, r.Timestamp))
            .ToList();

        foreach (var sink in sinks)
        {
            var provider = ExtractProvider(sink.Config);
            var adapter = provider is not null
                ? adapters.FirstOrDefault(a => a.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                : adapters.FirstOrDefault();

            if (adapter is null)
            {
                logger.LogWarning("No sink adapter for source {SourceId} ({Name}).", sink.Id, sink.Name);
                continue;
            }

            try
            {
                await adapter.UploadAsync(sink, payload, ct);
                logger.LogDebug("Uploaded {Count} readings to sink {SourceId} ({Name}).",
                    payload.Count, sink.Id, sink.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Upload failed for sink {SourceId} ({Name}).", sink.Id, sink.Name);
                sink.LastError = ex.Message;
                await db.SaveChangesAsync(ct);
            }
        }
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
