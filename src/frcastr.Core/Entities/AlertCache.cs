namespace frcastr.Core.Entities;

public class AlertCache
{
    public int Id { get; set; }
    public int SourceId { get; set; }
    public DataSource Source { get; set; } = null!;
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
    public string AlertsJson { get; set; } = null!;
    public DateTime ValidUntil { get; set; }
}
