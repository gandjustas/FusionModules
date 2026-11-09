namespace FusionModules.Tool.Tests;

/// <summary>
/// The whole tool over real hosts: restore and build into a scratch directory, then the probe
/// inside each application's runtime. The work directories are kept between runs, so only the
/// first run pays for a full build.
/// </summary>
[NotInParallel("end-to-end")]
public class EndToEndTests
{
    // One per checkout: worktrees and clones of the repository run these at the same time, each
    // building its own samples.
    private static readonly string Checkout = Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(Fixture.RepositoryRoot).ToLowerInvariant())))[..8].ToLowerInvariant();

    private static string WorkDirectory(string name) => Path.Combine(Path.GetTempPath(), "fusion-modules-tool-tests", $"work-{name}-{Checkout}");

    private static Task<InventoryDocument> Run(string path, string workName, params string[] extra)
    {
        var options = Program.Parse([path, "-q", "--work-dir", WorkDirectory(workName), "--timeout", "60", .. extra]);
        return new Inventory(options, _ => { }).RunAsync();
    }

    [Test]
    public async Task Sample01_TheBareHost_AndItsModules()
    {
        var host = Path.Combine(Fixture.RepositoryRoot, "samples", "01-minimal-api", "Host", "Host.csproj");

        var document = await Run(host, "sample01", "--env", "HOSTINGSTARTUPASSEMBLIES=WeatherModule;GreetingModule");

        await Assert.That(document.Errors).IsEmpty();
        var runtime = document.Hosts.Single().Runtime!;
        await Assert.That(runtime.Status).IsEqualTo("started");
        await Assert.That(runtime.Runtime).StartsWith(".NET 10");
        await Assert.That(runtime.HostingStartupAssemblies).IsEquivalentTo(["WeatherModule", "GreetingModule"]);

        // Each endpoint is owned by the assembly that mapped it: the host's own, or a module's.
        await Assert.That(runtime.Endpoints!.Select(e => (e.Route!, e.Assembly!))).IsEquivalentTo(
            [("/", "Host"), ("/greeting", "GreetingModule"), ("/weather/", "WeatherModule")]);
        await Assert.That(runtime.Services!).Contains(s => s.ServiceType == "WeatherModule.Forecaster" && s.Assembly == "WeatherModule");

        // Who registered what, as FusionModules recorded it: the module's own, and nobody's for the host's.
        await Assert.That(runtime.Services!.Single(s => s.ServiceType == "WeatherModule.Forecaster").Module).IsEqualTo("WeatherModule");
        await Assert.That(runtime.Services!.Where(s => s.Module is not null).All(s => s.Module is "WeatherModule" or "GreetingModule")).IsTrue();
        await Assert.That(runtime.HostedServices!.Select(h => h.Type!)).IsEquivalentTo(["Microsoft.AspNetCore.Hosting.GenericWebHostService"]);

        // FusionModules' own wiring is not the application's: the modules are listed apart, and
        // neither their IStartupFilter registrations nor the library's own services are services.
        await Assert.That(runtime.Modules!.Select(m => m.Assembly!)).IsEquivalentTo(["WeatherModule", "GreetingModule"]);
        await Assert.That(runtime.Services!).DoesNotContain(s => s.ServiceType == "Microsoft.AspNetCore.Hosting.IStartupFilter");
        await Assert.That(runtime.Services!).DoesNotContain(s => s.Assembly == "FusionModules" || s.ServiceAssembly == "FusionModules");
        await Assert.That(runtime.ApplicationAssemblies!).DoesNotContain("FusionModules");
    }

    [Test]
    public async Task Sample01_OnNet8_RunsInTheNet8Runtime()
    {
        var host = Path.Combine(Fixture.RepositoryRoot, "samples", "01-minimal-api", "Host", "Host.csproj");

        var document = await Run(host, "sample01", "--framework", "net8.0", "--env", "HOSTINGSTARTUPASSEMBLIES=WeatherModule");

        var runtime = document.Hosts.Single().Runtime!;
        await Assert.That(runtime.Status).IsEqualTo("started");
        await Assert.That(runtime.Runtime).StartsWith(".NET 8");
        await Assert.That(runtime.Endpoints!.Select(e => e.Assembly!)).IsEquivalentTo(["Host", "WeatherModule"]);
    }

    [Test]
    public async Task Sample06_ServicesEndpointsAndOwners_OfATopology()
    {
        var host = Path.Combine(Fixture.RepositoryRoot, "samples", "06-transports", "Host", "Host.csproj");

        var document = await Run(host, "sample06", "--env", "HOSTINGSTARTUPASSEMBLIES=CheckoutModule;PricingModule;PricingApiModule");

        await Assert.That(document.Errors).IsEmpty();
        var hostInfo = document.Hosts.Single();
        await Assert.That(hostInfo.LaunchProfiles!).Contains("PricingService");
        var runtime = hostInfo.Runtime!;
        await Assert.That(runtime.Status).IsEqualTo("started");

        await Assert.That(runtime.Endpoints!.Select(e => (e.Route!, e.Kind, e.Assembly!))).IsEquivalentTo(
        [
            ("/", "minimal", "Host"),
            ("/checkout/quote", "minimal", "CheckoutModule"),
            ("/pricing/quote", "minimal", "PricingApiModule"),
        ]);

        // The contract lives in one assembly, its implementation in the module that registers it.
        var pricing = runtime.Services!.Single(s => s.ServiceType == "Pricing.Contracts.IPricing");
        await Assert.That(pricing.ServiceAssembly).IsEqualTo("Pricing.Contracts");
        await Assert.That(pricing.Assembly).IsEqualTo("PricingModule");

        var project = document.Projects.Single(p => p.Path == "Host.csproj");
        await Assert.That(project.TransitiveProjectReferences).Contains(r => r.EndsWith("Pricing.Contracts.csproj", StringComparison.Ordinal));
    }

    [Test]
    public async Task Sample02_ControllersAndPages_AreOwnedByTheirModules_UnderALaunchProfile()
    {
        var host = Path.Combine(Fixture.RepositoryRoot, "samples", "02-mvc-razor", "Host", "Host.csproj");

        var document = await Run(host, "sample02", "--launch-profile", "AllModules");

        var service = document.Deployment.Services.Single();
        await Assert.That(service.LaunchProfile).IsEqualTo("AllModules");
        await Assert.That(service.Environment!["HOSTINGSTARTUPASSEMBLIES"]).IsEqualTo("DashboardModule;StatusModule");

        var endpoints = document.Hosts.Single().Runtime!.Endpoints!;
        // A conventional route, filled in with the values that select the action.
        var controller = endpoints.Single(e => e.Kind == "controller");
        await Assert.That(controller.Route).IsEqualTo("/Dashboard/Home/Index/{id?}");
        await Assert.That(controller.Template).IsEqualTo("{area:exists}/{controller=Home}/{action=Index}/{id?}");
        await Assert.That(controller.Assembly).IsEqualTo("DashboardModule");

        var page = endpoints.Single(e => e.Kind == "page");
        await Assert.That(page.Handler).IsEqualTo("/Status");
        await Assert.That(page.Assembly).IsEqualTo("StatusModule");

        // MVC's link-generation-only routes answer nothing, and are not listed.
        await Assert.That(endpoints).DoesNotContain(e => e.Route == "/{id?}");
    }

    [Test]
    public async Task AHostThatThrowsBeforeRun_StillReportsWhatItRegistered()
    {
        using var fixture = new Fixture(
            ("Api/Api.csproj", Fixture.Executable()),
            ("Api/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddSingleton<Api.Clock>();
                builder.Services.AddHostedService<Api.Poller>();
                var app = builder.Build();
                app.MapGet("/now", (Api.Clock clock) => clock.Now);
                // What a migration does without its database, logged in the machine's language first.
                Console.WriteLine("Не удалось подключиться к серверу — ошибка сокета");
                throw new InvalidOperationException("No database at Host=db.");

                namespace Api
                {
                    public sealed class Clock { public DateTime Now => DateTime.UtcNow; }
                    public sealed class Poller : BackgroundService
                    {
                        protected override Task ExecuteAsync(CancellationToken stoppingToken) => throw new InvalidOperationException("must never run");
                    }
                }
                """));

        var document = await Run(fixture["Api/Api.csproj"], $"throws-{Path.GetFileName(fixture.Root)}");

        var runtime = document.Hosts.Single().Runtime!;
        await Assert.That(runtime.Status).IsEqualTo("built");
        await Assert.That(runtime.Services!).Contains(s => s.ServiceType == "Api.Clock");
        await Assert.That(runtime.HostedServices!).Contains(h => h.Type == "Api.Poller" && h.Application);
        await Assert.That(runtime.Errors).Contains(e => e.Stage == "startup" && e.Message == "No database at Host=db.");
        await Assert.That(document.Errors).Contains(e => e.Stage == "probe" && e.Subject == "Api" && e.Message.Contains("after Build() threw"));

        // What Program.cs mapped before it threw, read off the WebApplication: no pipeline was built.
        await Assert.That(runtime.Endpoints!.Select(e => e.Route!)).IsEquivalentTo(["/now"]);
        await Assert.That(runtime.EndpointsReadFrom).IsEqualTo("application");

        // What the application wrote reaches the document as written, whatever the console's code page.
        await Assert.That(runtime.Errors.Single(e => e.Stage == "output").Detail!).Contains("Не удалось подключиться к серверу — ошибка сокета");
    }

    private const string TwoFrameworks = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
            <ImplicitUsings>enable</ImplicitUsings>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    /// <summary>
    /// Hosts that stop on the way to Run(), each differently, on net8 and net10 — the WebApplication
    /// is reached through framework internals that must hold on both.
    /// </summary>
    [Test]
    [Arguments("net10.0")]
    [Arguments("net8.0")]
    public async Task HostsThatStopBeforeRun_StillReportTheirEndpoints(string framework)
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("Disposing/Disposing.csproj", "Hanging/Hanging.csproj", "Pipeline/Pipeline.csproj", "Controllers/Controllers.csproj", "Health/Health.csproj")),
            ("Disposing/Disposing.csproj", TwoFrameworks),
            ("Disposing/Program.cs", """
                await using var app = WebApplication.CreateBuilder(args).Build();
                app.MapGet("/disposing", () => "a");
                // A seed with no database, inside `await using`: the container is gone when the entry point ends.
                throw new InvalidOperationException("No database.");
                """),
            ("Hanging/Hanging.csproj", TwoFrameworks),
            ("Hanging/Program.cs", """
                var app = WebApplication.CreateBuilder(args).Build();
                app.MapGet("/hanging", () => "c");
                // A migration retrying forever.
                Thread.Sleep(Timeout.Infinite);
                app.Run();
                """),
            ("Pipeline/Pipeline.csproj", TwoFrameworks),
            ("Pipeline/Program.cs", """
                var app = WebApplication.CreateBuilder(args).Build();
                app.UseMiddleware<Broken>();
                app.MapGet("/pipeline", () => "d");
                app.Run();

                // A rate limiter that needs Redis in its constructor: the pipeline fails to build, and Run() disposes the host.
                sealed class Broken
                {
                    public Broken(RequestDelegate next) => throw new InvalidOperationException("No Redis.");
                    public Task InvokeAsync(HttpContext context) => Task.CompletedTask;
                }
                """),
            ("Controllers/Controllers.csproj", TwoFrameworks),
            ("Controllers/Program.cs", """
                using Microsoft.AspNetCore.Mvc;

                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddControllers();
                var app = builder.Build();
                if (args.Length >= 0) throw new InvalidOperationException("No database.");
                app.MapControllers();
                app.Run();

                [ApiController, Route("api/items")]
                public sealed class ItemsController : ControllerBase
                {
                    [HttpGet("{id:int}")] public int Get(int id) => id;
                }
                """),
            ("Health/Health.csproj", TwoFrameworks),
            ("Health/Program.cs", """
                using Microsoft.Extensions.Diagnostics.HealthChecks;

                var builder = WebApplication.CreateBuilder(args);
                // What MassTransit's health check does: building the checks creates the bus, which needs a license.
                builder.Services.AddHealthChecks();
                builder.Services.Configure<HealthCheckServiceOptions>(_ => throw new InvalidOperationException("No license."));
                var app = builder.Build();
                app.MapHealthChecks("/health");
                app.MapGet("/health-app", () => "e");
                app.Run();
                """));

        var document = await Run(fixture["Shop.slnx"], $"stop-{framework}-{Path.GetFileName(fixture.Root)}", "--framework", framework, "--timeout", "15");
        var hosts = document.Hosts.ToDictionary(h => h.Service, h => h.Runtime!);

        await Assert.That(hosts["Disposing"].Status).IsEqualTo("built");
        await Assert.That(hosts["Disposing"].Endpoints!.Select(e => e.Route!)).IsEquivalentTo(["/disposing"]);

        await Assert.That(hosts["Hanging"].Status).IsEqualTo("timeout");
        await Assert.That(hosts["Hanging"].Endpoints!.Select(e => e.Route!)).IsEquivalentTo(["/hanging"]);
        await Assert.That(hosts["Hanging"].EndpointsReadFrom).IsEqualTo("application");

        await Assert.That(hosts["Pipeline"].Status).IsEqualTo("built");
        await Assert.That(hosts["Pipeline"].Endpoints!.Select(e => e.Route!)).IsEquivalentTo(["/pipeline"]);

        await Assert.That(hosts["Controllers"].Status).IsEqualTo("built");
        var action = hosts["Controllers"].Endpoints!.Single();
        await Assert.That((action.Kind, action.Route, action.Handler)).IsEqualTo(("controller", "/api/items/{id:int}", "ItemsController.Get"));
        await Assert.That(action.Methods!).IsEquivalentTo(["GET"]);
        await Assert.That(hosts["Controllers"].EndpointsReadFrom).IsEqualTo("action-descriptors");

        await Assert.That(hosts["Health"].Status).IsEqualTo("started");
        await Assert.That(hosts["Health"].Endpoints!.Select(e => e.Route!)).IsEquivalentTo(["/health", "/health-app"]);
    }

    [Test]
    public async Task ANet6Host_IsProbedRolledForward()
    {
        using var fixture = new Fixture(
            ("Legacy/Legacy.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net6.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <CheckEolTargetFramework>false</CheckEolTargetFramework>
                  </PropertyGroup>
                </Project>
                """),
            ("Legacy/Program.cs", """
                var builder = WebApplication.CreateBuilder(args);
                builder.Services.AddSingleton<Legacy.Clock>();
                var app = builder.Build();
                app.MapGet("/legacy", () => "old");
                app.Run();

                namespace Legacy { public sealed class Clock { } }
                """));

        var document = await Run(fixture["Legacy/Legacy.csproj"], $"net6-{Path.GetFileName(fixture.Root)}");

        var runtime = document.Hosts.Single().Runtime!;
        await Assert.That(runtime.Status).IsEqualTo("started");
        await Assert.That(runtime.RolledForwardFrom).IsEqualTo("net6.0");
        await Assert.That(runtime.Runtime).DoesNotStartWith(".NET 6");
        await Assert.That(runtime.Services!).Contains(s => s.ServiceType == "Legacy.Clock");
        await Assert.That(runtime.Endpoints!.Select(e => e.Route!)).IsEquivalentTo(["/legacy"]);
    }

    [Test]
    public async Task AnAppHostThatDoesNotBuild_GivesWayToTheComposeFiles()
    {
        using var fixture = new Fixture(
            ("Shop.slnx", Fixture.Solution("AppHost/AppHost.csproj", "Api/Api.csproj")),
            ("AppHost/AppHost.csproj", Fixture.Executable("Microsoft.NET.Sdk", "  <PropertyGroup><IsAspireHost>true</IsAspireHost></PropertyGroup>")),
            // An AppHost that does not build: on SDK 10, IsAspireHost alone is NETSDK1228 (the Aspire
            // workload is gone) — what an Aspire 8 preview AppHost amounts to; on others, the code.
            ("AppHost/Program.cs", "this does not compile"),
            ("Api/Api.csproj", Fixture.Executable()),
            ("Api/Program.cs", "var app = WebApplication.CreateBuilder(args).Build();\napp.MapGet(\"/\", () => \"api\");\napp.Run();\n"),
            ("Api/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nENTRYPOINT [\"dotnet\", \"Api.dll\"]\n"),
            ("docker-compose.yml", "services:\n  api:\n    build: ./Api\n  db:\n    image: postgres:16\n"));
        var log = new List<string>();

        var options = Program.Parse([fixture["Shop.slnx"], "--work-dir", WorkDirectory($"apphost-{Path.GetFileName(fixture.Root)}"), "--no-docker"]);
        var document = await new Inventory(options, log.Add).RunAsync();

        await Assert.That(document.Deployment.Kind).IsEqualTo("compose");
        // The reason, as the first error's code and message.
        var reason = new System.Text.RegularExpressions.Regex(@"could not be built \((NETSDK|CS)\d+: ");
        await Assert.That(document.Deployment.Notes).Contains(n => reason.IsMatch(n) && n.Contains("compose files"));
        await Assert.That(log).Contains(l => l.StartsWith("The AppHost AppHost could not be built (", StringComparison.Ordinal) && reason.IsMatch(l));
        await Assert.That(document.Errors).Contains(e => e.Stage == "build" && e.Subject == "AppHost/AppHost.csproj");
        await Assert.That(document.Hosts.Single().Runtime!.Status).IsEqualTo("started");
    }

    /// <summary>
    /// A converted host whose own code fails after Build(): its modules map in their IStartupFilter,
    /// which only runs when the pipeline is built — so the probe runs the filters itself.
    /// </summary>
    [Test]
    public async Task AModulesEndpoints_AreReported_WhenItsHostStopsBeforeRun()
    {
        var library = Path.Combine(Fixture.RepositoryRoot, "src", "FusionModules", "FusionModules.csproj");
        using var fixture = new Fixture(
            ("Host/Host.csproj", Fixture.Executable(body: $"""
                  <ItemGroup>
                    <ProjectReference Include="{library}" />
                    <ProjectReference Include="../Shop/ShopModule.csproj" />
                  </ItemGroup>
                """)),
            ("Host/Program.cs", """
                using Microsoft.AspNetCore.Hosting;

                var builder = WebApplication.CreateBuilder(args);
                // A filter of the application's own that fails while the pipeline is configured: the modules' endpoints are still collected.
                builder.Services.AddSingleton<IStartupFilter, Broken>();
                var app = builder.Build();
                app.MapGet("/host", () => "host");
                // The seeder, without its database.
                throw new InvalidOperationException("No database.");

                sealed class Broken : IStartupFilter
                {
                    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => _ => throw new InvalidOperationException("Broken filter.");
                }
                """),
            ("Shop/ShopModule.csproj", Fixture.Project(body: $"""
                  <PropertyGroup><AssemblyName>ShopModule</AssemblyName></PropertyGroup>
                  <ItemGroup>
                    <FrameworkReference Include="Microsoft.AspNetCore.App" />
                    <ProjectReference Include="{library}" />
                  </ItemGroup>
                """)),
            ("Shop/Module.cs", """
                using FusionModules;
                using Microsoft.AspNetCore.Builder;
                using Microsoft.AspNetCore.Hosting;

                [assembly: HostingStartup(typeof(ShopModule.Module))]

                namespace ShopModule;

                sealed class Module : ModuleBase
                {
                    protected override void Configure(IApplicationBuilder app) =>
                        app.UseEndpoints(endpoints => endpoints.MapGet("/shop/orders", () => "orders"));
                }
                """));

        var document = await Run(fixture["Host/Host.csproj"], $"module-{Path.GetFileName(fixture.Root)}", "--env", "HOSTINGSTARTUPASSEMBLIES=ShopModule");

        var runtime = document.Hosts.Single().Runtime!;
        await Assert.That(runtime.Status).IsEqualTo("built");
        await Assert.That(runtime.Modules!.Select(m => m.Type)).IsEquivalentTo(["ShopModule.Module"]);
        await Assert.That(runtime.Endpoints!.Select(e => (e.Route!, e.Assembly!))).IsEquivalentTo([("/host", "Host"), ("/shop/orders", "ShopModule")]);
        await Assert.That(runtime.EndpointsReadFrom).IsEqualTo("application+startup-filters");
        await Assert.That(runtime.Errors).Contains(e => e.Stage == "startup-filter" && e.Message.StartsWith("Warning:", StringComparison.Ordinal) && e.Message.Contains("Broken filter."));
    }
}
