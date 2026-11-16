using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;

namespace frcastr.Infrastructure.Adapters;

// Fetches current AQI from AirNow's EnviroFlash RSS feed (no API key needed).
// DataSource.Config: { "provider": "airnow", "feedId": 287 }
//                 or { "provider": "airnow", "rssUrl": "https://feeds.airnowapi.org/rss/realtime/287.xml" }
// feedId is the AirNow reporting-area ID from the feed URL; it may be a string or a number.
// Writes channel: aqi.outdoor (highest AQI across all reported pollutants).
// Failures throw with a descriptive message, which the Test button and the source's
// LastError both display.
//
// The JSON API mode was removed in 0.15.1: AirNow retired its observation/latLong service
// on 2026-10-01 (HTTP 410).
public partial class AirNowAdapter(IHttpClientFactory httpClientFactory) : IDataPullAdapter
{
    private const string RssUrlTemplate = "https://feeds.airnowapi.org/rss/realtime/{0}.xml";
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "airnow";

    public async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> FetchAsync(
        DataSource source, CancellationToken ct = default)
    {
        var cfg = ParseConfig(source.Config);

        var rssUrl = Get(cfg, "rssUrl")?.Trim();
        var feedId = Get(cfg, "feedId")?.Trim();
        if (string.IsNullOrWhiteSpace(rssUrl) && !string.IsNullOrWhiteSpace(feedId))
            rssUrl = string.Format(CultureInfo.InvariantCulture, RssUrlTemplate, Uri.EscapeDataString(feedId));

        if (string.IsNullOrWhiteSpace(rssUrl))
            throw new InvalidOperationException(cfg.ContainsKey("apiKey")
                ? "AirNow: the API key mode was removed because AirNow retired that service. Set 'feedId' to your reporting area's RSS feed number instead."
                : "AirNow: set 'feedId' (the number in feeds.airnowapi.org/rss/realtime/<id>.xml) or 'rssUrl' in the source config.");

        var aqi = await FetchRssAsync(httpClientFactory.CreateClient("airnow"), rssUrl, ct);
        return [("aqi.outdoor", aqi, "AQI")];
    }

    private static async Task<decimal> FetchRssAsync(HttpClient http, string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"AirNow: rssUrl '{url}' is not a valid absolute URL.");

        using var resp = await http.GetAsync(uri, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"AirNow: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase} from {uri}. {Snippet(body)}");

        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"AirNow: feed is not valid XML ({ex.Message}). {Snippet(body)}");
        }

        var description = doc.Root?.Element("channel")?.Element("item")?.Element("description")?.Value;
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException("AirNow: feed has no item description. Check the feed ID.");

        // The description is entity-escaped HTML; decode and strip tags to plain text.
        var text = TagRegex().Replace(WebUtility.HtmlDecode(description), "\n");

        var values = AqiLineRegex().Matches(text)
            .Select(m => int.Parse(m.Groups["aqi"].Value, CultureInfo.InvariantCulture))
            .Where(v => v >= 0)
            .ToList();

        if (values.Count == 0)
            throw new InvalidOperationException(
                "AirNow: feed has no AQI lines (the reporting area may not be reporting right now).");

        return values.Max();
    }

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

    private static string Snippet(string body)
    {
        var s = WhitespaceRegex().Replace(TagRegex().Replace(body ?? "", " "), " ").Trim();
        return s.Length > 200 ? s[..200] + "…" : s;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // e.g. "Good  - 22 AQI - Particle Pollution (2.5 microns)"
    [GeneratedRegex(@"(?<aqi>-?\d+)\s*AQI\s*-\s*(?<param>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex AqiLineRegex();
}
