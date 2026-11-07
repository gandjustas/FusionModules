using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace FusionModules;

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
/// Derived types override <see cref="ConfigureServices(IHostApplicationBuilder)"/>,
/// <see cref="Configure"/> and <see cref="ConfigureAppConfiguration"/>. The builder is the type
/// <c>Program.cs</c> has, so a service's registrations move into its module unchanged; the other
/// two are the classic <c>Startup</c> class's, so the composition model is one a .NET developer
/// already knows.
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
///     protected override void ConfigureServices(IHostApplicationBuilder builder)
///         =&gt; builder.Services.AddScoped&lt;IOrderService, OrderService&gt;();
///
///     protected override void Configure(IApplicationBuilder app)
///         =&gt; app.UseEndpoints(e =&gt; e.MapGroup("/orders").MapGet("/", () =&gt; "orders"));
/// }
/// </code>
/// </example>
public abstract class ModuleBase : IHostingStartup, IStartupFilter
{
    /// <summary>Creates the module. ASP.NET Core does, from the type the assembly's <c>HostingStartup</c> names.</summary>
    protected ModuleBase()
    {
    }

    /// <summary>
    /// Configuration section holding the registry of activated modules:
    /// <c>FusionModules:Modules:&lt;AssemblyName&gt;</c> = assembly-qualified name of the module type.
    /// </summary>
    /// <remarks>
    /// Using configuration as the registry is what lets the host stay ignorant of modularity:
    /// there is nothing to register and nothing to resolve, and any code that already has an
    /// <see cref="IConfiguration"/> can find out which modules are live.
    /// </remarks>
    internal const string ConfigurationSection = nameof(FusionModules) + ":Modules";

    private bool _validated;

    /// <summary>
    /// Registers the module's services the way the classic <c>Startup</c> did. Obsolete: override
    /// <see cref="ConfigureServices(IHostApplicationBuilder)"/>, which has the same services,
    /// configuration and environment.
    /// </summary>
    /// <param name="context">The web host builder context.</param>
    /// <param name="services">The service collection to add to.</param>
    /// <remarks>
    /// Still called, after the other overload, so a module written for 0.1 keeps working. Two
    /// overloads made modules split their registrations between them — the database in one,
    /// everything else in the other — and every explanation of the module had to say which runs
    /// first.
    /// </remarks>
    [Obsolete("Override ConfigureServices(IHostApplicationBuilder) instead: builder.Services, builder.Configuration and " +
              "builder.Environment are what this overload's parameters were. This overload still runs, after that one; " +
              "the code fix for this warning moves the body across where it can do so safely.")]
    protected virtual void ConfigureServices(WebHostBuilderContext context, IServiceCollection services)
    {
    }

    /// <summary>
    /// Registers the module's services through an <see cref="IHostApplicationBuilder"/>, for the
    /// extensions that are written against the builder rather than the service collection.
    /// </summary>
    /// <param name="builder">A builder over the host's environment, configuration and services.</param>
    /// <remarks>
    /// <para>
    /// The same type <c>Program.cs</c> has, so a service's registrations move into its module unchanged.
    /// That matters most for what is written as an extension of the builder rather than of
    /// <see cref="IServiceCollection"/>. Every Aspire client integration is of that shape, and most component packages —
    /// <c>AddNpgsqlDataSource</c>, <c>AddRedisClient</c> and their kind. They do more than register
    /// a client: they resolve the connection string out of <c>ConnectionStrings</c>, add a health
    /// check and instrument the calls. Calling them here keeps all of that; hand-registering the
    /// client keeps none of it.
    /// </para>
    /// <para>
    /// Integrations register with <c>TryAdd</c>, so an explicit registration here wins over an
    /// integration's default, before or after the call — which is the way round you want.
    /// </para>
    /// <para>
    /// The builder is the module's view of the host, not the host: its configuration is readable
    /// and closed to new sources (use <see cref="ConfigureAppConfiguration"/> for those), and it
    /// does not choose the container. <see cref="IHostApplicationBuilder.Environment"/> is the web
    /// host's environment; cast it to <c>IWebHostEnvironment</c> for <c>WebRootPath</c>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// protected override void ConfigureServices(IHostApplicationBuilder builder)
    /// {
    ///     builder.AddNpgsqlDataSource("orders");
    ///     builder.AddRedisClient("cache");
    ///     builder.Services.AddScoped&lt;IOrders, Orders&gt;();
    /// }
    /// </code>
    /// </example>
    protected virtual void ConfigureServices(IHostApplicationBuilder builder)
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
    /// Returns the assemblies of the modules activated in this host: the host's own assembly first,
    /// when it is a module, then each module after the modules it references, and otherwise by name.
    /// </summary>
    /// <param name="configuration">Configuration of the running host.</param>
    /// <returns>The activated module assemblies, the host's first, then dependencies first.</returns>
    /// <remarks>
    /// <para>
    /// Prefer this over <see cref="AppDomain.GetAssemblies()"/> when composing anything from
    /// modules — an EF Core model, for instance. Filtered by <c>HostingStartupAttribute</c>,
    /// <c>AppDomain</c> gives the same answer in the same order while one host owns the process;
    /// a module that was referenced but never activated is not loaded there, because nothing uses
    /// its types. It gives a different answer wherever several hosts share a process, as they do
    /// in any integration-test assembly: every topology's modules are loaded, so every topology
    /// sees the union. It also reports hosting startups that arrived with a package. Either way
    /// nothing throws — the model is simply composed from the wrong set.
    /// </para>
    /// <para>
    /// The order is a contract you can rely on: a module comes after every module its assembly
    /// references. A module that composes two others — adding a relationship between their
    /// entities, say — uses their types, so it comes after them, and its EF Core configuration is
    /// applied after theirs, wherever <c>HOSTINGSTARTUPASSEMBLIES</c> lists it. Modules that do not
    /// depend on each other come by name, so one set of modules has one order in every topology,
    /// and at design time. The host's own assembly — a module ASP.NET Core activates without being
    /// named in the variable, and before the ones that are — comes first, as it is activated.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Assembly> GetLoadedModules(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Asked for on every DbContext a request creates, and resolving each type from its
        // assembly-qualified name is most of the cost. The answer depends on the registry alone, so
        // it is kept per registry: the registry as it stands is the key, and one that is still being
        // written by activating modules is a different key, never a stale answer.
        var entries = configuration.GetSection(ConfigurationSection)
            .GetChildren()
            .Select(entry => (entry.Key, entry.Value))
            .ToList();
        var requested = configuration[WebHostDefaults.HostingStartupAssembliesKey] ?? string.Empty;
        var key = requested + "|" + string.Join(";", entries.Select(entry => entry.Key + "=" + entry.Value));

        return LoadedModulesByRegistry.GetOrAdd(key, _ => Compose(entries, requested));
    }

    private static readonly ConcurrentDictionary<string, IReadOnlyList<Assembly>> LoadedModulesByRegistry = new(StringComparer.Ordinal);

    private static IReadOnlyList<Assembly> Compose(List<(string Key, string? Value)> entries, string requested)
    {
        var named = new HashSet<string>(
            requested.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

        var resolved = entries
            .Select(entry => (entry.Key, Type: ResolveModuleType(entry.Value)))
            .Where(entry => entry.Type is not null)
            .Select(entry => (entry.Key, entry.Type!.Assembly))
            .OrderBy(entry => entry.Assembly.GetName().Name, StringComparer.Ordinal)
            .ToArray();

        // A module in the registry that the variable does not name is the host's own assembly:
        // ASP.NET Core activates it first, without being asked, and it comes first here too.
        var hosts = resolved.Where(entry => !named.Contains(entry.Key)).Select(entry => entry.Assembly).Distinct().ToArray();
        var modules = resolved.Select(entry => entry.Assembly).Distinct().ToArray();

        var index = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in modules)
        {
            index.TryAdd(module.GetName().Name ?? string.Empty, module);
        }

        // Each module after the modules its assembly references — the full assembly, as loaded, so
        // a reference that only a method body makes counts — and otherwise by name. Assembly
        // references cannot form a cycle, so marking a module on entry is enough.
        var placed = new HashSet<Assembly>(hosts);
        var ordered = new List<Assembly>(hosts);

        void Place(Assembly module)
        {
            if (!placed.Add(module))
            {
                return;
            }

            foreach (var dependency in module.GetReferencedAssemblies()
                         .Select(reference => reference.Name is { } name && index.TryGetValue(name, out var found) ? found : null)
                         .OfType<Assembly>()
                         .OrderBy(dependency => dependency.GetName().Name, StringComparer.Ordinal))
            {
                Place(dependency);
            }

            ordered.Add(module);
        }

        foreach (var module in modules)
        {
            Place(module);
        }

        // Read-only, not a bare array: the answer is shared, and a caller that cast it back to an
        // array could otherwise reorder every later caller's modules.
        return Array.AsReadOnly(ordered.ToArray());
    }

    /// <summary>
    /// Builds the configuration entries that <see cref="GetLoadedModules"/> reads, for code that
    /// runs without a web host.
    /// </summary>
    /// <param name="moduleAssemblyNames">Module assembly names, in the order they would be activated.</param>
    /// <returns>Entries to add to a configuration, via <c>AddInMemoryCollection</c>.</returns>
    /// <remarks>
    /// <c>dotnet ef</c> does build the application's host, so the modules named in
    /// <c>HOSTINGSTARTUPASSEMBLIES</c> activate and write themselves to the registry as usual. That
    /// is the hazard rather than the relief: the generated schema then depends on what the variable
    /// held in the shell that ran the command — empty when it is unset, one topology's tables when
    /// it names one, and no error in either case. Naming the modules here instead makes the
    /// migration the same on every machine and in CI.
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
            // So that the configuration looks as it does at runtime.
            new(WebHostDefaults.HostingStartupAssembliesKey, string.Join(';', moduleAssemblyNames)),
        };

        foreach (var name in moduleAssemblyNames)
        {
            // Only a module: at runtime nothing else writes itself to the registry either.
            var assembly = Assembly.Load(new AssemblyName(name));
            if (GetModuleType(assembly) is { } moduleType)
            {
                entries.Add(new(RegistryKey(assembly.GetName().Name), moduleType.AssemblyQualifiedName));
            }
        }

        return entries;
    }

    void IHostingStartup.Configure(IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) =>
        {
            services.AddSingleton<IStartupFilter>(this);

            // What this module registers is recorded, so that startup can tell two modules
            // registering one service apart from one module registering it twice.
            var ledger = ModuleLedger.For(services);
            services.TryAddSingleton(ledger);
            ledger.Register(ModuleName, GetType().Assembly, services, () =>
            {
                ConfigureServices(new ModuleHostBuilder(context, services));
#pragma warning disable CS0618 // still called: a module written for 0.1 keeps working
                ConfigureServices(context, services);
#pragma warning restore CS0618
            });
        });

        builder.ConfigureAppConfiguration((context, configuration) =>
        {
            var moduleType = GetType();
            configuration.AddInMemoryCollection(
            [
                new(RegistryKey(moduleType.Assembly.GetName().Name), moduleType.AssemblyQualifiedName)
            ]);

            ConfigureAppConfiguration(context, configuration);
        });
    }

    Action<IApplicationBuilder> IStartupFilter.Configure(Action<IApplicationBuilder> next) => app =>
    {
        var services = app.ApplicationServices;
        var configuration = services.GetRequiredService<IConfiguration>();
        var ledger = services.GetService<ModuleLedger>();

        // What is true of the host first — the ledger runs its checks for the first module to ask,
        // however many times a module ended up registered as a startup filter (MOD0009), and finds
        // them done for the rest — then what is true of this module.
        var checkedHere = ledger?.Validate(services, configuration) == true;

        ValidateReferences(configuration);
        next(app);
        Configure(app);

        // The first to ask is the outermost startup filter, and so the last to get here: every module
        // has mapped its endpoints, and the ledger reads them and writes the startup report.
        if (checkedHere)
        {
            ledger!.Configured(services);
        }
    };

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
        if (_validated)
        {
            return;
        }

        var missing = new List<string>();

        foreach (var reference in GetType().Assembly.GetReferencedAssemblies())
        {
            if (reference.Name is { } name &&
                configuration[RegistryKey(name)] is null &&
                IsModuleReference(reference))
            {
                missing.Add(name);
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(BuildMissingModulesMessage(configuration, missing));
        }

        _validated = true;
    }

    private string BuildMissingModulesMessage(IConfiguration configuration, List<string> missing)
    {
        var moduleName = GetType().Assembly.GetName().Name;
        var requested = configuration[WebHostDefaults.HostingStartupAssembliesKey];
        var names = string.Join(", ", missing.Select(m => $"'{m}'"));

        return $"Module '{moduleName}' requires {names}, " +
               $"which {(missing.Count == 1 ? "was" : "were")} not activated." + Environment.NewLine +
               $"HOSTINGSTARTUPASSEMBLIES = '{requested}'" + Environment.NewLine +
               $"Add {names} to HOSTINGSTARTUPASSEMBLIES, " +
               "or stop using types from that module so the reference goes away.";
    }

    /// <summary>
    /// Whether a referenced assembly is a module, answered once per process: every module asks it
    /// about the same framework assemblies, and loading one to read its attributes is the cost.
    /// Only FusionModules modules count; third-party hosting startups (Application Insights, for
    /// one) are activated by their own rules. One that cannot be loaded is not a module here.
    /// </summary>
    private static bool IsModuleReference(AssemblyName reference) =>
        ModuleReferences.GetOrAdd(
            reference.FullName,
            static (_, reference) => TryLoad(reference) is { } assembly && GetModuleType(assembly) is not null,
            reference);

    private static readonly ConcurrentDictionary<string, bool> ModuleReferences = new(StringComparer.OrdinalIgnoreCase);

    private static Assembly? TryLoad(AssemblyName name)
    {
        try
        {
            return Assembly.Load(name);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>The module type an assembly declares, or null when it declares none.</summary>
    private static Type? GetModuleType(Assembly assembly) =>
        assembly.GetCustomAttribute<HostingStartupAttribute>() is { } attribute &&
        typeof(ModuleBase).IsAssignableFrom(attribute.HostingStartupType)
            ? attribute.HostingStartupType
            : null;

    /// <summary>
    /// How the startup checks name this module: its assembly's name. A module type its assembly does
    /// not name as its one <c>HostingStartup</c> — one a test activates by hand, say — is told apart
    /// by its type name too, or every such module in an assembly would read as one.
    /// </summary>
    private string ModuleName
    {
        get
        {
            var type = GetType();
            var name = type.Assembly.GetName().Name ?? type.Name;
            var startups = type.Assembly.GetCustomAttributes<HostingStartupAttribute>().ToArray();
            return startups.Length == 1 && startups[0].HostingStartupType == type ? name : $"{name} ({type.Name})";
        }
    }

    private static string RegistryKey(string? assemblyName) => $"{ConfigurationSection}:{assemblyName}";

    private static Type? ResolveModuleType(string? assemblyQualifiedName) =>
        string.IsNullOrEmpty(assemblyQualifiedName) ? null : Type.GetType(assemblyQualifiedName, throwOnError: false);
}
