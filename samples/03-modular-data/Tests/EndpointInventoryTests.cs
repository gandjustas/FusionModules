using FusionModules.Testing;

namespace ModularData.Tests;

/// <summary>
/// Which routes each topology serves, and which module serves each.
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
    private static IReadOnlyList<ModuleRoute> RoutesFor(params string[] modules)
    {
        using var factory = new TopologyFactory(modules);

        // CustomersModule serves its route from a controller, whose attribute route is stored
        // without a leading slash, and BillingModule from MapGet, whose route keeps one. Routes()
        // writes both with one, so the difference between the two styles does not read as a
        // difference in what is deployed.
        return factory.Services.Routes();
    }

    private static string[] PatternsFor(params string[] modules) =>
        [.. RoutesFor(modules).Select(route => route.Pattern).Distinct()];

    [Test]
    public async Task TheHostAloneServesOnlyItsOwnRoute() =>
        await Assert.That(PatternsFor()).IsEquivalentTo(["/"]);

    [Test]
    public async Task EntityModulesContributeTablesAndNoRoutes() =>
        // A topology of nothing but entity modules is a database schema with a web server
        // attached, which is a legitimate thing to deploy and a surprising thing to discover.
        await Assert.That(PatternsFor(Modules.OrdersEntities, Modules.CustomersEntities))
            .IsEquivalentTo(["/"]);

    [Test]
    public async Task AFeatureModuleBringsItsOwnRoutesAndNobodyElses() =>
        // /customers is served by an internal controller, so this also asserts that
        // AllowInternalControllers did its job: without it the type is skipped for not being
        // public and the route is simply absent, with nothing said anywhere. And it is
        // CustomersModule's: the module that serves a route is part of the inventory.
        await Assert.That(RoutesFor(Modules.CustomersEntities, Modules.CustomersApi).Select(route => route.ToString()))
            .IsEquivalentTo(["GET / (host)", $"GET /customers ({Modules.CustomersApi})"]);

    [Test]
    public async Task TheFullTopologyServesEveryModulesRoutes() =>
        await Assert.That(PatternsFor(Modules.All))
            .IsEquivalentTo(["/", "/billing/overdue", "/billing/seed", "/customers"]);
}
