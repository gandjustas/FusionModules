using CatalogModule;
using FusionModules;
using Startup.Contracts;

[assembly: HostingStartup(typeof(Module))]

namespace CatalogModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<Catalog>();

        // The declaration, not the work. The host runs it between Build() and RunAsync(), which is
        // the only window where the container exists and no request can arrive yet.
        services.AddSingleton(new StartupTask("catalog-seed", (provider, _) =>
        {
            provider.GetRequiredService<Catalog>().Seed(["hammer", "nails", "saw"]);
            return Task.CompletedTask;
        }));

#pragma warning disable MOD0008 // Once per replica is intended: this refreshes a per-process cache.
        services.AddHostedService<CatalogRefresh>();
#pragma warning restore MOD0008

#pragma warning disable MOD0008 // Likewise, and it is here to be compared with the gate above.
        services.AddHostedService<WarmCache>();
#pragma warning restore MOD0008
    }

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/catalog", (Catalog catalog) => catalog.Items);

            // What the sample is really about: on the very first request this is already true.
            endpoints.MapGet("/catalog/seeded", (Catalog catalog) => catalog.Seeded);

            endpoints.MapGet("/startup-log", (StartupLog log) => log.Events);
        });
}
