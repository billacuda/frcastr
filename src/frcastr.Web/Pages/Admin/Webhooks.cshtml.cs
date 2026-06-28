using System.Text.Json;
using System.Text.Json.Serialization;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class WebhooksModel(ApplicationDbContext db) : PageModel
{
    public List<WebhookAlert> Webhooks { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Webhooks = await db.WebhookAlerts.OrderBy(w => w.Name).ToListAsync(ct);
    }

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
