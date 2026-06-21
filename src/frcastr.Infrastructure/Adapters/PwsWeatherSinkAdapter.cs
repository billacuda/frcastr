using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// Uploads readings to PWSWeather.com Personal Weather Station API.
// Identical parameter format to Weather Underground.
// DataSource.Config: { "stationId": "...", "apiKey": "...", "channelMapping": { ... } }

public class PwsWeatherSinkAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<PwsWeatherSinkAdapter> logger) : IDataSinkAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private const string UploadUrl = "https://www.pwsweather.com/pwsupdate/pwsupdate.php";

    public string Provider => "pwsweather";

    public async Task UploadAsync(
        DataSource source,
        IReadOnlyList<(string Channel, decimal Value, string Unit, DateTime Timestamp)> readings,
        CancellationToken ct = default)
    {
        if (readings.Count == 0) return;

        Dictionary<string, string>? cfg = null;
        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try { cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config, JsonOpts); }
            catch { }
        }

        var stationId = cfg?.GetValueOrDefault("stationId");
        var apiKey = cfg?.GetValueOrDefault("apiKey");
        if (string.IsNullOrWhiteSpace(stationId) || string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("PWSWeather: missing stationId or apiKey for source {Id}.", source.Id);
            return;
        }

        Dictionary<string, string>? customMap = null;
        if (cfg?.TryGetValue("channelMapping", out var mapJson) == true && !string.IsNullOrWhiteSpace(mapJson))
        {
            try { customMap = JsonSerializer.Deserialize<Dictionary<string, string>>(mapJson, JsonOpts); }
            catch { }
        }

        var paramMap = BuildParams(readings, customMap);
        if (paramMap.Count == 0)
        {
            logger.LogDebug("PWSWeather: no mappable readings for source {Id}.", source.Id);
            return;
        }

        var dateutc = readings.Max(r => r.Timestamp).ToString("yyyy-MM-dd HH:mm:ss");
        var qs = new System.Text.StringBuilder();
        qs.Append($"ID={Uri.EscapeDataString(stationId)}&PASSWORD={Uri.EscapeDataString(apiKey)}");
        qs.Append($"&dateutc={Uri.EscapeDataString(dateutc)}&action=updateraw&softwaretype=frcastr");
        foreach (var (k, v) in paramMap)
            qs.Append($"&{k}={Uri.EscapeDataString(v)}");

        try
        {
            var http = httpClientFactory.CreateClient();
            var resp = await http.GetAsync($"{UploadUrl}?{qs}", ct);
            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("PWSWeather upload returned {Status} for source {Id}.", (int)resp.StatusCode, source.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PWSWeather upload failed for source {Id}.", source.Id);
        }
    }

    private static Dictionary<string, string> BuildParams(
        IReadOnlyList<(string Channel, decimal Value, string Unit, DateTime Timestamp)> readings,
        Dictionary<string, string>? customMap)
    {
        var result = new Dictionary<string, string>();

        foreach (var (channel, value, unit, _) in readings)
        {
            if (customMap is not null && customMap.TryGetValue(channel, out var mapped))
            {
                result[mapped] = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            switch (channel)
            {
                case "temperature.outdoor":
                    result["tempf"] = CToF(value).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "humidity.outdoor":
                    result["humidity"] = value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "pressure":
                    result["baromin"] = HpaToInHg(value).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "wind.speed":
                    result["windspeedmph"] = KmhToMph(value).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "wind.direction":
                    result["winddir"] = ((int)value).ToString();
                    break;
                case "wind.gust":
                    result["windgustmph"] = KmhToMph(value).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "rainfall":
                    result["rainin"] = MmToIn(value).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "rainfall.daily":
                    result["dailyrainin"] = MmToIn(value).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "uv.index":
                    result["UV"] = value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "solar.radiation":
                    result["solarradiation"] = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                    break;
            }
        }
        return result;
    }

    private static decimal CToF(decimal c) => c * 9m / 5m + 32m;
    private static decimal HpaToInHg(decimal hpa) => hpa * 0.02953m;
    private static decimal KmhToMph(decimal kmh) => kmh * 0.621371m;
    private static decimal MmToIn(decimal mm) => mm * 0.0393701m;
}
