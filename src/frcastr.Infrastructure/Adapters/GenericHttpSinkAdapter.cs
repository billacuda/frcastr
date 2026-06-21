using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// POSTs a JSON object to a custom endpoint.
// DataSource.Config:
// {
//   "url": "https://my-sink.example.com/upload",
//   "method": "POST",             // POST (default) or PUT
//   "fieldMapping": {             // channel → JSON field name in request body
//     "temperature.outdoor": "temp",
//     "humidity.outdoor": "rh"
//   },
//   "extraFields": {              // static fields always included
//     "stationId": "HOME"
//   }
// }
// Sends: { "stationId": "HOME", "temp": 21.5, "rh": 65.0, "timestamp": "2026-06-21T10:00:00Z" }

public class GenericHttpSinkAdapter(
    IHttpClientFactory httpClientFactory,
    ILogger<GenericHttpSinkAdapter> logger) : IDataSinkAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "generic";

    public async Task UploadAsync(
        DataSource source,
        IReadOnlyList<(string Channel, decimal Value, string Unit, DateTime Timestamp)> readings,
        CancellationToken ct = default)
    {
        if (readings.Count == 0) return;

        Dictionary<string, JsonElement>? cfg = null;
        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try { cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.Config, JsonOpts); }
            catch { }
        }

        var url = !string.IsNullOrWhiteSpace(source.Url)
            ? source.Url
            : cfg?.GetValueOrDefault("url").GetString();
        if (string.IsNullOrWhiteSpace(url))
        {
            logger.LogWarning("GenericHttpSink: missing url for source {Id}.", source.Id);
            return;
        }

        Dictionary<string, string>? fieldMapping = null;
        if (cfg?.TryGetValue("fieldMapping", out var fmEl) == true && fmEl.ValueKind == JsonValueKind.Object)
            fieldMapping = JsonSerializer.Deserialize<Dictionary<string, string>>(fmEl.GetRawText(), JsonOpts);

        var method = cfg?.GetValueOrDefault("method").GetString() ?? "POST";

        // Build body
        var body = new JsonObject();

        // Static extra fields
        if (cfg?.TryGetValue("extraFields", out var extraEl) == true && extraEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in extraEl.EnumerateObject())
                body[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        }

        // Timestamp of most-recent reading
        body["timestamp"] = readings.Max(r => r.Timestamp).ToString("o") + (readings.Max(r => r.Timestamp).Kind == DateTimeKind.Utc ? "" : "Z");

        // Mapped channels
        foreach (var (channel, value, _, _) in readings)
        {
            var fieldName = fieldMapping?.GetValueOrDefault(channel) ?? channel;
            body[fieldName] = JsonValue.Create((double)value);
        }

        try
        {
            var http = httpClientFactory.CreateClient();
            HttpResponseMessage resp;
            if (method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                resp = await http.PutAsJsonAsync(url, body, JsonOpts, ct);
            else
                resp = await http.PostAsJsonAsync(url, body, JsonOpts, ct);

            if (!resp.IsSuccessStatusCode)
                logger.LogWarning("GenericHttpSink returned {Status} for source {Id}.", (int)resp.StatusCode, source.Id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GenericHttpSink upload failed for source {Id}.", source.Id);
        }
    }
}
