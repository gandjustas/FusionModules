using DashboardModule;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modulith;

[assembly: HostingStartup(typeof(Module))]

namespace DashboardModule;

sealed class Module : ModuleBase
{
    // An area keeps this module's routes from colliding with any other module's.
    public const string AreaName = "Dashboard";

    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
        // The module adds itself as an application part. The host cannot: it does not know this
        // assembly exists, and Application Part Discovery is switched off precisely so that it
        // cannot find out behind the module's back.
        services.AddControllersWithViews().AddApplicationPart(typeof(Module).Assembly);

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints.MapControllerRoute(
            name: AreaName,
            pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}"));
}
