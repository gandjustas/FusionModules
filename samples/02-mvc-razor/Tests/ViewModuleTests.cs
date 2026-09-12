using System.Net;
using Xunit;

namespace Sample.Mvc.Tests;

public class ViewModuleTests
{
    [Fact]
    public async Task NoModules_ServesNoControllersAndNoPages()
    {
        // The load-bearing test for MOD0004. Application Part Discovery would have wired the
        // modules' controllers and pages into the host at build time, entirely behind
        // HOSTINGSTARTUPASSEMBLIES' back — so these routes would answer here, in a topology that
        // asked for no modules at all, and in production too.
        using var factory = new ModularWebApplicationFactory();
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/status", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task MvcModule_ServesItsAreaAndOnlyItsArea()
    {
        using var factory = new ModularWebApplicationFactory(KnownModules.DashboardModule);
        var client = factory.CreateClient();

        Assert.Contains("<h1>Dashboard</h1>", await client.GetStringAsync("/Dashboard", TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/status", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task RazorPagesModule_ServesItsPages()
    {
        using var factory = new ModularWebApplicationFactory(KnownModules.StatusModule);
        var client = factory.CreateClient();

        Assert.Contains("<h1>Status</h1>", await client.GetStringAsync("/status", TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModulesStaticAssetsAreServedUnderItsOwnPath()
    {
        // Razor class library semantics put a module's wwwroot under _content/<AssemblyName>/,
        // so two modules can both ship site.css without knowing about each other.
        using var factory = new ModularWebApplicationFactory(KnownModules.StatusModule);
        var client = factory.CreateClient();

        var css = await client.GetAsync("/_content/StatusModule/status.css", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Contains(".ok", await css.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothModules_Coexist()
    {
        using var factory = new ModularWebApplicationFactory(KnownModules.All);
        var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Dashboard", TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/status", TestContext.Current.CancellationToken)).StatusCode);
    }
}
