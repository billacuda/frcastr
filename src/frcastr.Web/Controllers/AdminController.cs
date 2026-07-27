using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdministratorOnly")]
public class AdminController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IAuditService audit,
    IWebHostEnvironment webEnv,
    ISettingsService settings,
    IDataSourceTestService dataSourceTester,
    IWeatherDataService weatherData) : ControllerBase
{
    // ── Channels ──────────────────────────────────────────────────────────────

    [HttpGet("channels")]
    public async Task<IActionResult> GetChannels(CancellationToken ct)
    {
        var readings = await weatherData.GetCurrentReadingsAsync(ct);
        // "name" is the channel key ("temperature.indoor@greenhouse-01" for device readings) —
        // it is what widgets and history queries bind to.
        var result = readings
            .OrderBy(kv => kv.Key)
            .Select(kv => new
            {
                name        = kv.Key,
                channel     = kv.Value.ChannelName,
                value       = (double?)kv.Value.Value,
                unit        = kv.Value.Unit,
                lastUpdated = kv.Value.Timestamp,
                deviceId    = kv.Value.DeviceId,
                deviceKey   = kv.Value.DeviceKey,
                deviceName  = kv.Value.DeviceName,
                isCalculated = kv.Value.IsCalculated
            });
        return Ok(result);
    }

    // ── Channel labels ────────────────────────────────────────────────────────
    //
    // Display-only aliases. Channel names themselves are load-bearing — sanity bounds match on the
    // "temperature." prefix, the calculated channels look up "temperature.indoor"/"outdoor" by
    // exact name, and °C/°F conversion tests the same prefix — so a channel is relabeled for the
    // UI rather than renamed. Widgets keep binding to the key, so clearing a label breaks nothing.

    [HttpGet("channel-labels")]
    public async Task<IActionResult> GetChannelLabels(CancellationToken ct)
    {
        var labels = await db.Settings
            .Where(s => s.Key.StartsWith(WeatherController.ChannelLabelPrefix))
            .Select(s => new { s.Key, s.Value })
            .ToListAsync(ct);

        return Ok(labels.ToDictionary(
            l => l.Key[WeatherController.ChannelLabelPrefix.Length..], l => l.Value));
    }

    /// <summary>
    /// Replaces the label for each key supplied. An empty or whitespace value removes the row
    /// rather than storing a blank, so "no label" is one state rather than two.
    /// </summary>
    [HttpPut("channel-labels")]
    public async Task<IActionResult> SetChannelLabels(
        [FromBody] Dictionary<string, string?> labels, CancellationToken ct)
    {
        if (labels is null || labels.Count == 0) return BadRequest("No labels supplied.");

        var changed = 0;
        foreach (var (channelKey, rawLabel) in labels)
        {
            var key = (channelKey ?? "").Trim();
            if (key.Length == 0) continue;
            if (key.Length > 200) return BadRequest($"Channel key '{key}' is too long.");

            var settingKey = WeatherController.ChannelLabelPrefix + key;
            var label = (rawLabel ?? "").Trim();

            if (label.Length == 0)
            {
                changed += await db.Settings.Where(s => s.Key == settingKey).ExecuteDeleteAsync(ct);
                continue;
            }

            await settings.UpsertAsync(settingKey, label,
                description: $"Display label for channel {key}.",
                modifiedBy: UserName(), ct: ct);
            changed++;
        }

        await audit.LogAsync("ChannelLabels.Updated",
            userId: UserId(), userName: UserName(),
            entityType: "ChannelLabel", entityName: $"{changed} channel(s)", ct: ct);

        return Ok(new { changed });
    }

    // ── Devices ───────────────────────────────────────────────────────────────

    [HttpGet("devices")]
    public async Task<IActionResult> GetDevices(CancellationToken ct)
    {
        var devices = await db.Devices
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id, d.DeviceId, d.Name, d.Location, d.Model, d.FirmwareVersion,
                d.SourceId,
                SourceName = d.Source != null ? d.Source.Name : null,
                d.IsEnabled, d.IsPrimary, d.IsOnline, d.LastSeenAt,
                d.OfflineThresholdMinutes, d.ChannelOverrides, d.CreatedAt
            })
            .ToListAsync(ct);
        return Ok(devices);
    }

    [HttpPut("devices/{id:int}")]
    public async Task<IActionResult> UpdateDevice(int id, [FromBody] DeviceDto dto, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound();

        if (!DeviceChannelOverrides.TryNormalize(dto.ChannelOverrides, out var overrides, out var error))
            return BadRequest(error);

        device.Name                    = string.IsNullOrWhiteSpace(dto.Name) ? device.Name : dto.Name.Trim();
        device.Location                = string.IsNullOrWhiteSpace(dto.Location) ? null : dto.Location.Trim();
        device.Model                   = string.IsNullOrWhiteSpace(dto.Model) ? null : dto.Model.Trim();
        device.IsEnabled               = dto.IsEnabled;
        device.OfflineThresholdMinutes = dto.OfflineThresholdMinutes > 0 ? dto.OfflineThresholdMinutes : 0;
        device.ChannelOverrides        = overrides;

        await db.SaveChangesAsync(ct);
        await audit.LogAsync("Device.Updated",
            userId: UserId(), userName: UserName(),
            entityType: "Device", entityId: id.ToString(), entityName: device.Name, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Channels this device already has history under, with row counts. Backs the re-key UI:
    /// changing an override only redirects future readings, so the admin needs to see what is
    /// still filed under the old name.
    /// </summary>
    [HttpGet("devices/{id:int}/channels")]
    public async Task<IActionResult> GetDeviceChannels(int id, CancellationToken ct)
    {
        if (!await db.Devices.AnyAsync(d => d.Id == id, ct)) return NotFound();

        var readings = await db.WeatherReadings.Where(r => r.DeviceId == id)
            .GroupBy(r => r.ChannelName)
            .Select(g => new { Channel = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var aggregates = await db.WeatherReadingAggregates.Where(a => a.DeviceId == id)
            .GroupBy(a => a.ChannelName)
            .Select(g => new { Channel = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var records = await db.WeatherChannelRecords.Where(r => r.DeviceId == id)
            .Select(r => r.ChannelName)
            .ToListAsync(ct);

        var channels = readings.Select(r => r.Channel)
            .Concat(aggregates.Select(a => a.Channel))
            .Concat(records)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Select(c => new
            {
                channel    = c,
                readings   = readings.FirstOrDefault(r => r.Channel == c)?.Count ?? 0,
                aggregates = aggregates.FirstOrDefault(a => a.Channel == c)?.Count ?? 0,
                records    = records.Count(r => r == c)
            })
            .ToList();

        return Ok(channels);
    }

    /// <summary>
    /// Renames a device's stored channels, so history follows a changed channel override instead of
    /// being stranded under the old name. Only this device's rows are touched.
    /// </summary>
    [HttpPost("devices/{id:int}/rekey")]
    public async Task<IActionResult> RekeyDeviceChannels(int id,
        [FromBody] List<ChannelRekeyDto> pairs, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound();
        if (pairs is null || pairs.Count == 0) return BadRequest("No channels to re-key.");

        var moves = new List<(string From, string To)>();
        foreach (var p in pairs)
        {
            var from = (p.From ?? "").Trim();
            var to = (p.To ?? "").Trim();
            if (from.Length == 0 || to.Length == 0) continue;
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;
            if (!DeviceChannelOverrides.IsValidChannel(to, out var error)) return BadRequest(error);
            moves.Add((from, to));
        }

        if (moves.Count == 0) return BadRequest("No channels to re-key.");

        var movedReadings = 0;
        var movedAggregates = 0;
        var movedRecords = 0;
        var mergedRecords = 0;
        var mergedAggregates = 0;

        // ExecuteUpdate runs immediately while the record merges wait for SaveChanges, so without a
        // transaction a failure partway through would leave readings renamed and records not.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var (from, to) in moves)
        {
            movedReadings += await db.WeatherReadings
                .Where(r => r.DeviceId == id && r.ChannelName == from)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ChannelName, to), ct);

            movedAggregates += await db.WeatherReadingAggregates
                .Where(a => a.DeviceId == id && a.ChannelName == from)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ChannelName, to), ct);

            mergedAggregates += await MergeDuplicateAggregatesAsync(id, to, ct);

            // Records are unique per (channel, device), so a pre-existing row on the target has to
            // absorb the moved one rather than collide with it.
            var target = await db.WeatherChannelRecords
                .FirstOrDefaultAsync(r => r.DeviceId == id && r.ChannelName == to, ct);
            var source = await db.WeatherChannelRecords
                .FirstOrDefaultAsync(r => r.DeviceId == id && r.ChannelName == from, ct);

            if (source is null) continue;

            if (target is null)
            {
                source.ChannelName = to;
                movedRecords++;
            }
            else
            {
                AbsorbRecord(target, source);
                db.WeatherChannelRecords.Remove(source);
                mergedRecords++;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await audit.LogAsync("Device.ChannelsRekeyed",
            userId: UserId(), userName: UserName(),
            entityType: "Device", entityId: id.ToString(), entityName: device.Name,
            newValue: string.Join(", ", moves.Select(m => $"{m.From} → {m.To}")), ct: ct);

        return Ok(new
        {
            readings = movedReadings,
            aggregates = movedAggregates,
            records = movedRecords,
            mergedRecords,
            mergedAggregates
        });
    }

    /// <summary>
    /// Folds one record row into another, keeping the wider pair of extremes. The row that loses an
    /// extreme still loses its timestamp and source with it, so a record never reports a high from
    /// one sensor at another sensor's time.
    /// </summary>
    private static void AbsorbRecord(WeatherChannelRecord target, WeatherChannelRecord source)
    {
        if (source.AllTimeMax > target.AllTimeMax)
        {
            target.AllTimeMax = source.AllTimeMax;
            target.AllTimeMaxAt = source.AllTimeMaxAt;
            target.AllTimeMaxSourceId = source.AllTimeMaxSourceId;
        }
        if (source.AllTimeMin < target.AllTimeMin)
        {
            target.AllTimeMin = source.AllTimeMin;
            target.AllTimeMinAt = source.AllTimeMinAt;
            target.AllTimeMinSourceId = source.AllTimeMinSourceId;
        }
    }

    /// <summary>
    /// Collapses aggregate rows that now share a period with the channel they were moved onto.
    /// Averages are re-weighted by sample count so the merged row still reflects every reading.
    /// A null <paramref name="deviceId"/> addresses the station-wide rows, which is what a
    /// device's rows become once it is deleted.
    /// </summary>
    private async Task<int> MergeDuplicateAggregatesAsync(int? deviceId, string channel, CancellationToken ct)
    {
        var duplicateKeys = await db.WeatherReadingAggregates
            .Where(a => a.DeviceId == deviceId && a.ChannelName == channel)
            .GroupBy(a => new { a.Granularity, a.PeriodStart, a.SourceId })
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        if (duplicateKeys.Count == 0) return 0;

        var merged = 0;
        foreach (var key in duplicateKeys)
        {
            var rows = await db.WeatherReadingAggregates
                .Where(a => a.DeviceId == deviceId && a.ChannelName == channel
                         && a.Granularity == key.Granularity && a.PeriodStart == key.PeriodStart
                         && a.SourceId == key.SourceId)
                .ToListAsync(ct);

            if (rows.Count < 2) continue;

            var keep = rows[0];
            var totalCount = rows.Sum(r => r.Count);
            keep.Avg = totalCount > 0
                ? rows.Sum(r => r.Avg * r.Count) / totalCount
                : rows.Average(r => r.Avg);
            keep.Min = rows.Min(r => r.Min);
            keep.Max = rows.Max(r => r.Max);
            keep.Count = totalCount;

            db.WeatherReadingAggregates.RemoveRange(rows.Skip(1));
            merged += rows.Count - 1;
        }

        return merged;
    }

    [HttpDelete("devices/{id:int}")]
    public async Task<IActionResult> DeleteDevice(int id, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound();

        // Readings and aggregates keep their history: their FK is SetNull, so they become
        // station-wide. Records cannot be left to the FK. They are unique per
        // (ChannelName, DeviceId), unfiltered — SQL Server treats NULLs as equal — and a device
        // usually shares a channel with the station-wide row a pull source already keeps, so
        // nulling its DeviceId collided with that row and the delete came back as a 500.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var deviceRecords = await db.WeatherChannelRecords
            .Where(r => r.DeviceId == id).ToListAsync(ct);

        foreach (var source in deviceRecords)
        {
            var target = await db.WeatherChannelRecords
                .FirstOrDefaultAsync(r => r.DeviceId == null && r.ChannelName == source.ChannelName, ct);

            if (target is null)
            {
                // Nothing to collide with; this is what the FK would have done.
                source.DeviceId = null;
                continue;
            }

            AbsorbRecord(target, source);
            db.WeatherChannelRecords.Remove(source);
        }

        // Read before the delete, while the rows still carry the device id.
        var aggregateChannels = await db.WeatherReadingAggregates
            .Where(a => a.DeviceId == id)
            .Select(a => a.ChannelName)
            .Distinct()
            .ToListAsync(ct);

        db.Devices.Remove(device);
        await db.SaveChangesAsync(ct);

        // The aggregates the FK just made station-wide can now duplicate a period the station
        // already had. Nothing in the schema rejects that, but the History chart would draw the
        // same period twice, so collapse them the same way a re-key does.
        var mergedAggregates = 0;
        foreach (var channel in aggregateChannels)
            mergedAggregates += await MergeDuplicateAggregatesAsync(null, channel, ct);

        if (mergedAggregates > 0) await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await audit.LogAsync("Device.Deleted",
            userId: UserId(), userName: UserName(),
            entityType: "Device", entityId: id.ToString(), entityName: device.Name,
            newValue: $"{deviceRecords.Count} record row(s) folded into station-wide, " +
                      $"{mergedAggregates} aggregate row(s) merged",
            ct: ct);
        return NoContent();
    }

    [HttpPost("devices/{id:int}/toggle")]
    public async Task<IActionResult> ToggleDevice(int id, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound();
        device.IsEnabled = !device.IsEnabled;
        await db.SaveChangesAsync(ct);
        return Ok(new { device.IsEnabled });
    }

    /// <summary>Makes this device primary, clearing the flag on every other device.</summary>
    [HttpPost("devices/{id:int}/primary")]
    public async Task<IActionResult> SetPrimaryDevice(int id, [FromQuery] bool primary = true,
        CancellationToken ct = default)
    {
        var device = await db.Devices.FindAsync([id], ct);
        if (device is null) return NotFound();

        if (primary)
            await db.Devices.Where(d => d.Id != id && d.IsPrimary)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsPrimary, false), ct);

        device.IsPrimary = primary;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Device.PrimaryChanged",
            userId: UserId(), userName: UserName(),
            entityType: "Device", entityId: id.ToString(), entityName: device.Name,
            newValue: primary.ToString(), ct: ct);
        return Ok(new { device.IsPrimary });
    }

    // ── Data maintenance ──────────────────────────────────────────────────────

    /// <summary>
    /// Rows a purge would remove, without removing any. Shares its query builder with
    /// <see cref="PurgeData"/> so the preview can never describe a different deletion than the one
    /// that follows it.
    /// </summary>
    [HttpPost("data/purge/preview")]
    public async Task<IActionResult> PreviewPurge([FromBody] PurgeDto dto, CancellationToken ct)
    {
        var cutoff = await ResolvePurgeCutoffAsync(dto, ct);
        var q = BuildPurgeQueries(dto, cutoff);

        return Ok(new
        {
            before          = cutoff,
            readings        = q.Readings is null ? 0 : await q.Readings.CountAsync(ct),
            hourlyAggregates = q.Hourly is null ? 0 : await q.Hourly.CountAsync(ct),
            dailyAggregates = q.Daily is null ? 0 : await q.Daily.CountAsync(ct),
            records         = q.Records is null ? 0 : await q.Records.CountAsync(ct),
            forecastCache   = q.Forecasts is null ? 0 : await q.Forecasts.CountAsync(ct),
            alertCache      = q.Alerts is null ? 0 : await q.Alerts.CountAsync(ct)
        });
    }

    /// <summary>
    /// Deletes weather history. Only the six weather stores are ever touched — devices, data
    /// sources, widgets, dashboard layouts, users, settings and the audit log are out of scope and
    /// must stay that way.
    /// </summary>
    [HttpPost("data/purge")]
    public async Task<IActionResult> PurgeData([FromBody] PurgeDto dto, CancellationToken ct)
    {
        if (!dto.Readings && !dto.Aggregates && !dto.Records && !dto.Caches)
            return BadRequest("Select at least one kind of data to purge.");

        var cutoff = await ResolvePurgeCutoffAsync(dto, ct);
        var q = BuildPurgeQueries(dto, cutoff);

        var readings  = await DeleteInBatchesAsync(q.Readings, ct);
        var hourly    = await DeleteInBatchesAsync(q.Hourly, ct);
        var daily     = await DeleteInBatchesAsync(q.Daily, ct);
        var records   = await DeleteInBatchesAsync(q.Records, ct);
        var forecasts = await DeleteInBatchesAsync(q.Forecasts, ct);
        var alerts    = await DeleteInBatchesAsync(q.Alerts, ct);

        // Current conditions and daily extremes are output-cached for 15 s and would keep serving
        // numbers backed by rows that no longer exist.
        var invalidator = HttpContext.RequestServices.GetService<IWeatherCacheInvalidator>();
        if (invalidator is not null) await invalidator.InvalidateCurrentAsync(ct);

        var scope = cutoff is null ? "everything" : $"before {cutoff:u}";
        await audit.LogAsync("Data.Purged",
            userId: UserId(), userName: UserName(),
            entityType: "Data", entityName: "Weather history",
            newValue: $"{scope}: {readings} readings, {hourly} hourly, {daily} daily, " +
                      $"{records} records, {forecasts} forecast cache, {alerts} alert cache",
            ct: ct);

        return Ok(new
        {
            before           = cutoff,
            readings,
            hourlyAggregates = hourly,
            dailyAggregates  = daily,
            records,
            forecastCache    = forecasts,
            alertCache       = alerts
        });
    }

    private sealed record PurgeQueries(
        IQueryable<WeatherReading>? Readings,
        IQueryable<WeatherReadingAggregate>? Hourly,
        IQueryable<WeatherReadingAggregate>? Daily,
        IQueryable<WeatherChannelRecord>? Records,
        IQueryable<ForecastCache>? Forecasts,
        IQueryable<AlertCache>? Alerts);

    /// <summary>
    /// A null cutoff means "everything"; otherwise rows at or after the cutoff are kept. The date
    /// arrives as a local station date, matching what the admin picked on screen.
    /// </summary>
    private async Task<DateTime?> ResolvePurgeCutoffAsync(PurgeDto dto, CancellationToken ct)
    {
        if (dto.Before is null) return null;

        var tzId = await settings.GetAsync("Station.TimeZone", ct);
        var tz = tzId is not null ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;
        var localMidnight = new DateTime(dto.Before.Value.Year, dto.Before.Value.Month,
            dto.Before.Value.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
    }

    private PurgeQueries BuildPurgeQueries(PurgeDto dto, DateTime? cutoff)
    {
        var readings = dto.Readings
            ? db.WeatherReadings.Where(r => cutoff == null || r.Timestamp < cutoff)
            : null;

        var hourly = dto.Aggregates
            ? db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Hourly)
                .Where(a => cutoff == null || a.PeriodStart < cutoff)
            : null;

        var daily = dto.Aggregates
            ? db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Daily)
                .Where(a => cutoff == null || a.PeriodStart < cutoff)
            : null;

        // A record carries two timestamps rather than one. Under a date cutoff it only goes when
        // both extremes predate it — a record still anchored by a surviving reading is kept.
        var records = dto.Records
            ? db.WeatherChannelRecords
                .Where(r => cutoff == null || (r.AllTimeMaxAt < cutoff && r.AllTimeMinAt < cutoff))
            : null;

        var forecasts = dto.Caches
            ? db.ForecastCaches.Where(f => cutoff == null || f.FetchedAt < cutoff)
            : null;

        var alerts = dto.Caches
            ? db.AlertCaches.Where(a => cutoff == null || a.FetchedAt < cutoff)
            : null;

        return new PurgeQueries(readings, hourly, daily, records, forecasts, alerts);
    }

    /// <summary>
    /// Deletes in chunks. A year of raw readings runs to millions of rows, and a single unbounded
    /// DELETE escalates to a table lock long enough to stall ingestion.
    /// </summary>
    private static async Task<int> DeleteInBatchesAsync<T>(IQueryable<T>? query, CancellationToken ct)
        where T : class
    {
        if (query is null) return 0;

        const int batchSize = 50_000;
        var total = 0;
        while (true)
        {
            var deleted = await query.Take(batchSize).ExecuteDeleteAsync(ct);
            total += deleted;
            if (deleted < batchSize) return total;
        }
    }

    // ── Data Sources ──────────────────────────────────────────────────────────

    [HttpGet("datasources")]
    public async Task<IActionResult> GetDataSources(CancellationToken ct)
        => Ok(await db.DataSources.OrderBy(s => s.Name).ToListAsync(ct));

    [HttpPost("datasources")]
    public async Task<IActionResult> CreateDataSource([FromBody] DataSourceDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name required");
        if (await db.DataSources.AnyAsync(s => s.Name == dto.Name, ct))
            return Conflict("Name already in use");

        var source = new DataSource
        {
            Name = dto.Name,
            Type = dto.Type,
            IsEnabled = dto.IsEnabled,
            PollIntervalSeconds = dto.PollIntervalSeconds > 0 ? dto.PollIntervalSeconds : 300,
            Url = string.IsNullOrWhiteSpace(dto.Url) ? null : dto.Url.Trim(),
            Config = dto.Config
        };
        db.DataSources.Add(source);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DataSource.Created",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: source.Id.ToString(), entityName: source.Name, ct: ct);

        return CreatedAtAction(nameof(GetDataSources), new { }, source);
    }

    [HttpPut("datasources/{id:int}")]
    public async Task<IActionResult> UpdateDataSource(int id, [FromBody] DataSourceDto dto, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();

        source.Name = dto.Name ?? source.Name;
        source.Type = dto.Type;
        source.IsEnabled = dto.IsEnabled;
        source.PollIntervalSeconds = dto.PollIntervalSeconds > 0 ? dto.PollIntervalSeconds : source.PollIntervalSeconds;
        source.Url = string.IsNullOrWhiteSpace(dto.Url) ? null : dto.Url.Trim();
        source.Config = dto.Config ?? source.Config;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DataSource.Updated",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: id.ToString(), entityName: source.Name, ct: ct);
        return NoContent();
    }

    /// <summary>
    /// Deletes a data source. Readings and aggregates hold a non-nullable FK with
    /// <c>DeleteBehavior.Restrict</c>, so any source that has ever recorded anything cannot be
    /// removed while its history stands — the delete used to reach SQL Server, get rejected, and
    /// surface as a 500 the page threw away. Dependents are counted first and reported as a 409 so
    /// the caller can re-confirm with real numbers; <paramref name="force"/> then takes the
    /// history with the source, which is the only way to remove such a source at all.
    /// </summary>
    [HttpDelete("datasources/{id:int}")]
    public async Task<IActionResult> DeleteDataSource(int id, bool force, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();

        var readingCount = await db.WeatherReadings.CountAsync(r => r.SourceId == id, ct);
        var aggregateCount = await db.WeatherReadingAggregates.CountAsync(a => a.SourceId == id, ct);

        if ((readingCount > 0 || aggregateCount > 0) && !force)
        {
            return Conflict(new
            {
                name = source.Name,
                readings = readingCount,
                aggregates = aggregateCount,
                message = $"\"{source.Name}\" has {readingCount:N0} readings and " +
                          $"{aggregateCount:N0} aggregates. They must be deleted with the source."
            });
        }

        // The batched deletes run immediately while the source removal waits for SaveChanges, so
        // without a transaction a failure partway through would strip the history and leave the
        // source behind.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (readingCount > 0)
            await DeleteInBatchesAsync(db.WeatherReadings.Where(r => r.SourceId == id), ct);
        if (aggregateCount > 0)
            await DeleteInBatchesAsync(db.WeatherReadingAggregates.Where(a => a.SourceId == id), ct);

        db.DataSources.Remove(source);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await audit.LogAsync("DataSource.Deleted",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: id.ToString(), entityName: source.Name,
            newValue: readingCount > 0 || aggregateCount > 0
                ? $"with {readingCount} readings and {aggregateCount} aggregates"
                : null,
            ct: ct);
        return NoContent();
    }

    [HttpPost("datasources/{id:int}/toggle")]
    public async Task<IActionResult> ToggleDataSource(int id, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();
        source.IsEnabled = !source.IsEnabled;
        await db.SaveChangesAsync(ct);
        return Ok(new { source.IsEnabled });
    }

    [HttpPost("datasources/{id:int}/test")]
    public async Task<IActionResult> TestDataSource(int id, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await dataSourceTester.TestAsync(id, timeoutCts.Token);
        return Ok(result);
    }

    [HttpPost("datasources/{id:int}/rotate-key")]
    public async Task<IActionResult> RotateKey(int id, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();
        if (source.Type != DataSourceType.Push)
            return BadRequest("Key rotation is only supported for Push sources");

        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        var plainKey = Convert.ToBase64String(keyBytes);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey)));

        Dictionary<string, JsonElement>? cfg = null;
        try { cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.Config ?? "{}"); } catch { }
        cfg ??= [];
        cfg["apiKeyHash"] = JsonSerializer.SerializeToElement(hash);
        source.Config = JsonSerializer.Serialize(cfg);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DataSource.KeyRotated",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: id.ToString(), entityName: source.Name, ct: ct);

        return Ok(new { plainKey });
    }

    // ── Branding ──────────────────────────────────────────────────────────────

    [HttpPost("branding/logo")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp" };
        if (!allowed.Contains(ext)) return BadRequest("Invalid file type.");

        var uploadsDir = Path.Combine(webEnv.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var fileName = "logo" + ext;
        var filePath = Path.Combine(uploadsDir, fileName);
        await using var stream = System.IO.File.Create(filePath);
        await file.CopyToAsync(stream, ct);

        var logoUrl = "/uploads/" + fileName;
        await settings.UpsertAsync("Branding.Logo", logoUrl, modifiedBy: UserName(), ct: ct);

        await audit.LogAsync("Branding.LogoUploaded",
            userId: UserId(), userName: UserName(),
            entityType: "Setting", entityId: "Branding.Logo", entityName: "Logo", newValue: logoUrl, ct: ct);

        return Ok(new { url = logoUrl });
    }

    [HttpDelete("branding/logo")]
    public async Task<IActionResult> DeleteLogo(CancellationToken ct)
    {
        var logoUrl = await settings.GetAsync("Branding.Logo", ct);
        if (!string.IsNullOrEmpty(logoUrl))
        {
            var filePath = Path.Combine(webEnv.ContentRootPath, logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
        }
        await settings.UpsertAsync("Branding.Logo", "", modifiedBy: UserName(), ct: ct);
        return NoContent();
    }

    // ── Widgets ───────────────────────────────────────────────────────────────

    [HttpGet("widgets")]
    public async Task<IActionResult> GetWidgets(CancellationToken ct)
        => Ok(await db.WidgetDefinitions.OrderBy(w => w.SortOrder).ToListAsync(ct));

    [HttpPost("widgets")]
    public async Task<IActionResult> CreateWidget([FromBody] WidgetDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest("Title required");

        var maxSort = await db.WidgetDefinitions.MaxAsync(w => (int?)w.SortOrder, ct) ?? 0;
        var widget = new WidgetDefinition
        {
            Type          = dto.Type,
            Title         = dto.Title,
            Config        = dto.Config,
            GridX         = dto.GridX,
            GridY         = dto.GridY,
            GridW         = dto.GridW > 0 ? dto.GridW : 4,
            GridH         = dto.GridH > 0 ? dto.GridH : 3,
            SortOrder     = maxSort + 10,
            IsVisible     = dto.IsVisible,
            DashboardName = string.IsNullOrWhiteSpace(dto.DashboardName) ? "Default" : dto.DashboardName
        };
        db.WidgetDefinitions.Add(widget);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetWidgets), new { }, widget);
    }

    [HttpPut("widgets/{id:int}")]
    public async Task<IActionResult> UpdateWidget(int id, [FromBody] WidgetDto dto, CancellationToken ct)
    {
        var widget = await db.WidgetDefinitions.FindAsync([id], ct);
        if (widget is null) return NotFound();
        widget.Type          = dto.Type;
        widget.Title         = dto.Title ?? widget.Title;
        widget.Config        = dto.Config ?? widget.Config;
        widget.GridX         = dto.GridX;
        widget.GridY         = dto.GridY;
        widget.GridW         = dto.GridW > 0 ? dto.GridW : widget.GridW;
        widget.GridH         = dto.GridH > 0 ? dto.GridH : widget.GridH;
        widget.IsVisible     = dto.IsVisible;
        widget.SortOrder     = dto.SortOrder;
        widget.DashboardName = string.IsNullOrWhiteSpace(dto.DashboardName) ? widget.DashboardName : dto.DashboardName;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("widgets/{id:int}")]
    public async Task<IActionResult> DeleteWidget(int id, CancellationToken ct)
    {
        var widget = await db.WidgetDefinitions.FindAsync([id], ct);
        if (widget is null) return NotFound();
        db.WidgetDefinitions.Remove(widget);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    [HttpGet("webhooks")]
    public async Task<IActionResult> GetWebhooks(CancellationToken ct)
        => Ok(await db.WebhookAlerts.OrderBy(w => w.Name).ToListAsync(ct));

    [HttpPost("webhooks")]
    public async Task<IActionResult> CreateWebhook([FromBody] WebhookDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Channel) ||
            string.IsNullOrWhiteSpace(dto.WebhookUrl)) return BadRequest("Name, Channel, and WebhookUrl required");

        var hook = new WebhookAlert
        {
            Name = dto.Name,
            Channel = dto.Channel,
            Operator = dto.Operator,
            Threshold = dto.Threshold,
            Unit = dto.Unit ?? "",
            WebhookUrl = dto.WebhookUrl,
            IsEnabled = dto.IsEnabled
        };
        db.WebhookAlerts.Add(hook);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetWebhooks), new { }, hook);
    }

    [HttpPut("webhooks/{id:int}")]
    public async Task<IActionResult> UpdateWebhook(int id, [FromBody] WebhookDto dto, CancellationToken ct)
    {
        var hook = await db.WebhookAlerts.FindAsync([id], ct);
        if (hook is null) return NotFound();
        hook.Name = dto.Name ?? hook.Name;
        hook.Channel = dto.Channel ?? hook.Channel;
        hook.Operator = dto.Operator;
        hook.Threshold = dto.Threshold;
        hook.Unit = dto.Unit ?? hook.Unit;
        hook.WebhookUrl = dto.WebhookUrl ?? hook.WebhookUrl;
        hook.IsEnabled = dto.IsEnabled;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("webhooks/{id:int}")]
    public async Task<IActionResult> DeleteWebhook(int id, CancellationToken ct)
    {
        var hook = await db.WebhookAlerts.FindAsync([id], ct);
        if (hook is null) return NotFound();
        db.WebhookAlerts.Remove(hook);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var users = await userManager.Users.ToListAsync(ct);
        var result = new List<object>();
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            result.Add(new { u.Id, u.Email, u.UserName, u.CreatedAt, Roles = roles });
        }
        return Ok(result);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
            return BadRequest("Email and Password required");

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        if (dto.Roles is { Count: > 0 })
            foreach (var role in dto.Roles)
                await userManager.AddToRoleAsync(user, role);

        await audit.LogAsync("User.Created",
            userId: UserId(), userName: UserName(),
            entityType: "User", entityId: user.Id, entityName: user.Email);
        return Ok(new { user.Id, user.Email });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.Id == UserId()) return BadRequest("Cannot delete your own account");
        await userManager.DeleteAsync(user);
        await audit.LogAsync("User.Deleted",
            userId: UserId(), userName: UserName(),
            entityType: "User", entityId: id, entityName: user.Email);
        return NoContent();
    }

    [HttpPost("users/{id}/roles")]
    public async Task<IActionResult> SetUserRoles(string id, [FromBody] SetRolesDto dto)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        var existing = await userManager.GetRolesAsync(user);
        await userManager.RemoveFromRolesAsync(user, existing);
        if (dto.Roles is { Count: > 0 })
            await userManager.AddToRolesAsync(user, dto.Roles);
        return NoContent();
    }

    [HttpPost("users/{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordDto dto)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, dto.NewPassword);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));
        await audit.LogAsync("User.PasswordReset",
            userId: UserId(), userName: UserName(),
            entityType: "User", entityId: id, entityName: user.Email);
        return NoContent();
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        var roles = await roleManager.Roles.ToListAsync(ct);
        var perms = await db.Permissions.ToListAsync(ct);
        var result = roles.Select(r => new
        {
            r.Id, r.Name,
            Permissions = perms.Where(p => p.RoleId == r.Id).ToList()
        });
        return Ok(result);
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name required");
        var result = await roleManager.CreateAsync(new IdentityRole(dto.Name));
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));
        return Ok(new { Name = dto.Name });
    }

    [HttpDelete("roles/{id}")]
    public async Task<IActionResult> DeleteRole(string id)
    {
        var role = await roleManager.FindByIdAsync(id);
        if (role is null) return NotFound();
        if (role.Name == "Administrator") return BadRequest("Cannot delete the Administrator role");
        await roleManager.DeleteAsync(role);
        await db.Permissions.Where(p => p.RoleId == id).ExecuteDeleteAsync();
        return NoContent();
    }

    [HttpPut("roles/{id}/permissions")]
    public async Task<IActionResult> SetPermissions(string id, [FromBody] SetPermissionsDto dto, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(id);
        if (role is null) return NotFound();

        await db.Permissions.Where(p => p.RoleId == id).ExecuteDeleteAsync(ct);

        if (dto.Permissions is { Count: > 0 })
        {
            var perms = dto.Permissions.Select(p => new Permission
            {
                RoleId = id,
                Resource = p.Resource,
                Action = p.Action,
                Name = $"{p.Resource}.{p.Action}"
            }).ToList();
            db.Permissions.AddRange(perms);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? UserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    private string? UserName() => User.Identity?.Name;

    // ── DTOs ──────────────────────────────────────────────────────────────────

    public record DataSourceDto(
        string? Name,
        DataSourceType Type,
        bool IsEnabled,
        int PollIntervalSeconds,
        string? Url,
        string? Config);

    public record DeviceDto(
        string? Name,
        string? Location,
        string? Model,
        bool IsEnabled,
        int OfflineThresholdMinutes,
        string? ChannelOverrides = null);

    /// <summary>One channel rename for a device's stored history.</summary>
    public record ChannelRekeyDto(string From, string To);

    /// <summary>
    /// What a purge should remove. <paramref name="Before"/> is a local station date; null purges
    /// everything in the selected stores.
    /// </summary>
    public record PurgeDto(
        DateTime? Before,
        bool Readings,
        bool Aggregates,
        bool Records,
        bool Caches);

    public record WidgetDto(
        WidgetType Type,
        string? Title,
        string? Config,
        int GridX,
        int GridY,
        int GridW,
        int GridH,
        int SortOrder,
        bool IsVisible,
        string? DashboardName);

    public record WebhookDto(
        string? Name,
        string? Channel,
        AlertOperator Operator,
        decimal Threshold,
        string? Unit,
        string? WebhookUrl,
        bool IsEnabled);

    public record CreateUserDto(
        string? Email,
        string? Password,
        List<string>? Roles);

    public record SetRolesDto(List<string>? Roles);
    public record ResetPasswordDto(string NewPassword);
    public record CreateRoleDto(string? Name);
    public record PermissionRef(string Resource, string Action);
    public record SetPermissionsDto(List<PermissionRef>? Permissions);
}
