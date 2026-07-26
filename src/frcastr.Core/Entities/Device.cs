namespace frcastr.Core.Entities;

/// <summary>
/// A physical sensor device (e.g. an ESP32-S3) that publishes readings over MQTT.
/// Devices auto-register on their first message when the source has autoRegisterDevices enabled.
/// </summary>
public class Device
{
    public int Id { get; set; }

    /// <summary>Stable identifier extracted from the MQTT topic, e.g. "greenhouse-01".</summary>
    public string DeviceId { get; set; } = null!;

    /// <summary>Friendly display name; defaults to <see cref="DeviceId"/> on auto-registration.</summary>
    public string Name { get; set; } = null!;

    public string? Location { get; set; }
    public string? Model { get; set; }
    public string? FirmwareVersion { get; set; }

    /// <summary>The MQTT data source this device was last seen on.</summary>
    public int? SourceId { get; set; }
    public DataSource? Source { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// The primary device also publishes its readings under the bare canonical channel name
    /// (e.g. "temperature.outdoor" as well as "temperature.outdoor@greenhouse-01"), so widgets
    /// bound to canonical channels keep working. At most one device may be primary.
    /// </summary>
    public bool IsPrimary { get; set; }

    /// <summary>Driven by the retained MQTT last-will status topic.</summary>
    public bool IsOnline { get; set; } = true;

    public DateTime? LastSeenAt { get; set; }

    /// <summary>Minutes of silence before an offline alert fires. 0 inherits Alerts.SensorOfflineThresholdMinutes.</summary>
    public int OfflineThresholdMinutes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
