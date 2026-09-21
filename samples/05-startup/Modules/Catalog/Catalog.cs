using Startup.Contracts;

namespace CatalogModule;

/// <summary>What the first request would have missed if the seed had not already run.</summary>
sealed class Catalog
{
    private readonly List<string> _items = [];

    public bool Seeded { get; private set; }

    public IReadOnlyList<string> Items => _items;

    public void Seed(IEnumerable<string> items)
    {
        _items.AddRange(items);
        Seeded = true;
    }
}

/// <summary>
/// A worker, and the honest comparison with the gate.
/// </summary>
/// <remarks>
/// <para>
/// A module's hosted services are registered <i>before</i> <c>GenericWebHostService</c>, because a
/// hosting startup configures the builder before the web host adds its own — so this
/// <c>StartAsync</c> really does run with the port still closed, and blocking here delays the
/// first request rather than racing it. The test asserts that registration order rather than
/// asking you to believe it.
/// </para>
/// <para>
/// What it does not give you is ordering. Hosted services start in registration order, which is
/// <c>HOSTINGSTARTUPASSEMBLIES</c> order, so work that has to precede every other module's cannot
/// be expressed from inside one module. That, and not a race, is why the seed above is a
/// descriptor the host runs.
/// </para>
/// </remarks>
sealed class CatalogRefresh(StartupLog log) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        log.Record("worker:start");
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

/// <summary>
/// The other way to run work before the port opens, for comparison.
/// </summary>
/// <remarks>
/// <see cref="IHostedLifecycleService.StartingAsync"/> runs before any hosted service starts, by
/// specification rather than by registration order — which is the difference from the worker
/// above, whose timing is a fact about how the builder happens to be assembled. It costs the same
/// ordering, and trips MOD0008 the same way.
/// </remarks>
sealed class WarmCache(StartupLog log) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        log.Record("lifecycle:starting");
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
