using System.Globalization;
using System.Text.Json;

namespace frcastr.Infrastructure.Helpers;

/// <summary>
/// Turns an MQTT payload into zero or more channel readings. Supports a JSON object carrying
/// several measurements (the ESP32 device contract) and a bare decimal (the legacy one-value-per-
/// topic contract), so existing sources keep working unchanged.
/// </summary>
public static class MqttPayloadParser
{
    /// <summary>Payload keys consumed as device metadata rather than mapped to channels.</summary>
    private static readonly HashSet<string> ReservedKeys =
        new(StringComparer.OrdinalIgnoreCase) { "deviceId", "device", "firmware", "fw", "ts", "timestamp" };

    /// <summary>
    /// <paramref name="Field"/> is the payload field the value arrived in (the {measure} topic
    /// segment for bare-decimal payloads, or the topic itself for legacy channelMapping sources).
    /// It is carried alongside the resolved channel so per-device overrides can key on it —
    /// see <see cref="DeviceChannelOverrides"/>.
    /// </summary>
    public readonly record struct ParsedValue(string Field, string Channel, decimal Value, string Unit);

    public sealed record ParseResult(
        IReadOnlyList<ParsedValue> Values,
        string? DeviceId,
        string? FirmwareVersion);

    private static readonly ParseResult Empty = new([], null, null);

    /// <summary>
    /// <paramref name="measure"/> is the {measure} segment captured from the topic, used to resolve
    /// the channel for bare-decimal payloads.
    /// </summary>
    public static ParseResult Parse(
        MqttSourceConfig config, string topic, string? measure, string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return Empty;

        var trimmed = payload.Trim();

        // Bare decimal: one value, channel resolved from the topic.
        if (TryParseDecimal(trimmed, out var single))
        {
            var channel = ResolveChannel(config, topic, measure);
            if (channel is null) return Empty;
            var field = string.IsNullOrWhiteSpace(measure) ? topic : measure;
            return new ParseResult([new ParsedValue(field, channel, single, config.GetUnit(channel))], null, null);
        }

        if (trimmed[0] != '{') return Empty;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(trimmed); }
        catch { return Empty; }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;

            var values = new List<ParsedValue>();
            string? deviceId = null;
            string? firmware = null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (ReservedKeys.Contains(prop.Name))
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    if (prop.Name.Equals("deviceId", StringComparison.OrdinalIgnoreCase) ||
                        prop.Name.Equals("device", StringComparison.OrdinalIgnoreCase))
                        deviceId = prop.Value.GetString();
                    else if (prop.Name.Equals("firmware", StringComparison.OrdinalIgnoreCase) ||
                             prop.Name.Equals("fw", StringComparison.OrdinalIgnoreCase))
                        firmware = prop.Value.GetString();
                    continue;
                }

                if (config.FieldMapping is null ||
                    !config.FieldMapping.TryGetValue(prop.Name, out var map) ||
                    string.IsNullOrWhiteSpace(map?.Channel))
                    continue;

                if (!TryReadNumber(prop.Value, out var value)) continue;

                var unit = string.IsNullOrWhiteSpace(map.Unit) ? config.GetUnit(map.Channel) : map.Unit;
                values.Add(new ParsedValue(prop.Name, map.Channel, value, unit));
            }

            return new ParseResult(values, deviceId, firmware);
        }
    }

    /// <summary>
    /// Channel for a bare-decimal payload: the {measure} segment via fieldMapping, else the legacy
    /// exact-topic channelMapping.
    /// </summary>
    private static string? ResolveChannel(MqttSourceConfig config, string topic, string? measure)
    {
        if (measure is not null && config.FieldMapping is not null &&
            config.FieldMapping.TryGetValue(measure, out var map) &&
            !string.IsNullOrWhiteSpace(map?.Channel))
            return map.Channel;

        if (config.ChannelMapping is not null &&
            config.ChannelMapping.TryGetValue(topic, out var legacy) &&
            !string.IsNullOrWhiteSpace(legacy))
            return legacy;

        return null;
    }

    private static bool TryReadNumber(JsonElement element, out decimal value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out value);
            case JsonValueKind.String:
                return TryParseDecimal(element.GetString(), out value);
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>
    /// Invariant culture only — sensors publish "21.4" regardless of the server's locale, and the
    /// previous culture-sensitive parse misread that as 214 on comma-decimal servers.
    /// </summary>
    private static bool TryParseDecimal(string? text, out decimal value)
        => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
