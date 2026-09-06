using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CabinetNC.Cloud.Worker;

/// <summary>Polls the queue with one DI scope (one DbContext) per iteration and backs off when idle or failing.</summary>
public sealed class NestJobWorkerHost(
    IServiceProvider services,
    WorkerOptions options,
    ILogger<NestJobWorkerHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker {WorkerId} started; lease {Lease}, poll {Poll}", options.WorkerId, options.LeaseDuration, options.PollInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = options.PollInterval;
            try
            {
                await using var scope = services.CreateAsyncScope();
                var executor = scope.ServiceProvider.GetRequiredService<NestJobExecutor>();
                if (await executor.ExecuteOneAsync(stoppingToken))
                    delay = TimeSpan.Zero;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Database or object store unreachable: keep the process alive and keep trying.
                logger.LogError("Worker loop failed with {ExceptionType}; retrying in {Backoff}", ex.GetType().FullName, options.ErrorBackoff);
                delay = options.ErrorBackoff;
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        logger.LogInformation("Worker {WorkerId} stopped", options.WorkerId);
    }
}
