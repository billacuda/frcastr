using frcastr.Core.Enums;

namespace frcastr.Core.Entities;

public class WebhookAlert
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string Channel { get; set; } = null!;
    public AlertOperator Operator { get; set; }
    public decimal Threshold { get; set; }
    public string Unit { get; set; } = null!;
    public string WebhookUrl { get; set; } = null!;
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastTriggeredAt { get; set; }
    public DateTime? LastNotifiedAt { get; set; }
}
