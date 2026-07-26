using System.Text.Json;
using System.Text.Json.Serialization;

namespace frcastr.Infrastructure.Helpers;

/// <summary>
/// Parsed <c>DataSource.Config</c> for an MQTT source. Shared by the ingestion background service
/// and the admin Test button so the two can never disagree about what a config means.
/// </summary>
public sealed class MqttSourceConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string? Broker { get; init; }
    public int Port { get; init; }

    /// <summary>Explicit subscribe filter. When absent it is derived from <see cref="TopicPattern"/>.</summary>
    public string? Topic { get; init; }

    /// <summary>Template such as "frcastr/{device}/state" used to extract the device id.</summary>
    public string? TopicPattern { get; init; }

    /// <summary>Optional template for retained last-will status messages, e.g. "frcastr/{device}/status".</summary>
    public string? StatusTopicPattern { get; init; }

    public string? Username { get; init; }
    public string? Password { get; init; }

    /// <summary>Create a Device row automatically the first time an unknown device publishes.</summary>
    public bool? AutoRegisterDevices { get; init; }

    /// <summary>JSON payload field name (or topic {measure} segment) -> channel and unit.</summary>
    public Dictionary<string, FieldMap>? FieldMapping { get; init; }

    /// <summary>Legacy: exact topic -> channel name. Still honored for bare-decimal sources.</summary>
    public Dictionary<string, string>? ChannelMapping { get; init; }

    /// <summary>Legacy: channel name -> unit.</summary>
    public Dictionary<string, string>? ChannelUnits { get; init; }

    [JsonIgnore]
    public bool AutoRegister => AutoRegisterDevices ?? true;

    [JsonIgnore]
    public int EffectivePort => Port > 0 ? Port : 1883;

    /// <summary>The compiled topic pattern, or null when this source uses legacy exact-topic mapping.</summary>
    public MqttTopicPattern? GetTopicPattern() => MqttTopicPattern.TryParse(TopicPattern);

    public MqttTopicPattern? GetStatusTopicPattern() => MqttTopicPattern.TryParse(StatusTopicPattern);

    /// <summary>
    /// The filter to subscribe with: explicit <c>topic</c> wins, else derived from the pattern,
    /// else "#" to preserve the previous catch-all default.
    /// </summary>
    public string GetSubscribeFilter()
    {
        if (!string.IsNullOrWhiteSpace(Topic)) return Topic;
        var pattern = GetTopicPattern();
        return pattern?.ToSubscribeFilter() ?? "#";
    }

    /// <summary>Unit for a channel: fieldMapping wins, then the legacy channelUnits map.</summary>
    public string GetUnit(string channelName)
    {
        if (FieldMapping is not null)
        {
            foreach (var map in FieldMapping.Values)
            {
                if (string.Equals(map.Channel, channelName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(map.Unit))
                    return map.Unit;
            }
        }

        if (ChannelUnits is not null && ChannelUnits.TryGetValue(channelName, out var unit))
            return unit ?? "";

        return "";
    }

    public static MqttSourceConfig? TryParse(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return null;
        try { return JsonSerializer.Deserialize<MqttSourceConfig>(configJson, JsonOpts); }
        catch { return null; }
    }

    public sealed class FieldMap
    {
        public string? Channel { get; init; }
        public string? Unit { get; init; }
    }
}
