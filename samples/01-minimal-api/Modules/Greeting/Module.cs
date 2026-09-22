using GreetingModule;
using FusionModules;

[assembly: HostingStartup(typeof(Module))]

namespace GreetingModule;

sealed class Module : ModuleBase
{
    // A module can contribute configuration too — here, a default the host never has to know about.
    protected override void ConfigureAppConfiguration(WebHostBuilderContext context, IConfigurationBuilder configuration) =>
        configuration.AddInMemoryCollection([new("Greeting:Text", "Hello from a module")]);

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints
            .MapGet("/greeting", (IConfiguration configuration) => configuration["Greeting:Text"]));
}
