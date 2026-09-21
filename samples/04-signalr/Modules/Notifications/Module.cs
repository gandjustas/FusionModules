using Microsoft.AspNetCore.SignalR;
using Modulith;
using NotificationsModule;

[assembly: HostingStartup(typeof(Module))]

namespace NotificationsModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
        // AddSignalR configures HubOptions for the whole process, not for this hub. A second module
        // calling it with different options silently overwrites the first, and which one wins
        // depends on HOSTINGSTARTUPASSEMBLIES order — so anything set here is a claim about the
        // application, not about this module. Set only what genuinely is.
        services.AddSignalR();

    protected override void Configure(IApplicationBuilder app) =>
        // A hub is not a controller: no application part discovers it, so the module maps it
        // itself. This is the case Configure exists for.
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHub<NotificationHub>(NotificationHub.Path);

            // Something to push with, so the sample can be driven over HTTP.
            endpoints.MapPost("/notify/{topic}",
                async (string topic, Notification notification, IHubContext<NotificationHub, INotificationClient> hub) =>
                {
                    await hub.Clients.Group(topic).Notify(notification);
                    return Results.Accepted();
                });
        });
}
