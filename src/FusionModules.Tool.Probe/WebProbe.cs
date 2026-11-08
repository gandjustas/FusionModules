using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.RazorPages.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tool.Probe;

/// <summary>
/// Everything that touches ASP.NET Core types. Only reached once the application has shown it
/// registers an IServer, so a worker without Microsoft.AspNetCore.App never JITs any of it.
/// </summary>
internal static class WebProbe
{
    private const string WebApplicationBuilder = "Microsoft.AspNetCore.Builder.WebApplicationBuilder";

    private static readonly object Gate = new();

    // Caught at HostBuilt, while the container is fresh: what reading the endpoints needs later,
    // when the application may have stopped and disposed it.
    private static IServiceProvider? applicationServices;
    private static object? webApplicationBuilder;
    private static PageLoader? pageLoader;
    private static IActionDescriptorCollectionProvider? actions;

    // The endpoints as they were at the last moment the container could build them, if the host
    // was on its way down: the pipeline failing to build, or the host being disposed.
    private static List<Endpoint>? snapshot;
    private static string? snapshotSource;
    private static string? snapshotWhen;
    private static bool finished;

    // Why the WebApplication could not be found, when it could not: said if the endpoints are then missing.
    private static string? attachNote = "the host was never seen being built";

    public static void ReplaceServer(IServiceCollection services)
    {
        services.AddSingleton<IServer, NoopServer>();

        // First, so that it is the outermost filter and sees the pipeline being built even when
        // something inside it throws.
        services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new CapturingStartupFilter()));
    }

    /// <summary>
    /// The WebApplication is what Program.cs maps its endpoints on; until the pipeline is built,
    /// nothing else knows them. The host does not lead to it, but GenericWebHostService's options
    /// do: their ConfigureApplication delegate closes over WebApplicationBuilder's, and the builder
    /// keeps the application it built in a field. Called at HostBuilt, before the application exists.
    /// </summary>
    public static void Attach(object host)
    {
        if (HostServices.Of(host) is not { } services)
        {
            return;
        }

        applicationServices = services;
        try
        {
            var optionsType = Type.GetType("Microsoft.AspNetCore.Hosting.GenericWebHostServiceOptions, Microsoft.AspNetCore.Hosting");
            if (optionsType is not null
                && services.GetService(typeof(Microsoft.Extensions.Options.IOptions<>).MakeGenericType(optionsType)) is { } options
                && options.GetType().GetProperty("Value")?.GetValue(options) is { } value)
            {
                var configure = optionsType.GetProperty("ConfigureApplication")?.GetValue(value);
                webApplicationBuilder = FindBuilder(configure, 0);
                attachNote = webApplicationBuilder is null ? $"no WebApplicationBuilder behind {Describe(configure)}" : null;
            }
            else
            {
                attachNote = $"no GenericWebHostServiceOptions ({optionsType?.FullName ?? "type not found"})";
            }
        }
        catch (Exception exception)
        {
            // Without it, the endpoints come from the pipeline or not at all.
            attachNote = $"{exception.GetType().Name}: {exception.Message}";
        }

        try
        {
            pageLoader = services.GetService<PageLoader>();
            actions = services.GetService<IActionDescriptorCollectionProvider>();
        }
        catch
        {
            // MVC is optional.
        }
    }

    private static string Describe(object? target) => target switch
    {
        null => "nothing",
        Delegate d => $"{d.Method.DeclaringType?.FullName}.{d.Method.Name} on {d.Target?.GetType().FullName ?? "no target"}",
        _ => target.GetType().FullName ?? "?",
    };

    private static object? FindBuilder(object? target, int depth, HashSet<object>? seen = null)
    {
        // Delegate, closure, delegate…: GenericWebHostBuilder wraps the builder's delegate a few times over.
        seen ??= new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (target is null || depth > 12 || !seen.Add(target))
        {
            return null;
        }

        if (target.GetType().FullName == WebApplicationBuilder)
        {
            return target;
        }

        if (target is Delegate @delegate)
        {
            return @delegate.GetInvocationList().Select(d => FindBuilder(d.Target, depth + 1, seen)).FirstOrDefault(b => b is not null);
        }

        // A closure: the compiler's display class, holding the delegate or the builder in a field.
        if (!target.GetType().Name.Contains('<') && !target.GetType().Name.Contains("DisplayClass", StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (field.GetValue(target) is { } value && FindBuilder(value, depth + 1, seen) is { } builder)
            {
                return builder;
            }
        }

        return null;
    }

    /// <summary>The WebApplication the builder built, once it has: the endpoints Program.cs mapped are on it.</summary>
    private static IEndpointRouteBuilder? Application()
    {
        if (webApplicationBuilder is null)
        {
            return null;
        }

        var type = webApplicationBuilder.GetType();
        var field = type.GetField("_builtApplication", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic).FirstOrDefault(f => typeof(IEndpointRouteBuilder).IsAssignableFrom(f.FieldType));
        return field?.GetValue(webApplicationBuilder) as IEndpointRouteBuilder;
    }

    /// <summary>
    /// The endpoints as the container can still build them: the request pipeline's when it was
    /// built — complete, modules' included — else those mapped on the WebApplication so far.
    /// </summary>
    private static (List<Endpoint>? Endpoints, string? Source) Current(IServiceProvider services, bool pipelineOnly = false)
    {
        // Once the startup filters were run on a throwaway builder, the pipeline was not built: what
        // their UseEndpoints left in the routing options is theirs, not the pipeline's.
        var pipeline = filterEndpoints is null && services.GetService<EndpointDataSource>() is { } container ? CurrentEndpoints(container) : null;
        if (pipeline is { Count: > 0 } || (pipelineOnly && pipeline is not null))
        {
            return ([.. pipeline], "pipeline");
        }

        var application = Application();
        var filtered = StartupFilterEndpoints(services);
        if (application is null && filtered.Count == 0)
        {
            return (pipeline is null ? null : [], pipeline is null ? null : "pipeline");
        }

        var endpoints = application is null ? [] : application.DataSources.ToList().SelectMany(d => d.Endpoints).ToList();
        endpoints.AddRange(filtered);
        var source = (application is null ? "" : "application") + (filtered.Count == 0 ? "" : application is null ? "startup-filters" : "+startup-filters");
        return (endpoints, source);
    }

    /// <summary>
    /// The endpoints each source of the container's composite holds now. The composite builds its
    /// list once, when first asked, and the application's own route source never says it changed: a
    /// host that read the list while being built (DescribeApiVersions, for an OpenAPI UI) leaves it
    /// without what its modules map later — the same reading FusionModules' startup report does.
    /// </summary>
    private static IReadOnlyList<Endpoint> CurrentEndpoints(EndpointDataSource source) =>
        source is CompositeEndpointDataSource composite
            ? composite.DataSources.SelectMany(CurrentEndpoints).ToList()
            : source.Endpoints;

    // What the startup filters map — FusionModules' modules map in theirs — when the pipeline was
    // never built: computed once, on a throwaway builder.
    private static List<Endpoint>? filterEndpoints;
    private static readonly List<ProbeError> FilterErrors = [];

    /// <summary>
    /// Runs the application's IStartupFilter chain on a throwaway ApplicationBuilder, as
    /// GenericWebHostService would when it builds the pipeline, with UseRouting innermost in place of
    /// the application's own Configure: the endpoints the filters' UseEndpoints map are then on its
    /// route builder. Nothing is built and no middleware runs. A filter that throws (FusionModules'
    /// ledger validating the host, say) is reported and passed over; the rest of the chain still
    /// runs, and a module that threw before mapping is asked to map anyway.
    /// </summary>
    private static List<Endpoint> StartupFilterEndpoints(IServiceProvider services)
    {
        if (filterEndpoints is not null)
        {
            return filterEndpoints;
        }

        var endpoints = new List<Endpoint>();
        filterEndpoints = endpoints;
        List<IStartupFilter> filters;
        try
        {
            filters = [.. services.GetServices<IStartupFilter>().Where(f => f is not CapturingStartupFilter)];
        }
        catch (Exception exception)
        {
            FilterErrors.Add(ProbeError.From("startup-filter", exception));
            return endpoints;
        }

        if (filters.Count == 0)
        {
            return endpoints;
        }

        var app = new ApplicationBuilder(services);
        Action<IApplicationBuilder> configure = builder => builder.UseRouting();
        for (var i = filters.Count - 1; i >= 0; i--)
        {
            configure = Guarded(filters[i], configure);
        }

        try
        {
            configure(app);
        }
        catch (Exception exception)
        {
            FilterErrors.Add(ProbeError.From("startup-filter", exception));
        }

        if (app.Properties.TryGetValue("__EndpointRouteBuilder", out var value) && value is IEndpointRouteBuilder routes)
        {
            try
            {
                endpoints.AddRange(routes.DataSources.ToList().SelectMany(d => d.Endpoints));
            }
            catch (Exception exception)
            {
                FilterErrors.Add(ProbeError.From("startup-filter", exception));
            }
        }

        return endpoints;
    }

    private static Action<IApplicationBuilder> Guarded(IStartupFilter filter, Action<IApplicationBuilder> next)
    {
        var nextCalled = false;
        Action<IApplicationBuilder> configure;
        try
        {
            configure = filter.Configure(builder =>
            {
                nextCalled = true;
                next(builder);
            });
        }
        catch (Exception exception)
        {
            FilterErrors.Add(Warning(filter, exception));
            return next;
        }

        return builder =>
        {
            try
            {
                configure(builder);
            }
            catch (Exception exception)
            {
                FilterErrors.Add(Warning(filter, exception));
                if (!nextCalled)
                {
                    next(builder);
                    MapModule(filter, builder);
                }
            }
        };
    }

    /// <summary>A FusionModules module that threw before it got to map (its checks, say): its own Configure, called directly.</summary>
    private static void MapModule(IStartupFilter filter, IApplicationBuilder builder)
    {
        if (filter.GetType().GetMethod("Configure", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, [typeof(IApplicationBuilder)]) is not { } configure)
        {
            return;
        }

        try
        {
            configure.Invoke(filter, [builder]);
        }
        catch (Exception exception)
        {
            FilterErrors.Add(Warning(filter, exception));
        }
    }

    private static ProbeError Warning(IStartupFilter filter, Exception exception)
    {
        var error = ProbeError.From("startup-filter", exception);
        error.Message = $"Warning: the startup filter {TypeNames.Of(filter.GetType())} threw while its endpoints were being collected; the rest were collected. {error.Message}";
        return error;
    }

    /// <summary>Keeps the endpoints while the container still builds them: the host is going down.</summary>
    public static void Snapshot(string when)
    {
        lock (Gate)
        {
            if (finished || applicationServices is null)
            {
                return;
            }

            try
            {
                var (endpoints, source) = Current(applicationServices);
                if (endpoints is { Count: > 0 })
                {
                    (snapshot, snapshotSource, snapshotWhen) = (endpoints, source, when);
                }
            }
            catch
            {
                // Then whatever was read before stands.
            }
        }
    }

    public static void ReadEndpoints(IServiceProvider services, bool started, ProbeOutput result)
    {
        List<Endpoint>? endpoints = null;
        string? source = null;
        Exception? failure = null;
        lock (Gate)
        {
            finished = true;
            try
            {
                (endpoints, source) = Current(services, pipelineOnly: started);
            }
            catch (Exception exception)
            {
                // The application disposed its container on the way out: what was kept then.
                failure = exception;
            }

            if ((endpoints is null || (endpoints.Count == 0 && !started)) && snapshot is not null)
            {
                (endpoints, source) = (snapshot, snapshotSource);
                result.Errors.Add(ProbeError.Of("endpoints", $"Read at {snapshotWhen}, while its container could still build them."));
            }
        }

        if (!started && endpoints is not { Count: > 0 } && attachNote is not null)
        {
            result.Errors.Add(ProbeError.Of("endpoints", $"The WebApplication's own route builder could not be read: {attachNote}."));
        }

        result.Endpoints = endpoints is null ? null : [];
        result.EndpointsReadFrom = source;
        foreach (var endpoint in endpoints ?? [])
        {
            // Link generation only (MVC's conventional routes add one per area): it answers no request.
            if (endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching == true)
            {
                continue;
            }

            try
            {
                result.Endpoints!.Add(Describe(endpoint, pageLoader));
            }
            catch (Exception exception)
            {
                result.Errors.Add(ProbeError.From("endpoints", exception));
            }
        }

        if (!started)
        {
            AddUnmappedActions(endpoints, result);
        }

        if (result.Endpoints is null)
        {
            result.Errors.Add(webApplicationBuilder is null || failure is null
                ? ProbeError.Of("endpoints", "Not read: the host stopped before its request pipeline was built, and it has no WebApplication whose route builder holds what "
                                             + "it mapped. A Startup class maps in Configure, while the pipeline is built: if Configure threw (see the startup error), "
                                             + "it did so before it mapped anything; MVC knows no controllers to list instead, and no startup filter maps any.")
                : ProbeError.Of("endpoints", $"Not read: the application disposed its container before they could be ({failure.GetType().Name}: {failure.Message})."));
            return;
        }

        if (source is not null && source.StartsWith("application", StringComparison.Ordinal) && endpoints is { Count: > 0 })
        {
            result.Errors.Add(ProbeError.Of("endpoints", source.Contains("startup-filters", StringComparison.Ordinal)
                ? "Read before the request pipeline was built: what Program.cs mapped on the WebApplication before it stopped, and what the startup filters (modules) map, collected by running them on a throwaway application builder."
                : "Read from the WebApplication before its request pipeline was built: what Program.cs mapped before it stopped. No startup filter mapped anything."));
        }

        result.Errors.AddRange(FilterErrors);

        var collapsed = StaticAssets.Collapse(result.Endpoints);
        result.StaticAssetEndpoints = collapsed > 0 ? collapsed : null;
    }

    /// <summary>
    /// MVC knows its controllers and pages before anything maps them: when the host stopped before
    /// they were mapped, they are listed from the action descriptors — attribute routes as written,
    /// conventional ones without a route.
    /// </summary>
    private static void AddUnmappedActions(List<Endpoint>? endpoints, ProbeOutput result)
    {
        if (actions is null || (endpoints ?? []).Any(e => e.Metadata.GetMetadata<ActionDescriptor>() is not null))
        {
            return;
        }

        IReadOnlyList<ActionDescriptor> descriptors;
        try
        {
            descriptors = actions.ActionDescriptors.Items;
        }
        catch (Exception exception)
        {
            result.Errors.Add(ProbeError.From("endpoints", exception));
            return;
        }

        var added = 0;
        foreach (var descriptor in descriptors)
        {
            var description = new ProbeEndpoint
            {
                DisplayName = descriptor.DisplayName,
                Route = descriptor.AttributeRouteInfo?.Template is { } template ? "/" + template.TrimStart('/') : null,
                Methods = descriptor.ActionConstraints?.OfType<HttpMethodActionConstraint>().SelectMany(c => c.HttpMethods).Distinct().ToList() is { Count: > 0 } methods ? methods : null,
                Name = descriptor.AttributeRouteInfo?.Name,
                AllowAnonymous = descriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any(),
            };

            switch (descriptor)
            {
                case ControllerActionDescriptor controller:
                    description.Kind = "controller";
                    description.Handler = $"{TypeNames.Of(controller.ControllerTypeInfo.AsType())}.{controller.MethodInfo.Name}";
                    description.Assembly = controller.ControllerTypeInfo.Assembly.GetName().Name;
                    break;
                case PageActionDescriptor page:
                    description.Kind = "page";
                    description.Handler = page.ViewEnginePath;
                    break;
                default:
                    continue;
            }

            var authorization = descriptor.EndpointMetadata.OfType<IAuthorizeData>().Select(Authorization).Distinct().ToList();
            description.Authorization = authorization.Count > 0 ? authorization : null;
            (result.Endpoints ??= []).Add(description);
            added++;
        }

        if (added > 0)
        {
            result.EndpointsReadFrom = result.Endpoints!.Count == added ? "action-descriptors" : $"{result.EndpointsReadFrom}+action-descriptors";
            result.Errors.Add(ProbeError.Of("endpoints",
                $"{added} controller action(s) and page(s) listed from MVC's action descriptors: the host stopped before they were mapped. Attribute routes are as written; conventional ones have no route."));
        }
    }

    private static ProbeEndpoint Describe(Endpoint endpoint, PageLoader? pageLoader)
    {
        var description = new ProbeEndpoint
        {
            DisplayName = endpoint.DisplayName,
            Route = endpoint is RouteEndpoint routeEndpoint ? Render(routeEndpoint.RoutePattern) : null,
            Template = endpoint is RouteEndpoint { RoutePattern: var pattern } && pattern.Parameters.Any(p => pattern.RequiredValues.ContainsKey(p.Name)) ? pattern.RawText : null,
            Methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.ToList() is { Count: > 0 } methods ? methods : null,
            Name = endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
            AllowAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null,
        };

        Type? owner = null;
        if (endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action)
        {
            description.Kind = "controller";
            owner = action.ControllerTypeInfo.AsType();
            description.Handler = $"{TypeNames.Of(owner)}.{action.MethodInfo.Name}";
        }
        else if (endpoint.Metadata.GetMetadata<PageActionDescriptor>() is { } page)
        {
            description.Kind = "page";
            description.Handler = page.ViewEnginePath;
            owner = PageType(page, endpoint, pageLoader);
        }
        else if (endpoint.Metadata.GetMetadata<HubMetadata>() is { } hub)
        {
            description.Kind = "hub";
            owner = hub.HubType;
            description.Handler = TypeNames.Of(owner);
        }
        else if (endpoint.Metadata.FirstOrDefault(m => m.GetType().FullName == "Grpc.AspNetCore.Server.GrpcMethodMetadata") is { } grpc)
        {
            description.Kind = "grpc";
            owner = grpc.GetType().GetProperty("ServiceType")?.GetValue(grpc) as Type;
            var method = grpc.GetType().GetProperty("Method")?.GetValue(grpc);
            description.Handler = $"{TypeNames.Of(owner)}.{method?.GetType().GetProperty("Name")?.GetValue(method)}";
        }
        else if (endpoint.Metadata.FirstOrDefault(m => m.GetType().FullName == "Microsoft.AspNetCore.Components.Endpoints.ComponentTypeMetadata") is { } component)
        {
            // A routable Razor component: its endpoint's delegate belongs to the framework.
            description.Kind = "component";
            owner = component.GetType().GetProperty("Type")?.GetValue(component) as Type;
            description.Handler = TypeNames.Of(owner);
        }
        else if (endpoint.Metadata.GetMetadata<MethodInfo>() is { } handler)
        {
            description.Kind = "minimal";
            owner = TypeNames.Outermost(handler.DeclaringType);
            description.Handler = $"{TypeNames.Of(owner)}.{handler.Name}";
        }
        else if (endpoint.RequestDelegate?.Method is { } requestDelegate)
        {
            owner = TypeNames.Outermost(requestDelegate.DeclaringType);
            description.Handler = $"{TypeNames.Of(owner)}.{requestDelegate.Name}";
        }

        description.Assembly = owner?.Assembly.GetName().Name;

        var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(Authorization).ToList();
        if (endpoint.Metadata.GetMetadata<AuthorizationPolicy>() is not null)
        {
            authorization.Add("policy-object");
        }

        description.Authorization = authorization.Count > 0 ? authorization.Distinct().ToList() : null;
        return description;
    }

    private static string Authorization(IAuthorizeData data)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(data.Policy))
        {
            parts.Add($"policy={data.Policy}");
        }

        if (!string.IsNullOrEmpty(data.Roles))
        {
            parts.Add($"roles={data.Roles}");
        }

        if (!string.IsNullOrEmpty(data.AuthenticationSchemes))
        {
            parts.Add($"schemes={data.AuthenticationSchemes}");
        }

        return parts.Count == 0 ? "authenticated" : string.Join(";", parts);
    }

    /// <summary>
    /// The route an endpoint answers: for a conventional route, the template with the values that
    /// select this action filled in — {controller=Home}/{action=Index}/{id?} for AccountController.Login
    /// is Account/Login/{id?}. Otherwise the pattern as written.
    /// </summary>
    internal static string? Render(RoutePattern pattern)
    {
        if (!pattern.Parameters.Any(p => pattern.RequiredValues.ContainsKey(p.Name)))
        {
            return pattern.RawText;
        }

        var segments = new List<string>();
        foreach (var segment in pattern.PathSegments)
        {
            var text = new System.Text.StringBuilder();
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        text.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        text.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter
                        when pattern.RequiredValues.TryGetValue(parameter.Name, out var value) && value is string { Length: > 0 } required:
                        text.Append(required);
                        break;
                    case RoutePatternParameterPart parameter
                        when pattern.RequiredValues.ContainsKey(parameter.Name):
                        // Required to be empty (an area outside any area): no segment at all.
                        break;
                    case RoutePatternParameterPart parameter:
                        text.Append('{').Append(parameter.IsCatchAll ? "*" : "").Append(parameter.Name).Append(parameter.IsOptional ? "?" : "").Append('}');
                        break;
                }
            }

            if (text.Length > 0)
            {
                segments.Add(text.ToString());
            }
        }

        return "/" + string.Join("/", segments);
    }

    private static Type? PageType(PageActionDescriptor page, Endpoint endpoint, PageLoader? pageLoader)
    {
        var compiled = page as CompiledPageActionDescriptor;
        if (compiled is null && pageLoader is not null)
        {
            try
            {
                compiled = pageLoader.LoadAsync(page, endpoint.Metadata).GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }

        return compiled?.HandlerTypeInfo?.AsType() ?? compiled?.ModelTypeInfo?.AsType() ?? compiled?.PageTypeInfo?.AsType();
    }

    /// <summary>
    /// The outermost filter: when configuring the pipeline throws — a module's Configure, a
    /// Startup.Configure — the endpoints are kept before the host disposes its container.
    /// </summary>
    private sealed class CapturingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            try
            {
                next(app);
            }
            catch
            {
                Snapshot("the request pipeline failing to build");
                throw;
            }
        };
    }

    /// <summary>The server GetDocument.Insider swaps in: the pipeline is built, nothing listens.</summary>
    private sealed class NoopServer : IServer
    {
        public NoopServer()
        {
            Features.Set<IServerAddressesFeature>(new ServerAddressesFeature());
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();

        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken)
            where TContext : notnull => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
