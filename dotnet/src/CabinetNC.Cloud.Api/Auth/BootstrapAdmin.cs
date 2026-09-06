using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CabinetNC.Cloud.Api.Auth;

public static class BootstrapAdmin
{
    public static async Task MigrateAndSeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<CloudApiOptions>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<UserEntity>>();

        var values = new[]
        {
            options.BootstrapTenant,
            options.BootstrapAdminEmail,
            options.BootstrapAdminPassword,
        };
        var hasBootstrap = !values.All(string.IsNullOrWhiteSpace);
        if (hasBootstrap && values.Any(string.IsNullOrWhiteSpace))
            throw new InvalidOperationException(
                $"{CloudApiOptions.BootstrapTenantKey}, {CloudApiOptions.BootstrapAdminEmailKey}, and " +
                $"{CloudApiOptions.BootstrapAdminPasswordKey} must be supplied together.");
        if (hasBootstrap && options.BootstrapAdminPassword!.Length is
            < CloudApiOptions.MinBootstrapPasswordLength or
            > CloudApiOptions.MaxBootstrapPasswordLength)
            throw new InvalidOperationException(
                $"{CloudApiOptions.BootstrapAdminPasswordKey} must be between " +
                $"{CloudApiOptions.MinBootstrapPasswordLength} and " +
                $"{CloudApiOptions.MaxBootstrapPasswordLength} characters.");

        var tenantName = hasBootstrap ? NormalizeTenant(options.BootstrapTenant!) : null;
        var email = hasBootstrap ? NormalizeEmail(options.BootstrapAdminEmail!) : null;
        if (hasBootstrap && tenantName!.Length is 0 or > 200)
            throw new InvalidOperationException(
                $"{CloudApiOptions.BootstrapTenantKey} must be between 1 and 200 characters.");
        if (hasBootstrap
            && (email!.Length is 0 or > 320 || !email.Contains('@', StringComparison.Ordinal)))
            throw new InvalidOperationException($"{CloudApiOptions.BootstrapAdminEmailKey} is not a valid email address.");

        // Validate every bootstrap setting before touching the schema. Migrations still run when
        // bootstrap is intentionally omitted.
        await db.Database.MigrateAsync(ct);
        if (!hasBootstrap)
            return;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // One PostgreSQL-wide transaction lock keeps two API replicas starting together from both
        // observing an empty database and creating duplicate bootstrap tenants/users.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(1128352846)",
            ct);
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Name == tenantName!, ct);
        if (tenant is null)
        {
            tenant = new TenantEntity
            {
                Id = Guid.CreateVersion7(),
                Name = tenantName!,
                CreatedAtUtc = clock.GetUtcNow(),
            };
            db.Tenants.Add(tenant);
        }

        if (!await db.Users.AnyAsync(u => u.TenantId == tenant.Id && u.Email == email!, ct))
        {
            var user = new UserEntity
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenant.Id,
                Email = email!,
                PasswordHash = "",
                Role = "admin",
                IsActive = true,
                CreatedAtUtc = clock.GetUtcNow(),
            };
            user.PasswordHash = hasher.HashPassword(user, options.BootstrapAdminPassword!);
            db.Users.Add(user);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    public static string NormalizeTenant(string tenant) => tenant.Trim().ToLowerInvariant();
}
