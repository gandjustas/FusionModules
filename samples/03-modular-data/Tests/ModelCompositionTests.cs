using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

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

    [Test]
    public async Task OrdersAlone_BringsOnlyItsOwnTable() =>
        await Assert.That(TablesIn(ModelFor(Modules.OrdersEntities))).IsEquivalentTo(new[] { "Sales.Orders" });

    [Test]
    public async Task EachModuleOwnsItsSchema() =>
        await Assert.That(TablesIn(ModelFor(Modules.OrdersEntities, Modules.CustomersEntities)))
            .IsEquivalentTo(new[] { "Marketing.Customers", "Sales.Orders" });

    [Test]
    public async Task WithoutTheComposingModule_ThereIsNoRelationship()
    {
        // Orders and Customers in one database with no foreign key between them: the two modules
        // do not know about each other, and in this topology nothing else does either.
        var model = ModelFor(Modules.OrdersEntities, Modules.CustomersEntities);

        await Assert.That(model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys())).IsEmpty();
    }

    [Test]
    public async Task TheComposingModuleAddsTheRelationship()
    {
        var model = ModelFor(Modules.All);

        var foreignKeys = model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()).ToArray();

        await Assert.That(foreignKeys).HasSingleItem();
        await Assert.That(foreignKeys[0].DeclaringEntityType.GetSchema()).IsEqualTo("Sales");
        await Assert.That(foreignKeys[0].PrincipalEntityType.GetSchema()).IsEqualTo("Marketing");
        await Assert.That(foreignKeys[0].Properties.Single().Name).IsEqualTo("CustomerId");
    }

    [Test]
    public async Task TopologiesInOneProcessDoNotShareAModel()
    {
        // The reason ModuleAwareModelCacheKeyFactory exists. EF Core caches a built model in an
        // internal service provider shared by every context with the same options, keyed by
        // context type — so without it the second topology here silently gets the first one's
        // model, complete with tables it was never meant to have.
        var monolith = TablesIn(ModelFor(Modules.All));
        var ordersOnly = TablesIn(ModelFor(Modules.OrdersEntities));

        await Assert.That(monolith).Contains("Marketing.Customers");
        await Assert.That(ordersOnly).DoesNotContain("Marketing.Customers");
    }

    [Test]
    public async Task AModuleWhoseDependenciesAreMissingFailsStartup()
    {
        // BillingModule uses types from both entity modules, so the compiler records references
        // to them, so ModuleBase requires them to be activated. Nothing had to be declared.
        using var factory = new ModularWebApplicationFactory(Modules.OrdersEntities, Modules.Billing);

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        await Assert.That(error!.Message).Contains("Customers.Entities");
    }
}
