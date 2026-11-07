using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FusionModules;

/// <summary>
/// What the modules of one host registered, and the checks made over it once the host starts.
/// </summary>
/// <remarks>
/// <para>
/// One per host's service collection, shared by every module activated into it. Each module's
/// registrations run through <see cref="Register"/>, which records what they added. The first module
/// to build its part of the pipeline runs <see cref="Validate"/>, and the others find it done; that
/// module is the outermost startup filter, so it is also the last to finish, and it calls
/// <see cref="Configured"/> once every module has mapped its endpoints.
/// </para>
/// <para>
/// The collision rules live in <see cref="ServiceCollisions"/>; this is the one place that holds
/// state, and the one that writes the startup report.
/// </para>
/// </remarks>
internal sealed class ModuleLedger
{
    private static readonly ConditionalWeakTable<IServiceCollection, ModuleLedger> PerHost = new();

    private readonly object _gate = new();

    /// <summary>
    /// The host's service collection, the table's key: final by the time <see cref="Validate"/> runs,
    /// after the host's own registrations and a test's <c>ConfigureTestServices</c>. A value that
    /// refers to its key does not keep the key alive in a <see cref="ConditionalWeakTable{TKey, TValue}"/>.
    /// </summary>
    private IServiceCollection? _services;
    private readonly List<ModuleRegistration> _added = [];
    private bool _validated;
    private bool _checksOff;
    private Report? _report;

    private ModuleLedger(IServiceCollection services) => _services = services;

    /// <summary>The ledger for one host's service collection.</summary>
    public static ModuleLedger For(IServiceCollection services) => PerHost.GetValue(services, key => new ModuleLedger(key));

    /// <summary>Runs one module's registrations and remembers what they added.</summary>
    /// <remarks>
    /// The difference between before and after is taken by reference, not by index, so a module
    /// that removes or replaces descriptors (<c>RemoveAll</c>, <c>Replace</c>) cannot shift the
    /// indices and blame its neighbour.
    /// </remarks>
    public void Register(string moduleName, Assembly assembly, IServiceCollection services, Action register)
    {
        var before = new HashSet<ServiceDescriptor>(services, ReferenceEqualityComparer.Instance);

        register();

        var added = services.Where(descriptor => !before.Contains(descriptor)).Select(descriptor => new ModuleRegistration(moduleName, assembly, descriptor));
        lock (_gate)
        {
            _added.AddRange(added);
        }
    }

    /// <summary>
    /// Called once the pipeline is built, by the module whose <see cref="Validate"/> ran the checks:
    /// every module has mapped its endpoints by then, and the server is not listening yet. Reads the
    /// endpoints, checks the routes and writes the startup report.
    /// </summary>
    public void Configured(IServiceProvider services)
    {
        Report? report;
        bool checksOff;
        lock (_gate)
        {
            report = _report;
            checksOff = _checksOff;
        }

        if (report is null || services.GetService<ILoggerFactory>()?.CreateLogger(nameof(FusionModules)) is not { } logger)
        {
            return;
        }

        // Reading the endpoints builds them now rather than on the first request; with the checks
        // off neither the route check nor the counts are worth that.
        var application = services.GetService<IHostEnvironment>()?.ApplicationName;
        var endpoints = checksOff ? null : EndpointTable.Read(services, logger);
        if (endpoints is not null)
        {
            RouteCollisions.Warn(endpoints, report.Loaded.ToHashSet(StringComparer.Ordinal), application, logger);
        }

        Write(logger, report with
        {
            Endpoints = checksOff ? "not read: runtime topology checks are off" : Endpoints(report.Loaded, application, endpoints),
        });
    }

    /// <summary>The report as one line (<see cref="PartsLogLine"/>).</summary>
    private static void Write(ILogger logger, Report report) =>
        PartsLogLine.Write(
            logger,
            ("Topology: ", "Topology: "),
            [],
            [
                ("modules", "Modules", report.Modules),
                ("endpoints", "Endpoints", report.Endpoints),
                ("hosted services", "HostedServices", report.HostedServices),
                ("library hosted services", "LibraryHostedServices", report.LibraryHostedServices),
                ("shared on purpose", "Shared", report.Shared),
            ]);

    /// <summary>
    /// How many endpoints each loaded module serves and the host serves, by the assembly that
    /// declares their handlers; and the modules that serve none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A module meant to serve requests that serves none is the 404 this package keeps finding — an
    /// application part not added, a <c>UseEndpoints</c> not called. Listed apart, it is one line of
    /// the log rather than the first request. Many modules that serve nothing mean to — a transport,
    /// a module of entities — and a zero beside each of those read as a fault in every topology, so
    /// they are named, not counted.
    /// </para>
    /// <para>
    /// The host's own endpoints are counted as <c>host</c>: their owner is the application's
    /// assembly, <see cref="IHostEnvironment.ApplicationName"/>, whether or not that assembly is a
    /// module too — the modules part names it <c>host</c> as well — so that moving the host's own module out of
    /// its assembly does not rename its endpoints. Endpoints whose handler the framework or a library
    /// declares — health checks, an OpenAPI document, static files — are not counted: they are no
    /// module's and not the host's.
    /// </para>
    /// </remarks>
    private static string Endpoints(IReadOnlyList<string> loaded, string? application, IReadOnlyList<Endpoint>? endpoints)
    {
        if (endpoints is null)
        {
            return "not read";
        }

        var modules = loaded.ToHashSet(StringComparer.Ordinal);
        var hostLabel = EndpointTable.HostLabelFor(modules, application);
        var owned = endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => EndpointTable.ModuleOrHostLabel(endpoint, modules, application))
            .OfType<string>()
            .GroupBy(owner => owner, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // The host is not one of the modules here, even when its assembly is one of them.
        var others = loaded.Where(module => !string.Equals(module, application, StringComparison.Ordinal)).ToList();
        var counted = others.Where(owned.ContainsKey)
            .Select(module => $"{module} {owned[module]}")
            .ToList();
        if (owned.TryGetValue(hostLabel, out var host))
        {
            counted.Add($"{hostLabel} {host}");
        }

        var none = others.Where(module => !owned.ContainsKey(module)).ToList();

        return (counted.Count, none.Count) switch
        {
            (0, 0) => "none",
            (0, _) => $"none from {string.Join(", ", none)}",
            (_, 0) => string.Join(", ", counted),
            _ => $"{string.Join(", ", counted)} (none from {string.Join(", ", none)})",
        };
    }

    /// <summary>
    /// Once per host, before the pipeline is built: collisions between modules and options several
    /// modules configure. The startup report is written later, once the endpoints are there to count
    /// (<see cref="Configured"/>).
    /// </summary>
    /// <remarks>
    /// The registrations are judged as the container has them: one the host or a test removed
    /// afterwards — <c>ConfigureTestServices</c> replacing a module's service — no longer collides,
    /// and a hosted service it removed does not run.
    /// </remarks>
    /// <returns>True for the call that ran the checks, which then owes <see cref="Configured"/>.</returns>
    public bool Validate(IServiceProvider services, IConfiguration configuration)
    {
        List<ModuleRegistration> registered;
        IServiceCollection final;
        lock (_gate)
        {
            if (_validated || _services is null)
            {
                return false;
            }

            _validated = true;
            _checksOff = string.Equals(configuration[TopologyConventions.RuntimeTopologyChecksKey], "false", StringComparison.OrdinalIgnoreCase);
            registered = [.. _added];
            final = _services;
            _services = null;
            _added.Clear();
        }

        var remaining = new HashSet<ServiceDescriptor>(final, ReferenceEqualityComparer.Instance);
        var added = registered.Where(entry => remaining.Contains(entry.Descriptor)).ToList();

        IReadOnlyList<string> shared = [];
        if (!_checksOff)
        {
            var allowed = TypeNames.ReadList(configuration, TopologyConventions.AllowedServiceCollisionsKey);
            shared = ServiceCollisions.Check(added, allowed, configuration);
            SharedOptions.Warn(added, allowed, services);
        }

        var application = services.GetService<IHostEnvironment>()?.ApplicationName;
        var report = Describe(configuration, added, shared, application);
        lock (_gate)
        {
            _report = report;
        }

        return true;
    }

    /// <summary>
    /// One line saying what this replica is: its modules, the hosted services they registered, and
    /// the contracts several modules implement on purpose.
    /// </summary>
    /// <remarks>
    /// The module list is decided by configuration — <c>HOSTINGSTARTUPASSEMBLIES</c> — and so differs
    /// between deployments of one image without anything in the code saying which is which. When a
    /// job runs twice, or not at all, the first question is what the replica actually loaded. It lists
    /// what modules registered and the container still has when the checks run: the host's own hosted
    /// services are the host's, as everywhere else in the package, and a hosted service a module
    /// registered and the host or a test then removed (<c>ConfigureTestServices</c>,
    /// <c>WithoutApplicationHostedServices</c>) does not run, so it is not listed.
    /// <para>
    /// One rule decides whose a hosted service is: declared in the module's own assembly, it is the
    /// module's worker. Declared anywhere else — the framework (the health check publisher, Data
    /// Protection's key ring loader) or a library (OpenTelemetry's <c>TelemetryHostedService</c>) — it
    /// is listed apart. Such services are mostly added once with <c>TryAddEnumerable</c> by whichever
    /// module asked first, and that module is not their owner: on eShop's stand the health check
    /// publisher read "(Basket)", and elsewhere OpenTelemetry's service read as an auth module's
    /// worker. The framework's are listed without a module: which module asked first changes with the
    /// order of <c>HOSTINGSTARTUPASSEMBLIES</c>, and Data Protection's read as MVC's in one deployment
    /// and as Razor's in the next. A library's keep the module that registered it — a module may also
    /// choose to run a library's worker, and the report cannot tell which, so it says the two things
    /// it knows: whose code it is and who registered it.
    /// </para>
    /// <para>
    /// The modules come in the order <see cref="ModuleBase.GetLoadedModules"/> gives — by their
    /// references, then by name — and every other list is sorted, so one topology writes one line,
    /// in whatever order <c>HOSTINGSTARTUPASSEMBLIES</c> names its modules. Each part is a field of its own in the log entry — <c>Modules</c>,
    /// <c>Endpoints</c>, <c>HostedServices</c>, <c>LibraryHostedServices</c>, <c>Shared</c> — so a log
    /// store can answer "which replicas run this worker" without parsing the message.
    /// </para>
    /// </remarks>
    private static Report Describe(
        IConfiguration configuration,
        IReadOnlyList<ModuleRegistration> added,
        IReadOnlyList<string> shared,
        string? application)
    {
        var modules = ModuleBase.GetLoadedModules(configuration).Select(assembly => assembly.GetName().Name ?? "?").ToList();

        // The application's own assembly, when it is a module too, is named as its endpoints are:
        // one name for the host in every part of the line.
        var hostLabel = EndpointTable.HostLabelFor(modules.ToHashSet(StringComparer.Ordinal), application);
        string Label(string module) => string.Equals(module, application, StringComparison.Ordinal) ? hostLabel : module;

        var hosted = added
            .Where(entry => entry.Descriptor.ServiceType == typeof(IHostedService) && !entry.Descriptor.IsKeyedService)
            .Select(entry => (entry.Module, entry.Assembly, Worker: WorkerType(entry.Descriptor)))
            .ToList();

        var running = hosted
            .Where(IsModules)
            .Select(entry => $"{entry.Worker?.Name ?? "a hosted service from a factory"} ({Label(entry.Module)})");

        var elsewhere = hosted
            .Where(entry => !IsModules(entry))
            .Select(entry => ServiceCollisions.IsFrameworkType(entry.Worker!)
                ? entry.Worker!.Name
                : $"{entry.Worker!.Name} (registered by {Label(entry.Module)})");

        return new Report(string.Join(", ", modules.Select(Label)), string.Empty, List(running), List(elsewhere), List(shared), modules);

        // Declared in the module's own assembly. A factory whose return type says nothing is taken to
        // be the module's: the module wrote the factory.
        static bool IsModules((string Module, Assembly Assembly, Type? Worker) entry) =>
            entry.Worker is null || entry.Worker.Assembly == entry.Assembly;

        static string List(IEnumerable<string> items) =>
            string.Join(", ", items.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>The parts of the startup report; the endpoints are filled in once they are mapped.</summary>
    /// <param name="Modules">The loaded modules.</param>
    /// <param name="Endpoints">The endpoints each serves.</param>
    /// <param name="HostedServices">The modules' own hosted services.</param>
    /// <param name="LibraryHostedServices">The framework's and libraries' hosted services the modules registered.</param>
    /// <param name="Shared">The contracts several modules implement on purpose.</param>
    /// <param name="Loaded">The loaded modules' assembly names, which the endpoints are counted for.</param>
    private sealed record Report(string Modules, string Endpoints, string HostedServices, string LibraryHostedServices, string Shared, IReadOnlyList<string> Loaded);

    /// <summary>
    /// The worker a descriptor stands for. A factory is known by its declared return type, which is
    /// the worker for <c>AddHostedService(sp =&gt; new Worker(…))</c> and only <c>IHostedService</c>
    /// for a factory written against the interface.
    /// </summary>
    private static Type? WorkerType(ServiceDescriptor descriptor) =>
        descriptor.ImplementationType
        ?? descriptor.ImplementationInstance?.GetType()
        ?? (descriptor.ImplementationFactory?.Method.ReturnType is { } returned && returned != typeof(IHostedService) && returned != typeof(object) ? returned : null);

}

/// <summary>One service descriptor a module added, with the module and the assembly it came from.</summary>
internal readonly record struct ModuleRegistration(string Module, Assembly Assembly, ServiceDescriptor Descriptor);
