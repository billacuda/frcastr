using System.Text.Json;
using frcastr.Core.Models;

namespace frcastr.Infrastructure.Helpers;

/// <summary>
/// Per-device channel remapping. A data source's <c>fieldMapping</c> names channels once for every
/// device on it, so an outdoor sensor sharing a source with an indoor one would otherwise report
/// <c>temperature.indoor</c>. Overrides let a single device file the same payload field elsewhere.
/// </summary>
public static class DeviceChannelOverrides
{
    /// <summary>Longest channel name the column accepts (WeatherReading.ChannelName).</summary>
    public const int MaxChannelLength = 100;

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    /// <summary>
    /// Parses the stored JSON object. Malformed content yields an empty map rather than throwing —
    /// ingestion must not stop because a device carries bad configuration; the admin PUT validates
    /// it up front so this only guards hand-edited rows.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return Empty;

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String) continue;
                var channel = prop.Value.GetString();
                if (!string.IsNullOrWhiteSpace(channel))
                    map[prop.Name] = channel.Trim();
            }
            return map.Count == 0 ? Empty : map;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// The channel a reading should be filed under. Matches on the payload field name first, then
    /// on the channel the source resolved — the field name survives later edits to the source's
    /// <c>fieldMapping</c>, while the channel form also covers legacy <c>channelMapping</c>
    /// sources, whose "field" is the whole topic.
    /// </summary>
    public static string Apply(IReadOnlyDictionary<string, string> overrides, string field, string channel)
    {
        if (overrides.Count == 0) return channel;
        if (!string.IsNullOrEmpty(field) && overrides.TryGetValue(field, out var byField)) return byField;
        return overrides.TryGetValue(channel, out var byChannel) ? byChannel : channel;
    }

    /// <summary>
    /// Validates admin input. Returns the normalized JSON to store (null when empty), or an error.
    /// </summary>
    public static bool TryNormalize(string? json, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json)) return true;

        Dictionary<string, string> map;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Channel overrides must be a JSON object, e.g. {\"temperature\":\"temperature.outdoor\"}.";
                return false;
            }

            map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    error = $"Override for '{prop.Name}' must be a channel name in quotes.";
                    return false;
                }

                var key = prop.Name.Trim();
                var channel = (prop.Value.GetString() ?? "").Trim();
                if (key.Length == 0) continue;
                if (channel.Length == 0) continue;   // an empty value clears the override

                if (!IsValidChannel(channel, out error)) return false;
                map[key] = channel;
            }
        }
        catch (JsonException ex)
        {
            error = "Channel overrides are not valid JSON: " + ex.Message;
            return false;
        }

        if (map.Count == 0) return true;

        normalized = JsonSerializer.Serialize(map);
        if (normalized.Length > 2000)
        {
            error = "Too many channel overrides (the stored JSON exceeds 2000 characters).";
            normalized = null;
            return false;
        }
        return true;
    }

    /// <summary>
    /// A target must be a bare canonical channel name. The device dimension belongs in
    /// <c>DeviceId</c>, not in the string, so an '@' here would produce keys such as
    /// <c>temperature.outdoor@x@greenhouse-01</c> that <see cref="ChannelKey.Split"/> misreads.
    /// </summary>
    public static bool IsValidChannel(string channel, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(channel))
        {
            error = "Channel name is required.";
            return false;
        }

        if (channel.Length > MaxChannelLength)
        {
            error = $"Channel name '{channel}' is longer than {MaxChannelLength} characters.";
            return false;
        }

        if (channel.IndexOf(ChannelKey.Separator) >= 0)
        {
            error = $"Channel name '{channel}' must not contain '{ChannelKey.Separator}' — the device " +
                    "is already recorded separately and is appended to the key automatically.";
            return false;
        }

        if (channel.Any(char.IsWhiteSpace))
        {
            error = $"Channel name '{channel}' must not contain spaces.";
            return false;
        }

        return true;
    }
}
