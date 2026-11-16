using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;

namespace frcastr.Infrastructure.Adapters;

// Fetches current AQI from AirNow. Two modes, chosen by the DataSource.Config:
//
// 1. RSS feed (no API key): { "provider": "airnow", "feedId": 287 }
//    or { "provider": "airnow", "rssUrl": "https://feeds.airnowapi.org/rss/realtime/287.xml" }
//    feedId is the AirNow reporting-area ID from the EnviroFlash feed URL.
//
// 2. JSON API (https://docs.airnowapi.org/):
//    { "provider": "airnow", "apiKey": "YOUR_KEY", "latitude": 40.7, "longitude": -74.0, "distance": 25 }
//    apiKey falls back to the global setting "AirNow.ApiKey".
//    latitude/longitude fall back to Station.Latitude / Station.Longitude settings.
//
// Config values may be strings or numbers.
// Writes channel: aqi.outdoor (highest AQI across all reported parameters).
// Failures throw with a descriptive message, which the Test button and the source's
// LastError both display.
public partial class AirNowAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings) : IDataPullAdapter
{
    private const string RssUrlTemplate = "https://feeds.airnowapi.org/rss/realtime/{0}.xml";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "airnow";

    public async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> FetchAsync(
        DataSource source, CancellationToken ct = default)
    {
        var cfg = ParseConfig(source.Config);
        var http = httpClientFactory.CreateClient("airnow");

        var rssUrl = Get(cfg, "rssUrl");
        var feedId = Get(cfg, "feedId");
        if (string.IsNullOrWhiteSpace(rssUrl) && !string.IsNullOrWhiteSpace(feedId))
            rssUrl = string.Format(CultureInfo.InvariantCulture, RssUrlTemplate, Uri.EscapeDataString(feedId.Trim()));

        var aqi = !string.IsNullOrWhiteSpace(rssUrl)
            ? await FetchRssAsync(http, rssUrl.Trim(), ct)
            : await FetchApiAsync(http, cfg, ct);

        return [("aqi.outdoor", aqi, "AQI")];
    }

    // ── RSS mode ────────────────────────────────────────────────────────────

    private static async Task<decimal> FetchRssAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"AirNow: rssUrl '{url}' is not a valid absolute URL.");

        using var resp = await http.GetAsync(uri, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AirNow RSS: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {uri}. {Snippet(body)}");

        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"AirNow RSS: response is not valid XML ({ex.Message}). {Snippet(body)}");
        }

        var description = doc.Root?.Element("channel")?.Element("item")?.Element("description")?.Value;
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("AirNow RSS: feed has no item description. Check the feed ID.");

        // The description is entity-escaped HTML; decode and strip tags to plain text.
        var text = WebUtility.HtmlDecode(description);
        text = TagRegex().Replace(text, "\n");

        var values = AqiLineRegex().Matches(text)
            .Select(m => int.Parse(m.Groups["aqi"].Value, CultureInfo.InvariantCulture))
            .Where(v => v >= 0)
            .ToList();

        if (values.Count == 0)
            throw new InvalidOperationException(
                "AirNow RSS: feed has no AQI lines (the reporting area may not be reporting right now).");

        return values.Max();
    }

    // ── JSON API mode ───────────────────────────────────────────────────────

    private async Task<decimal> FetchApiAsync(
        HttpClient http, Dictionary<string, JsonElement> cfg, CancellationToken ct)
    {
        var apiKey   = Get(cfg, "apiKey");
        var lat      = Get(cfg, "latitude");
        var lon      = Get(cfg, "longitude");
        var distance = Get(cfg, "distance");

        if (string.IsNullOrWhiteSpace(apiKey))
            apiKey = await settings.GetAsync("AirNow.ApiKey", ct);
        if (string.IsNullOrWhiteSpace(lat))
            lat = await settings.GetAsync("Station.Latitude", ct);
        if (string.IsNullOrWhiteSpace(lon))
            lon = await settings.GetAsync("Station.Longitude", ct);
        if (string.IsNullOrWhiteSpace(distance))
            distance = "25";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "AirNow: no API key. Set 'apiKey' in the source config or the 'AirNow.ApiKey' setting, or use 'feedId' for the RSS feed instead.");

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
            throw new InvalidOperationException(
                "AirNow: latitude/longitude not set in the source config or Station settings.");

        if (!TryParseInvariant(lat, out var latD) || !TryParseInvariant(lon, out var lonD))
            throw new InvalidOperationException($"AirNow: latitude/longitude '{lat}', '{lon}' are not valid numbers.");
        if (!TryParseInvariant(distance, out var distD) || distD <= 0)
            throw new InvalidOperationException($"AirNow: distance '{distance}' is not a valid positive number.");

        var latS  = latD.ToString("0.####", CultureInfo.InvariantCulture);
        var lonS  = lonD.ToString("0.####", CultureInfo.InvariantCulture);
        var distS = ((int)Math.Ceiling(distD)).ToString(CultureInfo.InvariantCulture);

        var url = $"/aq/observation/latLong/current/?format=application/json" +
                  $"&latitude={latS}&longitude={lonS}&distance={distS}" +
                  $"&API_KEY={Uri.EscapeDataString(apiKey.Trim())}";

        using var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        body = body.Replace(apiKey.Trim(), "***", StringComparison.Ordinal);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AirNow: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. {Snippet(body)}");

        List<AirNowObservation>? observations;
        try
        {
            using var parsed = JsonDocument.Parse(body);
            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
                throw new InvalidOperationException($"AirNow: {ExtractServiceError(parsed.RootElement) ?? Snippet(body)}");

            observations = parsed.RootElement.Deserialize<List<AirNowObservation>>(JsonOpts);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"AirNow: response is not valid JSON ({ex.Message}). {Snippet(body)}");
        }

        if (observations is null || observations.Count == 0)
            throw new InvalidOperationException(
                $"AirNow: no reporting area within {distS} miles of {latS},{lonS}. Increase 'distance' in the config, or use 'feedId' for the RSS feed.");

        var valid = observations.Where(o => o.Aqi is >= 0).ToList();
        if (valid.Count == 0)
            throw new InvalidOperationException("AirNow: observations returned but none had a valid AQI.");

        return valid.Max(o => o.Aqi!.Value);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static Dictionary<string, JsonElement> ParseConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts);
            return raw is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, JsonElement>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"AirNow: config is not valid JSON ({ex.Message}).");
        }
    }

    // Accepts string or number values; anything else counts as unset.
    private static string? Get(Dictionary<string, JsonElement> cfg, string key) =>
        cfg.TryGetValue(key, out var el)
            ? el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                _                    => null
            }
            : null;

    private static bool TryParseInvariant(string? s, out double value) =>
        double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string? ExtractServiceError(JsonElement root)
    {
        if (root.TryGetProperty("WebServiceError", out var errs) && errs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errs.EnumerateArray())
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("Message", out var msg))
                    return msg.GetString();
        }
        return null;
    }

    private static string Snippet(string body)
    {
        var s = TagRegex().Replace(body ?? "", " ");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // e.g. "Good  - 22 AQI - Particle Pollution (2.5 microns)"
    [GeneratedRegex(@"(?<aqi>-?\d+)\s*AQI\s*-\s*(?<param>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AqiLineRegex();

    private sealed class AirNowObservation
    {
        [JsonPropertyName("ParameterName")] public string? ParameterName { get; init; }
        [JsonPropertyName("AQI")]           public int?    Aqi            { get; init; }
    }
}
