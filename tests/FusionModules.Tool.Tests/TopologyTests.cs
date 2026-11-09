using FusionModules.Tool.Probe;

namespace FusionModules.Tool.Tests;

/// <summary>
/// <c>dotnet modules topology</c> and the collisions under it: the hosts of an inventory as one host,
/// under the startup check's rules.
/// </summary>
public class TopologyTests
{
    [Test]
    public async Task TwoModulesImplementingOneContract_FailStartup_AndTheSameImplementationTwiceDoesNot()
    {
        // eShop: each outbox registers its own IIntegrationEventLogService.
        var inventory = Document(
            Host("catalog-api", ["Catalog.API"],
                Service("eShop.IIntegrationEventLogService", "eShop.EventLog<Catalog.CatalogContext>", module: "Catalog.API"),
                Service("Shared.IClock", "Shared.SystemClock", module: "Catalog.API", assembly: "Shared")),
            Host("ordering-api", ["Ordering.API"],
                Service("eShop.IIntegrationEventLogService", "eShop.EventLog<Ordering.OrderingContext>", module: "Ordering.API"),
                Service("Shared.IClock", "Shared.SystemClock", module: "Ordering.API", assembly: "Shared")));

        var report = Topology.Preview(inventory, []);

        await Assert.That(report.FailsStartup).IsTrue();
        var collision = report.Collisions.Services.Single();
        await Assert.That(collision.ServiceType).IsEqualTo("eShop.IIntegrationEventLogService");
        await Assert.That(collision.ByModules).IsTrue();
        await Assert.That(collision.Sites.Select(site => site.Service)).IsEquivalentTo(["catalog-api", "ordering-api"]);
    }

    [Test]
    public async Task TwoModulesOfOneHost_Collide_TooAsTheStartupCheckSays()
    {
        // A host that already composes two modules: the check is per module, not per service.
        var inventory = Document(Host("webapp", ["Catalog.API", "Ordering.API"],
            Service("eShop.IIntegrationEventLogService", "eShop.EventLog<Catalog.CatalogContext>", module: "Catalog.API"),
            Service("eShop.IIntegrationEventLogService", "eShop.EventLog<Ordering.OrderingContext>", module: "Ordering.API")));

        var collision = Topology.Preview(inventory, []).Collisions.Services.Single();

        await Assert.That(collision.Sites.Select(site => site.Module!)).IsEquivalentTo(["Catalog.API", "Ordering.API"]);
    }

    [Test]
    public async Task OneTypeName_FromTwoAssemblies_IsTwoImplementations()
    {
        // A clock compiled into each service from linked source: the same name, two types to the container.
        var inventory = Document(
            Host("a", ["A"], Service("Shared.IClock", "Shared.SystemClock", module: "A", assembly: "A")),
            Host("b", ["B"], Service("Shared.IClock", "Shared.SystemClock", module: "B", assembly: "B")));

        await Assert.That(Topology.Preview(inventory, []).FailsStartup).IsTrue();
    }

    [Test]
    public async Task WhatTheProgramsRegister_IsToReconcile_NotACollision()
    {
        // Each program's AddCarter scans its own assembly; the host will call it once.
        var inventory = Document(
            Host("basket-api", ["Basket.Api"],
                Service("Carter.ICarterModule", "Basket.Api.GetBasket"),
                Service("Basket.IBasketRepository", "Basket.BasketRepository", module: "Basket.Api")),
            Host("catalog-api", ["Catalog.Api"], Service("Carter.ICarterModule", "Catalog.Api.GetProducts")));

        var report = Topology.Preview(inventory, []);

        await Assert.That(report.FailsStartup).IsFalse();
        await Assert.That(report.Collisions.Services.Single().ByModules).IsFalse();
    }

    [Test]
    public async Task AServiceWithoutAModule_CountsAsItsFutureModule()
    {
        var inventory = Document(
            Host("a", [], Service("Shared.IStore", "A.Store")),
            Host("b", [], Service("Shared.IStore", "B.Store")));

        var report = Topology.Preview(inventory, []);

        await Assert.That(report.FailsStartup).IsTrue();
        await Assert.That(report.WithoutModules).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task TheFrameworksContracts_AndDbContextOptions_AreLeftAlone()
    {
        var inventory = Document(
            Host("a", ["A"],
                Service("Microsoft.Extensions.Options.IConfigureOptions<A.Options>", "A.Configure", module: "A", serviceAssembly: "Microsoft.Extensions.Options"),
                Service("Microsoft.EntityFrameworkCore.DbContextOptions", "A.Options", module: "A", serviceAssembly: "Microsoft.EntityFrameworkCore")),
            Host("b", ["B"],
                Service("Microsoft.Extensions.Options.IConfigureOptions<A.Options>", "B.Configure", module: "B", serviceAssembly: "Microsoft.Extensions.Options"),
                Service("Microsoft.EntityFrameworkCore.DbContextOptions", "B.Options", module: "B", serviceAssembly: "Microsoft.EntityFrameworkCore")));

        await Assert.That(Topology.Preview(inventory, []).Collisions.Services).IsEmpty();
    }

    [Test]
    public async Task EndpointNames_TypeNames_AndDbContexts_AreFound()
    {
        var a = Host("a", ["A"], Service("Clients.Catalog", "Clients.Catalog", module: "A", serviceAssembly: "A"));
        a.Runtime!.Endpoints = [new() { Route = "/orders/{id}", Methods = ["GET"], Name = "GetOrder" }];
        a.Runtime.DbContexts = [new() { Type = "Shared.AuditContext", Provider = "Npgsql", ConnectionString = "Database=a" }];
        var b = Host("b", ["B"], Service("Clients.Catalog", "Clients.Catalog", module: "B", serviceAssembly: "B"));
        b.Runtime!.Endpoints = [new() { Route = "/basket/{id}", Methods = ["GET"], Name = "GetOrder" }];
        b.Runtime.DbContexts = [new() { Type = "Shared.AuditContext", Provider = "Npgsql", ConnectionString = "Database=b" }];

        var collisions = Topology.Preview(Document(a, b), []).Collisions;

        await Assert.That(collisions.EndpointNames.Single().Name).IsEqualTo("GetOrder");
        await Assert.That(collisions.TypeNames.Single().Name).IsEqualTo("Clients.Catalog");
        await Assert.That(collisions.DbContexts.Single().Type).IsEqualTo("Shared.AuditContext");
    }

    [Test]
    public async Task TheReport_GivesEachKindWithWhatToDo_AndAnUnknownHostIsAnError()
    {
        var inventory = Document(
            Host("catalog-api", ["Catalog.API"], Service("eShop.ILog", "eShop.Log<A>", module: "Catalog.API")),
            Host("ordering-api", ["Ordering.API"], Service("eShop.ILog", "eShop.Log<B>", module: "Ordering.API")));
        inventory.Hosts[0].Configuration = new() { ["appsettings.json"] = new() { ["EventBus:SubscriptionClientName"] = "Catalog" } };
        inventory.Hosts[1].Configuration = new() { ["appsettings.json"] = new() { ["EventBus:SubscriptionClientName"] = "Ordering" } };

        var writer = new StringWriter();
        Topology.Preview(inventory, ["catalog-api", "ORDERING-API"]).Write(writer);
        var text = writer.ToString();

        await Assert.That(text).Contains("Fails startup: modules register one service differently (1):");
        await Assert.That(text).Contains("One configuration key, two values (1):");
        await Assert.That(text).Contains("What to do:");
        await Assert.That(text).Contains("1 service collision that fails startup, 1 configuration key with two values.");
        await Assert.That(() => Topology.Preview(inventory, ["basket-api"])).Throws<InventoryInputException>();
    }

    [Test]
    public async Task Pairs_SayWhichServicesMergeCleanly()
    {
        var inventory = Document(
            Host("a", ["A"], Service("Shared.IStore", "A.Store", module: "A")),
            Host("b", ["B"], Service("Shared.IStore", "B.Store", module: "B")),
            Host("c", ["C"], Service("C.IOwn", "C.Own", module: "C")));

        await Assert.That(Topology.Pairs(inventory, []).ToList()).IsEquivalentTo(
        [
            "a + b: 1 service collision that fails startup",
            "a + c: nothing",
            "b + c: nothing",
        ]);
    }

    internal static InventoryDocument Document(params HostInfo[] hosts) => new() { Tool = "test", Hosts = [.. hosts] };

    internal static HostInfo Host(string service, string[] modules, params ProbeService[] services) => new()
    {
        Service = service,
        Project = $"src/{service}/{service}.csproj",
        Runtime = new ProbeOutput
        {
            Status = "started",
            Services = [.. services],
            Modules = [.. modules.Select(module => new ProbeModule { Type = module + ".Module", Assembly = module })],
        },
    };

    internal static ProbeService Service(string type, string implementation, string? module = null, string? assembly = null, string? serviceAssembly = null) => new()
    {
        ServiceType = type,
        ServiceAssembly = serviceAssembly ?? type[..type.LastIndexOf('.')],
        Implementation = "type",
        ImplementationType = implementation,
        Assembly = assembly ?? module,
        Lifetime = "Singleton",
        Module = module,
    };
}
