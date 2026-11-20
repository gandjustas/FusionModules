using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using static FusionModules.Analyzers.Tests.RegistrationTest;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0017: a table or an entity with two owning modules, found by the host's build from what each
/// module's build recorded about the tables it maps.
/// </summary>
public class TableOwnershipTests
{
    /// <summary>
    /// EF Core's builders and the relational extensions, in the shapes the real ones have: extension
    /// methods in <c>Microsoft.EntityFrameworkCore</c> on the builders of <c>Metadata.Builders</c>.
    /// </summary>
    public const string EntityFrameworkStubs = """
        namespace Microsoft.EntityFrameworkCore.Metadata.Builders
        {
            public class EntityTypeBuilder { }

            public class EntityTypeBuilder<TEntity> : EntityTypeBuilder where TEntity : class
            {
                public EntityTypeBuilder<TEntity> OwnsOne<TOwned>(System.Func<TEntity, TOwned> navigation, System.Action<OwnedNavigationBuilder<TEntity, TOwned>> build) where TOwned : class => this;
                public EntityTypeBuilder<TEntity> HasOne<TRelated>() where TRelated : class => this;
                public EntityTypeBuilder<TEntity> HasKey(System.Func<TEntity, object> key) => this;
            }

            public class OwnedNavigationBuilder { }

            public class OwnedNavigationBuilder<TOwner, TDependent> : OwnedNavigationBuilder where TOwner : class where TDependent : class { }

            public class TableBuilder<TEntity> where TEntity : class
            {
                public TableBuilder<TEntity> ExcludeFromMigrations() => this;
            }

            public class SplitTableBuilder<TEntity> where TEntity : class { }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            public class ModelBuilder
            {
                public EntityTypeBuilder<TEntity> Entity<TEntity>() where TEntity : class => null;
                public ModelBuilder Entity<TEntity>(System.Action<EntityTypeBuilder<TEntity>> buildAction) where TEntity : class => this;
                public EntityTypeBuilder Entity(System.Type type) => null;
                public ModelBuilder HasDefaultSchema(string schema) => this;
            }

            public static class RelationalEntityTypeBuilderExtensions
            {
                public static EntityTypeBuilder<TEntity> ToTable<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, string name) where TEntity : class => entityTypeBuilder;
                public static EntityTypeBuilder<TEntity> ToTable<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, string name, string schema) where TEntity : class => entityTypeBuilder;
                public static EntityTypeBuilder<TEntity> ToTable<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, string name, System.Action<TableBuilder<TEntity>> buildAction) where TEntity : class => entityTypeBuilder;
                public static EntityTypeBuilder<TEntity> ToTable<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, string name, string schema, System.Action<TableBuilder<TEntity>> buildAction) where TEntity : class => entityTypeBuilder;
                public static EntityTypeBuilder<TEntity> ToTable<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, System.Action<TableBuilder<TEntity>> buildAction) where TEntity : class => entityTypeBuilder;
                public static EntityTypeBuilder ToTable(this EntityTypeBuilder entityTypeBuilder, string name) => entityTypeBuilder;
                public static EntityTypeBuilder<TEntity> ToView<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, string name, string schema) where TEntity : class => entityTypeBuilder;
                public static EntityTypeBuilder<TEntity> SplitToTable<TEntity>(this EntityTypeBuilder<TEntity> entityTypeBuilder, string name, System.Action<SplitTableBuilder<TEntity>> buildAction) where TEntity : class => entityTypeBuilder;
            }

            public static class RelationalOwnedNavigationBuilderExtensions
            {
                public static OwnedNavigationBuilder<TOwner, TDependent> ToTable<TOwner, TDependent>(this OwnedNavigationBuilder<TOwner, TDependent> builder, string name)
                    where TOwner : class where TDependent : class => builder;
                public static OwnedNavigationBuilder<TOwner, TDependent> ToTable<TOwner, TDependent>(this OwnedNavigationBuilder<TOwner, TDependent> builder, string name, string schema)
                    where TOwner : class where TDependent : class => builder;
            }
        }
        """;

    /// <summary>The stubs, and the entities a shared library declares for every module to use.</summary>
    private const string Contracts = EntityFrameworkStubs + """

        namespace Shop
        {
            public class Order { public int Id { get; set; } public int CustomerId { get; set; } }

            public class Customer { public int Id { get; set; } }

            public class Address { public string Street { get; set; } }
        }
        """;

    /// <summary>A module that maps entities in a method handed the model builder, and declares <paramref name="types"/>.</summary>
    private static Module Mapping(string name, string body, string types = "") => new(name, $$"""
        namespace Shop
        {
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            {{types}}

            static class Mappings
            {
                public static void Map(ModelBuilder modelBuilder)
                {
                    {{body}}
                }
            }
        }
        """);

    /// <summary>A module whose <c>IEntityTypeConfiguration&lt;T&gt;</c> of <paramref name="entity"/> does <paramref name="body"/> to <c>entity</c>.</summary>
    private static Module Configuring(string name, string entity, string body, string types = "") => new(name, $$"""
        namespace Shop
        {
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            {{types}}

            sealed class {{entity}}Configuration : IEntityTypeConfiguration<{{entity}}>
            {
                public void Configure(EntityTypeBuilder<{{entity}}> entity)
                {
                    {{body}}
                }
            }
        }
        """);

    private static Task<string?> GenerateAsync(Module module, string projectKind = "Module", bool registered = true) =>
        RegistrationTest.GenerateAsync(module.Source, Contracts, projectKind, registered, module.Name);

    private static Task<ImmutableArray<Diagnostic>> HostAsync(
        IEnumerable<Module> modules,
        Dictionary<string, string>? options = null,
        bool metadataOnly = false,
        string hostKind = "Host") =>
        RegistrationTest.HostAsync(modules, Contracts, options, metadataOnly, hostKind, Diagnostics.ModulesOwnOneTableId);

    private static string Message(Diagnostic diagnostic) => diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);

    // The generator: what a module records.

    [Test]
    public async Task RecordsToTableInAConfigurationClass()
    {
        var generated = await GenerateAsync(Configuring("Orders", "Order", """entity.ToTable("Orders", "Sales");"""));

        await Assert.That(generated).Contains("""ModuleTableAttribute("Sales", "Orders", "Shop.Order", "Shop.Order, Contracts", """);
        await Assert.That(generated).Contains("internal sealed class ModuleTableAttribute");
        await Assert.That(generated).Contains("namespace FusionModules.Generated.Orders");
        await Assert.That(generated).DoesNotContain("ModuleRegistrationAttribute");
    }

    [Test]
    public async Task RecordsBothFormsOfTheModelBuilder()
    {
        var generated = await GenerateAsync(Mapping("Orders", """
            modelBuilder.Entity<Order>().ToTable("Orders", "Sales");
            modelBuilder.Entity<Customer>(customer => customer.ToTable("Customers"));
            """));

        await Assert.That(generated).Contains("""("Sales", "Orders", "Shop.Order", "Shop.Order, Contracts", """);
        await Assert.That(generated).Contains("""("", "Customers", "Shop.Customer", "Shop.Customer, Contracts", """);
    }

    [Test]
    public async Task RecordsEveryOverloadThatNamesATable()
    {
        var generated = await GenerateAsync(Mapping(
            "Orders",
            """
            modelBuilder.Entity<Order>().ToTable("One");
            modelBuilder.Entity<Order>().ToTable("Two", "S2");
            modelBuilder.Entity<Order>().ToTable("Three", table => table.ExcludeFromMigrations());
            modelBuilder.Entity<Order>().ToTable("Four", "S4", table => { });
            modelBuilder.Entity<Order>().ToTable(Names.Five, Names.Schema);
            modelBuilder.Entity<Order>().ToTable(nameof(Customer));
            """,
            """static class Names { public const string Five = "Five"; public const string Schema = "S" + "5"; }"""));

        await Assert.That(generated).Contains("""("", "One", "Shop.Order", """);
        await Assert.That(generated).Contains("""("S2", "Two", "Shop.Order", """);
        await Assert.That(generated).Contains("""("", "Three", "Shop.Order", """);
        await Assert.That(generated).Contains("""("S4", "Four", "Shop.Order", """);
        await Assert.That(generated).Contains("""("S5", "Five", "Shop.Order", """);
        await Assert.That(generated).Contains("""("", "Customer", "Shop.Order", """);
    }

    [Test]
    public async Task DoesNotGuessANameThatIsNotAConstant()
    {
        var generated = await GenerateAsync(Mapping(
            "Orders",
            """
            modelBuilder.Entity<Order>().ToTable(Names.Table);
            modelBuilder.Entity<Order>().ToTable("Orders", Names.Table);
            modelBuilder.Entity<Order>().ToTable(table => table.ExcludeFromMigrations());
            modelBuilder.Entity<Order>().ToTable((string)null);
            """,
            """static class Names { public static string Table = "Orders"; }"""));

        await Assert.That(generated).IsNull();
    }

    [Test]
    public async Task AViewIsNotATableAModuleOwns()
    {
        var generated = await GenerateAsync(Mapping("Orders", """modelBuilder.Entity<Order>().ToView("OrderSummaries", "Sales");"""));

        await Assert.That(generated).IsNull();
    }

    [Test]
    public async Task RecordsTheTableAloneWhereTheBuilderNamesNoEntity()
    {
        // An owned type's table, a table an entity is split into, and a builder of no particular type
        // are tables the module maps; the entity key is the entity's own ToTable, which is not here.
        var generated = await GenerateAsync(Mapping("Orders", """
            modelBuilder.Entity<Customer>().OwnsOne(customer => new Address(), address => address.ToTable("Addresses"));
            modelBuilder.Entity<Order>().SplitToTable("OrderDetails", split => { });
            modelBuilder.Entity(typeof(Order)).ToTable("Legacy");
            """));

        await Assert.That(generated).Contains("""("", "Addresses", "", "", "")""");
        await Assert.That(generated).Contains("""("", "OrderDetails", "", "", "")""");
        await Assert.That(generated).Contains("""("", "Legacy", "", "", "")""");
    }

    [Test]
    public async Task TwoModulesJoinTables_AreTwoTables_NotOneEntity()
    {
        // A many-to-many join table is a shared-type entity: Dictionary<string, object> in every
        // module. The tables are each module's; the type is nobody's entity.
        var diagnostics = await HostAsync(
        [
            Mapping("Orders.Entities", """modelBuilder.Entity<System.Collections.Generic.Dictionary<string, object>>().ToTable("OrderTags");"""),
            Mapping("Customers.Entities", """modelBuilder.Entity<System.Collections.Generic.Dictionary<string, object>>().ToTable("CustomerTags");"""),
        ]);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task RecordsTheTableAttributeOfTheModulesOwnEntity()
    {
        var generated = await GenerateAsync(Mapping(
            "Billing",
            "",
            """
            [System.ComponentModel.DataAnnotations.Schema.Table("Invoices", Schema = "Billing")]
            public class Invoice { public int Id { get; set; } }

            [System.ComponentModel.DataAnnotations.Schema.Table("Payments")]
            public class Payment { public int Id { get; set; } }
            """));

        await Assert.That(generated).Contains("""("Billing", "Invoices", "Shop.Invoice", "Shop.Invoice, Billing", """);
        await Assert.That(generated).Contains("""("", "Payments", "Shop.Payment", "Shop.Payment, Billing", """);
    }

    [Test]
    public async Task DoesNotRecordATypeParameter()
    {
        var generated = await GenerateAsync(Mapping(
            "Orders",
            "",
            """
            static class Helper
            {
                public static void Map<T>(EntityTypeBuilder<T> entity) where T : class => entity.ToTable("Generic");
            }
            """));

        await Assert.That(generated).IsNull();
    }

    [Test]
    public async Task GeneratesNothingOutsideAModule()
    {
        var module = Configuring("Orders", "Order", """entity.ToTable("Orders", "Sales");""");

        await Assert.That(await GenerateAsync(module, registered: false)).IsNull();
        await Assert.That(await GenerateAsync(module, projectKind: "Host")).IsNull();
        await Assert.That(await GenerateAsync(module, projectKind: "Test")).IsNull();
    }

    [Test]
    public async Task RecordsRegistrationsAndTablesInOneFile()
    {
        var generated = await RegistrationTest.GenerateAsync(
            """
            namespace Shop
            {
                using Microsoft.EntityFrameworkCore;

                interface IChecker { }
                sealed class Checker : IChecker { }

                static class Setup
                {
                    public static void Register(IServiceCollection services) => services.AddSingleton<IChecker, Checker>();
                    public static void Map(ModelBuilder modelBuilder) => modelBuilder.Entity<Order>().ToTable("Orders", "Sales");
                }
            }
            """,
            Contracts);

        await Assert.That(generated).Contains("internal sealed class ModuleRegistrationAttribute");
        await Assert.That(generated).Contains("internal sealed class ModuleTableAttribute");
        await Assert.That(generated).Contains("""ModuleTableAttribute("Sales", "Orders", "Shop.Order", """);
    }

    // The host: what it makes of the records.

    [Test]
    public async Task ACopyOfAnotherModulesEntity_OnItsTable_IsReported()
    {
        // The article's Billing: its own Order, and the Orders module's table.
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Billing", "BillingOrder", """entity.ToTable("Orders", "Sales");""", "sealed class BillingOrder { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(diagnostics[0].Severity).IsEqualTo(DiagnosticSeverity.Error);
        await Assert.That(Message(diagnostics[0])).StartsWith("'Sales.Orders' is mapped by modules Billing, Orders.Entities");
    }

    [Test]
    public async Task OneEntityMappedByTwoModules_IsReportedForTheEntity()
    {
        // Billing maps the shared Order onto a table of its own: EF Core keeps whichever module was
        // applied last, and the other module's table is silently not the entity's any more.
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Billing", "Order", """entity.ToTable("BillingOrders", "Billing");"""),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(Message(diagnostics[0])).StartsWith("'Shop.Order' is mapped by modules Billing, Orders.Entities");
    }

    [Test]
    public async Task OneEntityMappedByTwoModulesToOneTable_IsReportedOnce()
    {
        // Both keys collide, and for one reason: the entity's message says it all.
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Billing", "Order", """entity.ToTable("Orders", "Sales");"""),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(Message(diagnostics[0])).StartsWith("'Shop.Order' is mapped by modules Billing, Orders.Entities");
    }

    [Test]
    public async Task AModuleThatOnlyJoinsAnothersEntity_DoesNotOwnIt()
    {
        // The composing module of samples/03: a relationship on Orders' entity, no ToTable.
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Customers.Entities", "Customer", """entity.ToTable("Customers", "Marketing");"""),
            Configuring("Billing", "Order", "entity.HasOne<Customer>();"),
        ]);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task ATableWithoutASchema_IsNotTheTableOfANamedSchema()
    {
        // Which schema "no schema" is depends on the provider and on HasDefaultSchema, which the
        // build does not know; the startup check does.
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Archive", "ArchivedOrder", """entity.ToTable("Orders");""", "sealed class ArchivedOrder { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task TablesAreComparedWithoutRegardToCase()
    {
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Billing", "BillingOrder", """entity.ToTable("orders", "sales");""", "sealed class BillingOrder { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(Message(diagnostics[0])).StartsWith("'Sales.Orders' is mapped by modules Billing, Orders.Entities");
    }

    [Test]
    public async Task SeveralEntitiesOfOneModuleOnOneTable_HaveOneOwner()
    {
        // Table splitting, an owned type kept in its owner's table, a hierarchy in one table.
        var diagnostics = await HostAsync(
        [
            Mapping("Orders.Entities", """
                modelBuilder.Entity<Order>().ToTable("Orders", "Sales");
                modelBuilder.Entity<OrderDetails>().ToTable("Orders", "Sales");
                modelBuilder.Entity<Order>().OwnsOne(order => new Address(), address => address.ToTable("Orders", "Sales"));
                """,
                "sealed class OrderDetails { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task NamesakeEntitiesOfTwoModules_AreTwoEntities()
    {
        // Each module declares its own internal Shop.Note: one name, two types, two tables.
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Note", """entity.ToTable("OrderNotes");""", "sealed class Note { public int Id { get; set; } }"),
            Configuring("Customers.Entities", "Note", """entity.ToTable("CustomerNotes");""", "sealed class Note { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task NamesakeEntitiesOfTwoModulesOnOneTable_AreReportedForTheTable()
    {
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Note", """entity.ToTable("Notes");""", "sealed class Note { public int Id { get; set; } }"),
            Configuring("Customers.Entities", "Note", """entity.ToTable("Notes");""", "sealed class Note { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(Message(diagnostics[0])).StartsWith("'Notes' is mapped by modules Customers.Entities, Orders.Entities");
    }

    [Test]
    public async Task ATableAttributeOwnsTheTableLikeToTable()
    {
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Mapping("Billing", "", """[System.ComponentModel.DataAnnotations.Schema.Table("Orders", Schema = "Sales")] sealed class BillingOrder { public int Id { get; set; } }"""),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(Message(diagnostics[0])).StartsWith("'Sales.Orders' is mapped by modules Billing, Orders.Entities");
    }

    [Test]
    public async Task ThreeModules_AreNamedInOneMessage()
    {
        var diagnostics = await HostAsync(
        [
            Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
            Configuring("Billing", "BillingOrder", """entity.ToTable("Orders", "Sales");""", "sealed class BillingOrder { public int Id { get; set; } }"),
            Configuring("Reports", "ReportOrder", """entity.ToTable("Orders", "Sales");""", "sealed class ReportOrder { public int Id { get; set; } }"),
        ]);

        await Assert.That(diagnostics).HasSingleItem();
        await Assert.That(Message(diagnostics[0])).StartsWith("'Sales.Orders' is mapped by modules Billing, Orders.Entities, Reports");
    }

    [Test]
    public async Task ReadsTheRecordsOutOfReferenceAssemblies()
    {
        var diagnostics = await HostAsync(
            [
                Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
                Configuring("Billing", "BillingOrder", """entity.ToTable("Orders", "Sales");""", "sealed class BillingOrder { public int Id { get; set; } }"),
            ],
            metadataOnly: true);

        await Assert.That(diagnostics).HasSingleItem();
    }

    [Test]
    [Arguments("fusion_modules_allowed_table_collisions", "Sales.Orders")]
    [Arguments("build_property.fusion_modules_allowed_table_collisions", "sales.orders")]
    [Arguments("fusion_modules_allowed_table_collisions", "Other; Sales.Orders")]
    public async Task AlternativesListedInTheOption_AreNotReportedForTheTable(string key, string value)
    {
        // A real payments module and a fake one, each with its own entity on the one table, and no
        // topology loading both.
        var diagnostics = await HostAsync(
            [
                Configuring("Payments", "Payment", """entity.ToTable("Orders", "Sales");""", "sealed class Payment { public int Id { get; set; } }"),
                Configuring("FakePayments", "Payment", """entity.ToTable("Orders", "Sales");""", "sealed class Payment { public int Id { get; set; } }"),
            ],
            new() { [key] = value });

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("Shop.Order")]
    [Arguments("Order")]
    public async Task AlternativesListedInTheOption_AreNotReportedForTheEntity(string value)
    {
        var diagnostics = await HostAsync(
            [
                Configuring("Payments", "Order", """entity.ToTable("Orders", "Sales");"""),
                Configuring("FakePayments", "Order", """entity.ToTable("FakeOrders");"""),
            ],
            new() { ["fusion_modules_allowed_table_collisions"] = value });

        await Assert.That(diagnostics).IsEmpty();
    }

    [Test]
    public async Task AListedTable_DoesNotHideAnotherOne()
    {
        var diagnostics = await HostAsync(
            [
                Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
                Configuring("Billing", "BillingOrder", """entity.ToTable("Orders", "Sales");""", "sealed class BillingOrder { public int Id { get; set; } }"),
            ],
            new() { ["fusion_modules_allowed_table_collisions"] = "Orders; Sales.Order" });

        await Assert.That(diagnostics).HasSingleItem();
    }

    [Test]
    public async Task ATestProjectIsNotAHost()
    {
        var diagnostics = await HostAsync(
            [
                Configuring("Orders.Entities", "Order", """entity.ToTable("Orders", "Sales");"""),
                Configuring("Billing", "BillingOrder", """entity.ToTable("Orders", "Sales");""", "sealed class BillingOrder { public int Id { get; set; } }"),
            ],
            hostKind: "Test");

        await Assert.That(diagnostics).IsEmpty();
    }
}
