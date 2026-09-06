using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CabinetNC.Cloud.Infrastructure.Tests;

/// <summary>
/// One PostgreSQL per test class (external via CABINETNC_TEST_PG, else a Testcontainers instance); tables
/// truncated between tests. Creates the schema with EnsureCreated; derive with <see cref="CreateSchema"/>
/// = false when the system under test applies migrations itself.
/// </summary>
public class PostgresFixture : IAsyncLifetime
{
    public const string Image = "postgres:17-alpine";

    PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    protected virtual bool CreateSchema => true;

    public async Task InitializeAsync()
    {
        if (!PostgresAvailability.IsAvailable)
            return;

        var external = Environment.GetEnvironmentVariable(PostgresAvailability.ConnectionStringVariable);
        if (!string.IsNullOrWhiteSpace(external))
        {
            ConnectionString = external;
        }
        else
        {
            _container = new PostgreSqlBuilder(Image).Build();
            await _container.StartAsync();
            ConnectionString = _container.GetConnectionString();
        }

        if (CreateSchema)
        {
            await using var db = CreateContext();
            await db.Database.EnsureCreatedAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    public CloudDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<CloudDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>Empties every application table; the EF migrations history table is left alone.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateContext();
        var schemaExists = await db.Database
            .SqlQueryRaw<bool>("SELECT to_regclass('\"Tenants\"') IS NOT NULL AS \"Value\"")
            .SingleAsync();
        if (!schemaExists)
            return;
        await db.Database.ExecuteSqlRawAsync(
            """TRUNCATE "ComputeJobs", "AuditEvents", "RefreshTokens", "Devices", "Users", "Tenants" RESTART IDENTITY CASCADE""");
    }
}

/// <summary>Bare database for hosts that run <c>Database.Migrate()</c> on startup (the Cloud API).</summary>
public sealed class MigratedByAppPostgresFixture : PostgresFixture
{
    protected override bool CreateSchema => false;
}

/// <summary>Deterministic clock so lease expiry is tested without sleeping.</summary>
public sealed class ManualClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
