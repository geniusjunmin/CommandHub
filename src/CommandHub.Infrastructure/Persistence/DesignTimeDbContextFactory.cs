using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CommandHub.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CommandHubDbContext>
{
    public CommandHubDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CommandHubDbContext>()
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection") ?? "Host=localhost;Port=5432;Database=commandhub;Username=commandhub")
            .Options;
        return new(options);
    }
}
