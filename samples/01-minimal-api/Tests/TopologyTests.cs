using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FusionModules;
using FusionModules.Testing;

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
        using var factory = new ModularWebApplicationFactory<Program>();
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/", cancellationToken)).IsEqualTo("host");
        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.GetAsync("/greeting", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task OneModule_ServesOnlyThatModule(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory<Program>(WeatherModule);
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetAsync("/greeting", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AllModules_ServeEverything(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory<Program>(WeatherModule, GreetingModule);
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/", cancellationToken)).IsEqualTo("host");
        await Assert.That((await client.GetAsync("/weather", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await client.GetStringAsync("/greeting", cancellationToken)).IsEqualTo("Hello from a module");
    }

    [Test]
    public async Task TheRegistryReportsExactlyWhatWasActivated()
    {
        using var factory = new ModularWebApplicationFactory<Program>(WeatherModule);
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
        using var factory = new ModularWebApplicationFactory<Program>(GreetingModule, "WeatherModul");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        await Assert.That(error.Message).Contains("WeatherModul");
        await Assert.That(error.Message).Contains("HOSTINGSTARTUPASSEMBLIES");
    }

    [Test]
    public async Task ATypoInEveryModuleNameFailsStartupToo()
    {
        // The check used to run from the modules that did load, so when none of them did there
        // was nobody left to notice. It runs from the host now, against the list its references
        // produced, and the host still names no module in its own code.
        using var factory = new ModularWebApplicationFactory<Program>("WeatherModul");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        await Assert.That(error.Message).Contains("WeatherModul");
    }

    [Test]
    public async Task AModuleWhoseStartupDidNotRunFailsStartup()
    {
        // Listed, loadable, one of the host's own modules — and excluded, so ASP.NET Core never
        // ran it. Exactly the module that is there and does nothing.
        using var factory = new ModularWebApplicationFactory<Program>(GreetingModule, WeatherModule)
            .WithWebHostBuilder(builder => builder.UseSetting(WebHostDefaults.HostingStartupExcludeAssembliesKey, WeatherModule));

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        await Assert.That(error.Message).Contains(WeatherModule);
    }
}
