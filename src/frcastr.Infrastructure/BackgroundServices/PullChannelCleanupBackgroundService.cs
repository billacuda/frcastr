using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.BackgroundServices;

/// <summary>
/// One-shot repair for the records a pull source is no longer allowed to hold.
/// <para>
/// Pull sources used to write <c>temperature.outdoor</c> and friends into the station's own
/// channels, so an all-time high could belong to a regional forecast rather than to anything the
/// station measured. <see cref="ChannelLogPolicy"/> stops that going forward, but a record already
/// standing would stand forever — nothing recalculates one until a new reading beats it.
/// </para>
/// <para>
/// Deleting the row is not enough on its own: the next device reading would recreate it holding
/// that single value as both the high and the low, throwing away years of genuine extremes. Each
/// affected record is recomputed instead, from the readings and aggregates that did not come from
/// a pull source. The pull readings themselves stay in the table and on history graphs; they just
/// stop counting towards a record.
/// </para>
/// <para>
/// Guarded by a settings key, so it runs once per install and is a no-op on every later boot.
/// </para>
/// </summary>
public class PullChannelCleanupBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<PullChannelCleanupBackgroundService> logger) : BackgroundService
{
    private const string DoneKey = "Maintenance.PullChannelCleanupAt";

    // Long enough for migrations and the sensor-status seed to have finished, so the first pass
    // does not contend with startup. Nothing depends on this having run.
    private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, stoppingToken);
            await RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down before the pass finished. The settings key is only written on success,
            // so the next boot picks it up again.
        }
        catch (Exception ex)
        {
            // A pre-setup database has no schema yet, and a failure here must not take the app with
            // it — the records are wrong, not the application.
            logger.LogError(ex, "Pull-channel record cleanup failed; it will be retried on the next start.");
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var weatherData = scope.ServiceProvider.GetRequiredService<IWeatherDataService>();

        if (!string.IsNullOrWhiteSpace(await settings.GetAsync(DoneKey, ct))) return;

        var pullSourceIds = await db.DataSources
            .Where(s => s.Type == DataSourceType.Pull || s.Type == DataSourceType.AirQuality)
            .Select(s => s.Id)
            .ToListAsync(ct);

        // Cloud coverage is not a channel any more — the Weather Animation widget reads sky cover
        // from the forecast — so no record for it means anything, whoever set it.
        var cloudRecords = await db.WeatherChannelRecords
            .Where(r => r.ChannelName == "cloud.coverage")
            .ToListAsync(ct);

        db.WeatherChannelRecords.RemoveRange(cloudRecords);

        // Everything else is judged by who set it. A record whose high and low both came from a
        // device is already correct and is left alone.
        var candidates = await db.WeatherChannelRecords
            .Where(r => r.ChannelName != "cloud.coverage")
            .Where(r => (r.AllTimeMaxSourceId != null && pullSourceIds.Contains(r.AllTimeMaxSourceId.Value))
                     || (r.AllTimeMinSourceId != null && pullSourceIds.Contains(r.AllTimeMinSourceId.Value)))
            .ToListAsync(ct);

        candidates = candidates.Where(r => ChannelLogPolicy.IsPullBlocked(r.ChannelName)).ToList();

        var cleared = 0;
        foreach (var record in candidates)
        {
            var max = await weatherData.FindChannelExtremeAsync(
                record.ChannelName, record.DeviceId, highest: true, pullSourceIds, ct);
            var min = await weatherData.FindChannelExtremeAsync(
                record.ChannelName, record.DeviceId, highest: false, pullSourceIds, ct);

            // Nothing but internet readings behind it — the station never measured this channel, so
            // there is no record to state.
            if (max is null || min is null)
            {
                db.WeatherChannelRecords.Remove(record);
                cleared++;
                continue;
            }

            record.AllTimeMax = max.Value;
            record.AllTimeMaxAt = max.At;
            record.AllTimeMaxSourceId = max.SourceId;
            record.AllTimeMin = min.Value;
            record.AllTimeMinAt = min.At;
            record.AllTimeMinSourceId = min.SourceId;
        }

        await db.SaveChangesAsync(ct);

        await settings.UpsertAsync(DoneKey, DateTime.UtcNow.ToString("O"),
            "When pull-source temperature/humidity/dew point records were recalculated and cloud coverage records removed.",
            isSystem: true, modifiedBy: "system", ct: ct);

        logger.LogInformation(
            "Pull-channel record cleanup: removed {Cloud} cloud coverage record(s), recalculated {Recalculated} record(s), of which {Cleared} had no device data left and were removed.",
            cloudRecords.Count, candidates.Count, cleared);
    }
}
