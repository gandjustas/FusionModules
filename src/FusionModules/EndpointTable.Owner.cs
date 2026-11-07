using System.Reflection;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

namespace FusionModules;

// Whose an endpoint is, in a file of its own: FusionModules.Testing compiles this file too, so that
// its route inventory names the same owner the startup checks do, without reaching into this
// assembly's internals across a package boundary.
internal static partial class EndpointTable
{
    /// <summary>
    /// The assembly that serves an endpoint — a module's, the host's, a library's or the framework's —
    /// or null when nothing names one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A controller action belongs to its controller, wherever the method is declared: controllers that
    /// inherit their auth and settings actions from a base controller in a shared library would
    /// otherwise lose half their endpoints to that library.
    /// </para>
    /// <para>
    /// A Razor Page belongs to the assembly its page was compiled into; its endpoint names no handler
    /// method either, and without this a module serving only pages read as serving nothing.
    /// </para>
    /// <para>
    /// A gRPC method and a SignalR hub carry no handler method; they belong to their service and hub
    /// type, which their metadata names. The metadata types are matched by name, so the package needs a
    /// reference to neither: eShop's Basket, which serves only gRPC, otherwise read as serving nothing.
    /// </para>
    /// </remarks>
    internal static string? Owner(RouteEndpoint endpoint)
    {
        var type = endpoint.Metadata.GetMetadata<ControllerActionDescriptor>()?.ControllerTypeInfo
            ?? endpoint.Metadata.GetMetadata<CompiledPageActionDescriptor>()?.PageTypeInfo
            ?? endpoint.Metadata.GetMetadata<MethodInfo>()?.DeclaringType
            ?? endpoint.Metadata.Select(ServiceTypeOf).FirstOrDefault(found => found is not null);
        return type?.Assembly.GetName().Name;
    }

    /// <summary>
    /// The module or the host that serves an endpoint: its <see cref="Owner"/> when that is a loaded
    /// module or the application's own assembly, and null otherwise.
    /// </summary>
    /// <param name="endpoint">The endpoint.</param>
    /// <param name="modules">The loaded modules' assembly names.</param>
    /// <param name="application">The application's assembly name: <c>IHostEnvironment.ApplicationName</c>.</param>
    /// <remarks>
    /// What the startup report counts and the route inventory names. An endpoint whose handler the
    /// framework or a library declares — <c>MapOpenApi</c>'s document, a health check, a static file —
    /// is mapped by somebody, but nothing on the endpoint says by which module; naming the library's
    /// assembly instead read as an owner that is neither a module nor the host.
    /// </remarks>
    internal static string? ModuleOrHost(RouteEndpoint endpoint, IReadOnlySet<string> modules, string? application) =>
        Owner(endpoint) is { } owner && (modules.Contains(owner) || string.Equals(owner, application, StringComparison.Ordinal))
            ? owner
            : null;

    /// <summary>What the startup report and the route inventory call the host's own endpoints.</summary>
    internal const string HostLabel = "host";

    /// <summary>
    /// <see cref="ModuleOrHost"/> as the startup report and the route inventory write it: the
    /// application's own assembly is <see cref="HostLabel"/>, whether or not it is a module too, and a
    /// module is its assembly's name.
    /// </summary>
    /// <remarks>
    /// One label for the host's endpoints however the host is built: a host that is a module itself —
    /// its assembly carries a <c>HostingStartup</c> of its own — read as its assembly name, and the same
    /// endpoints read as <c>host</c> once that module moved out. Only a loaded module whose assembly is
    /// itself named <c>host</c> keeps the application under its own name, so that the two stay apart.
    /// </remarks>
    internal static string? ModuleOrHostLabel(RouteEndpoint endpoint, IReadOnlySet<string> modules, string? application) =>
        ModuleOrHost(endpoint, modules, application) switch
        {
            { } owner when string.Equals(owner, application, StringComparison.Ordinal) => HostLabelFor(modules, application),
            var owner => owner,
        };

    /// <summary>The label of the application's own endpoints, given the loaded modules.</summary>
    internal static string HostLabelFor(IReadOnlySet<string> modules, string? application) =>
        application is not null && modules.Contains(HostLabel) && !string.Equals(application, HostLabel, StringComparison.Ordinal)
            ? application
            : HostLabel;

    /// <summary>The service a gRPC method or the hub a SignalR endpoint belongs to; null for other metadata.</summary>
    private static Type? ServiceTypeOf(object metadata)
    {
        var property = metadata.GetType().FullName switch
        {
            "Grpc.AspNetCore.Server.GrpcMethodMetadata" => "ServiceType",
            "Microsoft.AspNetCore.SignalR.HubMetadata" => "HubType",
            _ => null,
        };

        return property is null ? null : metadata.GetType().GetProperty(property)?.GetValue(metadata) as Type;
    }
}
