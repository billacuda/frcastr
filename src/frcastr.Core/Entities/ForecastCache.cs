namespace frcastr.Core.Entities;

public class ForecastCache
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public DataSource Source { get; set; } = null!;
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public string ForecastJson { get; set; } = null!;
    public DateTime ValidUntil { get; set; }
}
