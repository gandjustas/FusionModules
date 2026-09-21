using Modulith;
using WeatherModule;

// Without this attribute the module loads and silently does nothing. MOD0005 catches it.
[assembly: HostingStartup(typeof(Module))]

namespace WeatherModule;

// internal, like everything else in a module: nothing outside may reference it (MOD0001).
sealed class Module : ModuleBase
{
    // The builder overload, for registrations written against IHostApplicationBuilder rather than
    // IServiceCollection — every Aspire client integration, and most component packages. Those do
    // more than register a client: they resolve the connection string, add a health check and
    // instrument the calls, and a hand-written AddSingleton keeps none of it. Nothing here needs
    // one, so this is only the shape: the same environment, configuration and services the host has.
    protected override void ConfigureServices(IHostApplicationBuilder builder) =>
        builder.Services.AddSingleton(new Forecaster(
            builder.Configuration.GetValue("Weather:Days", 5)));

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints
            .MapGroup("/weather")
            .MapGet("/", (Forecaster forecaster) => forecaster.Next()));
}

sealed class Forecaster(int days)
{
    private static readonly string[] Summaries = ["Freezing", "Chilly", "Mild", "Balmy", "Sweltering"];

    public IEnumerable<object> Next() => Enumerable.Range(1, days).Select(day => new
    {
        Date = DateOnly.FromDateTime(DateTime.Now.AddDays(day)),
        TemperatureC = Random.Shared.Next(-20, 35),
        Summary = Summaries[Random.Shared.Next(Summaries.Length)],
    });
}
