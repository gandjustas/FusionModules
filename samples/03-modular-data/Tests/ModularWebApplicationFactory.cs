using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ModularData.Tests;

/// <summary>
/// Boots the host with a chosen set of modules and no database.
/// </summary>
/// <remarks>
/// The only place a module name appears in these tests, and it appears the same way a deployment
/// says it: a list of assembly names.
/// <para>
/// Building an EF Core model needs a provider, not a connection, so the connection string here is
/// never opened and migrations are switched off through configuration. What these tests are about
/// is which modules compose into which model and which endpoints — not persistence, which is the
/// modules' own business and is tested beside them.
/// </para>
/// </remarks>
internal sealed class ModularWebApplicationFactory(params string[] modules) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));
        builder.UseSetting("Database:Migrate", "false");
        builder.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=never-opened;Username=none;Password=none");
        base.ConfigureWebHost(builder);
    }
}
