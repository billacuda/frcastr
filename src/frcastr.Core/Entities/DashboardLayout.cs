namespace frcastr.Core.Entities;

public class DashboardLayout
{
    public int Id { get; set; }
    public string? OwnerId { get; set; }
    public string Name { get; set; } = "Default";
    public string LayoutJson { get; set; } = "[]";
    public string LayoutJsonMobile { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
