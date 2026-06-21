using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// Fetches a JSON endpoint and maps JSON fields to weather channels.
// DataSource.Config:
// {
//   "url": "http://local-station/api",
//   "channelMapping": { "temp": "temperature.outdoor", "rh": "humidity.outdoor" },
//   "units": { "temperature.outdoor": "°C", "humidity.outdoor": "%" }
// }
// Keys in channelMapping are dot-separated JSON paths (e.g. "sensors.outdoor.temp").

public class GenericHttpAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<GenericHttpAdapter> logger) : IDataPullAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "generic";

    public async Task<IReadOnlyList<(string Channel, decimal Value, string Unit)>> FetchAsync(
        DataSource source, CancellationToken ct = default)
    {
        Dictionary<string, string>? mapping = null;
        Dictionary<string, string>? units = null;

        string? rawUrl = source.Url;

        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(source.Config, JsonOpts);
                if (string.IsNullOrWhiteSpace(rawUrl) && doc.TryGetProperty("url", out var urlProp))
                    rawUrl = urlProp.GetString();
                if (doc.TryGetProperty("channelMapping", out var mapProp))
                    mapping = JsonSerializer.Deserialize<Dictionary<string, string>>(mapProp.GetRawText(), JsonOpts);
                if (doc.TryGetProperty("units", out var unitsProp))
                    units = JsonSerializer.Deserialize<Dictionary<string, string>>(unitsProp.GetRawText(), JsonOpts);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "GenericHttp: failed to parse config for source {Id}.", source.Id);
            }
        }

        if (!string.IsNullOrWhiteSpace(rawUrl))
        {
            if (mapping is null or { Count: 0 })
            {
                logger.LogWarning("GenericHttp: no channelMapping configured for source {Id}.", source.Id);
                return [];
            }
            return await FetchMappedAsync(rawUrl, mapping, units ?? [], source.Id, ct);
        }

        logger.LogWarning("GenericHttp: missing or invalid config for source {Id}.", source.Id);
        return [];
    }

    private async Task<IReadOnlyList<(string, decimal, string)>> FetchMappedAsync(
        string url,
        Dictionary<string, string> mapping,
        Dictionary<string, string> units,
        int sourceId,
        CancellationToken ct)
    {
        try
        {
            var http = httpClientFactory.CreateClient();
            var json = await http.GetStringAsync(url, ct);
            var node = JsonNode.Parse(json);
            if (node is null) return [];

            var results = new List<(string, decimal, string)>();
            foreach (var (jsonPath, channelName) in mapping)
            {
                var value = ResolvePath(node, jsonPath);
                if (value is null) continue;
                if (!decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var dec)) continue;
                var unit = units.GetValueOrDefault(channelName, "");
                results.Add((channelName, dec, unit));
            }
            return results;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GenericHttp: request failed for source {Id} (url={Url}).", sourceId, url);
            return [];
        }
    }

    private static JsonNode? ResolvePath(JsonNode root, string dotPath)
    {
        var node = root;
        foreach (var segment in dotPath.Split('.'))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(segment, out var child))
                node = child;
            else if (node is JsonArray arr && int.TryParse(segment, out var idx) && idx < arr.Count)
                node = arr[idx];
            else
                return null;
        }
        return node;
    }
}
