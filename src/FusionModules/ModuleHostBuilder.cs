using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FusionModules;

/// <summary>
/// An <see cref="IHostApplicationBuilder"/> over what a module is given: an environment, the
/// host's configuration and a service collection.
/// </summary>
/// <remarks>
/// <para>
/// A great deal of modern .NET is written as extensions of <see cref="IHostApplicationBuilder"/>
/// rather than of <see cref="IServiceCollection"/> — every Aspire client integration, and most
/// component packages. They are not decoration over <c>AddDbContext</c>: they resolve a
/// connection string out of configuration, register a health check and add query tracing.
/// Rewriting such a registration by hand loses precisely the part that is not yours.
/// </para>
/// <para>
/// A module has everything those extensions read. It does not have the shape, and this supplies it.
/// </para>
/// </remarks>
internal sealed class ModuleHostBuilder : IHostApplicationBuilder
{
    private static readonly ConditionalWeakTable<IServiceCollection, Dictionary<object, object>> HostProperties = new();

    private readonly ServicesBuilder _builder;

    internal ModuleHostBuilder(IHostEnvironment environment, IConfiguration configuration, IServiceCollection services)
    {
        Environment = environment;
        Services = services;
        Configuration = new ModuleConfiguration(configuration);
        Properties = HostProperties.GetOrCreateValue(services);
        _builder = new ServicesBuilder(services);
    }

    internal ModuleHostBuilder(WebHostBuilderContext context, IServiceCollection services)
        : this(context.HostingEnvironment, context.Configuration, services)
    {
    }

    /// <summary>
    /// Per host, as on a real builder — an integration that uses this bag to do something once
    /// does it once, however many modules are loaded.
    /// </summary>
    /// <remarks>
    /// Keyed on the service collection because that is what "one host" means here: every module
    /// activated into a host is handed the same <see cref="IServiceCollection"/>, and two hosts in
    /// one process — which is every integration-test assembly — are handed different ones. So the
    /// sharing and the isolation both fall out of the key, with nothing to reset between tests.
    /// </remarks>
    public IDictionary<object, object> Properties { get; }

    public IConfigurationManager Configuration { get; }

    public IHostEnvironment Environment { get; }

    /// <summary>Registers into <see cref="Services"/>: <c>AddFilter</c> is a <c>Configure</c> call.</summary>
    public ILoggingBuilder Logging => _builder;

    /// <summary>Registers into <see cref="Services"/>: <c>AddMeter</c> is a <c>Configure</c> call.</summary>
    public IMetricsBuilder Metrics => _builder;

    public IServiceCollection Services { get; }

    /// <summary>Always throws: the container is one decision for a process that holds many modules.</summary>
    public void ConfigureContainer<TContainerBuilder>(
        IServiceProviderFactory<TContainerBuilder> factory, Action<TContainerBuilder>? configure = null)
        where TContainerBuilder : notnull =>
        throw new NotSupportedException(
            "A module does not choose the DI container: that is the host's decision, and one " +
            "process holds many modules.");

    private sealed class ServicesBuilder(IServiceCollection services) : ILoggingBuilder, IMetricsBuilder
    {
        public IServiceCollection Services { get; } = services;
    }

    /// <summary>
    /// The host's configuration, readable and closed to new sources.
    /// </summary>
    /// <remarks>
    /// By the time a module runs, the host's configuration is built. A source added here would go
    /// into a copy nobody reads, which is exactly the kind of silence this package exists to
    /// remove — so it throws instead, and names the override that does work.
    /// </remarks>
    private sealed class ModuleConfiguration(IConfiguration configuration) : IConfigurationManager
    {
        public string? this[string key]
        {
            get => configuration[key];
            set => configuration[key] = value;
        }

        public IDictionary<string, object> Properties { get; } = new Dictionary<string, object>();

        /// <summary>
        /// Always empty, and refuses to be added to: the host owns the sources, and this is not
        /// the host.
        /// </summary>
        /// <remarks>
        /// A plain empty list would accept <c>Sources.Add(...)</c> and <c>Sources.Insert(0, ...)</c>
        /// and do nothing with either — the same silence <see cref="Add"/> exists to break, one
        /// property along. So it throws there too, with the same sentence.
        /// </remarks>
        public IList<IConfigurationSource> Sources { get; } = new ClosedSources();

        public IConfigurationSection GetSection(string key) => configuration.GetSection(key);

        public IEnumerable<IConfigurationSection> GetChildren() => configuration.GetChildren();

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => configuration.GetReloadToken();

        public IConfigurationBuilder Add(IConfigurationSource source) =>
            throw Closed();

        internal static NotSupportedException Closed() =>
            new("A module cannot add a configuration source here: the host's configuration is " +
                "already built, and this source would never be read. Override " +
                "ConfigureAppConfiguration instead — it runs while the host's configuration is " +
                "still being assembled.");

        /// <summary>An empty source list that refuses every way of adding to it.</summary>
        private sealed class ClosedSources : IList<IConfigurationSource>
        {
            public int Count => 0;

            public bool IsReadOnly => true;

            public IConfigurationSource this[int index]
            {
                get => throw new ArgumentOutOfRangeException(nameof(index));
                set => throw Closed();
            }

            public void Add(IConfigurationSource item) => throw Closed();

            public void Insert(int index, IConfigurationSource item) => throw Closed();

            public void Clear()
            {
            }

            public bool Contains(IConfigurationSource item) => false;

            public void CopyTo(IConfigurationSource[] array, int arrayIndex)
            {
            }

            public int IndexOf(IConfigurationSource item) => -1;

            public bool Remove(IConfigurationSource item) => false;

            public void RemoveAt(int index) => throw new ArgumentOutOfRangeException(nameof(index));

            public IEnumerator<IConfigurationSource> GetEnumerator() =>
                Enumerable.Empty<IConfigurationSource>().GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public IConfigurationRoot Build() =>
            configuration as IConfigurationRoot ??
            throw new NotSupportedException(
                "A module does not build the host's configuration; it reads it.");
    }
}
