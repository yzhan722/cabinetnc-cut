using CabinetNC.Cloud.Infrastructure.Jobs;
using CabinetNC.Cloud.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CabinetNC.Cloud.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the PostgreSQL DbContext and the job repository. The API and the worker both call this.</summary>
    public static IServiceCollection AddCloudPersistence(this IServiceCollection services, string connectionString) =>
        services.AddCloudPersistence(_ => connectionString);

    /// <summary>
    /// Deferred variant for hosts whose final configuration is assembled at build time (including
    /// WebApplicationFactory). The connection string is resolved when a DbContext scope is created.
    /// </summary>
    public static IServiceCollection AddCloudPersistence(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString)
    {
        services.AddDbContext<CloudDbContext>((provider, options) =>
            options.UseNpgsql(connectionString(provider)));
        services.AddScoped<IJobRepository, PostgresJobRepository>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }

    /// <summary>Registers the MinIO-backed <see cref="IObjectStore"/> as a singleton (the client is thread-safe and pools connections).</summary>
    public static IServiceCollection AddCloudObjectStore(
        this IServiceCollection services,
        ObjectStoreOptions options) =>
        services.AddCloudObjectStore(_ => options);

    public static IServiceCollection AddCloudObjectStore(
        this IServiceCollection services,
        Func<IServiceProvider, ObjectStoreOptions> options)
    {
        services.AddSingleton<IObjectStore>(provider =>
            new MinioObjectStore(options(provider)));
        return services;
    }
}
