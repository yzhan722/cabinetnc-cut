using CabinetNC.Cloud.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CabinetNC.Cloud.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the PostgreSQL DbContext and the job repository. The API and the worker both call this.</summary>
    public static IServiceCollection AddCloudPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<CloudDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IJobRepository, PostgresJobRepository>();
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
