using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// IDataPullAdapter: current conditions via current= endpoint.
// IForecastAdapter / IHourlyForecastAdapter: daily and hourly forecasts.
// DataSource.Config: { "latitude": "...", "longitude": "..." } (optional — falls back to Station.Latitude/Longitude).

public class OpenMeteoAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<OpenMeteoAdapter> logger) : IDataPullAdapter, IForecastAdapter, IHourlyForecastAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "openmeteo";

    // ── IDataPullAdapter ──────────────────────────────────────────────────────

    async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> IDataPullAdapter.FetchAsync(
        DataSource source, CancellationToken ct)
    {
        var (lat, lon) = await GetCoordsAsync(source, ct);
        if (lat is null) return [];

        var http = httpClientFactory.CreateClient("openmeteo");
        // Temperature, humidity and cloud cover are no longer requested: the first two are the
        // devices' to report and would be dropped before storage anyway, and nothing reads a
        // stored cloud coverage since the Weather Animation widget moved to the forecast.
        var url = $"/v1/forecast?latitude={lat}&longitude={lon}&timezone=UTC" +
                  "&current=surface_pressure,wind_speed_10m,wind_direction_10m,wind_gusts_10m" +
                  "&wind_speed_unit=kmh&temperature_unit=celsius";
        try
        {
            var resp = await http.GetFromJsonAsync<OmResponse>(url, JsonOpts, ct);
            var c = resp?.Current;
            if (c is null) return [];

            var readings = new List<(string, decimal, string)>();
            if (c.Pressure     != null) readings.Add(("pressure",            (decimal)c.Pressure.Value,                 "hPa"));
            if (c.WindSpeed    != null) readings.Add(("wind.speed",          Math.Round((decimal)c.WindSpeed.Value, 2),  "km/h"));
            if (c.WindDirection!= null) readings.Add(("wind.direction",      (decimal)c.WindDirection.Value,            "°"));
            if (c.WindGust     != null) readings.Add(("wind.gust",           Math.Round((decimal)c.WindGust.Value, 2),  "km/h"));
            return readings;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenMeteo current fetch failed for source {SourceId}.", source.Id);
            return [];
        }
    }

    // ── IForecastAdapter ──────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ForecastPeriod>> FetchAsync(DataSource source,
        CancellationToken ct = default)
    {
        var (lat, lon) = await GetCoordsAsync(source, ct);
        if (lat is null) return [];

        var http = httpClientFactory.CreateClient("openmeteo");
        var url  = $"/v1/forecast?latitude={lat}&longitude={lon}&timezone=UTC" +
                   "&daily=temperature_2m_max,temperature_2m_min,weather_code," +
                   "precipitation_probability_max,wind_speed_10m_max,wind_direction_10m_dominant" +
                   "&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm&forecast_days=14";
        try
        {
            var resp = await http.GetFromJsonAsync<OmResponse>(url, JsonOpts, ct);
            return ParseDaily(resp?.Daily);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenMeteo daily fetch failed for source {SourceId}.", source.Id);
            return [];
        }
    }

    public async Task<IReadOnlyList<ForecastPeriod>> FetchHourlyAsync(DataSource source,
        CancellationToken ct = default)
    {
        var (lat, lon) = await GetCoordsAsync(source, ct);
        if (lat is null) return [];

        var http = httpClientFactory.CreateClient("openmeteo");
        var url  = $"/v1/forecast?latitude={lat}&longitude={lon}&timezone=UTC" +
                   "&hourly=temperature_2m,weather_code,precipitation_probability" +
                   "&temperature_unit=celsius&wind_speed_unit=kmh&precipitation_unit=mm&forecast_days=14";
        try
        {
            var resp = await http.GetFromJsonAsync<OmResponse>(url, JsonOpts, ct);
            return ParseHourly(resp?.Hourly);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "OpenMeteo hourly fetch failed for source {SourceId}.", source.Id);
            return [];
        }
    }

    private async Task<(string? Lat, string? Lon)> GetCoordsAsync(DataSource source, CancellationToken ct)
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
            catch { }
        }
        lat ??= await settings.GetAsync("Station.Latitude",  ct);
        lon ??= await settings.GetAsync("Station.Longitude", ct);
        return (lat, lon);
    }

    private static IReadOnlyList<ForecastPeriod> ParseDaily(OmDaily? d)
    {
        if (d?.Time is null || d.TempMax is null) return [];
        var result = new List<ForecastPeriod>();
        for (int i = 0; i < d.Time.Count; i++)
        {
            if (!DateTime.TryParse(d.Time[i], out var date)) continue;
            var tMax = Dbl(d.TempMax, i);
            var tMin = Dbl(d.TempMin, i);
            var cond = WmoToCondition(Int(d.WeatherCode, i));
            var prcp = Dbl(d.PrecipProbMax, i);
            var wSpd = Dbl(d.WindSpeedMax, i);
            var wDir = DegToDir(Int(d.WindDirDominant, i));

            // Daytime period (high temp)
            result.Add(new ForecastPeriod(
                PeriodStart:   date.Date,
                PeriodEnd:     date.Date.AddHours(12),
                IsDaytime:     true,
                Temperature:   tMax ?? tMin,
                Condition:     cond,
                PrecipChance:  prcp,
                WindSpeed:     wSpd,
                WindDirection: wDir));

            // Nighttime period (low temp)
            if (tMin.HasValue)
                result.Add(new ForecastPeriod(
                    PeriodStart:   date.Date.AddHours(12),
                    PeriodEnd:     date.Date.AddHours(24),
                    IsDaytime:     false,
                    Temperature:   tMin,
                    Condition:     cond,
                    PrecipChance:  prcp,
                    WindSpeed:     wSpd,
                    WindDirection: wDir));
        }
        return result;
    }

    private static IReadOnlyList<ForecastPeriod> ParseHourly(OmHourly? h)
    {
        if (h?.Time is null || h.Temperature is null) return [];
        var result = new List<ForecastPeriod>();
        for (int i = 0; i < h.Time.Count; i++)
        {
            if (!DateTime.TryParse(h.Time[i], out var dt)) continue;
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            result.Add(new ForecastPeriod(
                PeriodStart:   dt,
                PeriodEnd:     dt.AddHours(1),
                IsDaytime:     dt.Hour >= 6 && dt.Hour < 20,
                Temperature:   Dbl(h.Temperature, i),
                Condition:     WmoToCondition(Int(h.WeatherCode, i)),
                PrecipChance:  Dbl(h.PrecipProbability, i),
                WindSpeed:     null,
                WindDirection: null));
        }
        return result;
    }

    private static double? Dbl(List<double?>? list, int i) =>
        list is not null && i < list.Count ? list[i] : null;

    private static int? Int(List<int?>? list, int i) =>
        list is not null && i < list.Count ? list[i] : null;

    private static string? DegToDir(int? deg)
    {
        if (deg is null) return null;
        var dirs = new[] { "N","NNE","NE","ENE","E","ESE","SE","SSE","S","SSW","SW","WSW","W","WNW","NW","NNW" };
        return dirs[(int)Math.Round(deg.Value / 22.5) % 16];
    }

    private static string WmoToCondition(int? code) => code switch
    {
        0            => "Clear",
        1            => "Mainly clear",
        2            => "Partly cloudy",
        3            => "Overcast",
        45 or 48     => "Fog",
        51 or 53     => "Drizzle",
        55           => "Heavy drizzle",
        56 or 57     => "Freezing drizzle",
        61 or 63     => "Rain",
        65           => "Heavy rain",
        66 or 67     => "Freezing rain",
        71 or 73     => "Snow",
        75           => "Heavy snow",
        77           => "Snow grains",
        80 or 81     => "Showers",
        82           => "Heavy showers",
        85 or 86     => "Snow showers",
        95           => "Thunderstorm",
        96 or 99     => "Thunderstorm",
        _            => "Unknown"
    };

    // ── JSON model ─────────────────────────────────────────────────────────────

    private sealed class OmResponse
    {
        [JsonPropertyName("current")] public OmCurrent? Current { get; init; }
        [JsonPropertyName("daily")]   public OmDaily?   Daily   { get; init; }
        [JsonPropertyName("hourly")]  public OmHourly?  Hourly  { get; init; }
    }

    private sealed class OmCurrent
    {
        [JsonPropertyName("surface_pressure")]     public double? Pressure      { get; init; }
        [JsonPropertyName("wind_speed_10m")]       public double? WindSpeed     { get; init; }
        [JsonPropertyName("wind_direction_10m")]   public double? WindDirection { get; init; }
        [JsonPropertyName("wind_gusts_10m")]       public double? WindGust      { get; init; }
    }

    private sealed class OmDaily
    {
        [JsonPropertyName("time")]                          public List<string>?  Time             { get; init; }
        [JsonPropertyName("temperature_2m_max")]            public List<double?>? TempMax          { get; init; }
        [JsonPropertyName("temperature_2m_min")]            public List<double?>? TempMin          { get; init; }
        [JsonPropertyName("weather_code")]                  public List<int?>?    WeatherCode      { get; init; }
        [JsonPropertyName("precipitation_probability_max")] public List<double?>? PrecipProbMax    { get; init; }
        [JsonPropertyName("wind_speed_10m_max")]            public List<double?>? WindSpeedMax     { get; init; }
        [JsonPropertyName("wind_direction_10m_dominant")]   public List<int?>?    WindDirDominant  { get; init; }
    }

    private sealed class OmHourly
    {
        [JsonPropertyName("time")]                      public List<string>?  Time             { get; init; }
        [JsonPropertyName("temperature_2m")]            public List<double?>? Temperature      { get; init; }
        [JsonPropertyName("weather_code")]              public List<int?>?    WeatherCode      { get; init; }
        [JsonPropertyName("precipitation_probability")] public List<double?>? PrecipProbability { get; init; }
    }
}
