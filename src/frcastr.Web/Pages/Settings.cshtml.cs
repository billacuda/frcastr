using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages;

[Authorize]
public class SettingsModel(ApplicationDbContext db) : PageModel
{
    public IReadOnlyDictionary<string, string> S { get; private set; } = new Dictionary<string, string>();

    public async Task OnGetAsync()
    {
        var rows = await db.Settings.ToListAsync();
        S = rows.ToDictionary(r => r.Key, r => r.Value);
    }

    public string Get(string key, string def = "") =>
        S.TryGetValue(key, out var v) ? v : def;
}
