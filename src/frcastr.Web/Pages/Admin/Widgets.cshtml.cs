using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class WidgetsModel(ApplicationDbContext db) : PageModel
{
    public List<WidgetDefinition> Widgets { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Widgets = await db.WidgetDefinitions.OrderBy(w => w.SortOrder).ToListAsync(ct);
    }

    public static IEnumerable<WidgetType> AllTypes => Enum.GetValues<WidgetType>();
}
