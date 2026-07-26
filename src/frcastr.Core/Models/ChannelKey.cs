namespace frcastr.Core.Models;

/// <summary>
/// Channel keys are "temperature.outdoor" for station-wide readings and
/// "temperature.outdoor@greenhouse-01" for a specific device. Channel names themselves stay
/// canonical so sanity bounds and calculated channels keep working per device; the device
/// dimension lives in the key suffix (and in WeatherReading.DeviceId).
/// </summary>
public static class ChannelKey
{
    public const char Separator = '@';

    public static string Format(string channelName, string? deviceKey)
        => string.IsNullOrWhiteSpace(deviceKey) ? channelName : channelName + Separator + deviceKey;

    /// <summary>Splits a key into its channel name and device id. Device id is null for bare keys.</summary>
    public static (string ChannelName, string? DeviceKey) Split(string key)
    {
        if (string.IsNullOrEmpty(key)) return (key, null);
        var idx = key.IndexOf(Separator);
        if (idx <= 0 || idx == key.Length - 1) return (key, null);
        return (key[..idx], key[(idx + 1)..]);
    }
}
