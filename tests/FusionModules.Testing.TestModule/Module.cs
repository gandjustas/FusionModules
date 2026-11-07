using FusionModules;
using FusionModules.Testing.TestModule;
using Microsoft.AspNetCore.Mvc;

[assembly: HostingStartup(typeof(Module))]

namespace FusionModules.Testing.TestModule;

/// <summary>
/// The test host's module: a minimal API, a route for any method, an internal controller and a worker.
/// </summary>
/// <remarks>
/// A project of its own rather than a class in the test assembly: while the hosting startups run,
/// WebApplicationFactory's host takes the entry assembly — the test runner — for the application, so
/// a HostingStartup attribute there would run in every host the tests start, named or not.
/// </remarks>
sealed class Module : ModuleBase
{
    protected override void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<WorkerLog>();
        builder.Services.AddHostedService<Worker>();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Module).Assembly)
            .AllowInternalControllers();
    }

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/module/ping", () => "pong");
            endpoints.MapGet("/module/worker", (WorkerLog log) => log.Started ? "started" : "not started");
            endpoints.Map("/module/any", () => "any method");
            endpoints.MapControllers();
        });
}

/// <summary>An attribute route, which is stored without its leading slash.</summary>
[ApiController]
sealed class ItemsController : ControllerBase
{
    [HttpGet("module/items/{id:int}")]
    public int Get(int id) => id;
}

/// <summary>Whether the worker started.</summary>
sealed class WorkerLog
{
    private int _started;

    public bool Started => Volatile.Read(ref _started) == 1;

    public void Start() => Volatile.Write(ref _started, 1);
}

/// <summary>
/// Stands for a worker that connects to something in <c>StartAsync</c>; here it only says that it ran.
/// </summary>
sealed class Worker(WorkerLog log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        log.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
