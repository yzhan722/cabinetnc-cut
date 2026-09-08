using CabinetNC.Cloud.Infrastructure;
using CabinetNC.Cloud.Infrastructure.Storage;
using CabinetNC.Compute.Core.Cam;
using CabinetNC.Compute.Core.Nesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CabinetNC.Cloud.Worker;

public static class Program
{
    public static void Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        // The poll loop issues SQL every second; per-command SQL logging would drown the job events.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

        // Secrets are environment-only, like the API. The worker never runs migrations; the API owns the schema.
        builder.Services.AddSingleton(_ => WorkerOptions.FromEnvironment());
        builder.Services.AddCloudPersistence(_ => RequireEnvironment("CABINETNC_DB_CONNECTION"));
        builder.Services.AddCloudObjectStore(_ => ObjectStoreOptions.FromEnvironment());
        builder.Services.AddSingleton<INestingRunner, NestingRunner>();
        builder.Services.AddSingleton<IOperationsRunner, OperationsRunner>();
        builder.Services.AddSingleton<IPostProcessorRunner, PostProcessorRunner>();
        builder.Services.AddScoped<NestJobExecutor>();
        builder.Services.AddHostedService<NestJobWorkerHost>();

        builder.Build().Run();
    }

    static string RequireEnvironment(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"{variable} is required.");
        return value;
    }
}
