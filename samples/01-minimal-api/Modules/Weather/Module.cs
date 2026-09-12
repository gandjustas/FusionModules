using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Modulith;
using WeatherModule;

// Without this attribute the module loads and silently does nothing. MOD0005 catches it.
[assembly: HostingStartup(typeof(Module))]

namespace WeatherModule;

// internal, like everything else in a module: nothing outside may reference it (MOD0001).
sealed class Module : ModuleBase
{
    protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services) =>
        services.AddSingleton<Forecaster>();

    protected override void Configure(IApplicationBuilder app) =>
        app.UseEndpoints(endpoints => endpoints
            .MapGroup("/weather")
            .MapGet("/", (Forecaster forecaster) => forecaster.Next(5)));
}

sealed class Forecaster
{
    private static readonly string[] Summaries = ["Freezing", "Chilly", "Mild", "Balmy", "Sweltering"];

    public IEnumerable<object> Next(int days) => Enumerable.Range(1, days).Select(day => new
    {
        Date = DateOnly.FromDateTime(DateTime.Now.AddDays(day)),
        TemperatureC = Random.Shared.Next(-20, 35),
        Summary = Summaries[Random.Shared.Next(Summaries.Length)],
    });
}
