using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/settings")]
[Authorize]
public class SettingsController(
    ApplicationDbContext db,
    ISettingsService settingsService,
    IAuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var all = await db.Settings.OrderBy(s => s.Key).ToListAsync(ct);
        return Ok(all);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert(
        [FromBody] UpsertRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Key) || req.Value is null) return BadRequest();

        var existing = await settingsService.GetAsync(req.Key, ct);
        await settingsService.UpsertAsync(req.Key, req.Value,
            modifiedBy: User.Identity?.Name, ct: ct);

        await audit.LogAsync("Settings.Updated",
            userId: User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            userName: User.Identity?.Name,
            entityType: "Setting", entityId: req.Key,
            oldValue: existing, newValue: req.Value, ct: ct);

        return NoContent();
    }

    [HttpDelete("{key}")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> Delete(string key, CancellationToken ct)
    {
        var setting = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (setting is null) return NotFound();
        if (setting.IsSystemSetting) return Forbid();

        db.Settings.Remove(setting);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("Settings.Deleted", entityType: "Setting", entityId: key, ct: ct);
        return NoContent();
    }

    public record UpsertRequest(string Key, string Value);
}
