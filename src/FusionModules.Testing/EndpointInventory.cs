using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FusionModules.Testing;

/// <summary>
/// The routes a started host serves, each with the module that serves it.
/// </summary>
/// <remarks>
/// Read off the endpoint table rather than probed over HTTP: a probe of an endpoint that queries the
/// database cannot tell "not deployed" from "deployed and broken". The inventory is also the
/// snapshot worth keeping when moving a service into a module — a mechanical migration that keeps
/// the route table is very likely right, and one that does not gives a reviewable diff.
/// </remarks>
public static class EndpointInventory
{
    /// <summary>
    /// The metadata a static asset's endpoint carries — <c>MapStaticAssets</c>, and the Razor class
    /// libraries' assets served through it — matched by name: the type is in the shared framework from
    /// ASP.NET Core 9, and this package's net8.0 build, which a net9.0 application also uses, cannot
    /// name it.
    /// </summary>
    private const string StaticAssetMetadata = "Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor";

    /// <summary>The pattern of the catch-all <c>MapStaticAssets</c> maps in Development.</summary>
    private const string DevelopmentFallbackPattern = "{**path:file}";

    /// <summary>What marks a <c>MapFallback</c> endpoint; internal to ASP.NET Core, so matched by name.</summary>
    private const string FallbackMetadata = "Microsoft.AspNetCore.Routing.FallbackMetadata";

    /// <summary>
    /// Every route the host matches requests against: one entry per HTTP method, ordered by pattern,
    /// then method, then module.
    /// </summary>
    /// <param name="services">
    /// The started host's services: <c>factory.Services</c>, which starts the host if it has not
    /// started yet.
    /// </param>
    /// <param name="includeStaticAssets">
    /// Whether to list the static assets <c>MapStaticAssets</c> serves (ASP.NET Core 9 and later); false
    /// by default. There is an endpoint per file, and per compressed and fingerprinted copy of it: in
    /// an application with a <c>wwwroot</c> or a Razor class library they are most of the table —
    /// about 534 of the 542 routes of the first application converted — and they change with every
    /// edit of a stylesheet. The catch-all <c>{**path:file}</c> that <c>MapStaticAssets</c> adds in
    /// Development goes with them. Static files served by <c>UseStaticFiles</c> are middleware, not
    /// endpoints, and are never listed.
    /// </param>
    /// <returns>The routes, in an order that is the same on every run.</returns>
    /// <remarks>
    /// <para>
    /// <see cref="ModuleRoute.Module"/> is what the FusionModules startup report counts, and as it
    /// writes it: the loaded module that serves the route, or <see cref="ModuleRoute.Host"/> when the
    /// application's assembly, <c>IHostEnvironment.ApplicationName</c>, serves it — whether or not
    /// that assembly is a module too. What serves a route is
    /// the assembly that declares a minimal API's handler, a controller (wherever its action is
    /// declared), a Razor Page, a gRPC service or a SignalR hub. A route whose handler is the
    /// framework's or a library's — <c>MapOpenApi</c>'s document, a health check, a bare
    /// <c>RequestDelegate</c>, a static asset — has null: nothing on the endpoint says which module
    /// mapped it.
    /// </para>
    /// <para>
    /// Patterns start with a slash however they were written: an attribute route is stored without
    /// one and a minimal API's with one, and the two would otherwise read as different routes.
    /// Endpoints that only generate links and never match a request are left out.
    /// </para>
    /// <para>
    /// Reading the endpoints builds them, as the first request would; a handler that cannot be built
    /// throws here, and the test fails with that rather than with a 500 later.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ModuleRoute> Routes(this IServiceProvider services, bool includeStaticAssets = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        var modules = ModuleBase.GetLoadedModules(services.GetRequiredService<IConfiguration>())
            .Select(assembly => assembly.GetName().Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var application = services.GetService<IHostEnvironment>()?.ApplicationName;

        return services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching != true)
            .Where(endpoint => includeStaticAssets || !IsStaticAsset(endpoint))
            .SelectMany(endpoint =>
            {
                var module = EndpointTable.ModuleOrHostLabel(endpoint, modules, application);
                var pattern = Pattern(endpoint.RoutePattern);
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } accepted
                    ? accepted
                    : [ModuleRoute.AnyMethod];
                return methods.Select(method => new ModuleRoute(module, method, pattern));
            })
            .Distinct()
            .OrderBy(route => route.Pattern, StringComparer.Ordinal)
            .ThenBy(route => route.Method, StringComparer.Ordinal)
            .ThenBy(route => route.Module, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// A static asset's endpoint, or the catch-all <c>MapStaticAssets</c> adds in Development — the
    /// environment a test host runs in — to serve files that are not in its manifest yet. The
    /// catch-all carries no descriptor; it is the fallback endpoint with the pattern
    /// <c>{**path:file}</c>.
    /// </summary>
    private static bool IsStaticAsset(RouteEndpoint endpoint) =>
        endpoint.Metadata.Any(metadata => metadata.GetType().FullName == StaticAssetMetadata) ||
        (endpoint.RoutePattern.RawText == DevelopmentFallbackPattern &&
         endpoint.Metadata.Any(metadata => metadata.GetType().FullName == FallbackMetadata));

    /// <summary>The pattern as it was written, with one leading slash.</summary>
    private static string Pattern(RoutePattern pattern) =>
        "/" + (pattern.RawText ?? Rebuild(pattern)).TrimStart('/');

    /// <summary>A pattern built in code has no text of its own; this is it, written out.</summary>
    private static string Rebuild(RoutePattern pattern) =>
        string.Join("/", pattern.PathSegments.Select(segment => string.Concat(segment.Parts.Select(part => part switch
        {
            RoutePatternLiteralPart literal => literal.Content,
            RoutePatternSeparatorPart separator => separator.Content,
            RoutePatternParameterPart parameter =>
                "{" + (parameter.IsCatchAll ? parameter.EncodeSlashes ? "*" : "**" : string.Empty) + parameter.Name +
                string.Concat(parameter.ParameterPolicies.Select(policy => ":" + policy.Content)) +
                (parameter.Default is { } value ? "=" + Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : string.Empty) +
                (parameter.IsOptional ? "?" : string.Empty) + "}",
            _ => string.Empty,
        }))));
}
