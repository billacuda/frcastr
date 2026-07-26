using frcastr.Core.Enums;

namespace frcastr.Core.Entities;

public class WeatherReadingAggregate
{
    public long Id { get; set; }
    public DateTime PeriodStart { get; set; }
    public AggregationGranularity Granularity { get; set; }
    public int SourceId { get; set; }
    public DataSource Source { get; set; } = null!;
    public int? DeviceId { get; set; }
    public Device? Device { get; set; }
    public string ChannelName { get; set; } = null!;
    public decimal Avg { get; set; }
    public decimal Min { get; set; }
    public decimal Max { get; set; }
    public int Count { get; set; }
    public string Unit { get; set; } = null!;
}
