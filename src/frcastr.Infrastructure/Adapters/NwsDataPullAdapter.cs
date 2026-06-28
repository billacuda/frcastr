using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// Fetches current conditions from the National Weather Service API.
// DataSource.Config: { "latitude": "44.05", "longitude": "-123.09" }
// Writes channels: temperature.outdoor, humidity.outdoor, pressure, wind.speed,
//                  wind.direction, wind.gust, dewpoint.outdoor, rainfall, cloud.coverage
public class NwsDataPullAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<NwsDataPullAdapter> logger) : IDataPullAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "nws-current";

    public async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> FetchAsync(
        DataSource source, CancellationToken ct = default)
    {
        string? lat = null, lon = null;

        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config, JsonOpts);
                lat = cfg?.GetValueOrDefault("latitude");
                lon = cfg?.GetValueOrDefault("longitude");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "NwsDataPull: failed to parse config for source {Id}.", source.Id);
            }
        }

        lat ??= await settings.GetAsync("Station.Latitude", ct);
        lon ??= await settings.GetAsync("Station.Longitude", ct);

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
        {
            logger.LogWarning("NwsDataPull: latitude/longitude not configured for source {Id}.", source.Id);
            return [];
        }

        var http = httpClientFactory.CreateClient("nws");

        try
        {
            // Step 1: resolve points → observation stations URL
            var points = await http.GetFromJsonAsync<NwsPointsResponse>($"/points/{lat},{lon}", JsonOpts, ct);
            var stationsUrl = points?.Properties?.ObservationStations;
            if (string.IsNullOrWhiteSpace(stationsUrl))
            {
                logger.LogWarning("NwsDataPull: no observation stations URL for source {Id}.", source.Id);
                return [];
            }

            // Step 2: get first station ID
            var stations = await http.GetFromJsonAsync<NwsStationsResponse>(stationsUrl, JsonOpts, ct);
            var stationId = stations?.Features?.FirstOrDefault()?.Properties?.StationIdentifier;
            if (string.IsNullOrWhiteSpace(stationId))
            {
                logger.LogWarning("NwsDataPull: no stations found for source {Id}.", source.Id);
                return [];
            }

            // Step 3: fetch latest observation
            var obs = await http.GetFromJsonAsync<NwsObservationResponse>(
                $"/stations/{stationId}/observations/latest", JsonOpts, ct);
            var p = obs?.Properties;
            if (p is null) return [];

            var results = new List<(string, decimal, string)>();

            TryAdd(results, "temperature.outdoor", p.Temperature?.Value, "°C");
            TryAdd(results, "humidity.outdoor",    p.RelativeHumidity?.Value, "%");
            TryAdd(results, "dewpoint.outdoor",    p.Dewpoint?.Value, "°C");
            TryAdd(results, "wind.speed",          p.WindSpeed?.Value, "km/h");
            TryAdd(results, "wind.direction",      p.WindDirection?.Value, "°");
            TryAdd(results, "wind.gust",           p.WindGust?.Value, "km/h");

            // Pressure: NWS reports in Pa — convert to hPa
            if (p.BarometricPressure?.Value is double pa)
                results.Add(("pressure", (decimal)(pa / 100.0), "hPa"));

            // Rainfall: NWS reports in metres — convert to mm
            if (p.PrecipitationLastHour?.Value is double precip)
                results.Add(("rainfall", (decimal)(precip * 1000.0), "mm"));

            // Cloud coverage: take highest-coverage layer, convert oktas to %
            var cloudPct = MaxCloudCoverage(p.CloudLayers);
            if (cloudPct >= 0)
                results.Add(("cloud.coverage", cloudPct, "%"));

            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NwsDataPull: fetch failed for source {Id}.", source.Id);
            return [];
        }
    }

    private static decimal MaxCloudCoverage(List<NwsCloudLayer>? layers)
    {
        if (layers is null || layers.Count == 0) return -1;
        var maxOktas = 0;
        foreach (var layer in layers)
        {
            var oktas = layer.Amount?.ToUpperInvariant() switch
            {
                "SKC" or "CLR" or "NSC" => 0,
                "FEW"                   => 2,
                "SCT"                   => 4,
                "BKN"                   => 6,
                "OVC" or "OBS" or "VV"  => 8,
                _                       => 0
            };
            if (oktas > maxOktas) maxOktas = oktas;
        }
        return (decimal)(maxOktas * 100) / 8;
    }

    private static void TryAdd(List<(string, decimal, string)> list, string channel, double? value, string unit)
    {
        if (value.HasValue)
            list.Add((channel, (decimal)value.Value, unit));
    }

    // ── NWS JSON model ────────────────────────────────────────────────────────

    private sealed class NwsPointsResponse
    {
        [JsonPropertyName("properties")] public NwsPointsProps? Properties { get; init; }
    }

    private sealed class NwsPointsProps
    {
        [JsonPropertyName("observationStations")] public string? ObservationStations { get; init; }
    }

    private sealed class NwsStationsResponse
    {
        [JsonPropertyName("features")] public List<NwsStationFeature>? Features { get; init; }
    }

    private sealed class NwsStationFeature
    {
        [JsonPropertyName("properties")] public NwsStationProps? Properties { get; init; }
    }

    private sealed class NwsStationProps
    {
        [JsonPropertyName("stationIdentifier")] public string? StationIdentifier { get; init; }
    }

    private sealed class NwsObservationResponse
    {
        [JsonPropertyName("properties")] public NwsObsProps? Properties { get; init; }
    }

    private sealed class NwsObsProps
    {
        [JsonPropertyName("temperature")]          public NwsQuantValue? Temperature { get; init; }
        [JsonPropertyName("dewpoint")]             public NwsQuantValue? Dewpoint { get; init; }
        [JsonPropertyName("windDirection")]        public NwsQuantValue? WindDirection { get; init; }
        [JsonPropertyName("windSpeed")]            public NwsQuantValue? WindSpeed { get; init; }
        [JsonPropertyName("windGust")]             public NwsQuantValue? WindGust { get; init; }
        [JsonPropertyName("barometricPressure")]   public NwsQuantValue? BarometricPressure { get; init; }
        [JsonPropertyName("relativeHumidity")]     public NwsQuantValue? RelativeHumidity { get; init; }
        [JsonPropertyName("precipitationLastHour")] public NwsQuantValue? PrecipitationLastHour { get; init; }
        [JsonPropertyName("cloudLayers")]           public List<NwsCloudLayer>? CloudLayers { get; init; }
    }

    private sealed class NwsQuantValue
    {
        [JsonPropertyName("value")] public double? Value { get; init; }
    }

    private sealed class NwsCloudLayer
    {
        [JsonPropertyName("amount")] public string? Amount { get; init; }
    }
}
