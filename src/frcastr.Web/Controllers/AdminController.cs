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
        if (ValidatePurge(dto) is { } invalid) return BadRequest(invalid);

        var device = await ResolvePurgeDeviceAsync(dto, ct);
        if (dto.DeviceId is not null && device is null) return BadRequest("Unknown sensor.");

        var window = await ResolvePurgeWindowAsync(dto, ct);
        var q = BuildPurgeQueries(dto, window);

        return Ok(new
        {
            from            = window.From,
            to              = window.To,
            device          = device?.Name,
            readings        = q.Readings is null ? 0 : await q.Readings.CountAsync(ct),
            hourlyAggregates = q.Hourly is null ? 0 : await q.Hourly.CountAsync(ct),
            dailyAggregates = q.Daily is null ? 0 : await q.Daily.CountAsync(ct),
            records         = q.Records is null ? 0 : await q.Records.CountAsync(ct),
            recordsReset    = await CountRecordsToResetAsync(dto, window, ct),
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
        if (ValidatePurge(dto) is { } invalid) return BadRequest(invalid);

        var device = await ResolvePurgeDeviceAsync(dto, ct);
        if (dto.DeviceId is not null && device is null) return BadRequest("Unknown sensor.");

        var window = await ResolvePurgeWindowAsync(dto, ct);
        var q = BuildPurgeQueries(dto, window);

        var readings  = await DeleteInBatchesAsync(q.Readings, ct);
        var hourly    = await DeleteInBatchesAsync(q.Hourly, ct);
        var daily     = await DeleteInBatchesAsync(q.Daily, ct);
        var records   = await DeleteInBatchesAsync(q.Records, ct);
        var forecasts = await DeleteInBatchesAsync(q.Forecasts, ct);
        var alerts    = await DeleteInBatchesAsync(q.Alerts, ct);

        // An all-time high set by a reading that no longer exists is a number with nothing behind
        // it. Run this after the deletes so the recalculation sees only what survived.
        var (recordsReset, recordsCleared) = await ResetRecordsAsync(dto, window, ct);

        // Current conditions and daily extremes are output-cached for 15 s and would keep serving
        // numbers backed by rows that no longer exist.
        var invalidator = HttpContext.RequestServices.GetService<IWeatherCacheInvalidator>();
        if (invalidator is not null) await invalidator.InvalidateCurrentAsync(ct);

        await audit.LogAsync("Data.Purged",
            userId: UserId(), userName: UserName(),
            entityType: "Data", entityName: "Weather history",
            newValue: $"{DescribeScope(window, device)}: {readings} readings, {hourly} hourly, " +
                      $"{daily} daily, {records} records deleted, {recordsReset} records " +
                      $"recalculated ({recordsCleared} cleared), {forecasts} forecast cache, " +
                      $"{alerts} alert cache",
            ct: ct);

        return Ok(new
        {
            from             = window.From,
            to               = window.To,
            device           = device?.Name,
            readings,
            hourlyAggregates = hourly,
            dailyAggregates  = daily,
            records,
            recordsReset,
            recordsCleared,
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

    /// <summary>Half-open UTC window a purge applies to. Null on either end is unbounded.</summary>
    private sealed record PurgeWindow(DateTime? From, DateTime? To);

    /// <summary>
    /// Null when the request is usable, otherwise why it is not. An unbounded purge has to be asked
    /// for outright: a request carrying neither date is far more likely to be a caller that forgot
    /// to send them than an admin meaning to erase every reading the station has.
    /// </summary>
    private static string? ValidatePurge(PurgeDto dto)
    {
        if (!dto.Readings && !dto.Aggregates && !dto.Records && !dto.Caches)
            return "Select at least one kind of data to purge.";

        if (!dto.Everything && dto.From is null && dto.To is null)
            return "Choose a from or to date, or ask for everything explicitly.";

        if (dto.From is not null && dto.To is not null && dto.To.Value.Date < dto.From.Value.Date)
            return "The to date cannot be earlier than the from date.";

        return null;
    }

    private async Task<Device?> ResolvePurgeDeviceAsync(PurgeDto dto, CancellationToken ct)
        => dto.DeviceId is null
            ? null
            : await db.Devices.FirstOrDefaultAsync(d => d.Id == dto.DeviceId, ct);

    /// <summary>
    /// Turns the admin's station-local dates into a half-open UTC window. Both dates read as
    /// inclusive on screen, so the To date resolves to the *following* local midnight — picking one
    /// day for both bounds purges exactly that day, which is what a sensor test run needs.
    /// </summary>
    private async Task<PurgeWindow> ResolvePurgeWindowAsync(PurgeDto dto, CancellationToken ct)
    {
        if (dto.Everything) return new PurgeWindow(null, null);

        var tzId = await settings.GetAsync("Station.TimeZone", ct);
        var tz = tzId is not null ? TimeZoneInfo.FindSystemTimeZoneById(tzId) : TimeZoneInfo.Local;

        return new PurgeWindow(
            dto.From is null ? null : LocalDateToUtc(dto.From.Value, addDays: 0, tz),
            dto.To   is null ? null : LocalDateToUtc(dto.To.Value,   addDays: 1, tz));
    }

    private static DateTime LocalDateToUtc(DateTime localDate, int addDays, TimeZoneInfo tz)
    {
        var midnight = new DateTime(localDate.Year, localDate.Month, localDate.Day,
            0, 0, 0, DateTimeKind.Unspecified).AddDays(addDays);
        return TimeZoneInfo.ConvertTimeToUtc(midnight, tz);
    }

    private static string DescribeScope(PurgeWindow window, Device? device)
    {
        var range = (window.From, window.To) switch
        {
            (null, null)         => "everything",
            (not null, null)     => $"from {window.From:u}",
            (null, not null)     => $"before {window.To:u}",
            _                    => $"from {window.From:u} to {window.To:u}"
        };
        return device is null ? range : $"{range} for sensor {device.Name}";
    }

    private PurgeQueries BuildPurgeQueries(PurgeDto dto, PurgeWindow window)
    {
        // Locals rather than property access so EF captures plain values in the expression trees.
        var from = window.From;
        var to = window.To;
        var deviceId = dto.DeviceId;

        var readings = dto.Readings
            ? db.WeatherReadings
                .Where(r => deviceId == null || r.DeviceId == deviceId)
                .Where(r => (from == null || r.Timestamp >= from) && (to == null || r.Timestamp < to))
            : null;

        var hourly = dto.Aggregates
            ? db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Hourly)
                .Where(a => deviceId == null || a.DeviceId == deviceId)
                .Where(a => (from == null || a.PeriodStart >= from) && (to == null || a.PeriodStart < to))
            : null;

        var daily = dto.Aggregates
            ? db.WeatherReadingAggregates
                .Where(a => a.Granularity == AggregationGranularity.Daily)
                .Where(a => deviceId == null || a.DeviceId == deviceId)
                .Where(a => (from == null || a.PeriodStart >= from) && (to == null || a.PeriodStart < to))
            : null;

        // A record carries two timestamps rather than one. It only goes when both extremes fall
        // inside the window — a record still anchored by a surviving reading is kept.
        var records = dto.Records
            ? db.WeatherChannelRecords
                .Where(r => deviceId == null || r.DeviceId == deviceId)
                .Where(r => (from == null || (r.AllTimeMaxAt >= from && r.AllTimeMinAt >= from))
                         && (to   == null || (r.AllTimeMaxAt <  to   && r.AllTimeMinAt <  to)))
            : null;

        // The caches are station-wide and carry no device column, so a purge aimed at one sensor
        // leaves them alone rather than silently clearing forecasts for the whole station.
        var forecasts = dto.Caches && deviceId is null
            ? db.ForecastCaches.Where(f => (from == null || f.FetchedAt >= from) && (to == null || f.FetchedAt < to))
            : null;

        var alerts = dto.Caches && deviceId is null
            ? db.AlertCaches.Where(a => (from == null || a.FetchedAt >= from) && (to == null || a.FetchedAt < to))
            : null;

        return new PurgeQueries(readings, hourly, daily, records, forecasts, alerts);
    }

    // ── All-time records after a purge ────────────────────────────────────────

    /// <summary>
    /// Records whose all-time high or low was set inside the purged window. Their extreme is about
    /// to lose the reading that produced it, so it has to be recalculated from what survives.
    /// Records the purge deletes outright are excluded — they are counted under <c>records</c>.
    /// </summary>
    private IQueryable<WeatherChannelRecord>? BuildRecordsToResetQuery(PurgeDto dto, PurgeWindow window)
    {
        // Only a purge that removes measurements can invalidate a record. Caches carry none.
        if (!dto.Readings && !dto.Aggregates) return null;

        var from = window.From;
        var to = window.To;
        var deviceId = dto.DeviceId;
        var deletingRecords = dto.Records;

        return db.WeatherChannelRecords
            .Where(r => deviceId == null || r.DeviceId == deviceId)
            // Either extreme inside the window is enough; the recalculation redoes whichever ones
            // no longer have data behind them.
            .Where(r => (from == null || r.AllTimeMaxAt >= from) && (to == null || r.AllTimeMaxAt < to)
                     || (from == null || r.AllTimeMinAt >= from) && (to == null || r.AllTimeMinAt < to))
            .Where(r => !deletingRecords
                     || !((from == null || (r.AllTimeMaxAt >= from && r.AllTimeMinAt >= from))
                       && (to   == null || (r.AllTimeMaxAt <  to   && r.AllTimeMinAt <  to))));
    }

    private async Task<int> CountRecordsToResetAsync(PurgeDto dto, PurgeWindow window, CancellationToken ct)
    {
        var q = BuildRecordsToResetQuery(dto, window);
        return q is null ? 0 : await q.CountAsync(ct);
    }

    /// <summary>
    /// Recalculates every all-time record the purge invalidated, from the readings and aggregates
    /// that survived it. A record with nothing left behind it is deleted rather than left holding a
    /// number no data supports.
    /// </summary>
    /// <returns>How many records were recalculated, and how many of those were cleared entirely.</returns>
    private async Task<(int Reset, int Cleared)> ResetRecordsAsync(
        PurgeDto dto, PurgeWindow window, CancellationToken ct)
    {
        var q = BuildRecordsToResetQuery(dto, window);
        if (q is null) return (0, 0);

        var candidates = await q.ToListAsync(ct);
        if (candidates.Count == 0) return (0, 0);

        var cleared = 0;
        foreach (var record in candidates)
        {
            var max = await FindExtremeAsync(record.ChannelName, record.DeviceId, highest: true, ct);
            var min = await FindExtremeAsync(record.ChannelName, record.DeviceId, highest: false, ct);

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
        return (candidates.Count, cleared);
    }

    /// <summary>
    /// The surviving high (or low) for a channel on a device, across both raw readings and rolled-up
    /// aggregates. Once raw readings age out, an aggregate is the only remaining evidence of an
    /// extreme, so ignoring them would walk records back to whatever the retention window still
    /// holds. An aggregate can only date the extreme to the start of its bucket — the exact minute
    /// went with the raw rows — which is the best available answer and still beats a stale one.
    /// </summary>
    private async Task<RecordExtreme?> FindExtremeAsync(
        string channel, int? deviceId, bool highest, CancellationToken ct)
    {
        // Written as two branches because EF renders `DeviceId == @p` with a null parameter as
        // `= NULL`, which matches no row — a station-wide record would silently find nothing.
        var readings = deviceId is null
            ? db.WeatherReadings.Where(r => r.ChannelName == channel && r.DeviceId == null)
            : db.WeatherReadings.Where(r => r.ChannelName == channel && r.DeviceId == deviceId);

        var aggregates = deviceId is null
            ? db.WeatherReadingAggregates.Where(a => a.ChannelName == channel && a.DeviceId == null)
            : db.WeatherReadingAggregates.Where(a => a.ChannelName == channel && a.DeviceId == deviceId);

        var fromReadings = await (highest
                ? readings.OrderByDescending(r => r.Value).ThenBy(r => r.Timestamp)
                : readings.OrderBy(r => r.Value).ThenBy(r => r.Timestamp))
            .Select(r => new RecordExtreme(r.Value, r.Timestamp, r.SourceId))
            .FirstOrDefaultAsync(ct);

        var fromAggregates = await (highest
                ? aggregates.OrderByDescending(a => a.Max).ThenBy(a => a.PeriodStart)
                    .Select(a => new RecordExtreme(a.Max, a.PeriodStart, a.SourceId))
                : aggregates.OrderBy(a => a.Min).ThenBy(a => a.PeriodStart)
                    .Select(a => new RecordExtreme(a.Min, a.PeriodStart, a.SourceId)))
            .FirstOrDefaultAsync(ct);

        if (fromReadings is null) return fromAggregates;
        if (fromAggregates is null) return fromReadings;

        return highest
            ? (fromAggregates.Value > fromReadings.Value ? fromAggregates : fromReadings)
            : (fromAggregates.Value < fromReadings.Value ? fromAggregates : fromReadings);
    }

    private sealed record RecordExtreme(decimal Value, DateTime At, int? SourceId);

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

    /// <summary>
    /// Data sources without their Config. That blob holds the MQTT broker password and every
    /// upstream API key, and a list endpoint is the wrong place for it — callers that genuinely
    /// need it ask for one source's config explicitly, below.
    /// </summary>
    [HttpGet("datasources")]
    public async Task<IActionResult> GetDataSources(CancellationToken ct)
        => Ok(await db.DataSources
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.Type, s.Url, s.IsEnabled, s.PollIntervalSeconds,
                s.LastPolledAt, s.LastError, s.CreatedAt,
                // Null check only. Config is encrypted at rest and Data Protection is randomised,
                // so a server-side comparison against "" encrypts the empty string afresh and never
                // matches — it would report every source as configured.
                HasConfig = s.Config != null
            })
            .ToListAsync(ct));

    /// <summary>One source's raw Config, for the edit form. Secrets in, secrets out.</summary>
    [HttpGet("datasources/{id:int}/config")]
    public async Task<IActionResult> GetDataSourceConfig(int id, CancellationToken ct)
    {
        var config = await db.DataSources
            .Where(s => s.Id == id)
            .Select(s => new { s.Config })
            .FirstOrDefaultAsync(ct);

        return config is null ? NotFound() : Ok(new { config = config.Config });
    }

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
        // SVG is deliberately absent. It is a script-bearing document, and /uploads is served
        // same-origin, so an SVG logo is a stored script that runs the moment anyone opens its URL.
        // The raster formats below cannot carry executable content.
        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        if (!allowed.Contains(ext))
            return BadRequest("Invalid file type. Use PNG, JPEG, GIF or WebP.");

        const long maxBytes = 5 * 1024 * 1024;
        if (file.Length > maxBytes) return BadRequest("Logo must be 5 MB or smaller.");

        if (!await LooksLikeImageAsync(file, ext, ct))
            return BadRequest("That file is not a valid image.");

        var uploadsDir = Path.Combine(webEnv.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        // Clear any previous logo first. The name carries the extension, so uploading a PNG over a
        // GIF used to leave the GIF behind — and any .svg left by an older build stayed reachable
        // at its URL even though the format is no longer accepted.
        foreach (var stale in Directory.EnumerateFiles(uploadsDir, "logo.*"))
        {
            try { System.IO.File.Delete(stale); } catch (IOException) { /* in use; overwritten below if same name */ }
        }

        var fileName = "logo" + ext;
        var filePath = Path.Combine(uploadsDir, fileName);
        await using (var stream = System.IO.File.Create(filePath))
        {
            await file.CopyToAsync(stream, ct);
        }

        var logoUrl = "/uploads/" + fileName;
        await settings.UpsertAsync("Branding.Logo", logoUrl, modifiedBy: UserName(), ct: ct);

        await audit.LogAsync("Branding.LogoUploaded",
            userId: UserId(), userName: UserName(),
            entityType: "Setting", entityId: "Branding.Logo", entityName: "Logo", newValue: logoUrl, ct: ct);

        return Ok(new { url = logoUrl });
    }

    /// <summary>
    /// Rejects anything whose leading bytes are not one of the accepted raster formats, so a file
    /// cannot get in on a renamed extension alone.
    /// </summary>
    private static async Task<bool> LooksLikeImageAsync(IFormFile file, string ext, CancellationToken ct)
    {
        var header = new byte[12];
        await using var probe = file.OpenReadStream();
        var read = await probe.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (read < header.Length) return false;

        bool Starts(params byte[] magic) => header.AsSpan(0, magic.Length).SequenceEqual(magic);

        return ext switch
        {
            ".png"  => Starts(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            ".jpg" or ".jpeg" => Starts(0xFF, 0xD8, 0xFF),
            ".gif"  => Starts((byte)'G', (byte)'I', (byte)'F', (byte)'8'),
            // "RIFF" .... "WEBP"
            ".webp" => Starts((byte)'R', (byte)'I', (byte)'F', (byte)'F') &&
                       header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            _       => false
        };
    }

    [HttpDelete("branding/logo")]
    public async Task<IActionResult> DeleteLogo(CancellationToken ct)
    {
        var logoUrl = await settings.GetAsync("Branding.Logo", ct);
        if (!string.IsNullOrEmpty(logoUrl))
        {
            // Resolved and range-checked rather than concatenated: the stored value is only ever
            // written by the upload above, but a path that escapes the uploads directory must not
            // be reachable even if that ever stops being true.
            var uploadsDir = Path.GetFullPath(Path.Combine(webEnv.ContentRootPath, "uploads"));
            var candidate = Path.GetFullPath(Path.Combine(
                webEnv.ContentRootPath, logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar)));

            if (candidate.StartsWith(uploadsDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                System.IO.File.Exists(candidate))
            {
                System.IO.File.Delete(candidate);
            }
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
    /// What a purge should remove.
    /// <paramref name="From"/> and <paramref name="To"/> are station-local dates, both inclusive;
    /// either may be null for an open end. <paramref name="DeviceId"/> narrows the purge to one
    /// sensor. <paramref name="Everything"/> is the explicit opt-in to an unbounded purge.
    /// </summary>
    public record PurgeDto(
        DateTime? From,
        DateTime? To,
        int? DeviceId,
        bool Everything,
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
