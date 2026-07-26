using frcastr.Core.Entities;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class IndexModel(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager) : PageModel
{
    public int UserCount     { get; private set; }
    public int SourceCount   { get; private set; }
    public int WidgetCount   { get; private set; }
    public int WebhookCount  { get; private set; }
    public int DeviceCount   { get; private set; }
    public List<frcastr.Core.Entities.AuditLog> RecentAudit { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        UserCount    = await userManager.Users.CountAsync(ct);
        SourceCount  = await db.DataSources.CountAsync(ct);
        WidgetCount  = await db.WidgetDefinitions.CountAsync(ct);
        WebhookCount = await db.WebhookAlerts.CountAsync(ct);
        DeviceCount  = await db.Devices.CountAsync(ct);
        RecentAudit  = await db.AuditLogs
            .OrderByDescending(a => a.CreatedAt)
            .Take(8)
            .ToListAsync(ct);
    }
}
