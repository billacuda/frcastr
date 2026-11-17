using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace frcastr.Infrastructure.Data;

public class ApplicationDbContextFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer("Server=.;Database=frcastr;Integrated Security=true;TrustServerCertificate=true")  // DevSkim: ignore DS114352 -- design-time only, local developer database
            .Options;
        return new ApplicationDbContext(options);
    }
}
