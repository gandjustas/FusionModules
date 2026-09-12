using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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

    [Fact]
    public void TheHostAloneServesOnlyItsOwnRoute() =>
        Assert.Equal(["/"], RoutesFor());

    [Fact]
    public void EntityModulesContributeTablesAndNoRoutes() =>
        // A topology of nothing but entity modules is a database schema with a web server
        // attached, which is a legitimate thing to deploy and a surprising thing to discover.
        Assert.Equal(["/"], RoutesFor(KnownModules.Orders_Entities, KnownModules.Customers_Entities));

    [Fact]
    public void AFeatureModuleBringsItsOwnRoutesAndNobodyElses() =>
        Assert.Equal(
            ["/", "/customers"],
            RoutesFor(KnownModules.Customers_Entities, KnownModules.CustomersModule));

    [Fact]
    public void TheFullTopologyServesEveryModulesRoutes() =>
        Assert.Equal(
            ["/", "/billing/overdue", "/billing/seed", "/customers"],
            RoutesFor(KnownModules.All));
}
