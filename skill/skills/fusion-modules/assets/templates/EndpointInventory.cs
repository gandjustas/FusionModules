// The route table of a running topology, as a stable sorted list.
//
// This is the strongest available signal that a migration is correct: a conversion that preserves
// the route set is very likely right, and one that does not gives you a reviewable diff instead
// of a feeling. Capture it per service before the migration and per topology after.
//
// Read off the endpoint table rather than probed over HTTP, because an HTTP probe cannot tell
// "not deployed" from "deployed and broken", and it drags a database into a question about
// composition.
//
// One normalisation, and it is not cosmetic. An attribute route is stored without its leading
// slash and a minimal-API route with one, so /customers and customers are the same URL written
// two ways. Converting a controller to a minimal API — or the reverse, which is the common
// direction here — would otherwise diff every route it touched while changing none of them, in
// the one artefact whose whole value is that its diff means something.
//
// For the services before they are converted, which reference no FusionModules package. A test
// project that references FusionModules.Testing has the same inventory in factory.Services.Routes(),
// with the module that serves each route besides: the lines below are written as
// Routes().Select(route => $"{route.Method} {route.Pattern}").Distinct().Order(StringComparer.Ordinal)
// writes them — one method per line, "*" for any — so the two sides of a diff read alike.
//
// Left out, as Routes() leaves them out: static assets (MapStaticAssets maps an endpoint per file and
// per compressed or fingerprinted copy, most of the table in an application with a wwwroot, changing
// with every stylesheet edit), the catch-all MapStaticAssets adds in Development, and endpoints that
// only generate links. The asset types are matched by name, since they exist only from ASP.NET Core 9.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

internal static class EndpointInventory
{
    public static string[] For(IServiceProvider services) =>
        [.. services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ISuppressMatchingMetadata>()?.SuppressMatching != true)
            .Where(endpoint => !IsStaticAsset(endpoint))
            .SelectMany(endpoint =>
            {
                var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods is { Count: > 0 } accepted ? accepted : ["*"];
                return methods.Select(method => $"{method} {route}");
            })
            .Distinct()
            .Order(StringComparer.Ordinal)];

    private static bool IsStaticAsset(RouteEndpoint endpoint) =>
        endpoint.Metadata.Any(metadata => metadata.GetType().FullName == "Microsoft.AspNetCore.StaticAssets.StaticAssetDescriptor") ||
        (endpoint.RoutePattern.RawText == "{**path:file}" &&
         endpoint.Metadata.Any(metadata => metadata.GetType().FullName == "Microsoft.AspNetCore.Routing.FallbackMetadata"));
}
