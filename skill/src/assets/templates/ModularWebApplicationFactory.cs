// Boots the host with a chosen set of modules — the same choice a deployment makes through
// HOSTINGSTARTUPASSEMBLIES, so a test topology and a production topology are the same thing.
//
// Copy this into your test project. There is no package for it, because there is nothing in it
// but a setting, and a package would drag opinions about your test framework along with it.

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;

namespace Tests;

internal sealed class ModularWebApplicationFactory(params string[] modules) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', modules));
        base.ConfigureWebHost(builder);
    }
}

// Settings that let the host come up without opening a single connection, and a way to strip the
// application's own hosted services. Opt-in: call them from ConfigureWebHost above when a module
// has a worker that touches infrastructure.
//
// A module's IHostedService really runs inside WebApplicationFactory, so a worker that loads
// translations from a database in StartAsync makes every topology test fail on a machine that has
// no database — and the failure says nothing about composition, which is the only thing these
// tests ask about. GenericWebHostService is kept: it is what builds the pipeline, and therefore
// the route table the test came for.
internal static class OfflineSettings
{
    public static IWebHostBuilder UseOfflineSettings(this IWebHostBuilder builder, params string[] connectionNames)
    {
        // The gate the host runs between Build() and RunAsync(); the same switch a second replica
        // uses in the compose file.
        builder.UseSetting("Database:Migrate", "false");

        // Syntactically valid and pointed at nothing. Npgsql and StackExchange.Redis both connect
        // lazily, so a host that issues no query never notices.
        foreach (var name in connectionNames)
        {
            builder.UseSetting($"ConnectionStrings:{name}",
                "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused");
        }

        return builder;
    }

    public static IWebHostBuilder RemoveApplicationHostedServices(this IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            foreach (var descriptor in services
                .Where(d => d.ServiceType == typeof(IHostedService) &&
                            d.ImplementationType?.FullName != "Microsoft.AspNetCore.Hosting.GenericWebHostService")
                .ToList())
            {
                services.Remove(descriptor);
            }
        });
}
