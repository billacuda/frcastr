using System.Text.Json;

namespace frcastr.Infrastructure.Helpers;

public static class ChannelProcessing
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, (decimal Min, decimal Max)> DefaultBounds = new()
    {
        ["temperature.outdoor"] = (-80, 80),
        ["temperature.indoor"] = (-20, 60),
        // Pool/spa water: freezes at 0 and a heater tops out well under 45, so anything outside
        // this is a failing probe rather than a swim.
        ["temperature.water"] = (-5, 60),
        ["humidity.outdoor"] = (0, 100),
        ["humidity.indoor"] = (0, 100),
        ["pressure"] = (800, 1100),
        ["wind.speed"] = (0, 400),
        ["rainfall"] = (0, 500),
        ["aqi.outdoor"] = (0, 2000),
        ["battery.voltage"] = (0, 20)
    };

    /// <summary>
    /// Fallback bounds by channel family, applied when no exact match exists. Without this any
    /// channel name outside the canonical set above is completely unbounded.
    /// </summary>
    private static readonly (string Prefix, decimal Min, decimal Max)[] PrefixBounds =
    [
        ("temperature.", -80, 80),
        ("humidity.", 0, 100),
        ("dewpoint.", -80, 80),
        ("pressure.", 800, 1100),
        ("battery.", 0, 20)
    ];

    /// <summary>
    /// Applies calibration offset and validates against sanity bounds.
    /// Returns adjusted value, or null if out of bounds (reading should be dropped).
    /// </summary>
    public static decimal? ApplyAndValidate(
        string channelName, decimal value, string? configJson)
    {
        Dictionary<string, decimal>? offsets = null;
        Dictionary<string, BoundsEntry>? bounds = null;

        if (!string.IsNullOrWhiteSpace(configJson))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<SourceConfig>(configJson, JsonOpts);
                offsets = cfg?.CalibrationOffsets;
                bounds = cfg?.ChannelBounds;
            }
            catch { /* ignore malformed config */ }
        }

        var adjusted = offsets?.TryGetValue(channelName, out var offset) == true
            ? value + offset
            : value;

        if (bounds?.TryGetValue(channelName, out var b) == true)
        {
            if (adjusted < b.Min || adjusted > b.Max) return null;
        }
        else if (DefaultBounds.TryGetValue(channelName, out var db))
        {
            if (adjusted < db.Min || adjusted > db.Max) return null;
        }
        else
        {
            foreach (var (prefix, min, max) in PrefixBounds)
            {
                if (!channelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (adjusted < min || adjusted > max) return null;
                break;
            }
        }

        return adjusted;
    }

    private sealed class SourceConfig
    {
        public Dictionary<string, decimal>? CalibrationOffsets { get; init; }
        public Dictionary<string, BoundsEntry>? ChannelBounds { get; init; }
    }

    private sealed class BoundsEntry
    {
        public decimal Min { get; init; }
        public decimal Max { get; init; }
    }
}
