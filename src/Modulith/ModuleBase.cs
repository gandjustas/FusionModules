using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Modulith;

/// <summary>
/// Base class for a module: a class library that contributes services, configuration and
/// middleware to a host that has no compile-time knowledge of it.
/// </summary>
/// <remarks>
/// <para>
/// A module is activated by naming its assembly in the <c>HOSTINGSTARTUPASSEMBLIES</c>
/// environment variable (or the <c>hostingStartupAssemblies</c> configuration key). ASP.NET Core
/// loads the assembly, reads its <see cref="HostingStartupAttribute"/> and runs the named type.
/// Nothing else is required: no registration in the host, no plugin engine, no manifest.
/// </para>
/// <para>
/// Derived types override <see cref="ConfigureServices"/>, <see cref="Configure"/> and
/// <see cref="ConfigureAppConfiguration"/> — the same shape as the classic
/// <c>Startup</c> class, so the composition model is one a .NET developer already knows.
/// </para>
/// <para>
/// The assembly must carry <c>[assembly: HostingStartup(typeof(TModule))]</c>. Forgetting it is a
/// silent failure — the assembly loads and does nothing — so the analyzer reports it as an error.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [assembly: HostingStartup(typeof(Module))]
///
/// class Module : ModuleBase
/// {
///     protected override void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
///         =&gt; services.AddScoped&lt;IOrderService, OrderService&gt;();
///
///     protected override void Configure(IApplicationBuilder app)
///         =&gt; app.UseEndpoints(e =&gt; e.MapGroup("/orders").MapGet("/", () =&gt; "orders"));
/// }
/// </code>
/// </example>
public abstract class ModuleBase : IHostingStartup, IStartupFilter
{
    /// <summary>
    /// Configuration section holding the registry of activated modules:
    /// <c>Modulith:Modules:&lt;AssemblyName&gt;</c> = assembly-qualified name of the module type.
    /// </summary>
    /// <remarks>
    /// Using configuration as the registry is what lets the host stay ignorant of modularity:
    /// there is nothing to register and nothing to resolve, and any code that already has an
    /// <see cref="IConfiguration"/> can find out which modules are live.
    /// </remarks>
    internal const string ConfigurationSection = nameof(Modulith) + ":Modules";

    private bool _validated;

    /// <summary>
    /// Registers the module's services. Runs during host construction, before the application is built.
    /// </summary>
    /// <param name="context">The web host builder context.</param>
    /// <param name="services">The service collection to add to.</param>
    protected virtual void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Adds the module's middleware and endpoints. Runs after the host has built its own pipeline,
    /// so the host's middleware (routing, authentication, …) is already in place.
    /// </summary>
    /// <param name="app">The application builder.</param>
    protected virtual void Configure(IApplicationBuilder app)
    {
    }

    /// <summary>
    /// Adds the module's configuration sources.
    /// </summary>
    /// <param name="context">The web host builder context.</param>
    /// <param name="configuration">The configuration builder to add sources to.</param>
    protected virtual void ConfigureAppConfiguration(WebHostBuilderContext context, IConfigurationBuilder configuration)
    {
    }

    /// <summary>
    /// Returns the assemblies of the modules activated in this host, in activation order
    /// (the order they appear in <c>HOSTINGSTARTUPASSEMBLIES</c>, with the entry assembly first).
    /// </summary>
    /// <param name="configuration">Configuration of the running host.</param>
    /// <returns>The activated module assemblies, in activation order.</returns>
    /// <remarks>
    /// <para>
    /// Prefer this over <see cref="AppDomain.GetAssemblies()"/> when composing anything from
    /// modules — an EF Core model, for instance. <c>AppDomain</c> reports whatever happens to be
    /// loaded in the process, which includes assemblies that were referenced but never activated,
    /// and every module of every other host when several hosts share a process (as they do in any
    /// integration-test assembly). This method reports exactly the modules that ran.
    /// </para>
    /// <para>
    /// Activation order is a contract you can rely on: a module that composes two others — adding
    /// a relationship between their entities, say — must be listed last.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Assembly> GetLoadedModules(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var activationOrder = GetActivationOrder(configuration);

        return configuration.GetSection(ConfigurationSection)
            .GetChildren()
            .Select(entry => (entry.Key, Type: ResolveModuleType(entry.Value)))
            .Where(module => module.Type is not null)
            // Configuration children come back sorted by key, so impose the real order here.
            // The entry assembly is activated first and is not named in the variable: rank -1.
            .OrderBy(module => activationOrder.TryGetValue(module.Key, out var rank) ? rank : -1)
            .Select(module => module.Type!.Assembly)
            .ToArray();
    }

    /// <summary>
    /// Builds the configuration entries that <see cref="GetLoadedModules"/> reads, for code that
    /// runs without a web host.
    /// </summary>
    /// <param name="moduleAssemblyNames">Module assembly names, in the order they would be activated.</param>
    /// <returns>Entries to add to a configuration, via <c>AddInMemoryCollection</c>.</returns>
    /// <remarks>
    /// The registry is written by the modules themselves as they are activated, so anything that
    /// builds the application's services without starting the host sees an empty one. The case
    /// that matters is <c>dotnet ef</c>: a design-time factory that does not do this produces an
    /// empty migration, silently, because the model it built had no modules in it.
    /// </remarks>
    /// <example>
    /// <code>
    /// var configuration = new ConfigurationBuilder()
    ///     .AddJsonFile("appsettings.json")
    ///     .AddInMemoryCollection(ModuleBase.CreateModuleRegistry("Orders.Entities", "Customers.Entities"))
    ///     .Build();
    /// </code>
    /// </example>
    public static IEnumerable<KeyValuePair<string, string?>> CreateModuleRegistry(params string[] moduleAssemblyNames)
    {
        ArgumentNullException.ThrowIfNull(moduleAssemblyNames);

        var entries = new List<KeyValuePair<string, string?>>(moduleAssemblyNames.Length + 1)
        {
            // So that activation order is available to GetLoadedModules, exactly as at runtime.
            new(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', moduleAssemblyNames)),
        };

        foreach (var name in moduleAssemblyNames)
        {
            var assembly = Assembly.Load(new AssemblyName(name));
            if (assembly.GetCustomAttribute<HostingStartupAttribute>() is { } attribute)
            {
                entries.Add(new($"{ConfigurationSection}:{assembly.GetName().Name}",
                    attribute.HostingStartupType.AssemblyQualifiedName));
            }
        }

        return entries;
    }

    void IHostingStartup.Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) =>
        {
            services.AddSingleton<IStartupFilter>(this);
            ConfigureServices(context, services);
        });

        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            var moduleType = GetType();
            configuration.AddInMemoryCollection(
            [
                new($"{ConfigurationSection}:{moduleType.Assembly.GetName().Name}", moduleType.AssemblyQualifiedName)
            ]);

            ConfigureAppConfiguration(context, configuration);
        });
    }

    Action<IApplicationBuilder> IStartupFilter.Configure(Action<IApplicationBuilder> next) => app =>
    {
        Validate(app.ApplicationServices.GetRequiredService<IConfiguration>());
        next(app);
        Configure(app);
    };

    private void Validate(IConfiguration configuration)
    {
        if (_validated)
        {
            return;
        }

        ValidateRequestedModules(configuration);
        ValidateReferences(configuration);

        _validated = true;
    }

    /// <summary>
    /// Fails fast when a name in <c>HOSTINGSTARTUPASSEMBLIES</c> did not produce a module.
    /// </summary>
    /// <remarks>
    /// ASP.NET Core logs a critical message for an assembly it cannot load and then carries on
    /// starting, so a typo in a deployment variable produces an application that looks healthy,
    /// passes its readiness probe and serves 404s. This turns that into a startup failure.
    /// <para>
    /// It runs from whichever modules did load, which is the price of the host knowing nothing
    /// about modularity: if every name is misspelled there is nobody left to notice.
    /// </para>
    /// </remarks>
    private static void ValidateRequestedModules(IConfiguration configuration)
    {
        foreach (var requested in GetActivationOrder(configuration).Keys)
        {
            if (configuration[$"{ConfigurationSection}:{requested}"] is not null)
            {
                continue;
            }

            Assembly assembly;
            try
            {
                assembly = Assembly.Load(new AssemblyName(requested));
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                throw new InvalidOperationException(
                    $"'{requested}' is listed in HOSTINGSTARTUPASSEMBLIES but could not be loaded. " +
                    "Check the spelling, and that the assembly is deployed next to the host.", e);
            }

            // No attribute at all means somebody wrote a module and forgot
            // [assembly: HostingStartup(typeof(TheModule))]. Anything else — a third-party
            // hosting startup, say — is not ours to judge.
            if (assembly.GetCustomAttribute<HostingStartupAttribute>() is null)
            {
                throw new InvalidOperationException(
                    $"'{requested}' is listed in HOSTINGSTARTUPASSEMBLIES but declares no " +
                    $"[assembly: {nameof(HostingStartupAttribute)}], so nothing in it ran.");
            }
        }
    }

    /// <summary>
    /// Fails fast when this module references another module that was not activated.
    /// </summary>
    /// <remarks>
    /// The compiler only emits an assembly reference for an assembly whose types are actually
    /// used, so the reference list is precisely the set of modules this one depends on — there is
    /// nothing to declare separately and nothing that can drift out of sync. Without this check a
    /// missing module surfaces much later as a 404 or a null reference.
    /// </remarks>
    private void ValidateReferences(IConfiguration configuration)
    {
        var missing = new List<string>();

        foreach (var reference in GetType().Assembly.GetReferencedAssemblies())
        {
            if (reference.Name is not { } name)
            {
                continue;
            }

            if (configuration[$"{ConfigurationSection}:{name}"] is not null)
            {
                continue;
            }

            Assembly referenced;
            try
            {
                referenced = Assembly.Load(reference);
            }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                continue;
            }

            // Only Modulith modules are our business: third-party hosting startups
            // (Application Insights, for one) are activated by their own rules.
            if (referenced.GetCustomAttribute<HostingStartupAttribute>() is { } attribute &&
                typeof(ModuleBase).IsAssignableFrom(attribute.HostingStartupType))
            {
                missing.Add(name);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(BuildMissingModulesMessage(configuration, missing));
        }
    }

    private string BuildMissingModulesMessage(IConfiguration configuration, List<string> missing)
    {
        var moduleName = GetType().Assembly.GetName().Name;
        var requested = configuration[WebHostDefaults.HostingStartupAssembliesKey];

        return $"Module '{moduleName}' requires {string.Join(", ", missing.Select(m => $"'{m}'"))}, " +
               $"which {(missing.Count == 1 ? "was" : "were")} not activated." + Environment.NewLine +
               $"HOSTINGSTARTUPASSEMBLIES = '{requested}'" + Environment.NewLine +
               $"Add {string.Join(", ", missing.Select(m => $"'{m}'"))} to HOSTINGSTARTUPASSEMBLIES, " +
               "or stop using types from that module so the reference goes away.";
    }

    private static Dictionary<string, int> GetActivationOrder(IConfiguration configuration)
    {
        var requested = configuration[WebHostDefaults.HostingStartupAssembliesKey] ?? string.Empty;
        var names = requested.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var order = new Dictionary<string, int>(names.Length, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < names.Length; i++)
        {
            order.TryAdd(names[i], i);
        }

        return order;
    }

    private static Type? ResolveModuleType(string? assemblyQualifiedName) =>
        string.IsNullOrEmpty(assemblyQualifiedName) ? null : Type.GetType(assemblyQualifiedName, throwOnError: false);
}
