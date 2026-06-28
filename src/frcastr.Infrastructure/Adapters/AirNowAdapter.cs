using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// Fetches current AQI from the AirNow API (https://docs.airnowapi.org/).
// DataSource.Config: { "provider": "airnow", "apiKey": "YOUR_KEY", "latitude": "40.7", "longitude": "-74.0", "distance": "25" }
// apiKey falls back to the global setting "AirNow.ApiKey".
// latitude/longitude fall back to Station.Latitude / Station.Longitude settings.
// Writes channel: aqi.outdoor (highest AQI across all reported parameters)
public class AirNowAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<AirNowAdapter> logger) : IDataPullAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "airnow";

    public async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> FetchAsync(
        DataSource source, CancellationToken ct = default)
    {
        string? apiKey = null, lat = null, lon = null, distance = null;

        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config, JsonOpts);
                apiKey   = cfg?.GetValueOrDefault("apiKey");
                lat      = cfg?.GetValueOrDefault("latitude");
                lon      = cfg?.GetValueOrDefault("longitude");
                distance = cfg?.GetValueOrDefault("distance");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AirNow: failed to parse config for source {Id}.", source.Id);
            }
        }

        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await settings.GetAsync("AirNow.ApiKey", ct);
        if (string.IsNullOrWhiteSpace(lat))
            lat = await settings.GetAsync("Station.Latitude", ct);
        if (string.IsNullOrWhiteSpace(lon))
            lon = await settings.GetAsync("Station.Longitude", ct);
        distance ??= "25";

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("AirNow: no API key configured for source {Id}. Set 'apiKey' in DataSource config or the 'AirNow.ApiKey' setting.", source.Id);
            return [];
        }

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
        {
            logger.LogWarning("AirNow: latitude/longitude not configured for source {Id}.", source.Id);
            return [];
        }

        var http = httpClientFactory.CreateClient("airnow");

        try
        {
            var url = $"/aq/observation/latLong/current/?latitude={lat}&longitude={lon}" +
                      $"&distance={distance}&API_KEY={apiKey}&format=application/json";

            var observations = await http.GetFromJsonAsync<List<AirNowObservation>>(url, JsonOpts, ct);

            if (observations is null || observations.Count == 0)
            {
                logger.LogWarning("AirNow: no observations returned for source {Id}.", source.Id);
                return [];
            }

            // Report the highest AQI across all parameters
            var highest = observations
                .Where(o => o.Aqi.HasValue)
                .OrderByDescending(o => o.Aqi!.Value)
                .FirstOrDefault();

            if (highest is null) return [];

            return [("aqi.outdoor", (decimal)highest.Aqi!.Value, "AQI")];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AirNow: fetch failed for source {Id}.", source.Id);
            return [];
        }
    }

    private sealed class AirNowObservation
    {
        [JsonPropertyName("ParameterName")] public string? ParameterName { get; init; }
        [JsonPropertyName("AQI")]           public int?    Aqi            { get; init; }
        [JsonPropertyName("Category")]      public AirNowCategory? Category { get; init; }
    }

    private sealed class AirNowCategory
    {
        [JsonPropertyName("Number")] public int    Number { get; init; }
        [JsonPropertyName("Name")]   public string? Name  { get; init; }
    }
}
