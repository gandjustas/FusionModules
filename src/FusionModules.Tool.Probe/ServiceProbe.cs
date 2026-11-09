using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Tool.Probe;

/// <summary>
/// Runs an application's entry point far enough to see what it composes, the way GetDocument.Insider
/// does for build-time OpenAPI: the services are read when the host is being built, then IServer is
/// swapped for one that listens on nothing and every hosted service but the web host's own is
/// removed, and the entry point is let run until the server "starts" — at which point the pipeline
/// is built and EndpointDataSource is complete.
/// </summary>
internal static class ServiceProbe
{
    private const string GenericWebHostService = "Microsoft.AspNetCore.Hosting.GenericWebHostService";
    private const string ServerInterface = "Microsoft.AspNetCore.Hosting.Server.IServer";

    public static void Run(Assembly target, string[] args, TimeSpan timeout, HashSet<string> application, bool allServices, ProbeOutput result)
    {
        var stopwatch = Stopwatch.StartNew();
        application.Add(target.GetName().Name!);
        ProbeError.ApplicationAssemblies = application;

        List<ServiceDescriptor>? snapshot = null;
        Dictionary<ServiceDescriptor, string>? byModule = null;
        IConfiguration? configuration = null;
        string? environmentName = null;
        var isWeb = false;
        var buildingThread = -1;
        var entryPointCompleted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        // HostBuilt, seen on the entry point's own thread before Build() returns — before the
        // application runs a line past it — so that what the endpoints need later is in hand even if
        // the application then disposes its container.
        using var hostBuilt = HostBuiltHook.Subscribe(host =>
        {
            if (Environment.CurrentManagedThreadId == buildingThread && isWeb)
            {
                Attach(host);
            }
        });

        var factory = HostFactoryResolver.ResolveHostFactory(
            target,
            waitTimeout: timeout,
            stopApplication: false,
            configureHostBuilder: builder =>
            {
                if (builder is not IHostBuilder hostBuilder)
                {
                    result.Errors.Add(ProbeError.Of("build", $"HostBuilding passed a {builder.GetType().FullName}, not an IHostBuilder."));
                    return;
                }

                // Registered last, so it runs after every ConfigureServices the application made,
                // including those WebApplicationBuilder.Host collected.
                hostBuilder.ConfigureServices((context, services) =>
                {
                    snapshot = [.. services];
                    byModule = RegisteredByModules(services);
                    configuration = context.Configuration;
                    environmentName = context.HostingEnvironment.EnvironmentName;
                    isWeb = services.Any(d => d.ServiceType.FullName == ServerInterface);
                    buildingThread = Environment.CurrentManagedThreadId;
                    Neutralize(services, isWeb);
                    if (isWeb)
                    {
                        HostDisposal.Hook(context.HostingEnvironment, OnHostDisposing);
                    }
                });
            },
            entrypointCompleted: exception => entryPointCompleted.TrySetResult(exception));

        if (factory is null)
        {
            result.Errors.Add(ProbeError.Of("resolve", "The assembly has no entry point, or does not use Microsoft.Extensions.Hosting 6.0 or later."));
            return;
        }

        object? host = null;
        try
        {
            host = factory(args);
        }
        catch (Exception exception)
        {
            result.Errors.Add(ProbeError.From("build", exception));
        }

        result.EnvironmentName = environmentName;
        result.HostingStartupAssemblies = SplitList(configuration?["hostingStartupAssemblies"]);

        if (snapshot is not null)
        {
            DescribeServices(snapshot, byModule ?? [], application, allServices, result);
        }

        if (host is null)
        {
            result.Status = "failed";
            return;
        }

        result.Status = "built";
        var services = HostServices.Of(host)!;
        var lifetime = services.GetService<IHostApplicationLifetime>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lifetime?.ApplicationStarted.Register(() => started.TrySetResult());

        var remaining = timeout - stopwatch.Elapsed;
        if (remaining < TimeSpan.FromSeconds(1))
        {
            remaining = TimeSpan.FromSeconds(1);
        }

        var winner = Task.WaitAny([started.Task, entryPointCompleted.Task], remaining);
        if (winner == 0)
        {
            result.Status = "started";
        }
        else if (winner == 1)
        {
            result.Errors.Add(entryPointCompleted.Task.Result is { } exception
                ? ProbeError.From("startup", exception)
                : ProbeError.Of("startup", "The entry point returned before the host started."));
        }
        else
        {
            result.Status = "timeout";
            result.Errors.Add(ProbeError.Of("startup",
                $"The host did not start within {timeout.TotalSeconds:0}s of the probe starting. Code between Build() and Run() is probably waiting for infrastructure that is not running (a database migration, a broker connection)."));
        }

        if (snapshot is not null)
        {
            DbContexts.Describe(snapshot, services, result);
        }

        if (isWeb)
        {
            try
            {
                ReadEndpoints(services, result.Status == "started", result);
            }
            catch (Exception exception)
            {
                result.Endpoints = null;
                result.Errors.Add(ProbeError.From("endpoints", exception));
            }
        }

        try
        {
            lifetime?.StopApplication();
            entryPointCompleted.Task.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Stopping is a courtesy; the process exits either way.
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReadEndpoints(IServiceProvider services, bool started, ProbeOutput result) => WebProbe.ReadEndpoints(services, started, result);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Attach(object host) => WebProbe.Attach(host);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OnHostDisposing() => WebProbe.Snapshot("the host being disposed");

    private const string HealthCheckService = "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService";

    /// <summary>
    /// Makes the host harmless, after its registrations were copied: nothing in what is reported
    /// changes. Hosted services go — they would connect, poll, consume. HealthCheckService becomes
    /// a no-op, because resolving the real one builds every health check's dependencies:
    /// MapHealthChecks resolves it while the application maps its endpoints, and MassTransit, for
    /// one, creates its bus (and checks its license) right there.
    /// </summary>
    private static void Neutralize(IServiceCollection services, bool isWeb)
    {
        var healthChecks = false;
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType == typeof(IHostedService)
                && !(descriptor is { IsKeyedService: false, ImplementationType.FullName: GenericWebHostService }))
            {
                services.RemoveAt(i);
            }
            else if (descriptor is { IsKeyedService: false, ServiceType.FullName: HealthCheckService })
            {
                services.RemoveAt(i);
                healthChecks = true;
            }
        }

        if (healthChecks)
        {
            try
            {
                AddNoopHealthCheckService(services);
            }
            catch (Exception)
            {
                // A health checks assembly older than the probe's: the application's own service stays out,
                // and MapHealthChecks says so if it is called.
            }
        }

        services.AddSingleton<IHostLifetime, NoopHostLifetime>();
        if (isWeb)
        {
            ReplaceServer(services);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddNoopHealthCheckService(IServiceCollection services) => HealthChecksProbe.AddNoop(services);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReplaceServer(IServiceCollection services) => WebProbe.ReplaceServer(services);

    private static void DescribeServices(List<ServiceDescriptor> snapshot, Dictionary<ServiceDescriptor, string> byModule, HashSet<string> application, bool allServices, ProbeOutput result)
    {
        result.TotalServices = snapshot.Count;
        result.Services = [];
        result.FrameworkServices = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        result.HostedServices = [];
        var modules = new List<ProbeModule>();

        var described = new List<(ServiceDescriptor Descriptor, ProbeService Service, bool Application)>();
        foreach (var descriptor in snapshot)
        {
            try
            {
                var service = Describe(descriptor);
                service.Module = byModule.GetValueOrDefault(descriptor);
                described.Add((descriptor, service, Touches(descriptor, application)));
            }
            catch (Exception exception)
            {
                result.Errors.Add(ProbeError.From("services", exception));
            }
        }

        // Over every registration, framework ones included: an application registration overridden
        // by a framework one is still overridden.
        Overrides.Mark([.. described.Select(d => d.Service)]);
        var contested = described
            .Where(d => d.Service.Wins is not null && d.Application)
            .Select(d => (d.Service.ServiceType, d.Service.Key))
            .ToHashSet();

        foreach (var (descriptor, service, touchesApplication) in described)
        {
            // FusionModules' own wiring: a module registers itself as an IStartupFilter.
            if (ModuleType(descriptor) is { } module)
            {
                if (!modules.Any(m => m.Type == TypeNames.Of(module)))
                {
                    modules.Add(new ProbeModule { Type = TypeNames.Of(module), Assembly = module.Assembly.GetName().Name });
                }

                continue;
            }

            if (descriptor.ServiceType == typeof(IHostedService))
            {
                result.HostedServices.Add(new ProbeHostedService
                {
                    Type = service.ImplementationType,
                    Factory = service.Factory,
                    Assembly = service.Assembly,
                    Application = touchesApplication,
                });
            }

            if (allServices || touchesApplication || contested.Contains((service.ServiceType, service.Key)))
            {
                result.Services.Add(service);
            }
            else
            {
                var key = descriptor.ServiceType.Assembly.GetName().Name ?? "?";
                result.FrameworkServices[key] = result.FrameworkServices.TryGetValue(key, out var count) ? count + 1 : 1;
            }
        }

        result.Modules = modules.Count > 0 ? modules : null;
    }

    /// <summary>
    /// Which module made each registration, from the record FusionModules keeps for its startup
    /// checks: the ledger the first module registers as an instance, whose list holds what each
    /// module's <c>ConfigureServices</c> added, by descriptor. Read while the host is being built, in
    /// the probe's own <c>ConfigureServices</c> — every module's has run, and the startup check that
    /// empties the list has not. Read by name: a FusionModules without the ledger, or a host without
    /// modules, leaves every registration unattributed.
    /// </summary>
    private static Dictionary<ServiceDescriptor, string> RegisteredByModules(IServiceCollection services)
    {
        var byModule = new Dictionary<ServiceDescriptor, string>(ReferenceEqualityComparer.Instance);
        var ledger = services.FirstOrDefault(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType.FullName == "FusionModules.ModuleLedger")?.ImplementationInstance;
        if (ledger?.GetType().GetField("_added", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ledger) is not System.Collections.IEnumerable added)
        {
            return byModule;
        }

        foreach (var entry in added)
        {
            var type = entry.GetType();
            if (type.GetProperty("Module")?.GetValue(entry) is string module &&
                type.GetProperty("Descriptor")?.GetValue(entry) is ServiceDescriptor descriptor)
            {
                byModule[descriptor] = module;
            }
        }

        return byModule;
    }

    private const string ModuleBase = "FusionModules.ModuleBase";

    /// <summary>The module class, when the registration is a FusionModules module registering itself.</summary>
    private static Type? ModuleType(ServiceDescriptor descriptor)
    {
        var implementation = descriptor.IsKeyedService
            ? descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType()
            : descriptor.ImplementationType ?? descriptor.ImplementationInstance?.GetType();
        for (var current = implementation?.BaseType; current is not null; current = current.BaseType)
        {
            if (current.FullName == ModuleBase)
            {
                return implementation;
            }
        }

        return null;
    }

    /// <summary>FusionModules' own assemblies: what they register is wiring, not the application.</summary>
    internal static bool IsFusionModules(string assembly) =>
        assembly.Equals("FusionModules", StringComparison.OrdinalIgnoreCase) || assembly.StartsWith("FusionModules.", StringComparison.OrdinalIgnoreCase);

    private static ProbeService Describe(ServiceDescriptor descriptor)
    {
        var service = new ProbeService
        {
            ServiceType = TypeNames.Of(descriptor.ServiceType),
            ServiceAssembly = descriptor.ServiceType.Assembly.GetName().Name,
            Lifetime = descriptor.Lifetime.ToString(),
        };

        Type? implementationType;
        object? instance;
        Delegate? factory;
        if (descriptor.IsKeyedService)
        {
            service.Key = descriptor.ServiceKey?.ToString();
            implementationType = descriptor.KeyedImplementationType;
            instance = descriptor.KeyedImplementationInstance;
            factory = descriptor.KeyedImplementationFactory;
        }
        else
        {
            implementationType = descriptor.ImplementationType;
            instance = descriptor.ImplementationInstance;
            factory = descriptor.ImplementationFactory;
        }

        if (implementationType is not null)
        {
            service.Implementation = "type";
        }
        else if (instance is not null)
        {
            service.Implementation = "instance";
            implementationType = instance.GetType();
        }
        else if (factory is not null)
        {
            service.Implementation = "factory";
            var method = factory.Method;
            service.Factory = $"{TypeNames.Of(TypeNames.Outermost(method.DeclaringType))}.{method.Name}";
            if (method.ReturnType != typeof(object) && method.ReturnType != descriptor.ServiceType)
            {
                implementationType = method.ReturnType;
            }

            service.Assembly = method.DeclaringType?.Assembly.GetName().Name;
        }

        if (implementationType is not null)
        {
            service.ImplementationType = TypeNames.Of(implementationType);
            service.Assembly ??= implementationType.Assembly.GetName().Name;
        }

        return service;
    }

    private static bool Touches(ServiceDescriptor descriptor, HashSet<string> application)
    {
        if (TypeNames.Mentions(descriptor.ServiceType, application))
        {
            return true;
        }

        if (descriptor.IsKeyedService)
        {
            return TypeNames.Mentions(descriptor.KeyedImplementationType, application)
                || TypeNames.Mentions(descriptor.KeyedImplementationInstance?.GetType(), application)
                || TypeNames.Mentions(descriptor.KeyedImplementationFactory?.Method.DeclaringType, application);
        }

        return TypeNames.Mentions(descriptor.ImplementationType, application)
            || TypeNames.Mentions(descriptor.ImplementationInstance?.GetType(), application)
            || TypeNames.Mentions(descriptor.ImplementationFactory?.Method.DeclaringType, application);
    }

    internal static List<string>? SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : [.. value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private sealed class NoopHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal static class TypeNames
{
    /// <summary>A readable name: generic arguments spelled out, no assembly-qualified noise.</summary>
    public static string Of(Type? type)
    {
        if (type is null)
        {
            return "?";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        var name = type.FullName ?? $"{type.Namespace}.{type.Name}";
        if (!type.IsGenericType)
        {
            return name.Replace('+', '.');
        }

        var tick = name.IndexOf('`');
        var bare = (tick < 0 ? name : name[..tick]).Replace('+', '.');
        var arguments = type.GetGenericArguments();
        return type.IsGenericTypeDefinition
            ? $"{bare}<{new string(',', arguments.Length - 1)}>"
            : $"{bare}<{string.Join(", ", arguments.Select(Of))}>";
    }

    /// <summary>The type a closure or a local function's display class is nested in.</summary>
    public static Type? Outermost(Type? type)
    {
        while (type?.DeclaringType is not null && type.Name.Contains('<'))
        {
            type = type.DeclaringType;
        }

        return type;
    }

    public static bool Mentions(Type? type, HashSet<string> assemblies)
    {
        if (type is null)
        {
            return false;
        }

        if (assemblies.Contains(type.Assembly.GetName().Name ?? ""))
        {
            return true;
        }

        return type.IsGenericType && !type.IsGenericTypeDefinition && type.GetGenericArguments().Any(a => Mentions(a, assemblies));
    }
}

internal static class HostServices
{
    /// <summary>
    /// The container of a host HostFactoryResolver captured, by its Services property — the lookup
    /// HostFactoryResolver.GetServiceProvider makes, private in a file kept as copied. By reflection,
    /// because the host's IHost may be another version of the interface than the probe's.
    /// </summary>
    public static IServiceProvider? Of(object? host) => host?.GetType().GetProperty("Services")?.GetValue(host) as IServiceProvider;
}
