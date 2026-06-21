using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// weatherapi.com free-tier 7-day forecast.
// DataSource.Config: { "apiKey": "...", "latitude": "...", "longitude": "..." }
// Falls back to Station.Latitude / Station.Longitude if not in config.

public class WeatherApiAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<WeatherApiAdapter> logger) : IForecastAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string BaseUrl = "https://api.weatherapi.com";

    public string Provider => "weatherapi";

    public async Task<IReadOnlyList<ForecastPeriod>> FetchAsync(DataSource source, CancellationToken ct)
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

        if (string.IsNullOrWhiteSpace(apiKey) || lat is null || lon is null)
        {
            logger.LogWarning("WeatherApi: missing apiKey or coordinates for source {Id}.", source.Id);
            return [];
        }

        try
        {
            var http = httpClientFactory.CreateClient();
            var url = $"{BaseUrl}/v1/forecast.json?key={apiKey}&q={lat},{lon}&days=7&aqi=no&alerts=no";
            var data = await http.GetFromJsonAsync<WaResponse>(url, JsonOpts, ct);
            if (data?.Forecast?.ForecastDay is null) return [];

            var periods = new List<ForecastPeriod>();
            foreach (var day in data.Forecast.ForecastDay)
            {
                if (DateTime.TryParse(day.Date, out var date))
                {
                    var dayEntry = day.Day;
                    periods.Add(new ForecastPeriod(
                        PeriodStart: date,
                        PeriodEnd: date.AddHours(12),
                        IsDaytime: true,
                        Temperature: dayEntry?.MaxtempC,
                        Condition: dayEntry?.Condition?.Text,
                        PrecipChance: dayEntry?.DailyChanceOfRain,
                        WindSpeed: dayEntry?.MaxwindKph,
                        WindDirection: null));

                    periods.Add(new ForecastPeriod(
                        PeriodStart: date.AddHours(12),
                        PeriodEnd: date.AddHours(24),
                        IsDaytime: false,
                        Temperature: dayEntry?.MintempC,
                        Condition: dayEntry?.Condition?.Text,
                        PrecipChance: dayEntry?.DailyChanceOfRain,
                        WindSpeed: dayEntry?.MaxwindKph,
                        WindDirection: null));
                }
            }
            return periods;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WeatherApi forecast failed for source {Id}.", source.Id);
            return [];
        }
    }

    private sealed class WaResponse
    {
        [JsonPropertyName("forecast")] public WaForecast? Forecast { get; init; }
    }

    private sealed class WaForecast
    {
        [JsonPropertyName("forecastday")] public List<WaForecastDay>? ForecastDay { get; init; }
    }

    private sealed class WaForecastDay
    {
        [JsonPropertyName("date")] public string? Date { get; init; }
        [JsonPropertyName("day")] public WaDayStats? Day { get; init; }
    }

    private sealed class WaDayStats
    {
        [JsonPropertyName("maxtemp_c")] public double? MaxtempC { get; init; }
        [JsonPropertyName("mintemp_c")] public double? MintempC { get; init; }
        [JsonPropertyName("maxwind_kph")] public double? MaxwindKph { get; init; }
        [JsonPropertyName("daily_chance_of_rain")] public double? DailyChanceOfRain { get; init; }
        [JsonPropertyName("condition")] public WaCondition? Condition { get; init; }
    }

    private sealed class WaCondition
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
    }
}
