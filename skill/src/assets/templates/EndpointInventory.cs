// The route table of a running topology, as a stable sorted list.
//
// This is the strongest available signal that a migration is correct: a conversion that preserves
// the route set is very likely right, and one that does not gives you a reviewable diff instead
// of a feeling. Capture it per service before the migration and per topology after.
//
// Read off the endpoint table rather than probed over HTTP, because an HTTP probe cannot tell
// "not deployed" from "deployed and broken", and it drags a database into a question about
// composition.

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
                return $"{string.Join(',', methods.Order(StringComparer.Ordinal))} {endpoint.RoutePattern.RawText}";
            })
            .Order(StringComparer.Ordinal)];
}
