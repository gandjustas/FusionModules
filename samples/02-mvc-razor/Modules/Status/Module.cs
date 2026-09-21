using Modulith;
using StatusModule;

[assembly: HostingStartup(typeof(Module))]

namespace StatusModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
        services.AddRazorPages().AddApplicationPart(typeof(Module).Assembly);

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints.MapRazorPages());
}
