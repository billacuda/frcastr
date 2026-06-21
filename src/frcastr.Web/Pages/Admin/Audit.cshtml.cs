using frcastr.Core.Entities;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Pages.Admin;

[Authorize(Roles = "Administrator")]
public class AuditModel(ApplicationDbContext db) : PageModel
{
    public List<AuditLog> Logs { get; private set; } = [];
    public int CurrentPage { get; private set; } = 1;
    public int TotalPages { get; private set; } = 1;
    public string? FilterEvent { get; private set; }
    public string? FilterUser  { get; private set; }
    public List<string> EventTypes { get; private set; } = [];

    private const int PageSize = 50;

    public async Task OnGetAsync(
        int page = 1,
        string? eventType = null,
        string? user = null,
        CancellationToken ct = default)
    {
        CurrentPage = Math.Max(1, page);
        FilterEvent = eventType;
        FilterUser  = user;

        EventTypes = await db.AuditLogs
            .Select(a => a.EventType)
            .Distinct()
            .OrderBy(e => e)
            .ToListAsync(ct);

        var query = db.AuditLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(a => a.EventType == eventType);
        if (!string.IsNullOrWhiteSpace(user))
            query = query.Where(a => a.UserName == user || a.UserId == user);

        var total = await query.CountAsync(ct);
        TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
        CurrentPage = Math.Min(CurrentPage, TotalPages);

        Logs = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}
