using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/ingest")]
[AllowAnonymous]
[EnableRateLimiting("IngestKeyPolicy")]
// Authenticated by X-Api-Key, not by a session cookie, and the callers are sensors and scripts that
// have no way to hold an antiforgery token. There is no ambient authority to forge here: without
// the key the request is rejected, and a browser cannot obtain the key.
[IgnoreAntiforgeryToken]
public class WeatherIngestController(
    ApplicationDbContext db,
    IWeatherDataService weatherData,
    IDataSourceStatusService statusService,
    IOutputCacheStore cacheStore,
    ILogger<WeatherIngestController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [HttpPost]
    public async Task<IActionResult> Ingest(
        [FromBody] IngestRequest request, CancellationToken ct)
    {
        var source = await AuthenticateApiKeyAsync(ct);
        if (source is null) return Unauthorized();

        var deviceId = await ResolveDeviceIdAsync(request.Device, ct);
        if (request.Device is not null && deviceId is null)
            return BadRequest($"Unknown device '{request.Device}'.");

        var accepted = await StoreAsync(source, [request], deviceId, ct);
        if (accepted == 0) return BadRequest("Reading out of configured bounds and was dropped.");

        await cacheStore.EvictByTagAsync("weather-current", ct);
        return Accepted();
    }

    [HttpPost("batch")]
    public async Task<IActionResult> IngestBatch(
        [FromBody] IReadOnlyList<IngestRequest> requests, CancellationToken ct)
    {
        if (requests.Count > 100)
            return BadRequest("Batch limit is 100 readings.");

        var source = await AuthenticateApiKeyAsync(ct);
        if (source is null) return Unauthorized();

        var groups = requests.GroupBy(r => r.Device).ToList();

        // Every device key is resolved before anything is written. Rejecting a bad one part-way
        // through would leave the earlier groups committed and still answer with an error, so the
        // caller could not tell what had landed.
        var deviceIds = new Dictionary<string, int?>();
        foreach (var key in groups.Select(g => g.Key).Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var deviceId = await ResolveDeviceIdAsync(key, ct);
            if (deviceId is null) return BadRequest($"Unknown device '{key}'.");
            deviceIds[key!] = deviceId;
        }

        // 100 readings used to cost 200 round trips; each group is now one insert plus one record
        // update.
        var dropped = 0;
        foreach (var group in groups)
        {
            var deviceId = string.IsNullOrWhiteSpace(group.Key) ? null : deviceIds[group.Key];
            var batch = group.ToList();
            dropped += batch.Count - await StoreAsync(source, batch, deviceId, ct);
        }

        await cacheStore.EvictByTagAsync("weather-current", ct);
        return Accepted(new { dropped });
    }

    /// <summary>Stores every in-bounds reading in the batch. Returns how many were accepted.</summary>
    private async Task<int> StoreAsync(DataSource source,
        IReadOnlyList<IngestRequest> requests, int? deviceId, CancellationToken ct)
    {
        var timestamp = DateTime.UtcNow;
        var accepted = new List<(string ChannelName, decimal Value)>(requests.Count);

        foreach (var req in requests)
        {
            var adjusted = ChannelProcessing.ApplyAndValidate(req.Channel, req.Value, source.Config);
            if (adjusted is null)
            {
                logger.LogWarning("Source {SourceId}: reading '{Channel}'={Value} dropped (out of bounds).",
                    source.Id, req.Channel, req.Value);
                continue;
            }

            db.WeatherReadings.Add(new WeatherReading
            {
                ChannelName = req.Channel,
                Value = adjusted.Value,
                Unit = req.Unit,
                Timestamp = timestamp,
                SourceId = source.Id,
                DeviceId = deviceId
            });

            accepted.Add((req.Channel, adjusted.Value));
        }

        if (accepted.Count == 0) return 0;

        await db.SaveChangesAsync(ct);

        foreach (var (channel, _) in accepted)
            statusService.RecordReading(channel, deviceId);

        await weatherData.UpdateChannelRecordsAsync(accepted, timestamp, source.Id, deviceId, ct);
        return accepted.Count;
    }

    /// <summary>
    /// Maps the optional device key in a request to a Devices row. Unlike MQTT this does not
    /// auto-register: a push source is authenticated by a shared key, so a typo would otherwise
    /// silently create a device rather than be reported back to the caller.
    /// </summary>
    private async Task<int?> ResolveDeviceIdAsync(string? deviceKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deviceKey)) return null;
        return await db.Devices
            .Where(d => d.DeviceId == deviceKey)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<DataSource?> AuthenticateApiKeyAsync(CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("X-Api-Key", out var keyHeader)) return null;
        var plainKey = keyHeader.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey))).ToLowerInvariant();

        var sources = await db.DataSources
            .Where(s => s.IsEnabled && s.Type == frcastr.Core.Enums.DataSourceType.Push)
            .ToListAsync(ct);

        return sources.FirstOrDefault(s =>
        {
            if (string.IsNullOrWhiteSpace(s.Config)) return false;
            try
            {
                using var doc = JsonDocument.Parse(s.Config);
                return doc.RootElement.TryGetProperty("apiKeyHash", out var h) &&
                       h.GetString() == hash;
            }
            catch { return false; }
        });
    }

    /// <summary>
    /// <paramref name="Device"/> is optional and names an already-registered device by its Device
    /// ID. Without it the reading is station-wide, which is what every existing push client sends.
    /// </summary>
    public record IngestRequest(string Channel, decimal Value, string Unit, string? Device = null);
}
