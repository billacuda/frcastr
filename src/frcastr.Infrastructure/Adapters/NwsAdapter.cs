using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

public class NwsAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<NwsAdapter> logger) : IForecastAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "nws";

    public async Task<IReadOnlyList<ForecastPeriod>> FetchAsync(DataSource source,
        CancellationToken ct = default)
    {
        string? lat, lon;

        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            var cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config, JsonOpts);
            lat = cfg?.GetValueOrDefault("latitude") ?? await settings.GetAsync("Station.Latitude", ct);
            lon = cfg?.GetValueOrDefault("longitude") ?? await settings.GetAsync("Station.Longitude", ct);
        }
        else
        {
            lat = await settings.GetAsync("Station.Latitude", ct);
            lon = await settings.GetAsync("Station.Longitude", ct);
        }

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
        {
            logger.LogWarning("NWS adapter: Station.Latitude/Longitude not configured.");
            return [];
        }

        try
        {
            var http = httpClientFactory.CreateClient("nws");

            var points = await http.GetFromJsonAsync<NwsPointsResponse>(
                $"/points/{lat},{lon}", JsonOpts, ct);

            var forecastUrl = points?.Properties?.Forecast;
            if (string.IsNullOrWhiteSpace(forecastUrl))
            {
                logger.LogWarning("NWS adapter: no forecast URL returned for {Lat},{Lon}.", lat, lon);
                return [];
            }

            var forecast = await http.GetFromJsonAsync<NwsForecastResponse>(
                forecastUrl, JsonOpts, ct);

            return forecast?.Properties?.Periods
                .Select(ParsePeriod)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NWS adapter fetch failed for source {SourceId}.", source.Id);
            return [];
        }
    }

    private static ForecastPeriod ParsePeriod(NwsPeriod p) => new(
        PeriodStart: p.StartTime.UtcDateTime,
        PeriodEnd: p.EndTime.UtcDateTime,
        IsDaytime: p.IsDaytime,
        Temperature: FToC(p.Temperature),
        Condition: p.ShortForecast,
        PrecipChance: p.ProbabilityOfPrecipitation?.Value,
        WindSpeed: ParseWindKmh(p.WindSpeed),
        WindDirection: p.WindDirection);

    private static double FToC(double f) => (f - 32.0) * 5.0 / 9.0;

    private static double? ParseWindKmh(string? windSpeed)
    {
        if (string.IsNullOrWhiteSpace(windSpeed)) return null;
        var token = windSpeed.Replace("mph", "", StringComparison.OrdinalIgnoreCase).Trim();
        var parts = token.Split("to", StringSplitOptions.TrimEntries);
        if (double.TryParse(parts[^1], out var mph)) return mph * 1.60934;
        return null;
    }

    // ── NWS JSON model ────────────────────────────────────────────────────────

    private sealed class NwsPointsResponse
    {
        [JsonPropertyName("properties")] public NwsPointsProps? Properties { get; init; }
    }

    private sealed class NwsPointsProps
    {
        [JsonPropertyName("forecast")] public string? Forecast { get; init; }
    }

    private sealed class NwsForecastResponse
    {
        [JsonPropertyName("properties")] public NwsForecastProps? Properties { get; init; }
    }

    private sealed class NwsForecastProps
    {
        [JsonPropertyName("periods")] public List<NwsPeriod> Periods { get; init; } = [];
    }

    private sealed class NwsPeriod
    {
        [JsonPropertyName("startTime")] public DateTimeOffset StartTime { get; init; }
        [JsonPropertyName("endTime")] public DateTimeOffset EndTime { get; init; }
        [JsonPropertyName("isDaytime")] public bool IsDaytime { get; init; }
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("temperatureUnit")] public string TemperatureUnit { get; init; } = "F";
        [JsonPropertyName("windSpeed")] public string? WindSpeed { get; init; }
        [JsonPropertyName("windDirection")] public string? WindDirection { get; init; }
        [JsonPropertyName("shortForecast")] public string? ShortForecast { get; init; }
        [JsonPropertyName("probabilityOfPrecipitation")] public NwsQuantValue? ProbabilityOfPrecipitation { get; init; }
    }

    private sealed class NwsQuantValue
    {
        [JsonPropertyName("value")] public double? Value { get; init; }
    }
}
