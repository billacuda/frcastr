namespace frcastr.Infrastructure.Helpers;

/// <summary>
/// A topic template such as "frcastr/{device}/state" that extracts a device id (and optionally a
/// measure name) from a concrete MQTT topic, and derives the wildcard filter to subscribe with.
/// </summary>
public sealed class MqttTopicPattern
{
    public const string DevicePlaceholder  = "{device}";
    public const string MeasurePlaceholder = "{measure}";

    private readonly string[] _segments;

    public string Pattern { get; }
    public bool HasDevice { get; }
    public bool HasMeasure { get; }

    private MqttTopicPattern(string pattern, string[] segments)
    {
        Pattern     = pattern;
        _segments   = segments;
        HasDevice   = segments.Contains(DevicePlaceholder, StringComparer.OrdinalIgnoreCase);
        HasMeasure  = segments.Contains(MeasurePlaceholder, StringComparer.OrdinalIgnoreCase);
    }

    public static MqttTopicPattern? TryParse(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return null;
        var segments = pattern.Split('/');
        if (segments.Length == 0) return null;
        return new MqttTopicPattern(pattern, segments);
    }

    /// <summary>
    /// The MQTT filter to subscribe with: every placeholder becomes a single-level wildcard.
    /// Deriving it from the pattern keeps the subscription and the matcher from drifting apart.
    /// </summary>
    public string ToSubscribeFilter()
        => string.Join('/', _segments.Select(s => IsPlaceholder(s) ? "+" : s));

    /// <summary>
    /// Matches a concrete topic against the pattern, capturing the placeholder segments.
    /// Literal segments must match exactly and the segment count must be equal.
    /// </summary>
    public bool TryMatch(string topic, out string? deviceId, out string? measure)
    {
        deviceId = null;
        measure  = null;
        if (string.IsNullOrEmpty(topic)) return false;

        var parts = topic.Split('/');
        if (parts.Length != _segments.Length) return false;

        for (var i = 0; i < _segments.Length; i++)
        {
            var seg = _segments[i];

            if (seg.Equals(DevicePlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(parts[i])) return false;
                deviceId = parts[i];
            }
            else if (seg.Equals(MeasurePlaceholder, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(parts[i])) return false;
                measure = parts[i];
            }
            else if (!string.Equals(seg, parts[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlaceholder(string segment)
        => segment.Length > 1 && segment[0] == '{' && segment[^1] == '}';
}
