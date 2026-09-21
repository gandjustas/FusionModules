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
            // An attribute route is stored without its leading slash and a minimal-API route with
            // one, so /customers and customers are the same URL written two ways. CustomersModule
            // serves its route from a controller and BillingModule from MapGet; without this the
            // inventory would report the difference between the two styles as a difference in
            // what is deployed.
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText!.TrimStart('/'))
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
        // /customers is served by an internal controller, so this also asserts that
        // AllowInternalControllers did its job: without it the type is skipped for not being
        // public and the route is simply absent, with nothing said anywhere.
        await Assert.That(RoutesFor(Modules.CustomersEntities, Modules.CustomersApi))
            .IsEquivalentTo(new[] { "/", "/customers" });

    [Test]
    public async Task TheFullTopologyServesEveryModulesRoutes() =>
        await Assert.That(RoutesFor(Modules.All))
            .IsEquivalentTo(new[] { "/", "/billing/overdue", "/billing/seed", "/customers" });
}
