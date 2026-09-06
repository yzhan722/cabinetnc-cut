using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CabinetNC.Cloud.Infrastructure;

/// <summary>
/// Lets <c>dotnet ef</c> inspect the persistence model without booting the API or requiring an
/// unrelated JWT key. The connection string is still explicit and environment-only.
/// </summary>
public sealed class CloudDbContextFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args)
    {
        const string variable = "CABINETNC_DB_CONNECTION";
        var connectionString = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException($"{variable} is required for EF design-time commands.");

        return new CloudDbContext(
            new DbContextOptionsBuilder<CloudDbContext>()
                .UseNpgsql(connectionString)
                .Options);
    }
}
