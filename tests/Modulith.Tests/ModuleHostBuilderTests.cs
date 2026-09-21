using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modulith.Tests;

/// <summary>
/// The builder a module is handed, exercised through the seam a module actually uses: a real
/// host, a real hosting startup, a real activation. Constructing the shim directly would test a
/// type no module can reach.
/// </summary>
public class ModuleHostBuilderTests
{
    [Test]
    public async Task Configuration_Read_SeesHostValues()
    {
        var module = await ActivateAsync(("Orders:PageSize", "50"));

        await Assert.That(module.SeenPageSize).IsEqualTo("50");
    }

    [Test]
    public async Task Environment_IsTheHostEnvironment()
    {
        var module = await ActivateAsync();

        await Assert.That(module.SeenEnvironmentName).IsEqualTo(Environments.Development);
    }

    [Test]
    public async Task Services_AreTheSameCollectionAsTheOtherOverload()
    {
        var module = await ActivateAsync();

        await Assert.That(module.ServicesAreTheSameInstance).IsTrue();
    }

    [Test]
    public async Task BuilderOverload_RunsBeforeTheServiceCollectionOverload()
    {
        // Integrations register with TryAdd, so an explicit registration in the classic overload
        // has to be able to beat an integration's default. That only holds in this order.
        var module = await ActivateAsync();

        await Assert.That(module.CallOrder).IsEquivalentTo(new[] { "builder", "services" });
    }

    [Test]
    public async Task Logging_And_Metrics_ShareServices()
    {
        var module = await ActivateAsync();

        await Assert.That(module.LoggingSharesServices).IsTrue();
        await Assert.That(module.MetricsSharesServices).IsTrue();
    }

    [Test]
    public async Task Configuration_Add_Throws()
    {
        // The silence this replaces: a source added to a copy nobody reads. The message has to
        // name the override that does work, or the throw is just a different dead end.
        var module = await ActivateAsync();

        await Assert.That(module.ConfigurationAddFailure).IsNotNull();
        await Assert.That(module.ConfigurationAddFailure!).Contains("ConfigureAppConfiguration");
    }

    [Test]
    public async Task Configuration_Sources_RefusesRatherThanSwallows()
    {
        // Add(...) throwing is not enough on its own: Sources is the other way in, and an empty
        // List<T> there would take the source and drop it — the same silence, one property along.
        var module = await ActivateAsync();

        await Assert.That(module.SourcesAddFailure).IsNotNull();
        await Assert.That(module.SourcesAddFailure!).Contains("ConfigureAppConfiguration");
        await Assert.That(module.SourcesInsertFailure).IsNotNull();
    }

    [Test]
    public async Task Properties_AreSharedByEveryModuleInOneHost()
    {
        // The bag an integration uses to do something once per application. Once per application
        // is what it has to mean, or "once" quietly becomes "once per module".
        var (first, second) = await ActivateTwoAsync();

        await Assert.That(first.SeenProperties).IsSameReferenceAs(second.SeenProperties);
    }

    [Test]
    public async Task Properties_AreNotSharedAcrossHosts()
    {
        // Which matters here more than in production: an integration-test assembly builds a host
        // per topology in one process, and a bag that leaked between them would carry a decision
        // from one test into the next.
        var first = await ActivateAsync();
        var second = await ActivateAsync();

        await Assert.That(first.SeenProperties).IsNotSameReferenceAs(second.SeenProperties);
    }

    [Test]
    public async Task ConfigureContainer_Throws()
    {
        var module = await ActivateAsync();

        await Assert.That(module.ConfigureContainerFailure).IsNotNull();
    }

    private static async Task<ProbeModule> ActivateAsync(params (string Key, string Value)[] settings) =>
        (await ActivateAsync(1, settings))[0];

    private static async Task<(ProbeModule First, ProbeModule Second)> ActivateTwoAsync()
    {
        var probes = await ActivateAsync(2);

        return (probes[0], probes[1]);
    }

    private static async Task<ProbeModule[]> ActivateAsync(int modules, params (string Key, string Value)[] settings)
    {
        var probes = Enumerable.Range(0, modules).Select(_ => new ProbeModule()).ToArray();

        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));

        // Exactly what ASP.NET Core does with each assembly named in HOSTINGSTARTUPASSEMBLIES.
        foreach (var probe in probes)
        {
            ((IHostingStartup)probe).Configure(builder.WebHost);
        }

        await using var app = builder.Build();

        return probes;
    }

    private sealed class ProbeModule : ModuleBase
    {
        public List<string> CallOrder { get; } = [];
        public string? SeenPageSize { get; private set; }
        public string? SeenEnvironmentName { get; private set; }
        public string? ConfigurationAddFailure { get; private set; }
        public string? SourcesAddFailure { get; private set; }
        public string? SourcesInsertFailure { get; private set; }
        public IDictionary<object, object>? SeenProperties { get; private set; }
        public string? ConfigureContainerFailure { get; private set; }
        public bool ServicesAreTheSameInstance { get; private set; }
        public bool LoggingSharesServices { get; private set; }
        public bool MetricsSharesServices { get; private set; }

        private IServiceCollection? _fromBuilder;

        protected override void ConfigureServices(IHostApplicationBuilder builder)
        {
            CallOrder.Add("builder");

            _fromBuilder = builder.Services;
            SeenPageSize = builder.Configuration["Orders:PageSize"];
            SeenEnvironmentName = builder.Environment.EnvironmentName;
            LoggingSharesServices = ReferenceEquals(builder.Logging.Services, builder.Services);
            MetricsSharesServices = ReferenceEquals(builder.Metrics.Services, builder.Services);

            try
            {
                builder.Configuration.AddInMemoryCollection([new("Orders:PageSize", "1")]);
            }
            catch (NotSupportedException e)
            {
                ConfigurationAddFailure = e.Message;
            }

            try
            {
                builder.Configuration.Sources.Add(new MemoryConfigurationSource());
            }
            catch (NotSupportedException e)
            {
                SourcesAddFailure = e.Message;
            }

            try
            {
                builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource());
            }
            catch (NotSupportedException e)
            {
                SourcesInsertFailure = e.Message;
            }

            SeenProperties = builder.Properties;

            try
            {
                builder.ConfigureContainer(new DefaultServiceProviderFactory());
            }
            catch (NotSupportedException e)
            {
                ConfigureContainerFailure = e.Message;
            }
        }

        protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
        {
            CallOrder.Add("services");
            ServicesAreTheSameInstance = ReferenceEquals(_fromBuilder, services);
        }
    }
}
