using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Sample.Startup.Tests;

/// <summary>
/// Startup ordering, asserted rather than described.
/// </summary>
public class StartupOrderTests
{
    private const string CatalogModule = "CatalogModule";

    [Test]
    public async Task TheSeedHasRunBeforeTheFirstRequestIsServed(CancellationToken cancellationToken)
    {
        // Which is the whole claim. The endpoint is not asked to seed anything and does not check
        // whether seeding happened: by the time it can be reached, it has.
        using var factory = new ModularWebApplicationFactory(CatalogModule);
        var client = factory.CreateClient();

        await Assert.That(await client.GetFromJsonAsync<bool>("/catalog/seeded", cancellationToken)).IsTrue();
        await Assert.That(await client.GetFromJsonAsync<string[]>("/catalog", cancellationToken))
            .IsEquivalentTo(new[] { "hammer", "nails", "saw" });
    }

    [Test]
    public async Task TheGateRunsBeforeAnyHostedService(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(CatalogModule);
        var client = factory.CreateClient();

        var events = await client.GetFromJsonAsync<string[]>("/startup-log", cancellationToken) ?? [];

        // The gate precedes everything, because it is not a hosted service at all. Then every
        // StartingAsync, then every StartAsync in registration order.
        await Assert.That(events).IsEquivalentTo(new[]
        {
            "task:catalog-seed",
            "lifecycle:starting",
            "worker:start",
        });
    }

    [Test]
    public async Task AModulesWorkerIsRegisteredBeforeTheWebHost(CancellationToken cancellationToken)
    {
        // Worth asserting because it is the opposite of what the shape suggests, and it is the
        // whole timing argument. A hosting startup configures the builder before the web host adds
        // GenericWebHostService, so a module's worker starts before Kestrel binds — blocking in it
        // delays the first request rather than racing it.
        //
        // It is registration order, not a contract. StartingAsync is the contractual version, and
        // neither gives ordering between modules, which is why the seed is a descriptor instead.
        using var factory = new ModularWebApplicationFactory(CatalogModule);
        _ = factory.CreateClient();

        var hosted = factory.Services
            .GetServices<IHostedService>()
            .Select(service => service.GetType().Name)
            .ToList();

        // By name, because the module's worker is internal and this project cannot reference it.
        await Assert.That(hosted.IndexOf("CatalogRefresh")).IsGreaterThanOrEqualTo(0);
        await Assert.That(hosted.IndexOf("CatalogRefresh"))
            .IsLessThan(hosted.IndexOf("GenericWebHostService"));
    }

    [Test]
    public async Task NoModules_HasNoCatalogAndNothingToSeed(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory();
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/catalog", cancellationToken)).StatusCode)
            .IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ATopologyTestCanStripTheModulesWorkers(CancellationToken cancellationToken)
    {
        // The recipe from the verification reference. A module's hosted services really run inside
        // WebApplicationFactory, so a worker that opens a database connection in StartAsync fails
        // every topology test on a machine that has no database — for a reason that has nothing to
        // do with composition, which is the only thing these tests ask about.
        using var factory = new OfflineFactory(CatalogModule);
        var client = factory.CreateClient();

        var events = await client.GetFromJsonAsync<string[]>("/startup-log", cancellationToken) ?? [];

        // The gate still ran: it is the host's, not a hosted service. The worker did not.
        await Assert.That(events).Contains("task:catalog-seed");
        await Assert.That(events).DoesNotContain("worker:start");

        // And the route table — the thing the test came for — is intact, because
        // GenericWebHostService is what builds it and is deliberately left in place.
        await Assert.That((await client.GetAsync("/catalog", cancellationToken)).StatusCode)
            .IsEqualTo(HttpStatusCode.OK);
    }

    /// <summary>
    /// The factory template plus the offline helper, which is how a real topology project would
    /// have it: the strip is opt-in, because a module with no infrastructure-touching worker does
    /// not need it.
    /// </summary>
    private sealed class OfflineFactory(params string[] modules)
        : Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));

            builder.ConfigureTestServices(services =>
            {
                foreach (var descriptor in services
                    .Where(d => d.ServiceType == typeof(IHostedService) &&
                                d.ImplementationType?.FullName != "Microsoft.AspNetCore.Hosting.GenericWebHostService")
                    .ToList())
                {
                    services.Remove(descriptor);
                }
            });

            base.ConfigureWebHost(builder);
        }
    }
}
