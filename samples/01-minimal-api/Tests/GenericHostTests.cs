using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;

namespace Sample.Tests;

/// <summary>
/// A generic host with a web host on it runs the hosting startups too, so it is validated too —
/// when it is built, which is when those startups run.
/// </summary>
public class GenericHostTests
{
    private static IHostBuilder GenericHost(string modules) =>
        Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web => web
                // The generic host reads HOSTINGSTARTUPASSEMBLIES from ASPNETCORE_ variables and
                // UseSetting only: command-line arguments arrive after the startups have run.
                .UseSetting(WebHostDefaults.HostingStartupAssembliesKey, modules)
                .Configure(app => app.UseRouting()))
            .ValidateModules();

    [Test]
    public async Task ATypoFailsTheBuild()
    {
        var error = Assert.Throws<InvalidOperationException>(() => GenericHost("WeatherModul").Build());

        await Assert.That(error.Message).Contains("WeatherModul");
    }

    [Test]
    public async Task ModulesThatLoadAndRunPass()
    {
        using var host = GenericHost("WeatherModule;GreetingModule").Build();

        await Assert.That(host).IsNotNull();
    }
}
