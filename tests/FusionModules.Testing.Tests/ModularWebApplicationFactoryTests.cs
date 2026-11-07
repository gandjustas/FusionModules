using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FusionModules.Testing.Tests;

/// <summary>
/// The factory, its host builder extensions and the route inventory, over a host with one module.
/// </summary>
/// <remarks>
/// The module is known here only by name, as a deployment knows it: what it did is read off its
/// routes.
/// </remarks>
public class ModularWebApplicationFactoryTests
{
    private const string Module = "FusionModules.Testing.TestModule";

    [Test]
    public async Task TheNamedModulesAreTheOnesThatLoad(CancellationToken cancellationToken)
    {
        await using var factory = new ModularWebApplicationFactory<Program>(Module);

        var response = await factory.CreateClient().GetStringAsync("/module/ping", cancellationToken);

        await Assert.That(response).IsEqualTo("pong");
        await Assert.That(factory.Modules).IsEquivalentTo([Module]);
    }

    [Test]
    public async Task NoModules_IsTheHostAlone(CancellationToken cancellationToken)
    {
        await using var factory = new ModularWebApplicationFactory<Program>();
        var client = factory.CreateClient();

        await Assert.That((await client.GetAsync("/", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await client.GetAsync("/module/ping", cancellationToken)).StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(factory.Modules).IsEmpty();
    }

    [Test]
    [NotInParallel]
    public async Task ModulesTheEnvironmentNames_FailTheFactory()
    {
        // ASP.NET Core would run them beside the ones the test named: a developer's shell left with
        // the variable set must not run every topology test against another topology. Not in
        // parallel: the variable is the whole process's.
        const string variable = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, Module);
        try
        {
            // Even the same names: the module would run twice and map its endpoints twice.
            foreach (var modules in new[] { Array.Empty<string>(), [Module] })
            {
                await using var factory = new ModularWebApplicationFactory<Program>(modules);
                var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
                await Assert.That(error.Message).Contains(variable);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Test]
    public async Task AMisspeltModule_FailsStartup_WhenTheHostValidatesItsModules()
    {
        // The factory only sets HOSTINGSTARTUPASSEMBLIES; the host's generated ValidateModules is
        // what refuses a name that does not load, exactly as it does in a deployment.
        await using var factory = new ModularWebApplicationFactory<Program>("FusionModules.Testing.Test");

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        await Assert.That(error.Message).Contains("'FusionModules.Testing.Test' could not be loaded");
    }

    [Test]
    public async Task NullModules_AreRejected()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new ModularWebApplicationFactory<Program>(null!));

        await Assert.That(error.ParamName).IsEqualTo("modules");
    }

    [Test]
    public async Task Routes_NameTheModuleThatServesEach()
    {
        await using var factory = new ModularWebApplicationFactory<Program>(Module);

        var routes = factory.Services.Routes();

        // Ordered by pattern, then method. The attribute route was written without its leading
        // slash, the minimal APIs with one; the inventory spells them alike.
        await Assert.That(routes).IsEquivalentTo(
            [
                new ModuleRoute(ModuleRoute.Host, "GET", "/"),
                new ModuleRoute(null, "GET", "/framework/id"),
                new ModuleRoute(Module, ModuleRoute.AnyMethod, "/module/any"),
                new ModuleRoute(Module, "GET", "/module/items/{id:int}"),
                new ModuleRoute(Module, "GET", "/module/ping"),
                new ModuleRoute(Module, "GET", "/module/worker"),
            ],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Routes_OfTheHostAlone_AreTheHostsOwn()
    {
        await using var factory = new ModularWebApplicationFactory<Program>();

        await Assert.That(factory.Services.Routes()).IsEquivalentTo(
            [new ModuleRoute(ModuleRoute.Host, "GET", "/"), new ModuleRoute(null, "GET", "/framework/id")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task AHostThatIsAModule_ServesItsRoutesAsTheHost_InTheRoutesAndTheReport(CancellationToken cancellationToken)
    {
        // The module's assembly is the application here: its HostingStartup runs as the host's own,
        // the way a host whose project carries [assembly: HostingStartup] runs it. Its routes are the
        // host's, as they are once that module moves out of the host's project, and every part of the
        // report calls it the host.
        var logged = new List<string>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = Module,
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(logged));
        await using var app = builder.Build();
        app.UseRouting();
        await app.StartAsync(cancellationToken);

        await Assert.That(app.Services.Routes()).IsEquivalentTo(
            [
                new ModuleRoute(ModuleRoute.Host, ModuleRoute.AnyMethod, "/module/any"),
                new ModuleRoute(ModuleRoute.Host, "GET", "/module/items/{id:int}"),
                new ModuleRoute(ModuleRoute.Host, "GET", "/module/ping"),
                new ModuleRoute(ModuleRoute.Host, "GET", "/module/worker"),
            ],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Report(logged)).IsEqualTo("Topology: modules: host; endpoints: host 4; hosted services: Worker (host)");

        await app.StopAsync(cancellationToken);
    }

    [Test]
    public async Task StaticAssets_AreLeftOut_UnlessAskedFor()
    {
        // An endpoint per file and per compressed or fingerprinted copy: in the first application
        // converted, about 534 of its 542 routes.
        await using var factory = new ModularWebApplicationFactory<Program>(Module);

        var routes = factory.Services.Routes();
        var withAssets = factory.Services.Routes(includeStaticAssets: true);

        await Assert.That(routes).DoesNotContain(route => route.Pattern.StartsWith("/site", StringComparison.Ordinal));
        await Assert.That(withAssets).Contains(route => route.Pattern == "/site.css" && route.Module == null);
        await Assert.That(withAssets.Except(routes).All(route => route.Module is null)).IsTrue();
        await Assert.That(routes.Except(withAssets)).IsEmpty();
    }

    [Test]
    public async Task TheStartupReport_CountsTheModulesAndTheHostsEndpoints_AndListsTheWorker()
    {
        var logged = new List<string>();
        await using var modular = new ModularWebApplicationFactory<Program>(Module);
        await using var factory = modular.WithWebHostBuilder(builder => builder.ConfigureServices(
            services => services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logged))));

        _ = factory.Services;

        // Four endpoints of the module's (two GETs, the any-method route, the controller's action) and
        // the host's one; the framework's route and the static assets are nobody's.
        await Assert.That(Report(logged)).IsEqualTo(
            $"Topology: modules: {Module}; endpoints: {Module} 4, host 1; hosted services: Worker ({Module})");
    }

    [Test]
    public async Task TheStartupReport_ListsNoWorkerATestRemoved()
    {
        var logged = new List<string>();
        await using var modular = new ModularWebApplicationFactory<Program>(Module);
        await using var factory = modular.WithWebHostBuilder(builder => builder
            .WithoutApplicationHostedServices()
            .ConfigureServices(services => services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logged))));

        _ = factory.Services;

        await Assert.That(Report(logged)).IsEqualTo($"Topology: modules: {Module}; endpoints: {Module} 4, host 1");
    }

    [Test]
    public async Task ARoute_ReadsAsALineOfTheRouteTable()
    {
        await Assert.That(new ModuleRoute("OrdersModule", "GET", "/orders/{id}").ToString()).IsEqualTo("GET /orders/{id} (OrdersModule)");
        await Assert.That(new ModuleRoute(null, ModuleRoute.AnyMethod, "/health").ToString()).IsEqualTo("* /health");
    }

    [Test]
    public async Task TheModulesWorkersRun_ByDefault(CancellationToken cancellationToken)
    {
        // The reason WithoutApplicationHostedServices exists: inside WebApplicationFactory a
        // module's hosted services really start.
        await using var factory = new ModularWebApplicationFactory<Program>(Module);

        await Assert.That(await factory.CreateClient().GetStringAsync("/module/worker", cancellationToken)).IsEqualTo("started");
    }

    [Test]
    public async Task WithoutApplicationHostedServices_TheWorkersDoNotStart_AndThePipelineIsBuilt(CancellationToken cancellationToken)
    {
        await using var factory = new OfflineTopology(Module);

        // GenericWebHostService stayed, so the modules' Configure ran and the route is served.
        var client = factory.CreateClient();

        await Assert.That(await client.GetStringAsync("/module/ping", cancellationToken)).IsEqualTo("pong");
        await Assert.That(await client.GetStringAsync("/module/worker", cancellationToken)).IsEqualTo("not started");
        await Assert.That(factory.Services.GetServices<IHostedService>().Select(service => service.GetType().Name))
            .IsEquivalentTo(["GenericWebHostService"]);
    }

    [Test]
    public async Task UseConnectionStrings_SetsEachAsGiven_OverTheApplicationsOwnFile()
    {
        await using var factory = new OfflineTopology(Module);

        // The host's appsettings.json names a Postgres server: the test's settings win over the
        // application's own file. Each string is in its own client's format. Migrating is the
        // host's own switch, which the package leaves alone.
        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        await Assert.That(configuration.GetValue<bool>("Database:Migrate")).IsTrue();
        await Assert.That(configuration.GetConnectionString("Postgres")).IsEqualTo(OfflinePostgres);
        await Assert.That(configuration.GetConnectionString("RabbitMQ")).IsEqualTo("host=127.0.0.1;port=1");
        await Assert.That(configuration.GetConnectionString("Unnamed")).IsNull();
    }

    [Test]
    public async Task UseConnectionStrings_RefusesAConnectionStringWithoutAValue()
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().WebHost;

        var error = Assert.Throws<ArgumentException>(() => builder.UseConnectionStrings(("Postgres", "")));

        await Assert.That(error.ParamName).IsEqualTo("connectionStrings");
    }

    private const string OfflinePostgres = "Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused";

    private static string Report(List<string> logged)
    {
        lock (logged)
        {
            return logged.Single(message => message.StartsWith("Topology:", StringComparison.Ordinal));
        }
    }

    /// <summary>A topology test's factory: its modules, no database and none of their workers.</summary>
    private sealed class OfflineTopology(params string[] modules) : ModularWebApplicationFactory<Program>(modules)
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseConnectionStrings(("Postgres", OfflinePostgres), ("RabbitMQ", "host=127.0.0.1;port=1")).WithoutApplicationHostedServices();
            base.ConfigureWebHost(builder);
        }
    }

    private sealed class ListLoggerProvider(List<string> messages) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(messages);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (messages)
                {
                    messages.Add(formatter(state, exception));
                }
            }
        }
    }
}
