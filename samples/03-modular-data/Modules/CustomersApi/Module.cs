using CustomersModule;
using Modulith;

[assembly: HostingStartup(typeof(Module))]

namespace CustomersModule;

sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
        // The module adds itself as an application part. The host cannot: it does not know this
        // assembly exists, and Application Part Discovery is switched off precisely so that it
        // cannot find out behind the module's back (MOD0004).
        services.AddControllers()
            .AddApplicationPart(typeof(Module).Assembly)
            // And this is what lets CustomersController stay internal, along with the model it
            // returns. One call configures discovery for the whole application; a second module
            // making it too is harmless.
            .AllowInternalControllers();

    protected override void Configure(IApplicationBuilder app) =>
        // MapControllers maps every controller in every registered application part, so exactly
        // one module should call it — a second call would map the same actions again, and a
        // duplicate endpoint throws at startup rather than quietly.
        app.UseEndpoints(endpoints => endpoints.MapControllers());
}
