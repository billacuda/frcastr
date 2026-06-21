namespace frcastr.Core.Entities;

public class Permission
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsSystemPermission { get; set; }
    public string? RoleId { get; set; }
    public string? Resource { get; set; }
    public string? Action { get; set; }
}
