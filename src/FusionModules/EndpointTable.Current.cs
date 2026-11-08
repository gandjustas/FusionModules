using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace FusionModules;

// Compiled into FusionModules.Testing too, like EndpointTable.Owner.cs: its route inventory reads the
// endpoints the same way the startup checks do.
internal static partial class EndpointTable
{
    /// <summary>
    /// The endpoints a data source holds now.
    /// </summary>
    /// <remarks>
    /// The container's <see cref="EndpointDataSource"/> is a composite that builds its list once, the
    /// first time it is asked, and rebuilds it only when one of its sources signals a change. The
    /// application's own <c>RouteEndpointDataSource</c> never does, and a module's minimal APIs, gRPC
    /// services and hubs go into that same source whenever the host maps an endpoint of its own. A host
    /// that reads the endpoints while it is still being built — Asp.Versioning's
    /// <c>DescribeApiVersions()</c> does, for an OpenAPI UI in Development — leaves the composite
    /// without every endpoint its modules map after it. Requests still reach them: the routing
    /// middleware builds its own list once the pipeline is complete. So each source is asked instead
    /// of the composite.
    /// </remarks>
    internal static IReadOnlyList<Endpoint> Current(EndpointDataSource source) =>
        source is CompositeEndpointDataSource composite
            ? composite.DataSources.SelectMany(Current).ToList()
            : source.Endpoints;
}
