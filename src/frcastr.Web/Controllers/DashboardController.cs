using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/dashboard")]
public class DashboardController(
    ApplicationDbContext db,
    IAuditService audit) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    [HttpGet("layout")]
    [AllowAnonymous]
    public async Task<IActionResult> GetLayout(string dashboard = "Default", CancellationToken ct = default)
    {
        var layout = await db.DashboardLayouts
            .Where(l => l.Name == dashboard)
            .OrderBy(l => l.OwnerId == null ? 0 : 1)
            .FirstOrDefaultAsync(ct);

        return Ok(new
        {
            desktop         = layout?.LayoutJson,
            mobile          = layout?.LayoutJsonMobile,
            mobileTwoColumn = layout?.MobileTwoColumn ?? false
        });
    }

    /// <summary>
    /// Per-dashboard phone layout: one column, or two so that widgets placed side by side on the
    /// desktop stay side by side. Separate from SaveLayout because the phone arrangement itself is
    /// derived rather than saved — this is the one thing about it worth persisting.
    /// </summary>
    [HttpPost("mobile-columns")]
    [Authorize]
    public async Task<IActionResult> SetMobileColumns(
        bool twoColumn, string dashboard = "Default", CancellationToken ct = default)
    {
        // Resolved exactly the way GetLayout resolves it, so the flag lands on the row the
        // dashboard actually reads back rather than on a shadowed one.
        var layout = await db.DashboardLayouts
            .Where(l => l.Name == dashboard)
            .OrderBy(l => l.OwnerId == null ? 0 : 1)
            .FirstOrDefaultAsync(ct);

        if (layout is null)
        {
            layout = new DashboardLayout
            {
                OwnerId   = null,
                Name      = dashboard,
                UpdatedAt = DateTime.UtcNow
            };
            db.DashboardLayouts.Add(layout);
        }

        layout.MobileTwoColumn = twoColumn;
        layout.UpdatedAt       = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Dashboard.MobileColumnsChanged",
            entityType: "DashboardLayout", entityName: dashboard,
            newValue: twoColumn ? "2" : "1", ct: ct);

        return NoContent();
    }

    [HttpPost("layout")]
    [Authorize]
    public async Task<IActionResult> SaveLayout(
        [FromBody] LayoutDto dto, string dashboard = "Default", CancellationToken ct = default)
    {
        var existing = await db.DashboardLayouts
            .FirstOrDefaultAsync(l => l.OwnerId == null && l.Name == dashboard, ct);

        if (existing is null)
        {
            db.DashboardLayouts.Add(new DashboardLayout
            {
                OwnerId          = null,
                Name             = dashboard,
                LayoutJson       = dto.Desktop ?? "[]",
                LayoutJsonMobile = dto.Mobile  ?? "[]",
                UpdatedAt        = DateTime.UtcNow
            });
        }
        else
        {
            existing.LayoutJson       = dto.Desktop ?? existing.LayoutJson;
            existing.LayoutJsonMobile = dto.Mobile  ?? existing.LayoutJsonMobile;
            existing.UpdatedAt        = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("widgets")]
    [AllowAnonymous]
    public async Task<IActionResult> GetWidgets(string dashboard = "Default", CancellationToken ct = default)
    {
        var userId = GetOptionalUserId();

        var widgets = await db.WidgetDefinitions
            .Where(w => w.IsVisible
                     && w.DashboardName == dashboard
                     && (userId == null || w.OwnerId == null || w.OwnerId == userId))
            .OrderBy(w => w.SortOrder)
            .ToListAsync(ct);

        return Ok(widgets.Select(w => new
        {
            id        = w.Id,
            type      = (int)w.Type,
            title     = w.Title,
            config    = w.Config,
            gridX     = w.GridX,
            gridY     = w.GridY,
            gridW     = w.GridW,
            gridH     = w.GridH,
            sortOrder = w.SortOrder,
            isVisible = w.IsVisible,
            ownerId   = w.OwnerId
        }));
    }

    [HttpGet("names")]
    [AllowAnonymous]
    public async Task<IActionResult> GetNames(CancellationToken ct = default)
    {
        var userId = GetOptionalUserId();

        var names = await db.WidgetDefinitions
            .Where(w => userId == null || w.OwnerId == null || w.OwnerId == userId)
            .Select(w => w.DashboardName)
            .Distinct()
            .OrderBy(n => n)
            .ToListAsync(ct);

        return Ok(names);
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreateDashboard(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest("Name is required.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var exists = await db.DashboardLayouts
            .AnyAsync(l => l.Name == name && (l.OwnerId == userId || l.OwnerId == null), ct);

        if (!exists)
        {
            db.DashboardLayouts.Add(new DashboardLayout
            {
                OwnerId   = userId,
                Name      = name,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        return NoContent();
    }

    [HttpPost("copy")]
    [Authorize]
    public async Task<IActionResult> CopyDashboard(string from, string to, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return BadRequest("Source and destination names are required.");

        if (from == to)
            return BadRequest("Source and destination names must differ.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var sourceLayout = await db.DashboardLayouts
            .FirstOrDefaultAsync(l => l.Name == from && (l.OwnerId == null || l.OwnerId == userId), ct);

        var newLayout = new DashboardLayout
        {
            OwnerId          = userId,
            Name             = to,
            LayoutJson       = sourceLayout?.LayoutJson ?? "[]",
            LayoutJsonMobile = sourceLayout?.LayoutJsonMobile ?? "[]",
            MobileTwoColumn  = sourceLayout?.MobileTwoColumn ?? false,
            UpdatedAt        = DateTime.UtcNow
        };
        db.DashboardLayouts.Add(newLayout);

        var sourceWidgets = await db.WidgetDefinitions
            .Where(w => w.DashboardName == from && (w.OwnerId == null || w.OwnerId == userId))
            .ToListAsync(ct);

        foreach (var w in sourceWidgets)
        {
            db.WidgetDefinitions.Add(new WidgetDefinition
            {
                Type          = w.Type,
                Title         = w.Title,
                Config        = w.Config,
                GridX         = w.GridX,
                GridY         = w.GridY,
                GridW         = w.GridW,
                GridH         = w.GridH,
                SortOrder     = w.SortOrder,
                IsVisible     = w.IsVisible,
                OwnerId       = userId,
                DashboardName = to
            });
        }

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Dashboard.Copied",
            entityType: "DashboardLayout", entityName: to,
            newValue: $"Copied from '{from}'", ct: ct);

        return NoContent();
    }

    [HttpDelete]
    [Authorize]
    public async Task<IActionResult> DeleteDashboard(string name, CancellationToken ct = default)
    {
        if (name == "Default")
            return BadRequest("Cannot delete the Default dashboard.");

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

        var layout = await db.DashboardLayouts
            .FirstOrDefaultAsync(l => l.Name == name && l.OwnerId == userId, ct);

        if (layout is not null)
        {
            db.DashboardLayouts.Remove(layout);
        }

        var widgets = await db.WidgetDefinitions
            .Where(w => w.DashboardName == name && (w.OwnerId == userId || w.OwnerId == null))
            .ToListAsync(ct);

        db.WidgetDefinitions.RemoveRange(widgets);
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPatch("widgets/{id}/config")]
    [Authorize]
    public async Task<IActionResult> UpdateWidgetConfig(int id,
        [FromBody] JsonElement config, CancellationToken ct = default)
    {
        var widget = await db.WidgetDefinitions.FindAsync([id], ct);
        if (widget is null) return NotFound();

        widget.Config = config.GetRawText();
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Widget.ConfigUpdated",
            entityType: "WidgetDefinition", entityId: id.ToString(), entityName: widget.Title,
            newValue: widget.Config, ct: ct);

        return NoContent();
    }

    private string? GetOptionalUserId()
    {
        if (User.Identity?.IsAuthenticated != true) return null;
        return User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }

    public record LayoutDto(string? Desktop, string? Mobile);
}
