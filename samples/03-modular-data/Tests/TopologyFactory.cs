using FusionModules.Testing;
using Microsoft.AspNetCore.Hosting;

namespace ModularData.Tests;

/// <summary>
/// Boots the host with a chosen set of modules and no database.
/// </summary>
/// <remarks>
/// The only place a module name appears in these tests, and it appears the same way a deployment
/// says it: a list of assembly names.
/// <para>
/// Building an EF Core model needs a provider, not a connection, so the connection string here is
/// never opened and migrations are switched off — <c>Database:Migrate</c> is the switch this host
/// reads in <c>Program.cs</c>. What these tests are about is which modules compose into which model
/// and which endpoints — not persistence, which is the modules' own business and is tested beside
/// them.
/// </para>
/// </remarks>
internal sealed class TopologyFactory(params string[] modules) : ModularWebApplicationFactory<Program>(modules)
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Npgsql's keyword form, pointing at port 1 on the loopback address: it parses, and nothing answers.
        builder
            .UseConnectionStrings(("Postgres", "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused"))
            .UseSetting("Database:Migrate", "false");
        base.ConfigureWebHost(builder);
    }
}
