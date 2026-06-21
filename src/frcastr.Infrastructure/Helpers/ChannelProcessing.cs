using System.Text.Json;

namespace frcastr.Infrastructure.Helpers;

public static class ChannelProcessing
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, (decimal Min, decimal Max)> DefaultBounds = new()
    {
        ["temperature.outdoor"] = (-80, 80),
        ["temperature.indoor"] = (-20, 60),
        ["humidity.outdoor"] = (0, 100),
        ["humidity.indoor"] = (0, 100),
        ["pressure"] = (800, 1100),
        ["wind.speed"] = (0, 400),
        ["rainfall"] = (0, 500),
        ["aqi.outdoor"] = (0, 2000)
    };

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
