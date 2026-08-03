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
    /// <summary>
    /// A row of the table, and the object embedded in its <c>data-source</c> attribute. Config is
    /// deliberately absent: it holds the MQTT broker password and every upstream API key, and this
    /// object is serialized into the rendered HTML. The edit modal fetches it on demand instead.
    /// </summary>
    public record SourceRow(
        int Id, string Name, DataSourceType Type, string? Url, bool IsEnabled,
        int PollIntervalSeconds, DateTime? LastPolledAt, string? LastError);

    public List<SourceRow> Sources { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken ct)
    {
        Sources = await db.DataSources
            .OrderBy(s => s.Name)
            .Select(s => new SourceRow(s.Id, s.Name, s.Type, s.Url, s.IsEnabled,
                s.PollIntervalSeconds, s.LastPolledAt, s.LastError))
            .ToListAsync(ct);
    }

    public static IEnumerable<DataSourceType> AllTypes =>
        Enum.GetValues<DataSourceType>();

    public static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
