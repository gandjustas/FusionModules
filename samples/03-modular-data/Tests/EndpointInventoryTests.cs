using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ModularData.Tests;

/// <summary>
/// Which routes each topology serves.
/// </summary>
/// <remarks>
/// Read off the endpoint table rather than probed over HTTP: an HTTP probe of an endpoint that
/// queries the database cannot tell "not deployed" from "deployed and broken", and would drag a
/// database into a question about composition. This is also the snapshot worth keeping when
/// migrating an existing service into a module — a mechanical migration that preserves the route
/// table is very likely correct, and one that does not gives you a reviewable diff.
/// </remarks>
public class EndpointInventoryTests
{
    private static string[] RoutesFor(params string[] modules)
    {
        using var factory = new ModularWebApplicationFactory(modules);

        return [.. factory.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText!)
            .Order(StringComparer.Ordinal)];
    }

    [Test]
    public async Task TheHostAloneServesOnlyItsOwnRoute() =>
        await Assert.That(RoutesFor()).IsEquivalentTo(new[] { "/" });

    [Test]
    public async Task EntityModulesContributeTablesAndNoRoutes() =>
        // A topology of nothing but entity modules is a database schema with a web server
        // attached, which is a legitimate thing to deploy and a surprising thing to discover.
        await Assert.That(RoutesFor(Modules.OrdersEntities, Modules.CustomersEntities))
            .IsEquivalentTo(new[] { "/" });

    [Test]
    public async Task AFeatureModuleBringsItsOwnRoutesAndNobodyElses() =>
        await Assert.That(RoutesFor(Modules.CustomersEntities, Modules.CustomersApi))
            .IsEquivalentTo(new[] { "/", "/customers" });

    [Test]
    public async Task TheFullTopologyServesEveryModulesRoutes() =>
        await Assert.That(RoutesFor(Modules.All))
            .IsEquivalentTo(new[] { "/", "/billing/overdue", "/billing/seed", "/customers" });
}
