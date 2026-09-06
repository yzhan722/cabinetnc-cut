using CabinetNC.Cloud.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;

namespace CabinetNC.Cloud.Infrastructure;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<DeviceEntity> Devices => Set<DeviceEntity>();
    public DbSet<RefreshTokenEntity> RefreshTokens => Set<RefreshTokenEntity>();
    public DbSet<ComputeJobEntity> ComputeJobs => Set<ComputeJobEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantEntity>(e =>
        {
            e.ToTable("Tenants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<UserEntity>(e =>
        {
            e.ToTable("Users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(500).IsRequired();
            e.Property(x => x.Role).HasMaxLength(50).IsRequired();
            e.HasIndex(x => new { x.TenantId, x.Email }).IsUnique();
        });

        modelBuilder.Entity<DeviceEntity>(e =>
        {
            e.ToTable("Devices");
            e.HasKey(x => x.Id);
            e.Property(x => x.DeviceKey).HasMaxLength(100).IsRequired();
            e.Property(x => x.DeviceName).HasMaxLength(200);
            e.HasIndex(x => new { x.TenantId, x.DeviceKey }).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<RefreshTokenEntity>(e =>
        {
            e.ToTable("RefreshTokens");
            e.HasKey(x => x.Id);
            e.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.FamilyId);
            e.HasIndex(x => new { x.DeviceId, x.RevokedAtUtc });
        });

        modelBuilder.Entity<ComputeJobEntity>(e =>
        {
            e.ToTable("ComputeJobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.JobType).HasMaxLength(50).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(200).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            e.Property(x => x.InputObjectKey).HasMaxLength(500).IsRequired();
            e.Property(x => x.InputSha256).HasMaxLength(64).IsRequired();
            e.Property(x => x.ResultObjectKey).HasMaxLength(500);
            e.Property(x => x.ResultSha256).HasMaxLength(64);
            e.Property(x => x.EngineVersion).HasMaxLength(100);
            e.Property(x => x.LockedBy).HasMaxLength(200);
            e.Property(x => x.ErrorCode).HasMaxLength(100);
            e.Property(x => x.ErrorMessage).HasMaxLength(2000);
            e.HasIndex(x => new { x.TenantId, x.UserId, x.IdempotencyKey }).IsUnique();
            // Claim scan: status + lease expiry, oldest first.
            e.HasIndex(x => new { x.Status, x.LockedUntilUtc, x.CreatedAtUtc });
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
        });

        modelBuilder.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("AuditEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.CorrelationId).HasMaxLength(100);
            e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.TenantId, x.CreatedAtUtc });
            e.HasIndex(x => x.JobId);
        });
    }
}
