using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Auth.Infrastructure.Persistence;

/// <summary>
/// Lets <c>dotnet ef</c> build the context without running the host.
/// The connection string is a local placeholder (no secret); it is never used to connect when
/// only generating migrations.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=auth;Username=auth")
            .UseOpenIddict()
            .Options;
        return new AuthDbContext(options);
    }
}
