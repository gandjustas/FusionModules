using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ModularData.Tests;

/// <summary>
/// Boots the host with a chosen set of modules against a chosen database.
/// </summary>
/// <remarks>
/// The only place a module name appears in these tests. A topology here is the same thing as a
/// topology in the compose file: a list of assembly names and a connection string.
/// </remarks>
internal sealed class ModularWebApplicationFactory(string connectionString, params string[] modules)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));
        builder.UseSetting("ConnectionStrings:Postgres", connectionString);
        base.ConfigureWebHost(builder);
    }
}
