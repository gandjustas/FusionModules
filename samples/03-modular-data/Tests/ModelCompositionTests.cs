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
public class ModelCompositionTests(PostgresFixture postgres)
{
    private async Task<(ModularWebApplicationFactory Factory, IModel Model)> BootAsync(params string[] modules)
    {
        var factory = new ModularWebApplicationFactory(
            await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken),
            modules);

        using var scope = factory.Services.CreateScope();

        // Modules depend on DbContext, so a test can too, and never needs the host's context type.
        return (factory, scope.ServiceProvider.GetRequiredService<DbContext>().Model);
    }

    private static string[] TablesIn(IModel model) =>
        [.. model.GetEntityTypes()
            .Select(entity => $"{entity.GetSchema()}.{entity.GetTableName()}")
            .Order(StringComparer.Ordinal)];

    [Fact]
    public async Task OrdersAlone_BringsOnlyItsOwnTable()
    {
        var (factory, model) = await BootAsync(KnownModules.Orders_Entities);
        using (factory)
        {
            Assert.Equal(["Sales.Orders"], TablesIn(model));
        }
    }

    [Fact]
    public async Task EachModuleOwnsItsSchema()
    {
        var (factory, model) = await BootAsync(KnownModules.Orders_Entities, KnownModules.Customers_Entities);
        using (factory)
        {
            Assert.Equal(["Marketing.Customers", "Sales.Orders"], TablesIn(model));
        }
    }

    [Fact]
    public async Task WithoutTheComposingModule_ThereIsNoRelationship()
    {
        // Orders and Customers in one database with no foreign key between them: the two modules
        // do not know about each other, and in this topology nothing else does either.
        var (factory, model) = await BootAsync(KnownModules.Orders_Entities, KnownModules.Customers_Entities);
        using (factory)
        {
            Assert.Empty(model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()));
        }
    }

    [Fact]
    public async Task TheComposingModuleAddsTheRelationship()
    {
        var (factory, model) = await BootAsync(KnownModules.All);
        using (factory)
        {
            var foreignKey = Assert.Single(model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()));

            Assert.Equal("Sales", foreignKey.DeclaringEntityType.GetSchema());
            Assert.Equal("Marketing", foreignKey.PrincipalEntityType.GetSchema());
            Assert.Equal("CustomerId", Assert.Single(foreignKey.Properties).Name);
        }
    }

    [Fact]
    public async Task TopologiesInOneProcessDoNotShareAModel()
    {
        // The reason ModuleAwareModelCacheKeyFactory exists. EF Core caches a built model in an
        // internal service provider shared by every context with the same options, keyed by
        // context type — so without it the second topology here silently gets the first one's
        // model, complete with tables it was never meant to have.
        var (monolithFactory, monolith) = await BootAsync(KnownModules.All);
        using (monolithFactory)
        {
            var (ordersFactory, ordersOnly) = await BootAsync(KnownModules.Orders_Entities);
            using (ordersFactory)
            {
                Assert.Contains("Marketing.Customers", TablesIn(monolith));
                Assert.DoesNotContain("Marketing.Customers", TablesIn(ordersOnly));
            }
        }
    }

    [Fact]
    public async Task MigrationsCreateTheWholeSchemaWhateverTheTopology()
    {
        // Migrations are generated against the union of every module, so a subset topology gets
        // tables it does not use rather than a database that only half exists. That is the point:
        // one migration history, whatever the replica happens to be running.
        var (factory, _) = await BootAsync(KnownModules.Orders_Entities);
        using (factory)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();

            var customers = await db.Database
                .SqlQuery<int>($"""
                    SELECT COUNT(*)::int AS "Value" FROM information_schema.tables
                    WHERE table_schema = 'Marketing' AND table_name = 'Customers'
                    """)
                .SingleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, customers);
        }
    }

    [Fact]
    public async Task AModuleWhoseDependenciesAreMissingFailsStartup()
    {
        // BillingModule uses types from both entity modules, so the compiler records references
        // to them, so ModuleBase requires them to be activated. Nothing had to be declared.
        using var factory = new ModularWebApplicationFactory(
            await postgres.CreateDatabaseAsync(TestContext.Current.CancellationToken),
            KnownModules.Orders_Entities,
            KnownModules.BillingModule);

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("Customers.Entities", error.Message, StringComparison.Ordinal);
    }
}
