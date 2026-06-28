using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using frcastr.Core.Entities;
using frcastr.Core.Enums;
using frcastr.Core.Interfaces;
using frcastr.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace frcastr.Web.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = "AdministratorOnly")]
public class AdminController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IAuditService audit,
    IWebHostEnvironment webEnv,
    ISettingsService settings,
    IDataSourceTestService dataSourceTester,
    IWeatherDataService weatherData) : ControllerBase
{
    // ── Channels ──────────────────────────────────────────────────────────────

    [HttpGet("channels")]
    public async Task<IActionResult> GetChannels(CancellationToken ct)
    {
        var readings = await weatherData.GetCurrentReadingsAsync(ct);
        var result = readings.Values
            .OrderBy(r => r.ChannelName)
            .Select(r => new
            {
                name        = r.ChannelName,
                value       = (double?)r.Value,
                unit        = r.Unit,
                lastUpdated = r.Timestamp
            });
        return Ok(result);
    }

    // ── Data Sources ──────────────────────────────────────────────────────────

    [HttpGet("datasources")]
    public async Task<IActionResult> GetDataSources(CancellationToken ct)
        => Ok(await db.DataSources.OrderBy(s => s.Name).ToListAsync(ct));

    [HttpPost("datasources")]
    public async Task<IActionResult> CreateDataSource([FromBody] DataSourceDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name required");
        if (await db.DataSources.AnyAsync(s => s.Name == dto.Name, ct))
            return Conflict("Name already in use");

        var source = new DataSource
        {
            Name = dto.Name,
            Type = dto.Type,
            IsEnabled = dto.IsEnabled,
            PollIntervalSeconds = dto.PollIntervalSeconds > 0 ? dto.PollIntervalSeconds : 300,
            Url = string.IsNullOrWhiteSpace(dto.Url) ? null : dto.Url.Trim(),
            Config = dto.Config
        };
        db.DataSources.Add(source);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DataSource.Created",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: source.Id.ToString(), entityName: source.Name, ct: ct);

        return CreatedAtAction(nameof(GetDataSources), new { }, source);
    }

    [HttpPut("datasources/{id:int}")]
    public async Task<IActionResult> UpdateDataSource(int id, [FromBody] DataSourceDto dto, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();

        source.Name = dto.Name ?? source.Name;
        source.Type = dto.Type;
        source.IsEnabled = dto.IsEnabled;
        source.PollIntervalSeconds = dto.PollIntervalSeconds > 0 ? dto.PollIntervalSeconds : source.PollIntervalSeconds;
        source.Url = string.IsNullOrWhiteSpace(dto.Url) ? null : dto.Url.Trim();
        source.Config = dto.Config ?? source.Config;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DataSource.Updated",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: id.ToString(), entityName: source.Name, ct: ct);
        return NoContent();
    }

    [HttpDelete("datasources/{id:int}")]
    public async Task<IActionResult> DeleteDataSource(int id, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();
        db.DataSources.Remove(source);
        await db.SaveChangesAsync(ct);
        await audit.LogAsync("DataSource.Deleted",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: id.ToString(), entityName: source.Name, ct: ct);
        return NoContent();
    }

    [HttpPost("datasources/{id:int}/toggle")]
    public async Task<IActionResult> ToggleDataSource(int id, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();
        source.IsEnabled = !source.IsEnabled;
        await db.SaveChangesAsync(ct);
        return Ok(new { source.IsEnabled });
    }

    [HttpPost("datasources/{id:int}/test")]
    public async Task<IActionResult> TestDataSource(int id, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await dataSourceTester.TestAsync(id, timeoutCts.Token);
        return Ok(result);
    }

    [HttpPost("datasources/{id:int}/rotate-key")]
    public async Task<IActionResult> RotateKey(int id, CancellationToken ct)
    {
        var source = await db.DataSources.FindAsync([id], ct);
        if (source is null) return NotFound();
        if (source.Type != DataSourceType.Push)
            return BadRequest("Key rotation is only supported for Push sources");

        var keyBytes = new byte[32];
        RandomNumberGenerator.Fill(keyBytes);
        var plainKey = Convert.ToBase64String(keyBytes);
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(plainKey)));

        Dictionary<string, JsonElement>? cfg = null;
        try { cfg = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(source.Config ?? "{}"); } catch { }
        cfg ??= [];
        cfg["apiKeyHash"] = JsonSerializer.SerializeToElement(hash);
        source.Config = JsonSerializer.Serialize(cfg);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("DataSource.KeyRotated",
            userId: UserId(), userName: UserName(),
            entityType: "DataSource", entityId: id.ToString(), entityName: source.Name, ct: ct);

        return Ok(new { plainKey });
    }

    // ── Branding ──────────────────────────────────────────────────────────────

    [HttpPost("branding/logo")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadLogo(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("No file provided.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        var allowed = new[] { ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp" };
        if (!allowed.Contains(ext)) return BadRequest("Invalid file type.");

        var uploadsDir = Path.Combine(webEnv.ContentRootPath, "uploads");
        Directory.CreateDirectory(uploadsDir);

        var fileName = "logo" + ext;
        var filePath = Path.Combine(uploadsDir, fileName);
        await using var stream = System.IO.File.Create(filePath);
        await file.CopyToAsync(stream, ct);

        var logoUrl = "/uploads/" + fileName;
        await settings.UpsertAsync("Branding.Logo", logoUrl, modifiedBy: UserName(), ct: ct);

        await audit.LogAsync("Branding.LogoUploaded",
            userId: UserId(), userName: UserName(),
            entityType: "Setting", entityId: "Branding.Logo", entityName: "Logo", newValue: logoUrl, ct: ct);

        return Ok(new { url = logoUrl });
    }

    [HttpDelete("branding/logo")]
    public async Task<IActionResult> DeleteLogo(CancellationToken ct)
    {
        var logoUrl = await settings.GetAsync("Branding.Logo", ct);
        if (!string.IsNullOrEmpty(logoUrl))
        {
            var filePath = Path.Combine(webEnv.ContentRootPath, logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
        }
        await settings.UpsertAsync("Branding.Logo", "", modifiedBy: UserName(), ct: ct);
        return NoContent();
    }

    // ── Widgets ───────────────────────────────────────────────────────────────

    [HttpGet("widgets")]
    public async Task<IActionResult> GetWidgets(CancellationToken ct)
        => Ok(await db.WidgetDefinitions.OrderBy(w => w.SortOrder).ToListAsync(ct));

    [HttpPost("widgets")]
    public async Task<IActionResult> CreateWidget([FromBody] WidgetDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest("Title required");

        var maxSort = await db.WidgetDefinitions.MaxAsync(w => (int?)w.SortOrder, ct) ?? 0;
        var widget = new WidgetDefinition
        {
            Type          = dto.Type,
            Title         = dto.Title,
            Config        = dto.Config,
            GridX         = dto.GridX,
            GridY         = dto.GridY,
            GridW         = dto.GridW > 0 ? dto.GridW : 4,
            GridH         = dto.GridH > 0 ? dto.GridH : 3,
            SortOrder     = maxSort + 10,
            IsVisible     = dto.IsVisible,
            DashboardName = string.IsNullOrWhiteSpace(dto.DashboardName) ? "Default" : dto.DashboardName
        };
        db.WidgetDefinitions.Add(widget);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetWidgets), new { }, widget);
    }

    [HttpPut("widgets/{id:int}")]
    public async Task<IActionResult> UpdateWidget(int id, [FromBody] WidgetDto dto, CancellationToken ct)
    {
        var widget = await db.WidgetDefinitions.FindAsync([id], ct);
        if (widget is null) return NotFound();
        widget.Type          = dto.Type;
        widget.Title         = dto.Title ?? widget.Title;
        widget.Config        = dto.Config ?? widget.Config;
        widget.GridX         = dto.GridX;
        widget.GridY         = dto.GridY;
        widget.GridW         = dto.GridW > 0 ? dto.GridW : widget.GridW;
        widget.GridH         = dto.GridH > 0 ? dto.GridH : widget.GridH;
        widget.IsVisible     = dto.IsVisible;
        widget.SortOrder     = dto.SortOrder;
        widget.DashboardName = string.IsNullOrWhiteSpace(dto.DashboardName) ? widget.DashboardName : dto.DashboardName;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("widgets/{id:int}")]
    public async Task<IActionResult> DeleteWidget(int id, CancellationToken ct)
    {
        var widget = await db.WidgetDefinitions.FindAsync([id], ct);
        if (widget is null) return NotFound();
        db.WidgetDefinitions.Remove(widget);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    [HttpGet("webhooks")]
    public async Task<IActionResult> GetWebhooks(CancellationToken ct)
        => Ok(await db.WebhookAlerts.OrderBy(w => w.Name).ToListAsync(ct));

    [HttpPost("webhooks")]
    public async Task<IActionResult> CreateWebhook([FromBody] WebhookDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Channel) ||
            string.IsNullOrWhiteSpace(dto.WebhookUrl)) return BadRequest("Name, Channel, and WebhookUrl required");

        var hook = new WebhookAlert
        {
            Name = dto.Name,
            Channel = dto.Channel,
            Operator = dto.Operator,
            Threshold = dto.Threshold,
            Unit = dto.Unit ?? "",
            WebhookUrl = dto.WebhookUrl,
            IsEnabled = dto.IsEnabled
        };
        db.WebhookAlerts.Add(hook);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(GetWebhooks), new { }, hook);
    }

    [HttpPut("webhooks/{id:int}")]
    public async Task<IActionResult> UpdateWebhook(int id, [FromBody] WebhookDto dto, CancellationToken ct)
    {
        var hook = await db.WebhookAlerts.FindAsync([id], ct);
        if (hook is null) return NotFound();
        hook.Name = dto.Name ?? hook.Name;
        hook.Channel = dto.Channel ?? hook.Channel;
        hook.Operator = dto.Operator;
        hook.Threshold = dto.Threshold;
        hook.Unit = dto.Unit ?? hook.Unit;
        hook.WebhookUrl = dto.WebhookUrl ?? hook.WebhookUrl;
        hook.IsEnabled = dto.IsEnabled;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("webhooks/{id:int}")]
    public async Task<IActionResult> DeleteWebhook(int id, CancellationToken ct)
    {
        var hook = await db.WebhookAlerts.FindAsync([id], ct);
        if (hook is null) return NotFound();
        db.WebhookAlerts.Remove(hook);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    [HttpGet("users")]
    public async Task<IActionResult> GetUsers(CancellationToken ct)
    {
        var users = await userManager.Users.ToListAsync(ct);
        var result = new List<object>();
        foreach (var u in users)
        {
            var roles = await userManager.GetRolesAsync(u);
            result.Add(new { u.Id, u.Email, u.UserName, u.CreatedAt, Roles = roles });
        }
        return Ok(result);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Email) || string.IsNullOrWhiteSpace(dto.Password))
            return BadRequest("Email and Password required");

        var user = new ApplicationUser
        {
            UserName = dto.Email,
            Email = dto.Email,
            EmailConfirmed = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await userManager.CreateAsync(user, dto.Password);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));

        if (dto.Roles is { Count: > 0 })
            foreach (var role in dto.Roles)
                await userManager.AddToRoleAsync(user, role);

        await audit.LogAsync("User.Created",
            userId: UserId(), userName: UserName(),
            entityType: "User", entityId: user.Id, entityName: user.Email);
        return Ok(new { user.Id, user.Email });
    }

    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(string id)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        if (user.Id == UserId()) return BadRequest("Cannot delete your own account");
        await userManager.DeleteAsync(user);
        await audit.LogAsync("User.Deleted",
            userId: UserId(), userName: UserName(),
            entityType: "User", entityId: id, entityName: user.Email);
        return NoContent();
    }

    [HttpPost("users/{id}/roles")]
    public async Task<IActionResult> SetUserRoles(string id, [FromBody] SetRolesDto dto)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        var existing = await userManager.GetRolesAsync(user);
        await userManager.RemoveFromRolesAsync(user, existing);
        if (dto.Roles is { Count: > 0 })
            await userManager.AddToRolesAsync(user, dto.Roles);
        return NoContent();
    }

    [HttpPost("users/{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, [FromBody] ResetPasswordDto dto)
    {
        var user = await userManager.FindByIdAsync(id);
        if (user is null) return NotFound();
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, token, dto.NewPassword);
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));
        await audit.LogAsync("User.PasswordReset",
            userId: UserId(), userName: UserName(),
            entityType: "User", entityId: id, entityName: user.Email);
        return NoContent();
    }

    // ── Roles ─────────────────────────────────────────────────────────────────

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles(CancellationToken ct)
    {
        var roles = await roleManager.Roles.ToListAsync(ct);
        var perms = await db.Permissions.ToListAsync(ct);
        var result = roles.Select(r => new
        {
            r.Id, r.Name,
            Permissions = perms.Where(p => p.RoleId == r.Id).ToList()
        });
        return Ok(result);
    }

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name required");
        var result = await roleManager.CreateAsync(new IdentityRole(dto.Name));
        if (!result.Succeeded)
            return BadRequest(result.Errors.Select(e => e.Description));
        return Ok(new { Name = dto.Name });
    }

    [HttpDelete("roles/{id}")]
    public async Task<IActionResult> DeleteRole(string id)
    {
        var role = await roleManager.FindByIdAsync(id);
        if (role is null) return NotFound();
        if (role.Name == "Administrator") return BadRequest("Cannot delete the Administrator role");
        await roleManager.DeleteAsync(role);
        await db.Permissions.Where(p => p.RoleId == id).ExecuteDeleteAsync();
        return NoContent();
    }

    [HttpPut("roles/{id}/permissions")]
    public async Task<IActionResult> SetPermissions(string id, [FromBody] SetPermissionsDto dto, CancellationToken ct)
    {
        var role = await roleManager.FindByIdAsync(id);
        if (role is null) return NotFound();

        await db.Permissions.Where(p => p.RoleId == id).ExecuteDeleteAsync(ct);

        if (dto.Permissions is { Count: > 0 })
        {
            var perms = dto.Permissions.Select(p => new Permission
            {
                RoleId = id,
                Resource = p.Resource,
                Action = p.Action,
                Name = $"{p.Resource}.{p.Action}"
            }).ToList();
            db.Permissions.AddRange(perms);
            await db.SaveChangesAsync(ct);
        }
        return NoContent();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string? UserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    private string? UserName() => User.Identity?.Name;

    // ── DTOs ──────────────────────────────────────────────────────────────────

    public record DataSourceDto(
        string? Name,
        DataSourceType Type,
        bool IsEnabled,
        int PollIntervalSeconds,
        string? Url,
        string? Config);

    public record WidgetDto(
        WidgetType Type,
        string? Title,
        string? Config,
        int GridX,
        int GridY,
        int GridW,
        int GridH,
        int SortOrder,
        bool IsVisible,
        string? DashboardName);

    public record WebhookDto(
        string? Name,
        string? Channel,
        AlertOperator Operator,
        decimal Threshold,
        string? Unit,
        string? WebhookUrl,
        bool IsEnabled);

    public record CreateUserDto(
        string? Email,
        string? Password,
        List<string>? Roles);

    public record SetRolesDto(List<string>? Roles);
    public record ResetPasswordDto(string NewPassword);
    public record CreateRoleDto(string? Name);
    public record PermissionRef(string Resource, string Action);
    public record SetPermissionsDto(List<PermissionRef>? Permissions);
}
