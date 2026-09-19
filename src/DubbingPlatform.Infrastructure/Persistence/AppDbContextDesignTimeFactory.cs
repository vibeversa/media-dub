using EFCore.NamingConventions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DubbingPlatform.Infrastructure.Persistence;

/// <summary>
/// Design-time factory for EF Core tooling (<c>dotnet ef migrations</c>).
/// Reads the connection string from the <c>ConnectionStrings__Default</c> environment
/// variable only; never hardcodes secrets. Fails fast with a message naming
/// <c>ConnectionStrings__Default</c> when the variable is missing or empty.
/// The returned options intentionally omit the tenant-session interceptor:
/// schema operations run without a tenant scope (maintenance context).
/// </summary>
public sealed class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string ConnectionStringEnvironmentVariable = "ConnectionStrings__Default";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"Missing required connection string '{ConnectionStringEnvironmentVariable}'. " +
                $"Set {ConnectionStringEnvironmentVariable}=\"Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing\" " +
                "before running EF Core commands.");
        }

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;

        return new AppDbContext(options);
    }
}
