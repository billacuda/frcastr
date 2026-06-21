using frcastr.Core.Enums;

namespace frcastr.Core.Entities;

public class DataSource
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public DataSourceType Type { get; set; }
    public string? Url { get; set; }
    public string? Config { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 300;
    public DateTime? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
