using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Tests;

/// <summary>
/// One line at startup that says what this replica is — the question asked first when a job runs
/// twice, or not at all.
/// </summary>
public class TopologyReportTests
{
    /// <summary>
    /// A host apart from the modules: the package's own assembly, which declares no handler and
    /// carries no HostingStartup. By default the test assembly is the application, and so the host is
    /// a module itself and every endpoint here is the host's.
    /// </summary>
    private const string HostApart = "FusionModules";

    [Test]
    public async Task TheReport_NamesModulesWorkersAndWhatIsShared()
    {
        var logged = await StartAsync([new OrdersModule(), new ShippingModule()]);

        var report = Report(logged);

        await Assert.That(report).StartsWith("Topology: modules: FusionModules.Tests; endpoints: ");
        await Assert.That(Segment(report, "hosted services")).IsEqualTo(
            "hosted services: Dispatcher (FusionModules.Tests (OrdersModule)), Fulfillment (FusionModules.Tests (OrdersModule))");
        await Assert.That(Segment(report, "shared on purpose")).IsEqualTo($"shared on purpose: {nameof(IStatusListener)}");
    }

    [Test]
    public async Task EveryPart_IsLabelledAlike_AndEmptyPartsAreLeftOut()
    {
        // modules: …; endpoints: …; and nothing for the hosted services, library hosted services
        // and shared contracts this topology does not have.
        var logged = await StartAsync([new ShippingModule()]);

        await Assert.That(Report(logged)).IsEqualTo("Topology: modules: FusionModules.Tests; endpoints: none from FusionModules.Tests");
    }

    [Test]
    public async Task FrameworkHostedServices_AreListedApart_WithoutAModule()
    {
        // Whichever module calls AddHealthChecks first registers the publisher, and that changes with
        // the order of HOSTINGSTARTUPASSEMBLIES: Data Protection's read as MVC's in one deployment and
        // as Razor's in the next. The framework's services are no module's, and read the same each time.
        var first = Report((await ModuleHost.StartAsync([new HealthModule(), new OtherHealthModule(), new OrdersModule()])).Logged);
        var second = Report((await ModuleHost.StartAsync([new OtherHealthModule(), new HealthModule(), new OrdersModule()])).Logged);

        await Assert.That(Segment(first, "hosted services")).DoesNotContain("HealthCheckPublisherHostedService");
        await Assert.That(Segment(first, "library hosted services")).IsEqualTo("library hosted services: HealthCheckPublisherHostedService");
        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task ALibrarysHostedService_IsListedApart_WithTheModuleThatRegisteredIt()
    {
        // OpenTelemetry's TelemetryHostedService, added once by whichever module first calls an Aspire
        // integration, is not that module's worker; but a module may run a library's worker on purpose.
        var result = await ModuleHost.StartAsync([new TelemetryModule(), new OrdersModule()]);
        var report = Report(result.Logged);

        await Assert.That(Segment(report, "hosted services")).DoesNotContain("TelemetryHostedService");
        await Assert.That(Segment(report, "library hosted services")).Contains("TelemetryHostedService (registered by FusionModules.Tests (TelemetryModule))");
        await Assert.That(Segment(report, "hosted services")).Contains("Fulfillment (FusionModules.Tests (OrdersModule))");
    }

    [Test]
    public async Task AHostedServiceRemovedAfterTheModules_IsNotListed()
    {
        // A test's ConfigureTestServices — WithoutApplicationHostedServices — takes a module's worker
        // out; the report lists what runs, not what the module once registered.
        var result = await ModuleHost.StartAsync(
            [new OrdersModule()],
            afterModules: services => services.Remove(services.Single(descriptor => descriptor.ImplementationType == typeof(Dispatcher))));

        await Assert.That(result.Failure).IsNull();
        await Assert.That(Segment(Report(result.Logged), "hosted services")).IsEqualTo("hosted services: Fulfillment (FusionModules.Tests (OrdersModule))");
    }

    [Test]
    public async Task EachPart_IsAFieldOfItsOwn()
    {
        // So a log store can ask which replicas run a worker without parsing the message.
        var result = await ModuleHost.StartAsync([new OrdersModule(), new ShippingModule()], applicationName: HostApart);

        var fields = Fields(result);

        await Assert.That((string)fields["Modules"]!).Contains("FusionModules.Tests");
        await Assert.That((string)fields["HostedServices"]!).Contains(nameof(Dispatcher));
        await Assert.That((string)fields["Shared"]!).Contains(nameof(IStatusListener));
        await Assert.That((string)fields["{OriginalFormat}"]!).IsEqualTo(
            "Topology: modules: {Modules}; endpoints: {Endpoints}; hosted services: {HostedServices}; shared on purpose: {Shared}");
    }

    [Test]
    public async Task APartLeftOutOfTheLine_IsStillAField()
    {
        // Every replica's entry has the same fields, whatever it runs.
        var result = await ModuleHost.StartAsync([new ShippingModule()]);

        var fields = Fields(result);

        await Assert.That(fields).ContainsKey("LibraryHostedServices");
        await Assert.That((string)fields["LibraryHostedServices"]!).IsEmpty();
        await Assert.That((string)fields["HostedServices"]!).IsEmpty();
        await Assert.That(fields).DoesNotContainKey("FrameworkHostedServices");
    }

    [Test]
    public async Task EachModule_IsCountedItsEndpoints()
    {
        var result = await ModuleHost.StartAsync([new EndpointsModule()], configureApp: app => app.UseRouting(), applicationName: HostApart);

        await Assert.That((string)Fields(result)["Endpoints"]!).IsEqualTo("FusionModules.Tests 2");
    }

    [Test]
    public async Task AHostThatIsAModule_IsCountedAsTheHost()
    {
        // The test assembly is the application and the module: its endpoints are the host's, as they
        // are for a host that is not a module, and the modules part names it the same way, so that one
        // line does not call the host by two names.
        var result = await ModuleHost.StartAsync([new EndpointsModule()], configureApp: app => app.UseRouting());

        await Assert.That((string)Fields(result)["Endpoints"]!).IsEqualTo("host 2");
        await Assert.That((string)Fields(result)["Modules"]!).IsEqualTo("host");
    }

    [Test]
    public async Task AHostThatIsAModule_AndServesNothing_IsNotAModuleThatServesNone()
    {
        // A host that is not a module and serves nothing is not named either.
        var logged = (await ModuleHost.StartAsync([new OrdersModule()])).Logged;

        await Assert.That(Segment(Report(logged), "endpoints")).IsEqualTo("endpoints: none");
    }

    [Test]
    public async Task AHubsEndpoints_CountForTheModule()
    {
        // A hub, like a gRPC service, has no handler method; its metadata names the hub type. Without
        // reading it, a module serving only a hub — eShop's gRPC Basket — read as serving nothing.
        var result = await ModuleHost.StartAsync([new HubModule()], configureApp: app => app.UseRouting(), applicationName: HostApart);

        await Assert.That((string)Fields(result)["Endpoints"]!).IsEqualTo("FusionModules.Tests 2");
    }

    [Test]
    public async Task EndpointsTheFrameworkServes_AreNotCounted()
    {
        // A health check is the framework's: no module's and not the host's, as the route inventory says too.
        var result = await ModuleHost.StartAsync(
            [new HealthModule(), new EndpointsModule()],
            configureApp: app =>
            {
                app.UseRouting();
                app.MapHealthChecks("/health");
            },
            applicationName: HostApart);

        await Assert.That((string)Fields(result)["Endpoints"]!).IsEqualTo("FusionModules.Tests 2");
    }

    [Test]
    public async Task AModuleWhoseStartupFilterWasRemoved_DoesNotSilenceTheReport()
    {
        // A test that takes one module's startup filter out still gets the line: the first module to
        // build its part of the pipeline writes it, whichever modules are left.
        var result = await ModuleHost.StartAsync(
            [new OrdersModule(), new ShippingModule()],
            afterModules: services => services.Remove(services.First(descriptor => descriptor.ImplementationInstance is ShippingModule)),
            applicationName: HostApart);

        await Assert.That(result.Failure).IsNull();
        await Assert.That(result.Logged).Contains(message => message.StartsWith("Topology:", StringComparison.Ordinal));
    }

    [Test]
    public async Task AModuleRegisteredAgainAsAStartupFilter_IsReportedOnce_AfterItsEndpoints()
    {
        // MOD0009 reports the extra registration; at runtime the module's Configure still runs for
        // each one, and the report must neither come twice nor before the endpoints are mapped.
        var result = await ModuleHost.StartAsync([new EndpointsModule(), new RegisteredAgainModule()], configureApp: app => app.UseRouting(), applicationName: HostApart);

        var reports = result.Logged.Where(message => message.StartsWith("Topology:", StringComparison.Ordinal)).ToList();
        await Assert.That(result.Failure).IsNull();
        await Assert.That(reports.Count).IsEqualTo(1);
        await Assert.That(reports[0]).Contains("endpoints: FusionModules.Tests 2");
    }

    private sealed class RegisteredAgainModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddSingleton<IStartupFilter>(this);
    }

    private static string Report(List<string> logged) =>
        logged.Single(message => message.StartsWith("Topology:", StringComparison.Ordinal));

    private static Dictionary<string, object?> Fields(ModuleHost.Result result) =>
        result.Fields.Single(entry => entry.TryGetValue("{OriginalFormat}", out var format) && format is string text && text.StartsWith("Topology:", StringComparison.Ordinal));

    /// <summary>The part of the report labelled <paramref name="label"/>, up to the next <c>;</c>; empty when there is none.</summary>
    private static string Segment(string report, string label)
    {
        var start = report.IndexOf("; " + label + ": ", StringComparison.Ordinal);
        start = start < 0 ? report.IndexOf(label + ": ", StringComparison.Ordinal) : start + 2;
        if (start < 0)
        {
            return string.Empty;
        }

        var end = report.IndexOf(';', start);
        return (end < 0 ? report[start..] : report[start..end]).Trim();
    }

    private static async Task<List<string>> StartAsync(ModuleBase[] modules)
    {
        var result = await ModuleHost.StartAsync(modules, applicationName: HostApart);
        if (result.Failure is not null)
        {
            throw result.Failure;
        }

        return result.Logged;
    }

    internal abstract class Worker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    internal sealed class Fulfillment : Worker;

    internal sealed class Dispatcher : Worker;

    [MultipleImplementations]
    internal interface IStatusListener;

    internal sealed class Notify : IStatusListener;

    internal sealed class Ship : IStatusListener;

    private sealed class HubModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) => builder.Services.AddSignalR();

        protected override void Configure(IApplicationBuilder app) =>
            app.UseEndpoints(endpoints => endpoints.MapHub<StatusHub>("/status"));
    }

    internal sealed class StatusHub : Microsoft.AspNetCore.SignalR.Hub;

    private sealed class EndpointsModule : ModuleBase
    {
        protected override void Configure(IApplicationBuilder app) =>
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/orders", () => "orders");
                endpoints.MapPost("/orders", () => "created");
            });
    }

    private sealed class OrdersModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder)
        {
            builder.Services.AddHostedService<Fulfillment>();
            builder.Services.AddHostedService<Dispatcher>();
            builder.Services.AddScoped<IStatusListener, Notify>();
        }
    }

    private sealed class TelemetryModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddSingleton(typeof(IHostedService), ForeignAssembly.HostedService);
    }

    private sealed class HealthModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddHealthChecks();
    }

    private sealed class OtherHealthModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddHealthChecks();
    }

    private sealed class ShippingModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) =>
            builder.Services.AddScoped<IStatusListener, Ship>();
    }
}
