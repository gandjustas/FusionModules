using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Modulith;
using Xunit;

namespace Sample.Tests;

/// <summary>
/// One host, several topologies, all in one test process — which is the case that catches
/// anything composed from modules being cached across them.
/// </summary>
public class TopologyTests
{
    [Fact]
    public async Task NoModules_ServesOnlyTheHost()
    {
        using var factory = new ModularWebApplicationFactory();
        var client = factory.CreateClient();

        Assert.Equal("host", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/weather", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/greeting", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task OneModule_ServesOnlyThatModule()
    {
        // KnownModules is generated from the <ModulithModule> items, so a rename is a compile
        // error here rather than a 404 in production.
        using var factory = new ModularWebApplicationFactory(KnownModules.WeatherModule);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/weather", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/greeting", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task AllModules_ServeEverything()
    {
        using var factory = new ModularWebApplicationFactory(KnownModules.All);
        var client = factory.CreateClient();

        Assert.Equal("host", await client.GetStringAsync("/", TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/weather", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("Hello from a module", await client.GetStringAsync("/greeting", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheRegistryReportsExactlyWhatWasActivated()
    {
        using var factory = new ModularWebApplicationFactory(KnownModules.WeatherModule);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        var loaded = ModuleBase.GetLoadedModules(configuration).Select(a => a.GetName().Name).ToArray();

        // The host itself is a hosting startup assembly, so it may appear; the point is that a
        // module which was not asked for is not here, however many other hosts this process ran.
        Assert.Contains(KnownModules.WeatherModule, loaded);
        Assert.DoesNotContain(KnownModules.GreetingModule, loaded);
    }

    [Fact]
    public void ATypoInTheModuleListFailsStartup()
    {
        // Stock ASP.NET Core logs a critical message for an assembly it cannot load and carries
        // on, so the application comes up, passes its readiness probe and serves 404s.
        using var factory = new ModularWebApplicationFactory(KnownModules.GreetingModule, "WeatherModul");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("WeatherModul", error.Message, StringComparison.Ordinal);
        Assert.Contains("HOSTINGSTARTUPASSEMBLIES", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypoInEveryModuleNameIsNotCaught()
    {
        // The limit of the check, recorded so it is a known shape rather than a surprise: it runs
        // from the modules that did load, so when none of them did there is nobody left to
        // notice. Closing this would mean the host knowing it has modules, which is the whole
        // thing the model is built to avoid. In practice a deployment misspells one name in a
        // list, not all of them.
        using var factory = new ModularWebApplicationFactory("WeatherModul");
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/weather", TestContext.Current.CancellationToken)).StatusCode);
    }
}
