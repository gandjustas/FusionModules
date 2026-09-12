using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ModularData.Tests;

/// <summary>
/// What each topology's model looks like, checked through the running application.
/// </summary>
/// <remarks>
/// Modules appear here only as the names handed to the factory, exactly as they appear in a
/// deployment — so these tests assert on tables and schemas rather than on entity types. A test
/// that wants a module's types is testing that module's business logic, and belongs beside it
/// without a host: see the Billing.UnitTests project.
/// </remarks>
public class ModelCompositionTests
{
    private static IModel ModelFor(params string[] modules)
    {
        using var factory = new ModularWebApplicationFactory(modules);
        using var scope = factory.Services.CreateScope();

        // Modules depend on DbContext, so a test can too, and never needs the host's context type.
        return scope.ServiceProvider.GetRequiredService<DbContext>().Model;
    }

    private static string[] TablesIn(IModel model) =>
        [.. model.GetEntityTypes()
            .Select(entity => $"{entity.GetSchema()}.{entity.GetTableName()}")
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void OrdersAlone_BringsOnlyItsOwnTable() =>
        Assert.Equal(["Sales.Orders"], TablesIn(ModelFor(KnownModules.Orders_Entities)));

    [Fact]
    public void EachModuleOwnsItsSchema() =>
        Assert.Equal(
            ["Marketing.Customers", "Sales.Orders"],
            TablesIn(ModelFor(KnownModules.Orders_Entities, KnownModules.Customers_Entities)));

    [Fact]
    public void WithoutTheComposingModule_ThereIsNoRelationship()
    {
        // Orders and Customers in one database with no foreign key between them: the two modules
        // do not know about each other, and in this topology nothing else does either.
        var model = ModelFor(KnownModules.Orders_Entities, KnownModules.Customers_Entities);

        Assert.Empty(model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()));
    }

    [Fact]
    public void TheComposingModuleAddsTheRelationship()
    {
        var model = ModelFor(KnownModules.All);

        var foreignKey = Assert.Single(model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()));

        Assert.Equal("Sales", foreignKey.DeclaringEntityType.GetSchema());
        Assert.Equal("Marketing", foreignKey.PrincipalEntityType.GetSchema());
        Assert.Equal("CustomerId", Assert.Single(foreignKey.Properties).Name);
    }

    [Fact]
    public void TopologiesInOneProcessDoNotShareAModel()
    {
        // The reason ModuleAwareModelCacheKeyFactory exists. EF Core caches a built model in an
        // internal service provider shared by every context with the same options, keyed by
        // context type — so without it the second topology here silently gets the first one's
        // model, complete with tables it was never meant to have.
        var monolith = TablesIn(ModelFor(KnownModules.All));
        var ordersOnly = TablesIn(ModelFor(KnownModules.Orders_Entities));

        Assert.Contains("Marketing.Customers", monolith);
        Assert.DoesNotContain("Marketing.Customers", ordersOnly);
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
