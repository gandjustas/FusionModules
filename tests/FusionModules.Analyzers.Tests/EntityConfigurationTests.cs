using System.Text;
using FusionModules.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;
using Microsoft.CodeAnalysis.Text;

namespace FusionModules.Analyzers.Tests;

/// <summary>
/// MOD0016: a context whose <c>OnModelCreating</c> configures entities that can each have an
/// <c>IEntityTypeConfiguration&lt;T&gt;</c> class, and the fix that writes the classes.
/// </summary>
public class EntityConfigurationTests
{
    /// <summary>EF Core as the rule meets it: the model builder, an entity's builder, what hangs off them.</summary>
    private const string EntityFrameworkStubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            public class DbContextOptions<TContext> { }

            public abstract class DbSet<TEntity> : System.Linq.IQueryable<TEntity> where TEntity : class
            {
                public abstract System.Type ElementType { get; }
                public abstract System.Linq.Expressions.Expression Expression { get; }
                public abstract System.Linq.IQueryProvider Provider { get; }
                public abstract System.Collections.Generic.IEnumerator<TEntity> GetEnumerator();
                System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
                public void Add(TEntity entity) { }
            }

            public static class RelationalEntityTypeBuilderExtensions { }

            public static class EF
            {
                public static System.Func<TContext, TResult> CompileQuery<TContext, TResult>(
                    System.Linq.Expressions.Expression<System.Func<TContext, TResult>> query) where TContext : DbContext => null;
            }

            public class DatabaseFacade
            {
                public bool IsSqlite() => false;
            }

            public class DbContext
            {
                public DbContext() { }
                public DbContext(object options) { }
                public DatabaseFacade Database => null;
                public DbSet<TEntity> Set<TEntity>() where TEntity : class => null;
                protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
            }

            public class EntityType
            {
                public void SetTableName(string name) { }
            }

            public class Model
            {
                public System.Collections.Generic.IEnumerable<EntityType> GetEntityTypes() => null;
            }

            public class ModelBuilder
            {
                public Model Model => null;
                public EntityTypeBuilder<TEntity> Entity<TEntity>() where TEntity : class => null;
                public ModelBuilder Entity<TEntity>(System.Action<EntityTypeBuilder<TEntity>> buildAction) where TEntity : class => this;
                public ModelBuilder ApplyConfiguration<TEntity>(IEntityTypeConfiguration<TEntity> configuration) where TEntity : class => this;
                public ModelBuilder ApplyConfigurationsFromAssembly(System.Reflection.Assembly assembly, System.Func<System.Type, bool> predicate = null) => this;
                public ModelBuilder HasDefaultSchema(string schema) => this;
                public ModelBuilder Owned<T>() => this;
            }

            public enum DeleteBehavior { Cascade, Restrict }
        }

        namespace Microsoft.EntityFrameworkCore.Metadata.Builders
        {
            using System;
            using System.Collections.Generic;

            public class PropertyBuilder
            {
                public PropertyBuilder HasMaxLength(int length) => this;
                public PropertyBuilder IsRequired() => this;
                public PropertyBuilder HasPrecision(int precision, int scale) => this;
                public PropertyBuilder HasColumnName(string name) => this;
            }

            public class OwnedNavigationBuilder<TOwner, TOwned>
            {
                public PropertyBuilder Property(Func<TOwned, object> property) => null;
                public OwnedNavigationBuilder<TOwner, TOwned> ToTable(string name) => this;
            }

            public class ReferenceCollectionBuilder<TPrincipal, TDependent>
            {
                public ReferenceCollectionBuilder<TPrincipal, TDependent> HasForeignKey(Func<TDependent, object> key) => this;
                public ReferenceCollectionBuilder<TPrincipal, TDependent> OnDelete(Microsoft.EntityFrameworkCore.DeleteBehavior behavior) => this;
            }

            public class ReferenceNavigationBuilder<TEntity, TRelated>
            {
                public ReferenceCollectionBuilder<TRelated, TEntity> WithMany(Func<TRelated, IEnumerable<TEntity>> navigation = null) => null;
            }

            public class CollectionCollectionBuilder<TLeft, TRight>
            {
                public EntityTypeBuilder<TJoin> UsingEntity<TJoin>(Action<EntityTypeBuilder<TJoin>> configure = null) where TJoin : class => null;
                public CollectionCollectionBuilder<TLeft, TRight> UsingEntity(string name) => this;
            }

            public class CollectionNavigationBuilder<TEntity, TRelated>
            {
                public CollectionCollectionBuilder<TRelated, TEntity> WithMany(Func<TRelated, IEnumerable<TEntity>> navigation = null) => null;
                public ReferenceCollectionBuilder<TEntity, TRelated> WithOne(Func<TRelated, TEntity> navigation = null) => null;
            }

            public class EntityTypeBuilder<TEntity> where TEntity : class
            {
                public EntityTypeBuilder<TEntity> ToTable(string name) => this;
                public EntityTypeBuilder<TEntity> ToTable(string name, string schema) => this;
                public EntityTypeBuilder<TEntity> HasKey(Func<TEntity, object> key) => this;
                public EntityTypeBuilder<TEntity> HasIndex(Func<TEntity, object> index) => this;
                public EntityTypeBuilder<TEntity> HasData(params object[] data) => this;
                public EntityTypeBuilder<TEntity> HasData(IEnumerable<TEntity> data) => this;
                public EntityTypeBuilder<TEntity> HasQueryFilter(Func<TEntity, bool> filter) => this;
                public PropertyBuilder Property(Func<TEntity, object> property) => null;
                public EntityTypeBuilder<TEntity> OwnsOne<TOwned>(Func<TEntity, TOwned> navigation, Action<OwnedNavigationBuilder<TEntity, TOwned>> build = null) => this;
                public ReferenceNavigationBuilder<TEntity, TRelated> HasOne<TRelated>(Func<TEntity, TRelated> navigation = null) => null;
                public CollectionNavigationBuilder<TEntity, TRelated> HasMany<TRelated>(Func<TEntity, IEnumerable<TRelated>> navigation = null) => null;
            }
        }

        namespace FusionModules.EntityFrameworkCore
        {
            public static class ModuleModelBuilderExtensions
            {
                public static Microsoft.EntityFrameworkCore.ModelBuilder ApplyModuleConfigurations(
                    this Microsoft.EntityFrameworkCore.ModelBuilder modelBuilder, Microsoft.EntityFrameworkCore.DbContext context) => modelBuilder;
            }
        }
        """;

    private static readonly Lazy<Task<MetadataReference>> EntityFramework = new(async () =>
        AnalyzerTest.Compile("Microsoft.EntityFrameworkCore.Stubs", [EntityFrameworkStubs], [.. await AnalyzerTest.BaseReferences.Value, await AnalyzerTest.Framework.Value]));

    /// <summary>A shop's entities, in the context's namespace.</summary>
    private const string Entities = """
        using System.Collections.Generic;

        namespace Shop.Data
        {
            internal sealed class Order
            {
                public int Id { get; set; }
                public string Number { get; set; }
                public int TenantId { get; set; }
                public int CustomerId { get; set; }
                public Customer Customer { get; set; }
                public Address ShipTo { get; set; }
                public List<Tag> Tags { get; set; }
            }

            internal sealed class Customer
            {
                public int Id { get; set; }
                public string Name { get; set; }
                public List<Order> Orders { get; set; }
            }

            internal sealed class Product
            {
                public int Id { get; set; }
                public string Name { get; set; }
                public decimal Price { get; set; }
            }

            internal sealed class Tag
            {
                public int Id { get; set; }
                public List<Order> Orders { get; set; }
            }

            internal sealed class OrderTag
            {
                public int OrderId { get; set; }
                public int TagId { get; set; }
            }

            internal sealed class Address
            {
                public string City { get; set; }
            }
        }
        """;

    private const string Module = """
        using FusionModules;
        using Microsoft.AspNetCore.Hosting;

        [assembly: HostingStartup(typeof(ShopModule))]

        internal sealed class ShopModule : ModuleBase { }
        """;

    [Test]
    public async Task EachEntityGetsAClass_AppliedWhereItsConfigurationStood()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.HasDefaultSchema("shop");

                    // Orders are looked up by number.
                    modelBuilder.Entity<Order>(e =>
                    {
                        e.HasKey(o => o.Id);
                        // The number is what a customer quotes.
                        e.HasIndex(o => o.Number);
                    });

                    modelBuilder.Entity<Customer>()
                        .ToTable("customers")
                        .HasKey(c => c.Id);
                    modelBuilder.Entity<Product>(p => p.ToTable("products"));
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.HasDefaultSchema("shop");

                    modelBuilder.ApplyConfiguration(new OrderConfiguration());

                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> e)
                {
                    // Orders are looked up by number.
                    e.HasKey(o => o.Id);
                    // The number is what a customer quotes.
                    e.HasIndex(o => o.Number);
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder
                        .ToTable("customers")
                        .HasKey(c => c.Id);
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
            {
                public void Configure(EntityTypeBuilder<Product> p)
                {
                    p.ToTable("products");
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task TheMessageSaysWhatMoves_AndWhatStaysAndWhy()
    {
        var test = await AnalyzerTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                private readonly int _tenantId = 1;

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>().HasKey(c => c.Id);
                    modelBuilder.Entity<Order>(e => e.HasQueryFilter(o => o.TenantId == _tenantId));
                    if (Database.IsSqlite())
                    {
                        modelBuilder.Entity<Product>().ToTable("products");
                    }
                }
            }
            """);
        test.TestState.Sources.Add(Entities);
        test.ExpectedDiagnostics.Add(new DiagnosticResult(Diagnostics.EntityConfigurationCanMove)
            .WithSpan(9, 29, 9, 44)
            .WithArguments(
                "ShopContext",
                1,
                0,
                "; left in place: Order uses the context's '_tenantId', Product is configured inside a larger statement"));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task WhatReadsTheContextStays_AndTheRestOfTheEntityMoves()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                internal const string Schema = "shop";
                private const string Prefix = "t_";
                private readonly int _tenantId = 1;

                public int TenantId => _tenantId;

                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(e =>
                    {
                        e.ToTable("orders", Schema);
                        e.HasKey(o => o.Id);
                    });

                    // Evaluated against the context of each query.
                    modelBuilder.Entity<Order>().HasQueryFilter(o => o.TenantId == TenantId);
                    modelBuilder.Entity<Customer>(e =>
                    {
                        e.ToTable(Prefix + "customers");
                        e.HasQueryFilter(c => c.Id != this._tenantId);
                    });

                    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                    {
                        entityType.SetTableName("x");
                    }

                    modelBuilder.Entity<Order>().HasIndex(o => o.Number);
                    modelBuilder.Entity<Product>().HasKey(p => p.Id);
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        // The filter and the private constant keep their statements; the index after the loop over
        // the model's entity types stays behind it; Product, after the loop, is applied after it.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                internal const string Schema = "shop";
                private const string Prefix = "t_";
                private readonly int _tenantId = 1;

                public int TenantId => _tenantId;

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());

                    // Evaluated against the context of each query.
                    modelBuilder.Entity<Order>().HasQueryFilter(o => o.TenantId == TenantId);
                    modelBuilder.Entity<Customer>(e =>
                    {
                        e.ToTable(Prefix + "customers");
                        e.HasQueryFilter(c => c.Id != this._tenantId);
                    });

                    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                    {
                        entityType.SetTableName("x");
                    }

                    modelBuilder.Entity<Order>().HasIndex(o => o.Number);
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> e)
                {
                    e.ToTable("orders", ShopContext.Schema);
                    e.HasKey(o => o.Id);
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
            {
                public void Configure(EntityTypeBuilder<Product> builder)
                {
                    builder.HasKey(p => p.Id);
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task OwnedTypesAndManyToMany_MoveWithTheirOwner_AndSeveralStatementsOfAnEntityBecomeOneClass()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(order =>
                    {
                        order.OwnsOne(o => o.ShipTo, address =>
                        {
                            address.Property(a => a.City).HasMaxLength(100);
                        });
                        order.HasMany(o => o.Tags)
                            .WithMany(t => t.Orders)
                            .UsingEntity<OrderTag>(join => join.ToTable("order_tags"));
                    });
                    modelBuilder.Entity<Product>().Property(p => p.Price).HasPrecision(18, 2);
                    modelBuilder.Entity<Order>().HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerId);
                    modelBuilder.Entity<Customer>(builder => builder.Property(c => c.Name).IsRequired());
                    modelBuilder.Entity<Order>().HasIndex(o => o.Number);
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        // Product names neither Order nor anything Order names, so Order's second statement joins
        // the first across it. Customer's names Customer, as Order's relationship does: the index
        // after it stays where it was.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                    modelBuilder.Entity<Order>().HasIndex(o => o.Number);
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> order)
                {
                    order.OwnsOne(o => o.ShipTo, address =>
                    {
                        address.Property(a => a.City).HasMaxLength(100);
                    });
                    order.HasMany(o => o.Tags)
                        .WithMany(t => t.Orders)
                        .UsingEntity<OrderTag>(join => join.ToTable("order_tags"));
                    order.HasOne(o => o.Customer).WithMany(c => c.Orders).HasForeignKey(o => o.CustomerId);
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
            {
                public void Configure(EntityTypeBuilder<Product> builder)
                {
                    builder.Property(p => p.Price).HasPrecision(18, 2);
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.Property(c => c.Name).IsRequired();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AClassThatConfiguresTheEntityAlready_TakesTheStatementsAfterIt()
    {
        const string Existing = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data.Configurations;

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasKey(o => o.Id);
                }
            }

            internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
            {
                public void Configure(EntityTypeBuilder<Tag> builder) => builder.HasKey(t => t.Id);
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Shop.Data.Configurations;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                    modelBuilder.ApplyConfiguration(new TagConfiguration());
                    modelBuilder.Entity<Order>(e =>
                    {
                        // By number.
                        e.HasIndex(o => o.Number);
                    });
                    modelBuilder.Entity<Tag>().ToTable("tags");
                    modelBuilder.Entity<Customer>().ToTable("customers");
                }
            }
            """);
        test.TestState.Sources.Add(Entities);
        test.TestState.Sources.Add(("/0/Configurations/OrderConfiguration.cs", Existing));

        // What comes before the class is applied stays before it; Tag's class has no block to add
        // to. Customer's class goes where the project keeps its configuration classes.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Shop.Data.Configurations;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                    modelBuilder.ApplyConfiguration(new TagConfiguration());
                    modelBuilder.Entity<Tag>().ToTable("tags");
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/Configurations/OrderConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data.Configurations;

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasKey(o => o.Id);
                    // By number.
                    builder.HasIndex(o => o.Number);
                }
            }

            internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
            {
                public void Configure(EntityTypeBuilder<Tag> builder) => builder.HasKey(t => t.Id);
            }
            """));
        test.FixedState.Sources.Add(("/0/Configurations/CustomerConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data.Configurations;

            internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> builder)
                {
                    builder.ToTable("customers");
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AClassOfTheEntityThatThisContextDoesNotApply_KeepsTheEntityInPlace()
    {
        var test = await AnalyzerTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                }
            }

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder) { }
            }
            """);
        test.TestState.Sources.Add(Entities);

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task InAModuleThatComposesItsModel_TheStatementsGo_AndTheCompositionFindsTheClasses()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using FusionModules.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");

                    modelBuilder.ApplyModuleConfigurations(this);

                    modelBuilder.Entity<Customer>().ToTable("customers");
                    ConfigureCatalog(modelBuilder);
                }

                private static void ConfigureCatalog(ModelBuilder builder)
                {
                    builder.Entity<Product>(product =>
                    {
                        product.ToTable("products");
                    });
                }
            }
            """);
        test.TestState.Sources.Add(Entities);
        test.TestState.Sources.Add(Module);

        test.FixedState.Sources.Add("""
            using FusionModules.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyModuleConfigurations(this);
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(Module);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", Class("Order", "builder", "builder.ToTable(\"orders\");")));
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", Class("Customer", "builder", "builder.ToTable(\"customers\");")));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", Class("Product", "product", "product.ToTable(\"products\");")));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task InAProjectThatIsNoModule_TheCompositionDoesNotFindTheClasses_SoEachIsApplied()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using FusionModules.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyModuleConfigurations(this);
                    modelBuilder.Entity<Customer>().ToTable("customers");
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using FusionModules.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyModuleConfigurations(this);
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", Class("Customer", "builder", "builder.ToTable(\"customers\");")));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AnAssemblyScan_FindsTheClasses_AndKeepsWhatWouldCrossAStatementThatStays()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.Entity<Customer>().ToTable("customers");
                    modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShopContext).Assembly);
                    modelBuilder.Entity<Product>().ToTable("products");
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShopContext).Assembly);
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", Class("Customer", "builder", "builder.ToTable(\"customers\");")));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", Class("Product", "builder", "builder.ToTable(\"products\");")));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    [Arguments("modelBuilder.ApplyConfigurationsFromAssembly(typeof(ShopContext).Assembly, type => type.Name.StartsWith(\"Order\"));")]
    [Arguments("modelBuilder.ApplyConfigurationsFromAssembly(Assemblies.Data);")]
    public async Task AScanWhoseReachCannotBeTold_LeavesTheContextAlone(string scan)
    {
        var test = await AnalyzerTestAsync();
        test.TestState.Sources.Add($$"""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    {{scan}}
                    modelBuilder.Entity<Order>().ToTable("orders");
                }
            }

            internal static class Assemblies
            {
                public static System.Reflection.Assembly Data => typeof(Assemblies).Assembly;
            }
            """);
        test.TestState.Sources.Add(Entities);

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AnotherContextThatScansTheAssembly_WouldFindTheClasses_SoNoneAreOffered()
    {
        var test = await AnalyzerTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                }
            }

            internal sealed class ReportingContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder) =>
                    modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReportingContext).Assembly);
            }
            """);
        test.TestState.Sources.Add(Entities);

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task HelpersOfTheContext_GiveUpTheirConfiguration_AndGoWhenNothingIsLeft()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    ConfigureOrders(modelBuilder);
                    modelBuilder.Entity<Customer>(ConfigureCustomer);
                    ConfigureProduct(modelBuilder.Entity<Product>());
                    ConfigureTags(modelBuilder, "tags");
                }

                private void ConfigureOrders(ModelBuilder builder)
                {
                    builder.HasDefaultSchema("orders");
                    builder.Entity<Order>(order => order.ToTable("orders"));
                }

                /// <summary>Customers.</summary>
                private static void ConfigureCustomer(EntityTypeBuilder<Customer> customer)
                {
                    customer.ToTable("customers");
                    customer.HasKey(c => c.Id);
                }

                private static void ConfigureProduct(EntityTypeBuilder<Product> product) => product.ToTable("products");

                private static void ConfigureTags(ModelBuilder builder, string table)
                {
                    builder.Entity<Tag>().ToTable(table);
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    ConfigureOrders(modelBuilder);
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());
                    ConfigureTags(modelBuilder, "tags");
                }

                private void ConfigureOrders(ModelBuilder builder)
                {
                    builder.HasDefaultSchema("orders");
                    builder.ApplyConfiguration(new OrderConfiguration());
                }

                private static void ConfigureTags(ModelBuilder builder, string table)
                {
                    builder.Entity<Tag>().ToTable(table);
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", Class("Order", "order", "order.ToTable(\"orders\");")));
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", Class("Customer", "customer", "customer.ToTable(\"customers\");\n        customer.HasKey(c => c.Id);")));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", Class("Product", "product", "product.ToTable(\"products\");")));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ALocalOnlyTheEntityReads_MovesWithIt_AndOneThatIsSharedKeepsItsStatements()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using System.Collections.Generic;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    // Seed data
                    var products = new List<Product>
                    {
                        new Product { Id = 1, Name = "Flashlight" },
                    };

                    modelBuilder.Entity<Product>().HasData(products);

                    var customer = new Customer { Id = 1 };
                    modelBuilder.Entity<Customer>().HasData(customer);
                    modelBuilder.Entity<Order>().HasData(new Order { Id = 1, CustomerId = customer.Id });
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        // The list was all the context needed System.Collections.Generic for.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());

                    var customer = new Customer { Id = 1 };
                    modelBuilder.Entity<Customer>().HasData(customer);
                    modelBuilder.Entity<Order>().HasData(new Order { Id = 1, CustomerId = customer.Id });
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", """
            using System.Collections.Generic;
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
            {
                public void Configure(EntityTypeBuilder<Product> builder)
                {
                    // Seed data
                    var products = new List<Product>
                    {
                        new Product { Id = 1, Name = "Flashlight" },
                    };
                    builder.HasData(products);
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task APartialContext_IsSplitWhereOnModelCreatingIs_AndAHelperInAnotherPartStays()
    {
        const string Sets = """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data
            {
                internal partial class ShopContext : DbContext
                {
                    public DbSet<Order> Orders { get; set; }

                    private void ConfigureCustomers(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Customer>().ToTable("customers");
                    }
                }
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data
            {
                internal partial class ShopContext
                {
                    protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                    {
                        ConfigureCustomers(modelBuilder);
                        modelBuilder.Entity<Order>(entity =>
                        {
                            entity.ToTable("orders");
                        });
                    }
                }
            }
            """);
        test.TestState.Sources.Add(Sets);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data
            {
                internal partial class ShopContext
                {
                    protected override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        ConfigureCustomers(modelBuilder);
                        modelBuilder.ApplyConfiguration(new OrderConfiguration());
                    }
                }
            }
            """);
        test.FixedState.Sources.Add(Sets);
        test.FixedState.Sources.Add(Entities);

        // The context's file declares its namespace with a block, and so does the class's.
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data
            {
                internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
                {
                    public void Configure(EntityTypeBuilder<Order> entity)
                    {
                        entity.ToTable("orders");
                    }
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AContextInASharedProject_GetsItsClassesBesideIt_WithTheUsingsItsCodeNeeds()
    {
        var test = await FixTestAsync();
        var domain = new ProjectState("Shop.Domain", LanguageNames.CSharp, "/Domain/", "cs");
        domain.Sources.Add("""
            namespace Shop.Domain
            {
                public sealed class Invoice
                {
                    public int Id { get; set; }
                    public decimal Total { get; set; }
                }
            }

            namespace Shop.Domain.Naming
            {
                public static class Tables
                {
                    public const string Invoices = "invoices";
                }
            }
            """);
        test.TestState.AdditionalProjects.Add(domain.Name, domain);
        test.TestState.AdditionalProjectReferences.Add(domain.Name);
        test.TestState.Sources.Add("""
            using System;
            using Microsoft.EntityFrameworkCore;
            using Shop.Domain;
            using static Shop.Domain.Naming.Tables;

            namespace Shop.Infrastructure.Data;

            public class SharedContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Invoice>(invoice =>
                    {
                        invoice.ToTable(Invoices);
                        invoice.Property(i => i.Total).HasPrecision(18, 2);
                    });
                    Console.WriteLine("configured");
                }
            }
            """);

        test.FixedState.AdditionalProjects.Add(domain.Name, domain);
        test.FixedState.AdditionalProjectReferences.Add(domain.Name);
        test.FixedState.Sources.Add("""
            using System;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Infrastructure.Data;

            public class SharedContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new InvoiceConfiguration());
                    Console.WriteLine("configured");
                }
            }
            """);
        test.FixedState.Sources.Add(("/0/InvoiceConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using Shop.Domain;
            using static Shop.Domain.Naming.Tables;

            namespace Shop.Infrastructure.Data;

            internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
            {
                public void Configure(EntityTypeBuilder<Invoice> invoice)
                {
                    invoice.ToTable(Invoices);
                    invoice.Property(i => i.Total).HasPrecision(18, 2);
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task TheByteOrderMarkAndTheLineEndings_AreTheContextFiles()
    {
        var utf8WithMark = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        static string Windows(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

        var test = await FixTestAsync();
        test.TestState.Sources.Add(("/0/ShopContext.cs", SourceText.From(Windows("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(e =>
                    {
                        e.ToTable("orders");
                        e.HasKey(o => o.Id);
                    });
                    modelBuilder.HasDefaultSchema("shop");
                }
            }
            """), utf8WithMark)));
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add(("/0/ShopContext.cs", SourceText.From(Windows("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                    modelBuilder.HasDefaultSchema("shop");
                }
            }
            """), utf8WithMark)));
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", SourceText.From(
            Windows(Class("Order", "e", "e.ToTable(\"orders\");\n        e.HasKey(o => o.Id);")), utf8WithMark)));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task FixAll_SplitsEveryContext_AndTwoThatConfigureOneEntityGetTwoClasses()
    {
        const string Reporting = """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ReportingContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder) =>
                    modelBuilder.Entity<Product>(product => product.ToTable("report_products"));
            }
            """;
        const string ReportingFixed = """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ReportingContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder) =>
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                }
            }
            """);
        test.TestState.Sources.Add(Reporting);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(ReportingFixed);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", Class("Order", "builder", "builder.ToTable(\"orders\");")));
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", Class("Product", "product", "product.ToTable(\"report_products\");")));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task WhatIsNoContextsOnModelCreating_OrHasNothingOfOneEntity_IsNotReported()
    {
        var test = await AnalyzerTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.HasDefaultSchema("shop");
                    modelBuilder.Owned<Address>();
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                }
            }

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order> { }

            internal abstract class Base
            {
                protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
            }

            internal sealed class NotAContext : Base
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                }
            }

            internal static class Tests
            {
                // A context nested in another type is not read.
                private sealed class NestedContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.Entity<Customer>().ToTable("customers");
                    }
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task WithoutEntityFramework_NothingIsRead() =>
        await AnalyzerTest.VerifyAsync<EntityConfigurationAnalyzer>("""
            internal class ModelBuilder { }

            internal class Base
            {
                protected virtual void OnModelCreating(ModelBuilder modelBuilder) { }
            }

            internal sealed class Derived : Base
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder) { }
            }
            """);

    [Test]
    public async Task UnderAScan_AClassOfTheEntity_TakesTheStatementsAfterTheScan_InItsOwnNameForTheBuilder()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
                    modelBuilder.Entity<Order>().HasIndex(o => o.Number);
                    modelBuilder.Entity<Order>(order => order.ToTable("orders"));
                }
            }

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> orders)
                {
                    orders.HasKey(o => o.Id);
                }
            }
            """);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfigurationsFromAssembly(GetType().Assembly);
                }
            }

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> orders)
                {
                    orders.HasKey(o => o.Id);
                    orders.HasIndex(o => o.Number);
                    orders.ToTable("orders");
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ANameThatIsTaken_GivesTheClassAnother_AndAnEntityConfiguredThroughALocalStays()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>().ToTable("orders");
                    var customers = modelBuilder.Entity<Customer>();
                    customers.ToTable("customers");
                }
            }

            /// <summary>Not a configuration of the entity: the options of ordering.</summary>
            internal sealed class OrderConfiguration { }
            """);
        test.TestState.Sources.Add(Entities);
        test.TestState.ExpectedDiagnostics.Add(new DiagnosticResult(Diagnostics.EntityConfigurationCanMove)
            .WithSpan(7, 29, 7, 44)
            .WithArguments("ShopContext", 1, 0, "; left in place: Customer is configured through a local variable"));

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderEntityConfiguration());
                    var customers = modelBuilder.Entity<Customer>();
                    customers.ToTable("customers");
                }
            }

            /// <summary>Not a configuration of the entity: the options of ordering.</summary>
            internal sealed class OrderConfiguration { }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/OrderEntityConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class OrderEntityConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.ToTable("orders");
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task DbSetProperties_BecomeExtensionPropertiesOfDbContext_AndTheirEntitiesClassesSayWhatTheySaid()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using System.Linq;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                /// <summary>The orders.</summary>
                public DbSet<Order> Orders { get; set; }

                public DbSet<Customer> Customers => Set<Customer>();

                public DbSet<Tag> Tag { get; set; } = null!;

                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.HasDefaultSchema("shop");
                    modelBuilder.Entity<Order>(e => e.HasKey(o => o.Id));
                    modelBuilder.Entity<Customer>().ToTable("customers");
                }

                public int Count() => Orders.Count();
            }

            internal sealed class Reports
            {
                public static readonly System.Func<ShopContext, int> Compiled = EF.CompileQuery((ShopContext c) => c.Orders.Count());

                public IQueryable<Order> WithCustomers(ShopContext context) =>
                    context.Orders.Where(o => context.Customers.Any(c => c.Id == o.CustomerId));
            }
            """);
        test.TestState.Sources.Add(Entities);

        // The entity that had a property and nothing else is applied first: it was in the model from
        // the start. Inside an expression tree a set is read as Set<T>(), which is what EF Core reads.
        test.FixedState.Sources.Add("""
            using System.Linq;
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new TagConfiguration());
                    base.OnModelCreating(modelBuilder);
                    modelBuilder.HasDefaultSchema("shop");
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                }

                public int Count() => this.Orders.Count();
            }

            internal sealed class Reports
            {
                public static readonly System.Func<ShopContext, int> Compiled = EF.CompileQuery((ShopContext c) => c.Set<Order>().Count());

                public IQueryable<Order> WithCustomers(ShopContext context) =>
                    context.Orders.Where(o => context.Set<Customer>().Any(c => c.Id == o.CustomerId));
            }
            """);
        test.FixedState.Sources.Add(Entities);

        // Orders named the table; Customers' is named by its configuration; Tag is the type's name anyway.
        test.FixedState.Sources.Add(("/0/OrderConfiguration.cs", Class("Order", "e", "e.ToTable(\"Orders\");\n        e.HasKey(o => o.Id);")));
        test.FixedState.Sources.Add(("/0/CustomerConfiguration.cs", Class("Customer", "builder", "builder.ToTable(\"customers\");")));
        test.FixedState.Sources.Add(("/0/TagConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
            {
                public void Configure(EntityTypeBuilder<Tag> builder)
                {
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ShopContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal static class ShopContextExtensions
            {
                extension(DbContext context)
                {
                    /// <summary>The orders.</summary>
                    public DbSet<Order> Orders => context.Set<Order>();
                    public DbSet<Customer> Customers => context.Set<Customer>();
                    public DbSet<Tag> Tag => context.Set<Tag>();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task BeforeCSharp14_TheSetsAreExtensionMethods_AndEveryUseGetsItsParentheses()
    {
        var test = await FixTestAsync();
        test.SolutionTransforms.Add((solution, _) =>
        {
            foreach (var id in solution.ProjectIds)
            {
                solution = solution.WithProjectParseOptions(
                    id, ((Microsoft.CodeAnalysis.CSharp.CSharpParseOptions)solution.GetProject(id)!.ParseOptions!).WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp12));
            }

            return solution;
        });

        const string Uses = """
            using System.Linq;
            using Shop.Data;

            namespace Shop.Web
            {
                internal static class Queries
                {
                    public static int Count(DiscountContext context) => context.Coupons.Count();

                    public static bool Any(DiscountContext context) => context.Coupons.Any(c => context.Coupons.Count() > c.Id);
                }
            }
            """;
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data
            {
                public class Coupon
                {
                    public int Id { get; set; }
                }

                public class {|MOD0016:DiscountContext|} : DbContext
                {
                    public DbSet<Coupon> Coupons { get; set; }
                }
            }
            """);
        test.TestState.Sources.Add(Uses);

        // A context that had no OnModelCreating gets one, which applies what the property said.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data
            {
                public class Coupon
                {
                    public int Id { get; set; }
                }

                public class DiscountContext : DbContext
                {
                    protected override void OnModelCreating(ModelBuilder modelBuilder)
                    {
                        modelBuilder.ApplyConfiguration(new CouponConfiguration());
                    }
                }
            }
            """);
        test.FixedState.Sources.Add(Uses
            .Replace("context.Coupons.Count();", "context.Coupons().Count();")
            .Replace("context.Coupons.Any(c => context.Coupons.Count()", "context.Coupons().Any(c => context.Set<Coupon>().Count()"));
        test.FixedState.Sources.Add(("/0/CouponConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data
            {
                internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
                {
                    public void Configure(EntityTypeBuilder<Coupon> builder)
                    {
                        builder.ToTable("Coupons");
                    }
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/DiscountContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data
            {
                public static class DiscountContextExtensions
                {
                    public static DbSet<Coupon> Coupons(this DbContext context) => context.Set<Coupon>();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task TheMessageCountsTheProperties_AndSaysWhichStayAndWhy()
    {
        var test = await AnalyzerTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal interface IShopContext
            {
                DbSet<Order> Orders { get; }
            }

            internal class ShopContext : DbContext, IShopContext
            {
                public DbSet<Order> Orders { get; set; }
                public virtual DbSet<Customer> Customers { get; set; }
                public DbSet<Product> Products { get; set; }
                public DbSet<Tag> Tags { get; set; }

                protected override void {|#0:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                    {
                        entityType.SetTableName("x");
                    }

                    modelBuilder.Entity<Product>().HasKey(p => p.Id);
                }
            }
            """);
        test.TestState.Sources.Add(Entities);
        test.ExpectedDiagnostics.Add(new DiagnosticResult(Diagnostics.EntityConfigurationCanMove)
            .WithLocation(0)
            .WithArguments(
                "ShopContext",
                1,
                1,
                "; left in place: property Orders implements IShopContext.Orders, property Customers is static or can be overridden, " +
                "property Products would come into the model after a statement that may read its entity types"));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task APropertyThatIsAssignedOrNamed_Stays_AndTheOthersGo()
    {
        const string Uses = """
            using Shop.Data;

            internal static class Uses
            {
                public static string Name(ShopContext context) => nameof(context.Orders);

                public static void Replace(ShopContext context, ShopContext other) => context.Customers = other.Customers;
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class {|MOD0016:ShopContext|} : DbContext
            {
                public DbSet<Order> Orders { get; set; }
                public DbSet<Customer> Customers { get; set; }
                public DbSet<Product> Product { get; set; }
            }
            """);
        test.TestState.Sources.Add(Uses);
        test.TestState.Sources.Add(Entities);

        // How a property is used only the fix reads, so the two it left are still reported.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal sealed class ShopContext : DbContext
            {
                public DbSet<Order> Orders { get; set; }
                public DbSet<Customer> Customers { get; set; }

                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new ProductConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Uses);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/ProductConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
            {
                public void Configure(EntityTypeBuilder<Product> builder)
                {
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ShopContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal static class ShopContextExtensions
            {
                extension(DbContext context)
                {
                    public DbSet<Product> Product => context.Set<Product>();
                }
            }

            """));
        test.FixedState.MarkupHandling = MarkupMode.Allow;
        test.NumberOfIncrementalIterations = 2;
        test.NumberOfFixAllIterations = 2;

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ASetAnotherNamespaceHasAMemberFor_Stays_WhereSomeCodeWouldFindBoth()
    {
        const string Uses = """
            using Shop.Auth;
            using Shop.Common;

            namespace Shop.Web;

            internal static class Queries
            {
                public static object Players(AuthContext context) => context.Players;

                public static object Partners(AuthContext context) => context.Partners;

                public static object Common(CommonContext context) => context.Players;
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Common;

            public class Player
            {
                public int Id { get; set; }
            }

            internal sealed class {|MOD0016:CommonContext|} : DbContext
            {
                public DbSet<Player> Players { get; set; }
            }
            """);
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Auth;

            public class Player
            {
                public int Id { get; set; }
            }

            public class Partner
            {
                public int Id { get; set; }
            }

            internal sealed class {|MOD0016:AuthContext|} : DbContext
            {
                public DbSet<Player> Players { get; set; }
                public DbSet<Partner> Partners { get; set; }
            }
            """);
        test.TestState.Sources.Add(Uses);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Common;

            public class Player
            {
                public int Id { get; set; }
            }

            internal sealed class CommonContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new PlayerConfiguration());
                }
            }
            """);

        // Two extension members of DbContext named Players, in two namespaces a file imports, are
        // ambiguous there (CS9339). The second context keeps its property, and is still reported.
        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;
            using Shop.Common;

            namespace Shop.Auth;

            public class Player
            {
                public int Id { get; set; }
            }

            public class Partner
            {
                public int Id { get; set; }
            }

            internal sealed class AuthContext : DbContext
            {
                public DbSet<Player> Players { get; set; }

                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new PartnerConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Uses);
        test.FixedState.Sources.Add(("/0/PlayerConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Common;

            internal sealed class PlayerConfiguration : IEntityTypeConfiguration<Player>
            {
                public void Configure(EntityTypeBuilder<Player> builder)
                {
                    builder.ToTable("Players");
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/CommonContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Common;

            internal static class CommonContextExtensions
            {
                extension(DbContext context)
                {
                    public DbSet<Player> Players => context.Set<Player>();
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/PartnerConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;
            using Shop.Auth;

            namespace Shop.Common;

            internal sealed class PartnerConfiguration : IEntityTypeConfiguration<Partner>
            {
                public void Configure(EntityTypeBuilder<Partner> builder)
                {
                    builder.ToTable("Partners");
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/AuthContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Auth;

            internal static class AuthContextExtensions
            {
                extension(DbContext context)
                {
                    public DbSet<Partner> Partners => context.Set<Partner>();
                }
            }

            """));
        test.FixedState.MarkupHandling = MarkupMode.Allow;
        test.NumberOfIncrementalIterations = 3;
        test.NumberOfFixAllIterations = 2;
        test.NumberOfFixAllInDocumentIterations = 3;

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task ASetOfTheSameName_StaysToo_WhereAReadOfTheOtherMemberWouldFindTheNewOne()
    {
        // The other way round: the member that is there already is read where the new one would be found.
        const string Existing = """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Auth
            {
                public class Player
                {
                    public int Id { get; set; }
                }

                internal static class AuthSets
                {
                    extension(DbContext context)
                    {
                        public DbSet<Player> Players => context.Set<Player>();
                    }
                }
            }

            namespace Shop.Web
            {
                using Shop.Auth;
                using Shop.Common;

                internal static class Queries
                {
                    public static object Players(DbContext context) => context.Players;

                    public static object Tags(CommonContext context) => context.Tags;
                }
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Common;

            public class Player
            {
                public int Id { get; set; }
            }

            public class Tag
            {
                public int Id { get; set; }
            }

            internal sealed class {|MOD0016:CommonContext|} : DbContext
            {
                public DbSet<Player> Players { get; set; }
                public DbSet<Tag> Tags { get; set; }
            }
            """);
        test.TestState.Sources.Add(Existing);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Common;

            public class Player
            {
                public int Id { get; set; }
            }

            public class Tag
            {
                public int Id { get; set; }
            }

            internal sealed class CommonContext : DbContext
            {
                public DbSet<Player> Players { get; set; }

                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new TagConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Existing);
        test.FixedState.Sources.Add(("/0/TagConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Common;

            internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
            {
                public void Configure(EntityTypeBuilder<Tag> builder)
                {
                    builder.ToTable("Tags");
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/CommonContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Common;

            internal static class CommonContextExtensions
            {
                extension(DbContext context)
                {
                    public DbSet<Tag> Tags => context.Set<Tag>();
                }
            }

            """));
        test.FixedState.MarkupHandling = MarkupMode.Allow;
        test.NumberOfIncrementalIterations = 2;
        test.NumberOfFixAllIterations = 2;

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AClassTheContextApplies_SaysTheTableName_AndADerivedEntitySharesItsBasesTable()
    {
        const string Accounts = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal class Account
            {
                public int Id { get; set; }
            }

            internal sealed class PremiumAccount : Account
            {
            }

            internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
            {
                public void Configure(EntityTypeBuilder<Order> builder)
                {
                    builder.HasKey(o => o.Id);
                }
            }
            """;

        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal abstract class AuditedContext : DbContext
            {
            }

            internal sealed class ShopContext : AuditedContext
            {
                public DbSet<Order> Orders { get; set; }

                public DbSet<Account> Accounts { get; set; }

                public DbSet<PremiumAccount> PremiumAccounts { get; set; }

                protected override void {|MOD0016:OnModelCreating|}(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                }
            }
            """);
        test.TestState.Sources.Add(Accounts);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal abstract class AuditedContext : DbContext
            {
            }

            internal sealed class ShopContext : AuditedContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new AccountConfiguration());
                    modelBuilder.ApplyConfiguration(new PremiumAccountConfiguration());
                    modelBuilder.ApplyConfiguration(new OrderConfiguration());
                }
            }
            """);
        test.FixedState.Sources.Add(Accounts.Replace(
            "        builder.HasKey(o => o.Id);",
            "        builder.HasKey(o => o.Id);\n        builder.ToTable(\"Orders\");"));
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/AccountConfiguration.cs", Class("Account", "builder", "builder.ToTable(\"Accounts\");")));
        test.FixedState.Sources.Add(("/0/PremiumAccountConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class PremiumAccountConfiguration : IEntityTypeConfiguration<PremiumAccount>
            {
                public void Configure(EntityTypeBuilder<PremiumAccount> builder)
                {
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ShopContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal static class ShopContextExtensions
            {
                extension(DbContext context)
                {
                    public DbSet<Order> Orders => context.Set<Order>();
                    public DbSet<Account> Accounts => context.Set<Account>();
                    public DbSet<PremiumAccount> PremiumAccounts => context.Set<PremiumAccount>();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    [Test]
    public async Task AContextWithoutOnModelCreating_OverABaseOfItsOwn_AppliesItsClassesBeforeTheBasesModel()
    {
        var test = await FixTestAsync();
        test.TestState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal abstract class AuditedContext : DbContext
            {
            }

            internal sealed class {|MOD0016:ShopContext|} : AuditedContext
            {
                public DbSet<Tag> Tag { get; set; }
            }
            """);
        test.TestState.Sources.Add(Entities);

        test.FixedState.Sources.Add("""
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal abstract class AuditedContext : DbContext
            {
            }

            internal sealed class ShopContext : AuditedContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new TagConfiguration());
                    base.OnModelCreating(modelBuilder);
                }
            }
            """);
        test.FixedState.Sources.Add(Entities);
        test.FixedState.Sources.Add(("/0/TagConfiguration.cs", """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Shop.Data;

            internal sealed class TagConfiguration : IEntityTypeConfiguration<Tag>
            {
                public void Configure(EntityTypeBuilder<Tag> builder)
                {
                }
            }

            """));
        test.FixedState.Sources.Add(("/0/ShopContextExtensions.cs", """
            using Microsoft.EntityFrameworkCore;

            namespace Shop.Data;

            internal static class ShopContextExtensions
            {
                extension(DbContext context)
                {
                    public DbSet<Tag> Tag => context.Set<Tag>();
                }
            }

            """));

        await test.RunAsync(CancellationToken.None);
    }

    /// <summary>A configuration class as the fix writes it, in the entities' namespace.</summary>
    private static string Class(string entity, string builder, string body) => $$"""
        using Microsoft.EntityFrameworkCore;
        using Microsoft.EntityFrameworkCore.Metadata.Builders;

        namespace Shop.Data;

        internal sealed class {{entity}}Configuration : IEntityTypeConfiguration<{{entity}}>
        {
            public void Configure(EntityTypeBuilder<{{entity}}> {{builder}})
            {
                {{body}}
            }
        }

        """;

    private static async Task<CSharpCodeFixTest<EntityConfigurationAnalyzer, EntityConfigurationCodeFixProvider, DefaultVerifier>> FixTestAsync()
    {
        var test = new CSharpCodeFixTest<EntityConfigurationAnalyzer, EntityConfigurationCodeFixProvider, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        test.TestState.AdditionalReferences.Add(await AnalyzerTest.Framework.Value);
        test.TestState.AdditionalReferences.Add(await EntityFramework.Value);
        return test;
    }

    private static async Task<CSharpAnalyzerTest<EntityConfigurationAnalyzer, DefaultVerifier>> AnalyzerTestAsync()
    {
        var test = new CSharpAnalyzerTest<EntityConfigurationAnalyzer, DefaultVerifier>
        {
            ReferenceAssemblies = ReferenceAssemblies.Default,
        };

        test.TestState.AdditionalReferences.Add(await AnalyzerTest.Framework.Value);
        test.TestState.AdditionalReferences.Add(await EntityFramework.Value);
        return test;
    }
}
