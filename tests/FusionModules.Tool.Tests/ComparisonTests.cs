using FusionModules.Tool.Probe;
using static FusionModules.Tool.Tests.TopologyTests;

namespace FusionModules.Tool.Tests;

/// <summary><c>dotnet modules compare</c>: what a step changed in what each host composes, and only that.</summary>
public class ComparisonTests
{
    [Test]
    public async Task MovingProgramCsIntoAModule_ComparesEqual()
    {
        // The lambda moves from Program to Module and is renumbered; the module brings FusionModules' own.
        var before = Document(Converted("Program.<Main>$b__0_1", module: null));
        var after = Document(Converted("Catalog.Module.<ConfigureServices>b__3_0", module: "Catalog"));
        after.Hosts[0].Runtime!.Services!.Add(new() { ServiceType = "FusionModules.ModuleLedger", ServiceAssembly = "FusionModules", Assembly = "FusionModules", Implementation = "instance", Lifetime = "Singleton" });
        after.Hosts[0].Runtime!.Modules = [new() { Type = "Catalog.Module", Assembly = "Catalog" }];

        var result = Comparison.Compare(before, after);

        await Assert.That(result.Same).IsTrue();
        await Assert.That(result.Hosts.Single().Notes).IsEquivalentTo(["+ module Catalog.Module"]);
    }

    [Test]
    public async Task ARegistrationOrEndpointChange_IsADifference_AndAWinnerToo()
    {
        var before = Document(Host("catalog", [], Service("Catalog.IStore", "Catalog.Store", assembly: "Catalog")));
        before.Hosts[0].Runtime!.Endpoints = [new() { Route = "/items", Methods = ["GET"], Kind = "minimal", Assembly = "Catalog" }];
        var after = Document(Host("catalog", [], Service("Catalog.IStore", "Catalog.CachedStore", assembly: "Catalog")));
        after.Hosts[0].Runtime!.Endpoints = [new() { Route = "items", Methods = ["GET"], Kind = "minimal", Assembly = "Catalog", Authorization = ["Bearer"] }];

        var differences = Comparison.Compare(before, after).Hosts.Single().Differences;

        await Assert.That(differences).IsEquivalentTo(
        [
            "- endpoint GET /items (minimal, Catalog; no authorization)",
            "+ endpoint GET /items (minimal, Catalog; Bearer)",
            "- registration Singleton Catalog.IStore -> Catalog.Store (Catalog)",
            "+ registration Singleton Catalog.IStore -> Catalog.CachedStore (Catalog)",
        ]);
    }

    [Test]
    public async Task TheSameHostedServicesInAnotherOrder_AreADifference()
    {
        static ProbeHostedService Hosted(string type) => new() { Type = type, Assembly = "Catalog", Application = true };
        var before = Document(Host("catalog", []));
        before.Hosts[0].Runtime!.HostedServices = [Hosted("Catalog.Seed"), Hosted("Catalog.Worker")];
        var after = Document(Host("catalog", []));
        after.Hosts[0].Runtime!.HostedServices = [Hosted("Catalog.Worker"), Hosted("Catalog.Seed")];

        await Assert.That(Comparison.Compare(before, after).Hosts.Single().Differences).IsEquivalentTo(
            ["start order of the application's hosted services: Catalog.Seed, Catalog.Worker -> Catalog.Worker, Catalog.Seed"]);
    }

    [Test]
    public async Task AnErrorGone_IsANote_AndOneAdded_IsADifference()
    {
        var before = Document(Host("catalog", []));
        before.Hosts[0].Runtime!.Errors = [ProbeError.Of("endpoints", "could not read")];
        var after = Document(Host("catalog", []));
        after.Hosts[0].Runtime!.Errors = [ProbeError.Of("services", "broken")];

        var host = Comparison.Compare(before, after).Hosts.Single();

        await Assert.That(host.Differences).IsEquivalentTo(["+ error services: error"]);
        await Assert.That(host.Notes).IsEquivalentTo(["- error endpoints: error"]);
    }

    [Test]
    public async Task KeysMadeUniquePerRun_AndHostsOnOneSide_AreReported()
    {
        static ProbeService Keyed(string guid) => new() { ServiceType = "Catalog.IStore", Key = $"decorated-{guid}", ServiceAssembly = "Catalog", ImplementationType = "Catalog.Store", Implementation = "type", Lifetime = "Singleton" };
        var before = Document(Host("catalog", [], Keyed("0f8fad5b-d9cb-469f-a165-70867728950e")), Host("basket", []));
        var after = Document(Host("catalog", [], Keyed("7c9e6679-7425-40de-944b-e07fc1f90ae7")), Host("ordering", []));

        var result = Comparison.Compare(before, after);
        var writer = new StringWriter();
        result.Write(writer, "before.json", "after.json");

        await Assert.That(result.Hosts.Single().Differences).IsEmpty();
        await Assert.That(result.Same).IsFalse();
        await Assert.That(writer.ToString()).Contains("1 host compared: 1 the same, 0 different; 1 only before, 1 only after.");
    }

    [Test]
    public async Task AMergedHost_HasWhatItsServicesHad_TheirSharedRegistrationsOnce()
    {
        // Both programs registered the same validator; the host registers it once. Basket's endpoint is missing.
        var shared = Service("Shared.IClock", "Shared.SystemClock", assembly: "Shared");
        var before = Document(Host("catalog-api", [], shared, Service("Catalog.IStore", "Catalog.Store", assembly: "Catalog")), Host("basket-api", [], shared));
        before.Hosts[1].Runtime!.Endpoints = [new() { Route = "/basket", Methods = ["GET"], Kind = "minimal", Assembly = "Basket" }];
        // Two modules that call one shared library's registration: twice in the host, and no difference.
        var after = Document(Host("webapp", ["Catalog", "Basket"], shared, shared, Service("Catalog.IStore", "Catalog.Store", assembly: "Catalog")));

        var host = Comparison.CompareMerged(before, ["catalog-api", "basket-api"], after, "webapp").Hosts.Single();

        await Assert.That(host.Service).IsEqualTo("webapp = catalog-api + basket-api");
        await Assert.That(host.Differences).IsEquivalentTo(["- endpoint GET /basket (minimal, Basket; no authorization)"]);
        await Assert.That(() => Comparison.CompareMerged(before, ["basket"], after, "webapp")).Throws<InventoryInputException>();
    }

    [Test]
    [Arguments("Program.<Main>$b__0_1", "lambda")]
    [Arguments("Catalog.Module.<ConfigureServices>b__3_0", "lambda")]
    [Arguments("Catalog.Extensions.<>c__DisplayClass3_0.<AddStore>b__1", "lambda")]
    [Arguments("Catalog.Extensions.CreateStore", "Catalog.Extensions.CreateStore")]
    public async Task CompilerNames_AreMadeComparable(string name, string expected) =>
        await Assert.That(Names.Method(name)).IsEqualTo(expected);

    [Test]
    public async Task ALambdaMovedIntoAMethodOfItsOwn_IsTheSameRegistration()
    {
        // eShop's catalog: the keyed file provider moved from Program.cs into AddApplicationServices.
        var before = Document(Converted("Program.<Main>$b__0_1", module: null));
        var after = Document(Converted("Catalog.Extensions.<AddApplicationServices>b__2_0", module: null));

        await Assert.That(Comparison.Compare(before, after).Same).IsTrue();
    }

    private static HostInfo Converted(string factory, string? module)
    {
        var host = Host("catalog", []);
        host.Runtime!.Services = [new() { ServiceType = "Catalog.IStore", ServiceAssembly = "Catalog", Implementation = "factory", Factory = factory, Assembly = "Catalog", Lifetime = "Scoped", Module = module }];
        return host;
    }
}
