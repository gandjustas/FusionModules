using CatalogModule;
using FusionModules;
using Startup.Contracts;

[assembly: HostingStartup(typeof(Module))]

namespace CatalogModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddSingleton<Catalog>();

        // The declaration, not the work. The host runs it between Build() and RunAsync(), which is
        // the only window where the container exists and no request can arrive yet.
        builder.Services.AddSingleton(new StartupTask("catalog-seed", (provider, _) =>
        {
            provider.GetRequiredService<Catalog>().Seed(["hammer", "nails", "saw"]);
            return Task.CompletedTask;
        }));

        // Once per replica is intended: this refreshes a per-process cache.
        builder.Services.AddHostedService<CatalogRefresh>();

        // Likewise, and it is here to be compared with the gate above.
        builder.Services.AddHostedService<WarmCache>();
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
