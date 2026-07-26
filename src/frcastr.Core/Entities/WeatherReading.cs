namespace frcastr.Core.Entities;

public class WeatherReading
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public int SourceId { get; set; }
    public DataSource Source { get; set; } = null!;
    public int? DeviceId { get; set; }
    public Device? Device { get; set; }
    public string ChannelName { get; set; } = null!;
    public decimal Value { get; set; }
    public string Unit { get; set; } = null!;
}
