using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ModularData.Tests;

/// <summary>
/// Boots the host with a chosen set of modules and no database.
/// </summary>
/// <remarks>
/// Building an EF Core model needs a provider, not a connection, so everything here runs against
/// a connection string that is never opened. Migrations are switched off through configuration
/// for the same reason.
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
