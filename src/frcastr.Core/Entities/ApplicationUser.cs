using Microsoft.AspNetCore.Identity;

namespace frcastr.Core.Entities;

public class ApplicationUser : IdentityUser
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
