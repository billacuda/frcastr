namespace frcastr.Core.Entities;

public class Setting
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Key { get; set; } = null!;
    public string Value { get; set; } = null!;
    public string? Description { get; set; }
    public bool IsSystemSetting { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastModifiedAt { get; set; } = DateTime.UtcNow;
    public string? LastModifiedBy { get; set; }
}
