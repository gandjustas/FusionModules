using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FusionModules;

namespace Sample.Tests;

/// <summary>
/// One host, several topologies, all in one test process — which is the case that catches
/// anything composed from modules being cached across them.
/// </summary>
public class TopologyTests
{
    private const string WeatherModule = "WeatherModule";
    private const string GreetingModule = "GreetingModule";

    [Test]
    public async Task NoModules_ServesOnlyTheHost(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory();
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/", cancellationToken)).IsEqualTo("host");
        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.GetAsync("/greeting", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task OneModule_ServesOnlyThatModule(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(WeatherModule);
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetAsync("/greeting", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AllModules_ServeEverything(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(WeatherModule, GreetingModule);
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/", cancellationToken)).IsEqualTo("host");
        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await client.GetStringAsync("/greeting", cancellationToken)).IsEqualTo("Hello from a module");
    }

    [Test]
    public async Task TheRegistryReportsExactlyWhatWasActivated()
    {
        using var factory = new ModularWebApplicationFactory(WeatherModule);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var loaded = ModuleBase.GetLoadedModules(configuration).Select(a => a.GetName().Name).ToArray();

        // The host itself is a hosting startup assembly, so it may appear; the point is that a
        // module which was not asked for is not here, however many other hosts this process ran.
        await Assert.That(loaded).Contains(WeatherModule);
        await Assert.That(loaded).DoesNotContain(GreetingModule);
    }

    [Test]
    public async Task ATypoInTheModuleListFailsStartup()
    {
        // Stock ASP.NET Core logs a critical message for an assembly it cannot load and carries
        // on, so the application comes up, passes its readiness probe and serves 404s.
        using var factory = new ModularWebApplicationFactory(GreetingModule, "WeatherModul");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        await Assert.That(error!.Message).Contains("WeatherModul");
        await Assert.That(error.Message).Contains("HOSTINGSTARTUPASSEMBLIES");
    }

    [Test]
    public async Task ATypoInEveryModuleNameIsNotCaught(CancellationToken cancellationToken)
    {
        // The limit of the check, recorded so it is a known shape rather than a surprise: it runs
        // from the modules that did load, so when none of them did there is nobody left to
        // notice. Closing this would mean the host knowing it has modules, which is the whole
        // thing the model is built to avoid. In practice a deployment misspells one name in a
        // list, not all of them.
        using var factory = new ModularWebApplicationFactory("WeatherModul");
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }
}
