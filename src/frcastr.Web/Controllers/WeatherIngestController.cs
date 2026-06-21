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

        var result = await IngestOneAsync(source, request.Channel, request.Value, request.Unit, ct);
        if (!result) return BadRequest("Reading out of configured bounds and was dropped.");

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

        var dropped = 0;
        foreach (var req in requests)
        {
            if (!await IngestOneAsync(source, req.Channel, req.Value, req.Unit, ct))
                dropped++;
        }

        await cacheStore.EvictByTagAsync("weather-current", ct);
        return Accepted(new { dropped });
    }

    private async Task<bool> IngestOneAsync(DataSource source,
        string channel, decimal value, string unit, CancellationToken ct)
    {
        var adjusted = ChannelProcessing.ApplyAndValidate(channel, value, source.Config);
        if (adjusted is null)
        {
            logger.LogWarning("Source {SourceId}: reading '{Channel}'={Value} dropped (out of bounds).",
                source.Id, channel, value);
            return false;
        }

        var timestamp = DateTime.UtcNow;
        db.WeatherReadings.Add(new WeatherReading
        {
            ChannelName = channel,
            Value = adjusted.Value,
            Unit = unit,
            Timestamp = timestamp,
            SourceId = source.Id
        });
        await db.SaveChangesAsync(ct);

        statusService.RecordReading(channel);
        await weatherData.UpdateChannelRecordAsync(channel, adjusted.Value, timestamp, source.Id, ct);
        return true;
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

    public record IngestRequest(string Channel, decimal Value, string Unit);
}
