using System.Net.Http.Json;
using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Core.Models;
using Microsoft.Extensions.Logging;

namespace frcastr.Infrastructure.Adapters;

// Pass-through adapter for a custom proxy API that already returns a ForecastPeriod[] array.
// DataSource.Config:
// {
//   "url": "https://my-proxy.example.com/forecast?lat={lat}&lon={lon}",
//   "latitude": "...",   // optional — overrides station setting
//   "longitude": "..."   // optional — overrides station setting
// }
// The URL may contain {lat} and {lon} tokens, which are substituted before requesting.

public class GenericJsonForecastAdapter(
    IHttpClientFactory httpClientFactory,
    ISettingsService settings,
    ILogger<GenericJsonForecastAdapter> logger) : IForecastAdapter
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Provider => "generic-json";

    public async Task<IReadOnlyList<ForecastPeriod>> FetchAsync(DataSource source, CancellationToken ct)
    {
        Dictionary<string, string>? cfg = null;
        if (!string.IsNullOrWhiteSpace(source.Config))
        {
            try { cfg = JsonSerializer.Deserialize<Dictionary<string, string>>(source.Config, JsonOpts); }
            catch { }
        }

        var rawUrl = !string.IsNullOrWhiteSpace(source.Url) ? source.Url : cfg?.GetValueOrDefault("url");
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            logger.LogWarning("GenericJsonForecast: no url configured for source {Id}.", source.Id);
            return [];
        }

        var lat = cfg?.GetValueOrDefault("latitude") ?? await settings.GetAsync("Station.Latitude", ct);
        var lon = cfg?.GetValueOrDefault("longitude") ?? await settings.GetAsync("Station.Longitude", ct);
        var url = rawUrl
            .Replace("{lat}", lat ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{lon}", lon ?? "", StringComparison.OrdinalIgnoreCase);

        try
        {
            var http = httpClientFactory.CreateClient();
            var periods = await http.GetFromJsonAsync<List<ForecastPeriod>>(url, JsonOpts, ct);
            return periods ?? [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GenericJsonForecast failed for source {Id}.", source.Id);
            return [];
        }
    }
}
