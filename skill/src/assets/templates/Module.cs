// A module. Copy, rename, delete what you do not need.
//
// Everything here is internal: nothing outside a module may reference its types, and MOD0001
// enforces that. If another module needs one of these types, it is a contract — put it in a plain
// class library with no [HostingStartup], which both modules reference.

// Recent FusionModules versions contribute these to a module project the way Microsoft.NET.Sdk.Web
// used to, so on those they are redundant. Losing them is what makes the first build of a
// converted service a wall of CS0246 — the types are in the framework reference either way.
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FusionModules;

// Without this the module loads and silently does nothing. MOD0005 catches it at build time.
[assembly: HostingStartup(typeof(ExampleModule.Module))]

namespace ExampleModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureAppConfiguration(WebHostBuilderContext context, IConfigurationBuilder configuration) =>
        // Defaults belong with the feature that needs them, not in the host's appsettings.json.
        configuration.AddInMemoryCollection([new("Example:PageSize", "50")]);

    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        // TryAdd for anything this module provides but does not own: two modules registering the
        // same service type means the winner depends on HOSTINGSTARTUPASSEMBLIES order.
        services.AddScoped<ExampleService>();
    }

    protected override void Configure(IApplicationBuilder app) =>
        // Runs after the host's pipeline, so routing is already in place. Never call UseRouting
        // or IWebHostBuilder.Configure here — the latter replaces the pipeline entirely.
        app.UseEndpoints(endpoints =>
        {
            // A group prefix (or an MVC area) keeps this module's routes from colliding with
            // another module's.
            var group = endpoints.MapGroup("/example");

            group.MapGet("/", (ExampleService service) => service.All());
        });
}

sealed class ExampleService
{
    public IEnumerable<string> All() => ["one", "two"];
}
