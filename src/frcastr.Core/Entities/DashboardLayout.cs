namespace frcastr.Core.Entities;

public class DashboardLayout
{
    public int Id { get; set; }
    public string? OwnerId { get; set; }
    public string Name { get; set; } = "Default";
    public string LayoutJson { get; set; } = "[]";
    public string LayoutJsonMobile { get; set; } = "[]";

    /// <summary>
    /// Lets the phone view keep widgets that sit side by side on the desktop side by side,
    /// two to a row, instead of stacking every widget full width. A widget alone in a desktop
    /// row still spans the phone's width.
    /// </summary>
    public bool MobileTwoColumn { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
