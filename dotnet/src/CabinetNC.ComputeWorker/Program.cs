using System.Net;
using CabinetNC.Compute.Contracts;
using CabinetNC.Compute.Core.Nesting;
using CabinetNC.ComputeWorker.Services;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenNamedPipe(WorkerPipes.Name, o =>
    {
        o.Protocols = HttpProtocols.Http2;
    });
});

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});

builder.Services.AddGrpc();
builder.Services.AddSingleton<INestingRunner, NestingRunner>();

var app = builder.Build();
app.MapGrpcService<WorkerHealthService>();
app.MapGrpcService<NestingServiceImpl>();
app.MapGrpcService<OperationsServiceImpl>();
app.MapGrpcService<PostProcessorServiceImpl>();
app.MapGet("/", () => $"CabinetNC ComputeWorker {WorkerHealthService.WorkerVersion} pipe={WorkerPipes.Name}");

Console.WriteLine($"CabinetNC ComputeWorker listening on Named Pipe '{WorkerPipes.Name}'");
app.Run();
