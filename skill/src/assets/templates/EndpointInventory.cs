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

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Tests;

internal static class EndpointInventory
{
    public static string[] For(IServiceProvider services) =>
        [.. services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                var route = "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/');
                return $"{string.Join(',', methods.Order(StringComparer.Ordinal))} {route}";
            })
            .Order(StringComparer.Ordinal)];
}
