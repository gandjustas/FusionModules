using DashboardModule;
using Modulith;

[assembly: HostingStartup(typeof(Module))]

namespace DashboardModule;

sealed class Module : ModuleBase
{
    // An area keeps this module's routes from colliding with any other module's.
    public const string AreaName = "Dashboard";

    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
        services.AddSingleton<Areas.Dashboard.Controllers.Tiles>();

        // The module adds itself as an application part. The host cannot: it does not know this
        // assembly exists, and Application Part Discovery is switched off precisely so that it
        // cannot find out behind the module's back.
        services.AddControllersWithViews()
            .AddApplicationPart(typeof(Module).Assembly)
            // So the module's controllers can be internal like everything else in it. One call
            // configures discovery for the whole application; calling it from another module too
            // is harmless.
            .AllowInternalControllers()
            // And the same for view components, which MVC gates the same way and for which the
            // cascade is identical. Invoked by name from the view — see TilesViewComponent.
            .AllowInternalViewComponents();
    }

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints.MapControllerRoute(
            name: AreaName,
            pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}"));
}
