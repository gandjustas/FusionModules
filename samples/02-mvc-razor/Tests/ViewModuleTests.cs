using System.Net;

namespace Sample.Mvc.Tests;

public class ViewModuleTests
{
    private const string DashboardModule = "DashboardModule";
    private const string StatusModule = "StatusModule";

    [Test]
    public async Task NoModules_ServesNoControllersAndNoPages(CancellationToken cancellationToken)
    {
        // The load-bearing test for MOD0004. Application Part Discovery would have wired the
        // modules' controllers and pages into the host at build time, entirely behind
        // HOSTINGSTARTUPASSEMBLIES' back — so these routes would answer here, in a topology that
        // asked for no modules at all, and in production too.
        using var factory = new ModularWebApplicationFactory();
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/Dashboard", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await client.GetAsync("/status", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task MvcModule_ServesItsAreaAndOnlyItsArea(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(DashboardModule);
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/Dashboard", cancellationToken)).Contains("<h1>Dashboard</h1>");
        await Assert.That((await client.GetAsync("/status", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task RazorPagesModule_ServesItsPages(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(StatusModule);
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/status", cancellationToken)).Contains("<h1>Status</h1>");
    }

    [Test]
    public async Task AModulesStaticAssetsAreServedUnderItsOwnPath(CancellationToken cancellationToken)
    {
        // Razor class library semantics put a module's wwwroot under _content/<AssemblyName>/,
        // so two modules can both ship site.css without knowing about each other.
        using var factory = new ModularWebApplicationFactory(StatusModule);
        var client = factory.CreateClient();

        var css = await client.GetAsync("/_content/StatusModule/status.css", cancellationToken);

        await Assert.That(css.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(await css.Content.ReadAsStringAsync(cancellationToken)).Contains(".ok");
    }

    [Test]
    public async Task BothModules_Coexist(CancellationToken cancellationToken)
    {
        using var factory = new ModularWebApplicationFactory(DashboardModule, StatusModule);
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/Dashboard", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetAsync("/status", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
    }
}
