using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using frcastr.Infrastructure.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;

namespace frcastr.Infrastructure.Services;

public class DataSourceTestService(
    ApplicationDbContext db,
    IServiceProvider services,
    IHttpClientFactory httpClientFactory) : IDataSourceTestService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<DataSourceTestResult> TestAsync(int sourceId, CancellationToken ct = default)
    {
        var source = await db.DataSources.FindAsync([sourceId], ct);
        if (source is null)
            return new DataSourceTestResult(false, "Data source not found.", null);

        return source.Type switch
        {
            DataSourceType.Forecast                               => await TestForecastAsync(source, ct),
            DataSourceType.Pull or DataSourceType.AirQuality     => await TestPullAsync(source, ct),
            DataSourceType.Alerts                                 => await TestAlertsAsync(source, ct),
            DataSourceType.Mqtt                                   => await TestMqttAsync(source, ct),
            DataSourceType.RadarMapServer                         => await TestRadarAsync(source, ct),
            _                                                     => new DataSourceTestResult(false, $"Test not supported for type '{source.Type}'.", null)
        };
    }

    private async Task<DataSourceTestResult> TestForecastAsync(
        frcastr.Core.Entities.DataSource source, CancellationToken ct)
    {
        try
        {
            var adapters = services.GetServices<IForecastAdapter>().ToList();
            var provider = ExtractProvider(source.Config);
            if (provider is null && source.Url?.Contains("api.weather.gov", StringComparison.OrdinalIgnoreCase) == true)
                provider = "nws";
            else if (provider is null && source.Url?.Contains("api.open-meteo.com", StringComparison.OrdinalIgnoreCase) == true)
                provider = "openmeteo";
            else if (provider is null && !string.IsNullOrWhiteSpace(source.Url))
                provider = "generic-json";
            var adapter = provider is not null
                ? adapters.FirstOrDefault(a => a.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                : adapters.FirstOrDefault();

            if (adapter is null)
                return new DataSourceTestResult(false, "No forecast adapter found for this source.", null);

            var periods = await adapter.FetchAsync(source, ct);
            if (periods.Count == 0)
                return new DataSourceTestResult(false, "Adapter returned 0 forecast periods. Check source configuration.", null);

            var sample = periods.Take(3).Select(p => new
            {
                periodStart  = p.PeriodStart,
                periodEnd    = p.PeriodEnd,
                temperature  = p.Temperature,
                condition    = p.Condition,
                precipChance = p.PrecipChance,
                windSpeed    = p.WindSpeed,
                windDirection = p.WindDirection
            }).ToList();

            return new DataSourceTestResult(true, null, new { totalPeriods = periods.Count, sample });
        }
        catch (Exception ex)
        {
            return new DataSourceTestResult(false, ex.Message, null);
        }
    }

    private async Task<DataSourceTestResult> TestPullAsync(
        frcastr.Core.Entities.DataSource source, CancellationToken ct)
    {
        try
        {
            var adapters = services.GetServices<IDataPullAdapter>().ToList();
            var provider = ExtractProvider(source.Config);
            if (provider is null && !string.IsNullOrWhiteSpace(source.Url)) provider = "generic";
            IDataPullAdapter? adapter = provider is not null
                ? adapters.FirstOrDefault(a => a.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase))
                : adapters.FirstOrDefault(a => a.Provider.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
                  ?? adapters.FirstOrDefault();

            if (adapter is null)
                return new DataSourceTestResult(false, "No pull adapter found for this source.", null);

            var readings = await adapter.FetchAsync(source, ct);
            if (readings.Count == 0)
                return new DataSourceTestResult(false, "Adapter returned 0 readings. Check source configuration.", null);

            var sample = readings.Take(5).Select(r => new { channel = r.Channel, value = r.Value, unit = r.Unit }).ToList();
            return new DataSourceTestResult(true, null, new { totalReadings = readings.Count, sample });
        }
        catch (Exception ex)
        {
            return new DataSourceTestResult(false, ex.Message, null);
        }
    }

    private async Task<DataSourceTestResult> TestAlertsAsync(
        frcastr.Core.Entities.DataSource source, CancellationToken ct)
    {
        var settingsSvc = services.GetRequiredService<ISettingsService>();
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

        lat ??= await settingsSvc.GetAsync("Station.Latitude", ct);
        lon ??= await settingsSvc.GetAsync("Station.Longitude", ct);

        if (string.IsNullOrWhiteSpace(lat) || string.IsNullOrWhiteSpace(lon))
            return new DataSourceTestResult(false, "Station.Latitude/Longitude not configured.", null);

        try
        {
            var http = httpClientFactory.CreateClient("nws");
            var response = await http.GetFromJsonAsync<System.Text.Json.JsonElement>(
                $"/alerts/active?point={lat},{lon}", JsonOpts, ct);
            var count = response.TryGetProperty("features", out var f) ? f.GetArrayLength() : 0;
            return new DataSourceTestResult(true, null, new { activeAlerts = count, point = $"{lat},{lon}" });
        }
        catch (Exception ex)
        {
            return new DataSourceTestResult(false, ex.Message, null);
        }
    }

    private static async Task<DataSourceTestResult> TestMqttAsync(
        frcastr.Core.Entities.DataSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.Config))
            return new DataSourceTestResult(false, "No MQTT configuration.", null);

        var cfg = MqttSourceConfig.TryParse(source.Config);
        if (cfg is null)
            return new DataSourceTestResult(false, "Invalid MQTT config JSON.", null);

        if (string.IsNullOrWhiteSpace(cfg.Broker))
            return new DataSourceTestResult(false, "MQTT broker address is not configured.", null);

        var pattern = cfg.GetTopicPattern();
        var messages = new List<object>();
        var devices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        IMqttClient? client = null;
        try
        {
            var factory = new MqttClientFactory();
            client = factory.CreateMqttClient();

            var optionsBuilder = new MqttClientOptionsBuilder()
                .WithClientId($"frcastr-test-{Guid.NewGuid():N}"[..23])
                .WithTcpServer(cfg.Broker, cfg.EffectivePort);
            if (!string.IsNullOrWhiteSpace(cfg.Username))
                optionsBuilder.WithCredentials(cfg.Username, cfg.Password);

            client.ApplicationMessageReceivedAsync += e =>
            {
                if (messages.Count < 5)
                {
                    var topic = e.ApplicationMessage.Topic;
                    var payload = e.ApplicationMessage.Payload.Length > 0
                        ? Encoding.UTF8.GetString(e.ApplicationMessage.Payload)
                        : "";

                    // Report what the ingestion path would actually make of this message, so a
                    // mismatched topicPattern or fieldMapping is visible from the admin UI.
                    string? deviceId = null;
                    string? measure = null;
                    var matched = pattern?.TryMatch(topic, out deviceId, out measure) ?? false;

                    var parsed = MqttPayloadParser.Parse(cfg, topic, measure, payload);
                    if (!string.IsNullOrWhiteSpace(parsed.DeviceId)) deviceId = parsed.DeviceId;
                    if (deviceId is not null) devices.Add(deviceId);

                    messages.Add(new
                    {
                        topic,
                        payload,
                        matchesPattern = pattern is null ? (bool?)null : matched,
                        device = deviceId,
                        channels = parsed.Values
                            .Select(v => new { channel = v.Channel, value = v.Value, unit = v.Unit })
                            .ToList()
                    });
                }
                return Task.CompletedTask;
            };

            await client.ConnectAsync(optionsBuilder.Build(), timeoutCts.Token);

            var subOptions = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(new MqttTopicFilterBuilder().WithTopic(cfg.GetSubscribeFilter()).Build())
                .Build();
            await client.SubscribeAsync(subOptions, timeoutCts.Token);

            // Wait up to 10 seconds for messages, stop early if we have 5
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (messages.Count < 5 && DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                await Task.Delay(200, ct).ConfigureAwait(false);

            return new DataSourceTestResult(true, null,
                new
                {
                    connected = true, broker = cfg.Broker,
                    subscribedTo = cfg.GetSubscribeFilter(),
                    topicPattern = cfg.TopicPattern,
                    devicesSeen = devices.OrderBy(d => d).ToList(),
                    messagesReceived = messages.Count, messages
                });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timed out — but we may still have collected messages
            return new DataSourceTestResult(true, "Timed out after 10s.",
                new
                {
                    connected = true, broker = cfg.Broker,
                    subscribedTo = cfg.GetSubscribeFilter(),
                    devicesSeen = devices.OrderBy(d => d).ToList(),
                    messagesReceived = messages.Count, messages
                });
        }
        catch (Exception ex)
        {
            return new DataSourceTestResult(false, ex.Message, null);
        }
        finally
        {
            if (client is not null)
            {
                try { await client.DisconnectAsync(); } catch { }
                client.Dispose();
            }
        }
    }

    private async Task<DataSourceTestResult> TestRadarAsync(
        frcastr.Core.Entities.DataSource source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source.Url))
            return new DataSourceTestResult(false, "No MapServer URL configured.", null);

        try
        {
            using var http = httpClientFactory.CreateClient();
            var baseUrl  = source.Url.TrimEnd('/');
            var sep      = baseUrl.Contains('?') ? '&' : '?';
            var infoUrl  = baseUrl + sep + "f=json";

            var response = await http.GetAsync(infoUrl, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("serviceDescription", out _) &&
                !root.TryGetProperty("mapName", out _) &&
                !root.TryGetProperty("layers", out _))
                return new DataSourceTestResult(false, "Response is not a valid ArcGIS MapServer service.", null);

            var sample = new
            {
                serviceDescription = root.TryGetProperty("serviceDescription", out var sd) ? sd.GetString() : null,
                mapName            = root.TryGetProperty("mapName", out var mn) ? mn.GetString() : null,
                layerCount         = root.TryGetProperty("layers", out var ly) ? ly.GetArrayLength() : 0,
                tileUrl            = baseUrl + "/tile/{z}/{y}/{x}"
            };

            return new DataSourceTestResult(true, null, sample);
        }
        catch (Exception ex)
        {
            return new DataSourceTestResult(false, ex.Message, null);
        }
    }

    private static string? ExtractProvider(string? config)
    {
        if (string.IsNullOrWhiteSpace(config)) return null;
        try
        {
            using var doc = JsonDocument.Parse(config);
            return doc.RootElement.TryGetProperty("provider", out var p) ? p.GetString() : null;
        }
        catch { return null; }
    }

}
