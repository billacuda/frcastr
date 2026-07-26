namespace frcastr.Core.Entities;

public class WeatherChannelRecord
{
    public int Id { get; set; }
    public string ChannelName { get; set; } = null!;
    /// <summary>Null means a station-wide record (non-device sources such as NWS or Open-Meteo).</summary>
    public int? DeviceId { get; set; }
    public Device? Device { get; set; }
    public decimal AllTimeMax { get; set; }
    public DateTime AllTimeMaxAt { get; set; }
    public int? AllTimeMaxSourceId { get; set; }
    public decimal AllTimeMin { get; set; }
    public DateTime AllTimeMinAt { get; set; }
    public int? AllTimeMinSourceId { get; set; }
}
