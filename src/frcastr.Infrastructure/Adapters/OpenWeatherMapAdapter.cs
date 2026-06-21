using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// Implements both IDataPullAdapter (current weather) and IForecastAdapter (5-day forecast).
// DataSource.Config: { "apiKey": "...", "latitude": "...", "longitude": "..." }
// Falls back to Station.Latitude / Station.Longitude settings if lat/lon not in config.

public class OpenWeatherMapAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<OpenWeatherMapAdapter> logger) : IDataPullAdapter, IForecastAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string BaseUrl = "https://api.openweathermap.org";

    public string Provider => "openweathermap";

    // ── IDataPullAdapter ──────────────────────────────────────────────────────

    async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> IDataPullAdapter.FetchAsync(
        DataSource source, CancellationToken ct)
    {
        var (apiKey, lat, lon) = await GetConfigAsync(source, ct);
        if (string.IsNullOrWhiteSpace(apiKey) || lat is null || lon is null)
        {
            logger.LogWarning("OpenWeatherMap pull: missing apiKey or coordinates for source {Id}.", source.Id);
            return [];
        }

        try
        {
            var http = httpClientFactory.CreateClient();
            var url = $"{BaseUrl}/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}&units=metric";
            var data = await http.GetFromJsonAsync<OWMCurrentResponse>(url, JsonOpts, ct);
            if (data is null) return [];

            var readings = new List<(string, decimal, string)>
            {
                ("temperature.outdoor", (decimal)data.Main.Temp, "°C"),
                ("humidity.outdoor", (decimal)data.Main.Humidity, "%"),
                ("pressure", (decimal)data.Main.Pressure, "hPa"),
            };

            if (data.Wind is not null)
            {
                readings.Add(("wind.speed", Math.Round((decimal)(data.Wind.Speed * 3.6), 2), "km/h"));
                readings.Add(("wind.direction", (decimal)data.Wind.Deg, "°"));
            }
            if (data.Rain is not null)
                readings.Add(("rainfall", (decimal)data.Rain.OneHour, "mm"));

            return readings;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenWeatherMap pull failed for source {Id}.", source.Id);
            return [];
        }
    }

    // ── IForecastAdapter ──────────────────────────────────────────────────────

    async Task<IReadOnlyList<ForecastPeriod>> IForecastAdapter.FetchAsync(
        DataSource source, CancellationToken ct)
    {
        var (apiKey, lat, lon) = await GetConfigAsync(source, ct);
        if (string.IsNullOrWhiteSpace(apiKey) || lat is null || lon is null)
        {
            logger.LogWarning("OpenWeatherMap forecast: missing apiKey or coordinates for source {Id}.", source.Id);
            return [];
        }

        try
        {
            var http = httpClientFactory.CreateClient();
            var url = $"{BaseUrl}/data/2.5/forecast?lat={lat}&lon={lon}&appid={apiKey}&units=metric&cnt=40";
            var data = await http.GetFromJsonAsync<OWMForecastResponse>(url, JsonOpts, ct);
            if (data?.List is null) return [];

            return data.List.Select(item =>
            {
                var start = DateTimeOffset.FromUnixTimeSeconds(item.Dt).UtcDateTime;
                var isDaytime = start.Hour is >= 6 and < 20;
                return new ForecastPeriod(
                    PeriodStart: start,
                    PeriodEnd: start.AddHours(3),
                    IsDaytime: isDaytime,
                    Temperature: item.Main.Temp,
                    Condition: item.Weather?.FirstOrDefault()?.Description,
                    PrecipChance: item.Pop * 100,
                    WindSpeed: item.Wind?.Speed * 3.6,
                    WindDirection: item.Wind is not null ? DegreesToDir(item.Wind.Deg) : null);
            }).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenWeatherMap forecast failed for source {Id}.", source.Id);
            return [];
        }
    }

    private async Task<(string? ApiKey, string? Lat, string? Lon)> GetConfigAsync(
        DataSource source, CancellationToken ct)
    {
        Dictionary<string, string>? cfg = null;
        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try { cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config, JsonOpts); }
            catch { }
        }
        var apiKey = cfg?.GetValueOrDefault("apiKey");
        var lat = cfg?.GetValueOrDefault("latitude") ?? await settings.GetAsync("Station.Latitude", ct);
        var lon = cfg?.GetValueOrDefault("longitude") ?? await settings.GetAsync("Station.Longitude", ct);
        return (apiKey, lat, lon);
    }

    private static string DegreesToDir(double deg)
    {
        var dirs = new[] { "N","NNE","NE","ENE","E","ESE","SE","SSE","S","SSW","SW","WSW","W","WNW","NW","NNW" };
        return dirs[(int)Math.Round(deg / 22.5) % 16];
    }

    // ── JSON models ───────────────────────────────────────────────────────────

    private sealed class OWMCurrentResponse
    {
        [JsonPropertyName("main")] public OWMMain Main { get; init; } = null!;
        [JsonPropertyName("wind")] public OWMWind? Wind { get; init; }
        [JsonPropertyName("rain")] public OWMRain? Rain { get; init; }
        [JsonPropertyName("weather")] public List<OWMWeather>? Weather { get; init; }
    }

    private sealed class OWMForecastResponse
    {
        [JsonPropertyName("list")] public List<OWMForecastItem>? List { get; init; }
    }

    private sealed class OWMForecastItem
    {
        [JsonPropertyName("dt")]   public long Dt { get; init; }
        [JsonPropertyName("main")] public OWMMain Main { get; init; } = null!;
        [JsonPropertyName("wind")] public OWMWind? Wind { get; init; }
        [JsonPropertyName("weather")] public List<OWMWeather>? Weather { get; init; }
        [JsonPropertyName("pop")] public double Pop { get; init; }
    }

    private sealed class OWMMain
    {
        [JsonPropertyName("temp")] public double Temp { get; init; }
        [JsonPropertyName("humidity")] public double Humidity { get; init; }
        [JsonPropertyName("pressure")] public double Pressure { get; init; }
    }

    private sealed class OWMWind
    {
        [JsonPropertyName("speed")] public double Speed { get; init; }
        [JsonPropertyName("deg")] public double Deg { get; init; }
    }

    private sealed class OWMRain
    {
        [JsonPropertyName("1h")] public double OneHour { get; init; }
    }

    private sealed class OWMWeather
    {
        [JsonPropertyName("main")] public string? Main { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
    }
}
