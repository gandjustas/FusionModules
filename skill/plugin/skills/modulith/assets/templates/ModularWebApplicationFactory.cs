// Boots the host with a chosen set of modules — the same choice a deployment makes through
// HOSTINGSTARTUPASSEMBLIES, so a test topology and a production topology are the same thing.
//
// Copy this into your test project. There is no package for it, because there is nothing in it
// but a setting, and a package would drag opinions about your test framework along with it.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Tests;

internal sealed class ModularWebApplicationFactory(params string[] modules) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));
        base.ConfigureWebHost(builder);
    }
}
