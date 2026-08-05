using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

public class NwsAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<NwsAdapter> logger) : IForecastAdapter, IHourlyForecastAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly Regex PointsUrlPattern =
        new(@"/points/(-?\d+\.?\d*),(-?\d+\.?\d*)", RegexOptions.None);

    public string Provider => "nws";

    public async Task<IReadOnlyList<ForecastPeriod>> FetchAsync(DataSource source,
        CancellationToken ct = default)
    {
        var (points, http) = await GetPointsAsync(source, ct);
        if (points is null) return [];

        var forecastUrl = points.Properties?.Forecast;
        if (string.IsNullOrWhiteSpace(forecastUrl))
        {
            logger.LogWarning("NWS adapter: no forecast URL returned.");
            return [];
        }

        try
        {
            var forecast = await http.GetFromJsonAsync<NwsForecastResponse>(forecastUrl, JsonOpts, ct);
            return forecast?.Properties?.Periods.Select(ParsePeriod).ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NWS adapter daily fetch failed for source {SourceId}.", source.Id);
            return [];
        }
    }

    public async Task<IReadOnlyList<ForecastPeriod>> FetchHourlyAsync(DataSource source,
        CancellationToken ct = default)
    {
        var (points, http) = await GetPointsAsync(source, ct);
        if (points is null) return [];

        var hourlyUrl = points.Properties?.ForecastHourly;
        if (string.IsNullOrWhiteSpace(hourlyUrl))
        {
            logger.LogWarning("NWS adapter: no hourly forecast URL returned.");
            return [];
        }

        try
        {
            var forecast = await http.GetFromJsonAsync<NwsForecastResponse>(hourlyUrl, JsonOpts, ct);
            return forecast?.Properties?.Periods.Select(ParsePeriod).ToList() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NWS adapter hourly fetch failed for source {SourceId}.", source.Id);
            return [];
        }
    }

    private async Task<(NwsPointsResponse? Points, HttpClient Http)> GetPointsAsync(
        DataSource source, CancellationToken ct)
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
            var m = PointsUrlPattern.Match(source.Url ?? "");
            if (m.Success) { lat = m.Groups[1].Value; lon = m.Groups[2].Value; }
        }

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
        {
            logger.LogWarning("NWS adapter: Station.Latitude/Longitude not configured.");
            return (null, null!);
        }

        var http = httpClientFactory.CreateClient("nws");
        try
        {
            var points = await http.GetFromJsonAsync<NwsPointsResponse>(
                $"/points/{lat},{lon}", JsonOpts, ct);
            return (points, http);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "NWS adapter points fetch failed for source {SourceId}.", source.Id);
            return (null, http);
        }
    }

    private static ForecastPeriod ParsePeriod(NwsPeriod p) => new(
        PeriodStart: p.StartTime.UtcDateTime,
        PeriodEnd: p.EndTime.UtcDateTime,
        IsDaytime: p.IsDaytime,
        Temperature: ToCelsius(p.Temperature, p.TemperatureUnit),
        Condition: p.ShortForecast,
        PrecipChance: p.ProbabilityOfPrecipitation?.Value,
        WindSpeed: ParseWindKmh(p.WindSpeed),
        WindDirection: p.WindDirection);

    /// <summary>
    /// The default endpoint answers in Fahrenheit, but a source URL carrying <c>?units=si</c> gets
    /// Celsius back. Converting that a second time threw every temperature about 18° low, which is
    /// silently wrong rather than obviously broken — cold enough to look like weather.
    /// </summary>
    private static double ToCelsius(double value, string? unit)
        => unit?.Trim().ToUpperInvariant() is "C" or "°C" ? value : (value - 32.0) * 5.0 / 9.0;

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
        [JsonPropertyName("forecast")]       public string? Forecast { get; init; }
        [JsonPropertyName("forecastHourly")] public string? ForecastHourly { get; init; }
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
