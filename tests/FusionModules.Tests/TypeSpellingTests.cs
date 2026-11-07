using Microsoft.Extensions.Hosting;
using FusionModules.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FusionModules.Tests;

/// <summary>
/// Every spelling in <see cref="TypeSpellingCases"/> names the type for the runtime's
/// <c>FusionModules:AllowedServiceCollisions</c>.
/// </summary>
/// <remarks>
/// Two modules register the same service type through factories of their own, which the startup
/// check reads as a collision unless the allow-list names the type.
/// </remarks>
public class TypeSpellingTests
{
    public static IEnumerable<Func<string>> WorkerOfOrder() =>
        TypeSpellingCases.WorkerOfOrder.Select(spelling => new Func<string>(() => spelling));

    public static IEnumerable<Func<string>> PairOfOrders() =>
        TypeSpellingCases.PairOfOrders.Select(spelling => new Func<string>(() => spelling));

    [Test]
    public async Task WithoutAnEntry_TheTwoRegistrationsCollide()
    {
        // So that the tests below are about the spelling, not about a check that never fires.
        var result = await ModuleHost.StartAsync([new FirstModule(), new SecondModule()]);

        await Assert.That(result.Failure).IsNotNull();
    }

    [Test]
    [MethodDataSource(nameof(WorkerOfOrder))]
    public async Task TheAllowList_AcceptsTheSpelling(string spelling)
    {
        var result = await ModuleHost.StartAsync([new FirstModule(), new SecondModule()],
            settings: [("FusionModules:AllowedServiceCollisions", spelling), ("FusionModules:AllowedServiceCollisions:1", PairEntry)]);

        await Assert.That(result.Failure).IsNull();
    }

    [Test]
    [MethodDataSource(nameof(PairOfOrders))]
    public async Task ATwoArgumentGeneric_IsOneEntry(string spelling)
    {
        var result = await ModuleHost.StartAsync([new FirstModule(), new SecondModule()],
            settings: [("FusionModules:AllowedServiceCollisions", spelling), ("FusionModules:AllowedServiceCollisions:1", WorkerEntry)]);

        await Assert.That(result.Failure).IsNull();
    }

    [Test]
    public async Task ACommaBetweenNames_SeparatesThem()
    {
        var result = await ModuleHost.StartAsync([new FirstModule(), new SecondModule()],
            settings: ("FusionModules:AllowedServiceCollisions", TypeSpellingCases.BothWorkers));

        await Assert.That(result.Failure).IsNull();
    }

    public static IEnumerable<Func<string>> ItemOfBox() =>
        TypeSpellingCases.ItemOfBox.Select(spelling => new Func<string>(() => spelling));

    [Test]
    public async Task WithoutAnEntry_TheNestedTypesCollide()
    {
        var result = await ModuleHost.StartAsync([new FirstItemModule(), new SecondItemModule()]);

        await Assert.That(result.Failure).IsNotNull();
        await Assert.That(result.Failure!.Message).Contains("Shop.Box<Order>+Item");
    }

    [Test]
    [MethodDataSource(nameof(ItemOfBox))]
    public async Task ATypeNestedInAGeneric_IsNamedAsCSharpWritesIt(string spelling)
    {
        var result = await ModuleHost.StartAsync([new FirstItemModule(), new SecondItemModule()],
            settings: ("FusionModules:AllowedServiceCollisions", spelling));

        await Assert.That(result.Failure).IsNull();
    }

    private sealed class FirstItemModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) => builder.Services.AddSingleton(Create);

        private static Shop.Box<Shop.Order>.Item Create(IServiceProvider services) => new();
    }

    private sealed class SecondItemModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder) => builder.Services.AddSingleton(Create);

        private static Shop.Box<Shop.Order>.Item Create(IServiceProvider services) => new();
    }

    private const string WorkerEntry = "Shop.Outer.Worker<Shop.Order>";

    private const string PairEntry = "Shop.Outer.Pair<,>";

    private sealed class FirstModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder)
        {
            builder.Services.AddSingleton(CreateWorker);
            builder.Services.AddSingleton(CreatePair);
        }

        private static Shop.Outer.Worker<Shop.Order> CreateWorker(IServiceProvider services) => new();

        private static Shop.Outer.Pair<Shop.Order, Shop.Order> CreatePair(IServiceProvider services) => new();
    }

    private sealed class SecondModule : ModuleBase
    {
        protected override void ConfigureServices(IHostApplicationBuilder builder)
        {
            builder.Services.AddSingleton(CreateWorker);
            builder.Services.AddSingleton(CreatePair);
        }

        private static Shop.Outer.Worker<Shop.Order> CreateWorker(IServiceProvider services) => new();

        private static Shop.Outer.Pair<Shop.Order, Shop.Order> CreatePair(IServiceProvider services) => new();
    }
}
