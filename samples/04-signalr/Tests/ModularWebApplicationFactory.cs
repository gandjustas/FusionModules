using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Sample.SignalR.Tests;

/// <summary>
/// Boots the host with a chosen set of modules — the same choice a deployment makes through
/// HOSTINGSTARTUPASSEMBLIES, so a test topology and a production topology are the same thing.
/// </summary>
/// <remarks>
/// Copy this into your own test project. It is deliberately not a package: there is nothing here
/// but a setting, and a package would drag in opinions about your test framework along with it.
/// </remarks>
internal sealed class ModularWebApplicationFactory(params string[] modules) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));
        base.ConfigureWebHost(builder);
    }
}
