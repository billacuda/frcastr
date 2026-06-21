using frcastr.Core.Enums;

namespace frcastr.Core.Entities;

public class WidgetDefinition
{
    public int Id { get; set; }
    public WidgetType Type { get; set; }
    public string Title { get; set; } = null!;
    public string? Config { get; set; }
    public int GridX { get; set; }
    public int GridY { get; set; }
    public int GridW { get; set; } = 2;
    public int GridH { get; set; } = 2;
    public int SortOrder { get; set; }
    public bool IsVisible { get; set; } = true;
    public string? OwnerId { get; set; }
    public string DashboardName { get; set; } = "Default";
}
