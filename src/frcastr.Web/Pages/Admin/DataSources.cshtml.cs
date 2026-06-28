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
public class DataSourcesModel(ApplicationDbContext db) : PageModel
{
    public List<DataSource> Sources { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Sources = await db.DataSources.OrderBy(s => s.Name).ToListAsync(ct);
    }

    public static IEnumerable<DataSourceType> AllTypes =>
        Enum.GetValues<DataSourceType>();

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
