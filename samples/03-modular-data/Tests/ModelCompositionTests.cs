using Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Orders;
using Xunit;

namespace ModularData.Tests;

/// <summary>
/// The model is whatever the loaded modules say it is, and these check that it says different
/// things in different topologies — in one process, which is where it goes wrong quietly.
/// </summary>
public class ModelCompositionTests
{
    private static IModel ModelFor(params string[] modules)
    {
        using var factory = new ModularWebApplicationFactory(modules);
        using var scope = factory.Services.CreateScope();

        // Modules depend on DbContext, so a test can too, and never needs the host's context type.
        return scope.ServiceProvider.GetRequiredService<DbContext>().Model;
    }

    [Fact]
    public void OrdersAlone_HasOrdersAndNothingElse()
    {
        var model = ModelFor(KnownModules.Orders_Entities);

        Assert.NotNull(model.FindEntityType(typeof(Order)));
        Assert.Null(model.FindEntityType(typeof(Customer)));
    }

    [Fact]
    public void EachModuleOwnsItsSchema()
    {
        var model = ModelFor(KnownModules.Orders_Entities, KnownModules.Customers_Entities);

        Assert.Equal("Sales", model.FindEntityType(typeof(Order))!.GetSchema());
        Assert.Equal("Marketing", model.FindEntityType(typeof(Customer))!.GetSchema());
    }

    [Fact]
    public void WithoutTheComposingModule_ThereIsNoRelationship()
    {
        // Orders and Customers in the same database, and no foreign key between them: the two
        // modules do not know about each other, and in this topology nothing else does either.
        var model = ModelFor(KnownModules.Orders_Entities, KnownModules.Customers_Entities);

        Assert.Empty(model.FindEntityType(typeof(Order))!.GetForeignKeys());
    }

    [Fact]
    public void TheComposingModuleAddsTheRelationship()
    {
        var model = ModelFor(KnownModules.All);

        var foreignKey = Assert.Single(model.FindEntityType(typeof(Order))!.GetForeignKeys());

        Assert.Equal(typeof(Customer), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(nameof(Order.CustomerId), Assert.Single(foreignKey.Properties).Name);
    }

    [Fact]
    public void TopologiesInOneProcessDoNotShareAModel()
    {
        // The reason ModuleAwareModelCacheKeyFactory exists. EF Core caches a built model in an
        // internal service provider shared by every context with the same options, keyed by
        // context type — so without it the second topology here silently gets the first one's
        // model, complete with tables it was never meant to have.
        var monolith = ModelFor(KnownModules.All);
        var ordersOnly = ModelFor(KnownModules.Orders_Entities);

        Assert.NotNull(monolith.FindEntityType(typeof(Customer)));
        Assert.Null(ordersOnly.FindEntityType(typeof(Customer)));
    }

    [Fact]
    public void EndpointsAndTablesAreChosenSeparately()
    {
        // A feature module and the entity module it reads are different things, which is what
        // lets one topology take the tables without the endpoints and another take both.
        var withoutTheApi = ModelFor(KnownModules.Customers_Entities);
        var withTheApi = ModelFor(KnownModules.Customers_Entities, KnownModules.CustomersModule);

        Assert.NotNull(withoutTheApi.FindEntityType(typeof(Customer)));
        Assert.NotNull(withTheApi.FindEntityType(typeof(Customer)));
    }

    [Fact]
    public void AModuleWhoseDependenciesAreMissingFailsStartup()
    {
        // BillingModule uses types from both entity modules, so the compiler records references
        // to them, so ModuleBase requires them to be activated. Nothing had to be declared.
        using var factory = new ModularWebApplicationFactory(KnownModules.Orders_Entities, KnownModules.BillingModule);

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Customers.Entities", error.Message, StringComparison.Ordinal);
    }
}
