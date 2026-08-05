namespace frcastr.Infrastructure.Helpers;

/// <summary>
/// Which channels a non-device source is allowed to write.
/// <para>
/// Pull sources (Open-Meteo, NWS observations, generic HTTP) report a whole station's worth of
/// values for a coordinate, not for this station. Left alone they write <c>temperature.outdoor</c>
/// and friends into the same station-wide channels the MQTT sensors feed, so a regional figure
/// lands on the same history line as the station's own readings and can hold an all-time record
/// the station never measured. Those channels belong to the devices.
/// </para>
/// <para>
/// Air quality, wind, pressure and rainfall are unaffected — there is no device reporting them.
/// Cloud coverage is blocked for a different reason: nothing consumes it as a stored reading any
/// more, since the Weather Animation widget now reads sky cover from the forecast.
/// </para>
/// <para>
/// This governs writes. The rows written before it existed are read back out of history and the
/// monthly averages by the same rule, spelled out in SQL in
/// <c>WeatherDataService.GetHistoryAsync</c> and <c>GetMonthlyStatsAsync</c> because EF cannot see
/// through a method call — keep the prefixes below in step with those queries.
/// </para>
/// </summary>
public static class ChannelLogPolicy
{
    private static readonly string[] BlockedPrefixes =
        ["temperature.", "humidity.", "dewpoint."];

    private static readonly string[] BlockedExact =
        ["cloud.coverage"];

    /// <summary>True when a Pull or AirQuality source must not store this channel.</summary>
    public static bool IsPullBlocked(string? channelName)
    {
        if (string.IsNullOrWhiteSpace(channelName)) return false;

        foreach (var prefix in BlockedPrefixes)
            if (channelName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var exact in BlockedExact)
            if (channelName.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
