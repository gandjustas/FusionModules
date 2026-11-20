using System.ComponentModel.DataAnnotations.Schema;
using FusionModules.EntityFrameworkCore.TestModule;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.Extensions.Configuration;
using static FusionModules.EntityFrameworkCore.ModuleTables;

namespace FusionModules.EntityFrameworkCore.Tests;

/// <summary>
/// MOD0017 in the model (<c>CheckModuleTableOwners</c>): a model composed from modules in which two
/// modules own one table or one entity fails to build, with what the build cannot see — tables named
/// by convention, names read from configuration, the schema the finished model gives a table.
/// </summary>
/// <remarks>
/// Most cases compose a model from "modules" that are lambdas over one model builder, so that one
/// test assembly can hold them all: a configuration class here would be applied by every test that
/// names this assembly. The last ones go through <c>UseModules</c> with a second module assembly.
/// </remarks>
public class TableOwnershipTests
{
    private const string Module = "FusionModules.EntityFrameworkCore.Tests";
    private const string LegacyModule = "FusionModules.EntityFrameworkCore.TestModule";

    private static Contribution Of(string name, Action<ModelBuilder> apply, params Type[] entities) =>
        new(name, null, entities, apply);

    /// <summary>
    /// Applies the modules in order to a model builder with SQLite's conventions, lets the "context"
    /// configure more after them, finalizes the model and checks it — what the convention does, last.
    /// </summary>
    private static void Compose(bool check, Action<ModelBuilder>? after, params Contribution[] modules)
    {
        var modelBuilder = SqliteConventionSetBuilder.CreateModelBuilder();
        ModuleTables.Apply(modelBuilder, modules, "ShopContext", check);
        after?.Invoke(modelBuilder);
        modelBuilder.FinalizeModel();
        ModuleTables.Check(modelBuilder.Model);
    }

    private static Task<InvalidOperationException?> FailsAsync(params Contribution[] modules) =>
        FailsAsync(null, modules);

    private static async Task<InvalidOperationException?> FailsAsync(Action<ModelBuilder>? after, params Contribution[] modules) =>
        await Assert.That(() => Compose(check: true, after, modules)).Throws<InvalidOperationException>();

    private static async Task PassesAsync(params Contribution[] modules) =>
        await Assert.That(() => Compose(check: true, null, modules)).ThrowsNothing();

    [Test]
    public async Task ACopyOfAnotherModulesEntity_OnItsTable_Fails()
    {
        var exception = await FailsAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Orders.Order)),
            Of("Billing", model => model.Entity<Ownership.Billing.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Billing.Order)));

        await Assert.That(exception!.Message).Contains("The model of ShopContext");
        await Assert.That(exception.Message).Contains("'Sales.Orders' is mapped by Billing (Ownership.Billing.Order) and Orders.Entities (Ownership.Orders.Order)");
        await Assert.That(exception.Message).Contains("MOD0017");
        await Assert.That(exception.Message).Contains("HOSTINGSTARTUPASSEMBLIES");
    }

    [Test]
    public async Task OneEntityGivenATableByTwoModules_Fails()
    {
        // EF Core keeps the table applied last, and says nothing.
        var exception = await FailsAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Shared.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Shared.Order)),
            Of("Billing", model => model.Entity<Ownership.Shared.Order>().ToTable("BillingOrders", "Billing"), typeof(Ownership.Shared.Order)));

        await Assert.That(exception!.Message).Contains("'Ownership.Shared.Order' is given its table by Billing and Orders.Entities; EF Core keeps the one applied last, 'Billing.BillingOrders' (Orders.Entities: 'Sales.Orders', Billing: 'Billing.BillingOrders').");
        await Assert.That(exception.Message).DoesNotContain("'Sales.Orders' is mapped by");
    }

    [Test]
    public async Task AModuleThatOnlyJoinsAnothersEntity_DoesNotOwnIt() =>
        await PassesAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Shared.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Shared.Order)),
            Of("Customers.Entities", model => model.Entity<Ownership.Shared.Customer>().ToTable("Customers", "Marketing"), typeof(Ownership.Shared.Customer)),
            Of("Billing", model => model.Entity<Ownership.Shared.Order>().HasOne<Ownership.Shared.Customer>().WithMany().HasForeignKey(order => order.CustomerId), typeof(Ownership.Shared.Order)));

    [Test]
    public async Task TablesNamedByConvention_AreSeen()
    {
        // What the build cannot see: nobody calls ToTable, and both classes are called Note.
        var exception = await FailsAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Note>(), typeof(Ownership.Orders.Note)),
            Of("Billing", model => model.Entity<Ownership.Billing.Note>(), typeof(Ownership.Billing.Note)));

        await Assert.That(exception!.Message).Contains("'Note' is mapped by Billing (Ownership.Billing.Note, by convention) and Orders.Entities (Ownership.Orders.Note, by convention)");
    }

    [Test]
    public async Task ANameReadFromConfiguration_IsSeen()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Billing:Table"] = "Orders" }).Build();

        var exception = await FailsAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Orders.Order)),
            Of("Billing", model => model.Entity<Ownership.Billing.Order>().ToTable(configuration["Billing:Table"]!, "Sales"), typeof(Ownership.Billing.Order)));

        await Assert.That(exception!.Message).Contains("'Sales.Orders' is mapped by Billing");
    }

    [Test]
    public async Task TheModelsDefaultSchema_IsTheSchemaOfATableWithoutOne()
    {
        // Set by the context after ApplyModuleConfigurations, as often as before it: the tables are
        // compared once the model is finished.
        var exception = await FailsAsync(
            model => model.HasDefaultSchema("Sales"),
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders"), typeof(Ownership.Orders.Order)),
            Of("Billing", model => model.Entity<Ownership.Billing.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Billing.Order)));

        await Assert.That(exception!.Message).Contains("'Sales.Orders' is mapped by Billing");
    }

    [Test]
    public async Task ATableTheContextRenamesAfterTheModules_IsComparedByItsFinalName() =>
        // Billing's copy, renamed onto the Orders table by the context itself: the context is not a
        // module, so Billing still owns the entity, and the table is what the finished model says.
        await FailsAsync(
            model => model.Entity<Ownership.Billing.Order>().ToTable("Orders", "Sales"),
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Orders.Order)),
            Of("Billing", model => model.Entity<Ownership.Billing.Order>().ToTable("BillingOrders"), typeof(Ownership.Billing.Order)));

    [Test]
    public async Task TablesAreComparedWithoutRegardToCase()
    {
        var exception = await FailsAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Orders.Order)),
            Of("Billing", model => model.Entity<Ownership.Billing.Order>().ToTable("orders", "sales"), typeof(Ownership.Billing.Order)));

        await Assert.That(exception!.Message).Contains("'Sales.Orders' is mapped by Billing");
    }

    [Test]
    public async Task ATableWithoutASchema_IsNotTheTableOfANamedSchema() =>
        await PassesAsync(
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales"), typeof(Ownership.Orders.Order)),
            Of("Archive", model => model.Entity<Ownership.Billing.Order>().ToTable("Orders"), typeof(Ownership.Billing.Order)));

    [Test]
    public async Task TableSplittingInOneModule_HasOneOwner() =>
        await PassesAsync(
            Of(
                "Orders.Entities",
                model =>
                {
                    model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales");
                    model.Entity<Ownership.Orders.OrderDetails>().ToTable("Orders", "Sales");
                    model.Entity<Ownership.Orders.OrderDetails>().HasOne<Ownership.Orders.Order>().WithOne().HasForeignKey<Ownership.Orders.OrderDetails>(details => details.Id);
                },
                typeof(Ownership.Orders.Order),
                typeof(Ownership.Orders.OrderDetails)));

    [Test]
    public async Task AnOwnedTypeAndADerivedType_LiveInTheirRootsTable() =>
        // A derived type configured by another module is stored in the hierarchy's table, which the
        // root's module names; an owned type, in its owner's.
        await PassesAsync(
            Of(
                "Payments",
                model =>
                {
                    model.Entity<Ownership.Shared.Payment>().ToTable("Payments");
                    model.Entity<Ownership.Shared.Customer>().ToTable("Customers").OwnsOne(customer => customer.Address);
                },
                typeof(Ownership.Shared.Payment),
                typeof(Ownership.Shared.Customer)),
            Of("Cards", model => model.Entity<Ownership.Shared.CardPayment>().Property(card => card.Last4), typeof(Ownership.Shared.CardPayment)));

    [Test]
    public async Task ATableAttribute_IsOwnedByTheModuleThatDeclaresTheClass()
    {
        var exception = await FailsAsync(
            new Contribution("Orders.Entities", typeof(Ownership.Orders.Invoice).Assembly, [], model => { }),
            Of("Billing", model => model.Entity<Ownership.Orders.Invoice>().HasKey(invoice => invoice.Id), typeof(Ownership.Orders.Invoice)),
            Of("Reports", model => model.Entity<Ownership.Billing.Order>().ToTable("Invoices"), typeof(Ownership.Billing.Order)));

        await Assert.That(exception!.Message).Contains("'Invoices' is mapped by Orders.Entities (Ownership.Orders.Invoice) and Reports (Ownership.Billing.Order)");
    }

    [Test]
    public async Task EveryCollisionIsNamed_InOneMessage()
    {
        var exception = await FailsAsync(
            Of("Orders.Entities", model =>
            {
                model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales");
                model.Entity<Ownership.Shared.Order>().ToTable("SharedOrders");
            }),
            Of("Billing", model =>
            {
                model.Entity<Ownership.Billing.Order>().ToTable("Orders", "Sales");
                model.Entity<Ownership.Shared.Order>().ToTable("BillingOrders");
            }));

        await Assert.That(exception!.Message).Contains("'Sales.Orders' is mapped by");
        await Assert.That(exception.Message).Contains("'Ownership.Shared.Order' is given its table by");
    }

    [Test]
    public async Task OffItChecksNothing() =>
        await Assert.That(() => Compose(
            check: false,
            null,
            Of("Orders.Entities", model => model.Entity<Ownership.Orders.Order>().ToTable("Orders", "Sales")),
            Of("Billing", model => model.Entity<Ownership.Billing.Order>().ToTable("Orders", "Sales")))).ThrowsNothing();

    // Through UseModules, with two module assemblies: the model a host, dotnet ef and a test build.

    [Test]
    public async Task EachModuleAlone_ComposesAModel()
    {
        using var legacy = Create<LegacyContext>(options => options.UseModules(LegacyModule));

        await Assert.That(legacy.Model.FindEntityType(typeof(LegacyOrder))!.GetTableName()).IsEqualTo("Orders");
    }

    [Test]
    public async Task WithoutCheckModuleTableOwners_NothingIsRecordedOrChecked()
    {
        // Off unless asked for: EF Core's own validation is what is left, and it refuses two unrelated
        // entity types on one table in its own words.
        using var context = Create<UncheckedByDefaultContext>(options => options.UseModules(Module, LegacyModule));

        var exception = await Assert.That(() => context.Model).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).DoesNotContain("MOD0017");
    }

    [Test]
    public async Task TwoModulesOnOneTable_StopTheModel()
    {
        using var context = Create<CollidingContext>(options => options.UseModules(Module, LegacyModule).CheckModuleTableOwners());

        var exception = await Assert.That(() => context.Model).Throws<InvalidOperationException>();
        // Modules in order of their names, whichever order the model applied them in.
        await Assert.That(exception!.Message).Contains($"'Sales.Orders' is mapped by {LegacyModule} (FusionModules.EntityFrameworkCore.TestModule.LegacyOrder) and {Module} (FusionModules.EntityFrameworkCore.Tests.Order).");
    }

    [Test]
    public async Task TheDesignTimeModel_IsCheckedToo()
    {
        // dotnet ef builds the model with the design-time flag, which is part of the cache key: a
        // model of its own, composed by the same OnModelCreating.
        using var context = Create<DesignTimeContext>(options => options.UseModules(Module, LegacyModule).CheckModuleTableOwners());

        var exception = await Assert.That(() => context.GetService<IDesignTimeModel>().Model).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).Contains("'Sales.Orders' is mapped by");
    }

    [Test]
    public async Task TheRuntimeTopologyChecksSwitch_TurnsItOff()
    {
        // With the check off, EF Core's own validation is what is left: it refuses two unrelated
        // entity types on one table in its own words, and says nothing of modules.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ModuleBase.CreateModuleRegistry(Module, LegacyModule))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["FusionModules:RuntimeTopologyChecks"] = "false" })
            .Build();
        using var context = Create<UncheckedContext>(options => options.UseModules(configuration).CheckModuleTableOwners());

        var exception = await Assert.That(() => context.Model).Throws<InvalidOperationException>();
        await Assert.That(exception!.Message).DoesNotContain("MOD0017");
    }

    private static TContext Create<TContext>(Action<DbContextOptionsBuilder<TContext>> configure)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>().UseSqlite("DataSource=:memory:");
        configure(options);
        return (TContext)Activator.CreateInstance(typeof(TContext), options.Options)!;
    }
}

internal sealed class LegacyContext(DbContextOptions<LegacyContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class CollidingContext(DbContextOptions<CollidingContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class DesignTimeContext(DbContextOptions<DesignTimeContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class UncheckedByDefaultContext(DbContextOptions<UncheckedByDefaultContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}

internal sealed class UncheckedContext(DbContextOptions<UncheckedContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyModuleConfigurations(this);
}
